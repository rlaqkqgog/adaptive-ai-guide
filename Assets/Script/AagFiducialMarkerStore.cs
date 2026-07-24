using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Stores the pose of one physical QR marker in each room's MRUK floor frame.
/// The stable room id in the QR payload is authoritative; room and floor UUIDs
/// are retained only to invalidate calibration after a deliberate re-scan.
/// </summary>
public static class AagFiducialMarkerStore
{
    public static string SchemaVersion =>
        $"aag-{ExperimentSpaceRuntime.StorageKey}-fiducial-zones/v1";
    public static string PayloadPrefix => $"AAG-{ExperimentSpaceRuntime.SpaceId}-ZONE:";
    public const string DirectoryName = "AagFiducialMarkers";
    public static string FileName =>
        ExperimentSpaceRuntime.NamespacedFileName("fiducial_zone_calibrations");

    [Serializable]
    public sealed class Catalog
    {
        public string schemaVersion = SchemaVersion;
        public string floorPlanId = ExperimentSpaceRuntime.FloorPlanId;
        public string updatedAtUtc = string.Empty;
        public List<ZoneCalibration> zones = new List<ZoneCalibration>();
    }

    [Serializable]
    public sealed class ZoneCalibration
    {
        public string roomId = string.Empty;
        public string roomUuid = string.Empty;
        public string floorAnchorUuid = string.Empty;
        public string markerPayload = string.Empty;
        public float floorLocalX;
        public float floorLocalY;
        public float floorLocalZ;
        public float floorLocalRotationX;
        public float floorLocalRotationY;
        public float floorLocalRotationZ;
        public float floorLocalRotationW = 1f;
        public string calibratedAtUtc = string.Empty;

        public Pose FloorLocalPose => new Pose(
            new Vector3(floorLocalX, floorLocalY, floorLocalZ),
            new Quaternion(
                floorLocalRotationX,
                floorLocalRotationY,
                floorLocalRotationZ,
                floorLocalRotationW));
    }

    public static string CatalogPath => Path.Combine(
        Application.persistentDataPath,
        DirectoryName,
        FileName);

    public static string PayloadForRoom(string roomId) =>
        PayloadPrefix + (roomId ?? string.Empty).Trim().ToLowerInvariant();

