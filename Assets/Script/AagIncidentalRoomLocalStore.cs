using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>Persistent FP2 incidental-object poses in MRUK floor-local frames.</summary>
public static class AagIncidentalRoomLocalStore
{
    public const string SchemaVersion = "aag-incidental-floor-local/v1";
    public const float Fp2FloorHeightMeters = 0.20f;
    private static string SeedResourcePath =>
        $"AAG/{ExperimentSpaceRuntime.StorageKey}_incidental_floor_local";

    [Serializable]
    private sealed class Catalog
    {
        public string schemaVersion = SchemaVersion;
        public string floorPlanId = string.Empty;
        public string updatedAtUtc = string.Empty;
        public List<SetRecord> sets = new List<SetRecord>();
    }

    [Serializable]
    private sealed class SetRecord
    {
        public string setId = string.Empty;
        public List<Placement> placements = new List<Placement>();
    }

    [Serializable]
    private sealed class Placement
    {
        public string objectId = string.Empty;
        public string roomUuid = string.Empty;
        public string floorAnchorUuid = string.Empty;
        public float localX;
        public float localY;
        public float localZ;
        public float localRotationX;
        public float localRotationY;
        public float localRotationZ;
        public float localRotationW = 1f;
    }

    public readonly struct PoseInput
    {
        public PoseInput(string objectId, Vector3 position, Quaternion rotation)
        {
            ObjectId = objectId;
            Position = position;
            Rotation = rotation;
        }

        public string ObjectId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }

    public static string CatalogPath => Path.Combine(
        AagManualAnchorSetStore.FolderPath,
        ExperimentSpaceRuntime.NamespacedFileName("incidental_floor_local"));

    public static bool HasCompleteSet(string setId) =>
        TryLoad(out var catalog, out _)
        && TryFindValidSet(catalog, setId, out _);

    public static bool HasAllCompleteSets =>
        TryLoad(out var catalog, out _)
        && ExperimentSpaceRuntime.SetIds.All(setId =>
            TryFindValidSet(catalog, setId, out _));

    public static bool TryCaptureAndSave(
        string setId,
        IReadOnlyCollection<PoseInput> poses,
        out string detail,
        out string failure)
    {
        detail = string.Empty;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(setId)
            || poses == null
            || poses.Count != AagIncidentalAnchorStore.RequiredObjectsPerSet)
        {
            failure = $"incidental_capture_requires_{AagIncidentalAnchorStore.RequiredObjectsPerSet}_actual_{poses?.Count ?? 0}";
            return false;
        }

        var placements = new List<Placement>();
        foreach (var pose in poses.OrderBy(value => value.ObjectId, StringComparer.Ordinal))
        {
            if (!MrukRoomLocalPlacementStore.TryConvertObservedPoseToFloorLocal(
                    pose.Position,
                    pose.Rotation,
                    out var roomUuid,
                    out var floorUuid,
                    out var localPosition,
                    out var localRotation,
                    out failure))
            {
                failure = $"{failure}_{pose.ObjectId}";
                return false;
            }

            placements.Add(new Placement
            {
                objectId = pose.ObjectId,
                roomUuid = roomUuid,
                floorAnchorUuid = floorUuid,
                localX = localPosition.x,
                localY = localPosition.y,
                localZ = localPosition.z,
                localRotationX = localRotation.x,
                localRotationY = localRotation.y,
                localRotationZ = localRotation.z,
                localRotationW = localRotation.w,
            });
        }

        var catalog = TryLoad(out var loaded, out _) ? loaded : NewCatalog();
        catalog.sets.RemoveAll(value =>
            string.Equals(value?.setId, setId, StringComparison.Ordinal));
        catalog.sets.Add(new SetRecord { setId = setId, placements = placements });
        catalog.updatedAtUtc = DateTime.UtcNow.ToString("O");
        if (!TryWrite(catalog, out failure)) return false;

        detail = $"set={setId}; incidentals={placements.Count}; path={CatalogPath}; rooms="
            + string.Join("|", placements.Select(value => value.roomUuid).Distinct());
        return true;
    }

    public static bool TryResolve(
        string setId,
        string objectId,
        out Vector3 position,
        out Quaternion rotation,
        out string failure)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (!TryLoad(out var catalog, out failure)
            || !TryFindValidSet(catalog, setId, out var set))
        {
            if (string.IsNullOrEmpty(failure)) failure = $"incidental_set_missing_{setId}";
            return false;
        }

        var placement = set.placements.FirstOrDefault(value =>
            string.Equals(value.objectId, objectId, StringComparison.Ordinal));
        if (placement == null)
        {
            failure = $"incidental_local_missing_{objectId}";
            return false;
        }

        return MrukRoomLocalPlacementStore.TryResolveWorldPose(
            new MrukRoomLocalPlacementStore.Placement
            {
                objectId = placement.objectId,
                roomUuid = placement.roomUuid,
                floorAnchorUuid = placement.floorAnchorUuid,
                localX = placement.localX,
                localY = placement.localY,
                localZ = ExperimentSpaceRuntime.IsFp2
                    ? Fp2FloorHeightMeters
                    : placement.localZ,
                localRotationX = placement.localRotationX,
                localRotationY = placement.localRotationY,
                localRotationZ = placement.localRotationZ,
                localRotationW = placement.localRotationW,
            },
            out position,
            out rotation,
            out failure);
    }

    private static Catalog NewCatalog() => new Catalog
    {
        floorPlanId = ExperimentSpaceRuntime.FloorPlanId,
        updatedAtUtc = DateTime.UtcNow.ToString("O"),
    };

    private static bool TryLoad(out Catalog catalog, out string failure)
    {
        catalog = null;
        failure = string.Empty;
        try
        {
            if (File.Exists(CatalogPath))
            {
                catalog = JsonUtility.FromJson<Catalog>(File.ReadAllText(CatalogPath));
                if (IsValidCatalog(catalog)) return true;
            }

            var seed = Resources.Load<TextAsset>(SeedResourcePath);
            catalog = seed == null
                ? null
                : JsonUtility.FromJson<Catalog>(seed.text.TrimStart('\uFEFF'));
            if (!IsValidCatalog(catalog))
            {
                failure = "incidental_local_catalog_and_seed_invalid";
                catalog = null;
                return false;
            }
            if (!TryWrite(catalog, out var writeFailure))
                Debug.LogWarning(
                    $"[Incidental RoomLocal] using bundled seed without persistent copy: {writeFailure}");
            return true;
        }
        catch (Exception exception)
        {
            failure = $"incidental_catalog_read_{exception.GetType().Name}_{exception.Message}";
            catalog = null;
            return false;
        }
    }

    private static bool IsValidCatalog(Catalog catalog) =>
        catalog != null
        && string.Equals(catalog.schemaVersion, SchemaVersion, StringComparison.Ordinal)
        && string.Equals(
            catalog.floorPlanId,
            ExperimentSpaceRuntime.FloorPlanId,
            StringComparison.Ordinal)
        && catalog.sets != null;

    private static bool TryFindValidSet(Catalog catalog, string setId, out SetRecord set)
    {
        set = catalog?.sets?.FirstOrDefault(value =>
            string.Equals(value?.setId, setId, StringComparison.Ordinal));
        if (set?.placements == null
            || set.placements.Count != AagIncidentalAnchorStore.RequiredObjectsPerSet)
            return false;
        return set.placements.Select(value => value.objectId).Distinct(StringComparer.Ordinal).Count()
            == AagIncidentalAnchorStore.RequiredObjectsPerSet
            && set.placements.All(value =>
                value != null
                && Guid.TryParse(value.roomUuid, out var roomUuid)
                && roomUuid != Guid.Empty
                && Guid.TryParse(value.floorAnchorUuid, out var floorUuid)
                && floorUuid != Guid.Empty);
    }

    private static bool TryWrite(Catalog catalog, out string failure)
    {
        failure = string.Empty;
        try
        {
            Directory.CreateDirectory(AagManualAnchorSetStore.FolderPath);
            var temporaryPath = CatalogPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(catalog, true));
            if (File.Exists(CatalogPath)) File.Copy(CatalogPath, CatalogPath + ".bak", true);
            File.Copy(temporaryPath, CatalogPath, true);
            File.Delete(temporaryPath);
            return true;
        }
        catch (Exception exception)
        {
            failure = $"incidental_catalog_write_{exception.GetType().Name}_{exception.Message}";
            return false;
        }
    }
}