    public static bool TryParsePayload(string payload, out string roomId)
    {
        roomId = string.Empty;
        if (string.IsNullOrWhiteSpace(payload)
            || !payload.StartsWith(PayloadPrefix, StringComparison.Ordinal))
            return false;

        var value = payload.Substring(PayloadPrefix.Length).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value)
            || value.Any(character => !(char.IsLetterOrDigit(character) || character == '-')))
            return false;

        roomId = value;
        return true;
    }

    public static Catalog LoadOrCreate()
    {
        try
        {
            if (File.Exists(CatalogPath))
            {
                var loaded = JsonUtility.FromJson<Catalog>(File.ReadAllText(CatalogPath));
                if (IsValidCatalog(loaded)) return loaded;
                Debug.LogError($"[Fiducial] Ignoring invalid calibration catalog: {CatalogPath}");
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Fiducial] Calibration catalog read failed: {exception.Message}");
        }

        return new Catalog();
    }

    public static bool Save(Catalog catalog, out string failure)
    {
        failure = string.Empty;
        if (!IsValidCatalog(catalog))
        {
            failure = "invalid_catalog";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            catalog.updatedAtUtc = DateTime.UtcNow.ToString("O");
            catalog.zones = catalog.zones
                .OrderBy(value => value.roomId, StringComparer.Ordinal)
                .ToList();
            var temporaryPath = CatalogPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(catalog, true), Encoding.UTF8);
            if (File.Exists(CatalogPath))
                File.Copy(CatalogPath, CatalogPath + ".bak", true);
            File.Copy(temporaryPath, CatalogPath, true);
            File.Delete(temporaryPath);
            return true;
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}_{exception.Message}";
            return false;
        }
    }

    public static bool Upsert(
        Catalog catalog,
        string roomId,
        Guid roomUuid,
        Guid floorAnchorUuid,
        string payload,
        Pose floorLocalPose,
        out string failure)
    {
        failure = string.Empty;
        if (catalog == null
            || string.IsNullOrWhiteSpace(roomId)
            || roomUuid == Guid.Empty
            || floorAnchorUuid == Guid.Empty
            || !TryParsePayload(payload, out var payloadRoomId)
            || !string.Equals(payloadRoomId, roomId, StringComparison.Ordinal)
            || !IsFinite(floorLocalPose))
        {
            failure = "invalid_calibration_input";
            return false;
        }

        catalog.zones ??= new List<ZoneCalibration>();
        catalog.zones.RemoveAll(value =>
            value != null && string.Equals(value.roomId, roomId, StringComparison.Ordinal));
        catalog.zones.Add(new ZoneCalibration
        {
            roomId = roomId,
            roomUuid = roomUuid.ToString(),
            floorAnchorUuid = floorAnchorUuid.ToString(),
            markerPayload = payload,
            floorLocalX = floorLocalPose.position.x,
            floorLocalY = floorLocalPose.position.y,
            floorLocalZ = floorLocalPose.position.z,
            floorLocalRotationX = floorLocalPose.rotation.x,
            floorLocalRotationY = floorLocalPose.rotation.y,
            floorLocalRotationZ = floorLocalPose.rotation.z,
            floorLocalRotationW = floorLocalPose.rotation.w,
            calibratedAtUtc = DateTime.UtcNow.ToString("O"),
        });
        return Save(catalog, out failure);
    }

    public static bool TryGet(
        Catalog catalog,
        string roomId,
        out ZoneCalibration calibration)
    {
        calibration = catalog?.zones?.FirstOrDefault(value =>
            value != null && string.Equals(value.roomId, roomId, StringComparison.Ordinal));
        return IsValidCalibration(calibration);
    }

    public static Pose Compose(Pose parent, Pose child) => new Pose(
        parent.position + parent.rotation * child.position,
        parent.rotation * child.rotation);

    public static Pose Inverse(Pose pose)
    {
        var inverseRotation = Quaternion.Inverse(pose.rotation);
        return new Pose(inverseRotation * -pose.position, inverseRotation);
    }

    public static Pose RelativeTo(Pose referenceWorld, Pose worldPose) =>
        Compose(Inverse(referenceWorld), worldPose);

    public static bool IsFinite(Pose pose) =>
        AagRigidPoseRecovery.IsFinite(pose.position)
        && AagRigidPoseRecovery.IsFinite(pose.rotation)
        && Quaternion.Dot(pose.rotation, pose.rotation) > 0.25f;

    private static bool IsValidCatalog(Catalog catalog)
    {
        if (catalog == null
            || !string.Equals(catalog.schemaVersion, SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(catalog.floorPlanId, ExperimentSpaceRuntime.FloorPlanId, StringComparison.Ordinal)
            || catalog.zones == null)
            return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        return catalog.zones.All(value =>
            IsValidCalibration(value) && ids.Add(value.roomId));
    }

    private static bool IsValidCalibration(ZoneCalibration calibration)
    {
        return calibration != null
            && !string.IsNullOrWhiteSpace(calibration.roomId)
            && Guid.TryParse(calibration.roomUuid, out var roomUuid)
            && roomUuid != Guid.Empty
            && Guid.TryParse(calibration.floorAnchorUuid, out var floorUuid)
            && floorUuid != Guid.Empty
            && TryParsePayload(calibration.markerPayload, out var payloadRoomId)
            && string.Equals(payloadRoomId, calibration.roomId, StringComparison.Ordinal)
            && IsFinite(calibration.FloorLocalPose);
    }
}
