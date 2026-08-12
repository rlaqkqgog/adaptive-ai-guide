using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Persists task-object poses relative to a concrete MRUK floor anchor. Unlike
/// MRUKRoom.transform (which is identity in the FP1 scan), the floor-anchor pose
/// follows Meta scene relocalization across application restarts.
/// </summary>
public static class MrukRoomLocalPlacementStore
{
    public const string SchemaVersion = "aag-fp1-floor-anchor-local-placements/v3-space-20260721";
    private const string DirectoryName = "AagRoomLocalPlacements";
    private static string FileName =>
        ExperimentSpaceRuntime.NamespacedFileName("floor_anchor_local_placements");
    private static string SeedResourcePath =>
        $"AAG/{ExperimentSpaceRuntime.StorageKey}_floor_anchor_local_placements";

    [Serializable]
    public sealed class Catalog
    {
        public string schemaVersion = SchemaVersion;
        public string updatedAtUtc = string.Empty;
        public List<SetRecord> sets = new List<SetRecord>();
    }

    [Serializable]
    public sealed class SetRecord
    {
        public string setId = string.Empty;
        public string calibratedAtUtc = string.Empty;
        public string calibrationSource = "validated_runtime_spawn";
        public List<Placement> placements = new List<Placement>();
    }

    [Serializable]
    public sealed class Placement
    {
        public string objectId = string.Empty;
        public string color = string.Empty;
        public string roomUuid = string.Empty;
        public string floorAnchorUuid = string.Empty;
        public float localX;
        public float localY;
        public float localZ;
        public float localRotationX;
        public float localRotationY;
        public float localRotationZ;
        public float localRotationW = 1f;

        public Vector3 LocalPosition => new Vector3(localX, localY, localZ);
        public Quaternion LocalRotation => new Quaternion(
            localRotationX, localRotationY, localRotationZ, localRotationW);
    }

    public readonly struct PoseInput
    {
        public string ObjectId { get; }
        public string Color { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        public PoseInput(string objectId, string color, Vector3 position, Quaternion rotation)
        {
            ObjectId = objectId ?? string.Empty;
            Color = color ?? string.Empty;
            Position = position;
            Rotation = rotation;
        }
    }

    public static string CatalogPath => Path.Combine(
        Application.persistentDataPath, DirectoryName, FileName);

    public static bool TryGetSet(string setId, out SetRecord set, out string failure)
    {
        set = null;
        if (!TryLoadCatalog(out var catalog, out failure)) return false;
        set = catalog.sets?.FirstOrDefault(value =>
            string.Equals(value?.setId, setId, StringComparison.Ordinal));
        if (set == null)
        {
            failure = $"set_not_calibrated_{setId}";
            return false;
        }
        if (!TryValidateSet(set, out failure))
        {
            set = null;
            return false;
        }
        return true;
    }

    public static bool TryCaptureAndSave(
        string setId,
        IReadOnlyCollection<PoseInput> poses,
        out string detail,
        out string failure)
    {
        detail = string.Empty;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(setId) || poses == null || poses.Count != 12)
        {
            failure = $"capture_requires_12_poses_actual_{poses?.Count ?? 0}";
            return false;
        }
        if (MRUK.Instance == null || MRUK.Instance.Rooms == null || MRUK.Instance.Rooms.Count == 0)
        {
            failure = "mruk_rooms_unavailable";
            return false;
        }

        var placements = new List<Placement>();
        foreach (var pose in poses.OrderBy(value => value.ObjectId, StringComparer.Ordinal))
        {
            // Runtime content is translated into the physical AprilTag frame.
            // Persist it against the unchanged MRUK floor only after undoing the
            // tag translation; the separate content fine tune intentionally stays.
            var mrukWorldPosition = AagMrukSpaceCorrection.ObservedToMrukPosition(
                pose.Position);
            var mrukWorldRotation = AagMrukSpaceCorrection.ObservedToMrukRotation(
                pose.Rotation);
            var floorFailure = string.Empty;
            MRUKRoom room = null;
            MRUKAnchor floor = null;
            if (!AagRigidPoseRecovery.IsFinite(mrukWorldPosition)
                || !AagRigidPoseRecovery.IsFinite(pose.Rotation)
                || !TryFindContainingFloor(mrukWorldPosition, out room, out floor, out floorFailure))
            {
                failure = string.IsNullOrEmpty(floorFailure)
                    ? $"invalid_pose_{pose.ObjectId}"
                    : $"{floorFailure}_{pose.ObjectId}";
                return false;
            }

            ToLocalPose(floor.transform, mrukWorldPosition, mrukWorldRotation,
                out var localPosition, out var localRotation);
            placements.Add(new Placement
            {
                objectId = pose.ObjectId,
                color = pose.Color,
                roomUuid = room.Anchor.Uuid.ToString(),
                floorAnchorUuid = floor.Anchor.Uuid.ToString(),
                localX = localPosition.x,
                localY = localPosition.y,
                localZ = localPosition.z,
                localRotationX = localRotation.x,
                localRotationY = localRotation.y,
                localRotationZ = localRotation.z,
                localRotationW = localRotation.w,
            });
        }

        var record = new SetRecord
        {
            setId = setId,
            calibratedAtUtc = DateTime.UtcNow.ToString("O"),
            placements = placements,
        };
        if (!TryValidateSet(record, out failure)) return false;

        Catalog catalog;
        if (!TryLoadCatalog(out catalog, out _)) catalog = NewCatalog();
        catalog.sets.RemoveAll(value => string.Equals(value?.setId, setId, StringComparison.Ordinal));
        catalog.sets.Add(record);
        catalog.sets = catalog.sets.OrderBy(value => value.setId, StringComparer.Ordinal).ToList();
        catalog.updatedAtUtc = DateTime.UtcNow.ToString("O");
        if (!TryWriteCatalog(catalog, out failure)) return false;

        detail = $"set={setId}; placements={placements.Count}; path={CatalogPath}; "
            + $"rooms={string.Join("|", placements.Select(value => value.roomUuid).Distinct().OrderBy(value => value))}; "
            + $"floors={string.Join("|", placements.Select(value => value.floorAnchorUuid).Distinct().OrderBy(value => value))}";
        return true;
    }

    public static bool TryResolveWorldPose(
        Placement placement,
        out Vector3 position,
        out Quaternion rotation,
        out string failure)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        failure = string.Empty;
        if (placement == null
            || !Guid.TryParse(placement.roomUuid, out var roomUuid)
            || !Guid.TryParse(placement.floorAnchorUuid, out var floorUuid))
        {
            failure = $"invalid_room_or_floor_uuid_{placement?.objectId ?? "NULL"}";
            return false;
        }
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace)
        {
            if (!AagFp2BakedSpace.TryResolveFloorLocalPose(
                    floorUuid,
                    placement.LocalPosition,
                    placement.LocalRotation,
                    out position,
                    out rotation,
                    out failure))
                return false;
            position = AagMrukSpaceCorrection.MrukToObservedPosition(position);
            rotation = AagMrukSpaceCorrection.MrukToObservedRotation(rotation);
            if (!AagRigidPoseRecovery.IsFinite(position) || !AagRigidPoseRecovery.IsFinite(rotation))
            {
                failure = $"non_finite_baked_world_pose_{placement.objectId}";
                return false;
            }
            return true;
        }
        var room = MRUK.Instance?.Rooms?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == roomUuid);
        if (room == null)
        {
            failure = $"room_not_loaded_{roomUuid}";
            return false;
        }
        var floor = room.FloorAnchors?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == floorUuid);
        if (floor == null)
        {
            failure = $"floor_not_loaded_{floorUuid}";
            return false;
        }

        ToWorldPose(floor.transform, placement.LocalPosition, placement.LocalRotation,
            out position, out rotation);
        position = AagMrukSpaceCorrection.MrukToObservedPosition(position);
        rotation = AagMrukSpaceCorrection.MrukToObservedRotation(rotation);
        if (!AagRigidPoseRecovery.IsFinite(position) || !AagRigidPoseRecovery.IsFinite(rotation))
        {
            failure = $"non_finite_world_pose_{placement.objectId}";
            return false;
        }
        return true;
    }

    public static bool TryValidateSet(SetRecord set, out string failure)
    {
        failure = string.Empty;
        var floorPlan = ExperimentSpaceRuntime.FloorPlan;
        if (set == null || string.IsNullOrWhiteSpace(set.setId)
            || floorPlan == null
            || !floorPlan.SupportsSet(set.setId)
            || set.placements == null || set.placements.Count != 12)
        {
            failure = $"invalid_set_or_count_{set?.placements?.Count ?? 0}";
            return false;
        }
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in set.placements)
        {
            var rotationMagnitudeSquared = placement == null
                ? 0f
                : Quaternion.Dot(placement.LocalRotation, placement.LocalRotation);
            if (placement == null || string.IsNullOrWhiteSpace(placement.objectId)
                || !objectIds.Add(placement.objectId)
                || !Guid.TryParse(placement.roomUuid, out var roomUuid)
                || !floorPlan.ContainsRoom(roomUuid)
                || floorPlan.IsExcludedRoom(roomUuid)
                || !Guid.TryParse(placement.floorAnchorUuid, out var floorUuid)
                || floorUuid == Guid.Empty
                || !AagRigidPoseRecovery.IsFinite(placement.LocalPosition)
                || !AagRigidPoseRecovery.IsFinite(placement.LocalRotation)
                || rotationMagnitudeSquared < 0.25f
                || rotationMagnitudeSquared > 2.25f)
            {
                failure = $"invalid_or_duplicate_placement_{placement?.objectId ?? "NULL"}";
                return false;
            }
        }
        return true;
    }

    public static void ToLocalPose(
        Transform reference,
        Vector3 worldPosition,
        Quaternion worldRotation,
        out Vector3 localPosition,
        out Quaternion localRotation)
    {
        localPosition = reference.InverseTransformPoint(worldPosition);
        localRotation = Quaternion.Inverse(reference.rotation) * worldRotation;
    }

    public static void ToWorldPose(
        Transform reference,
        Vector3 localPosition,
        Quaternion localRotation,
        out Vector3 worldPosition,
        out Quaternion worldRotation)
    {
        worldPosition = reference.TransformPoint(localPosition);
        worldRotation = reference.rotation * localRotation;
    }

    public static bool TryConvertObservedPoseToFloorLocal(
        Vector3 observedWorldPosition,
        Quaternion observedWorldRotation,
        out string roomUuid,
        out string floorAnchorUuid,
        out Vector3 localPosition,
        out Quaternion localRotation,
        out string failure)
    {
        roomUuid = string.Empty;
        floorAnchorUuid = string.Empty;
        localPosition = Vector3.zero;
        localRotation = Quaternion.identity;
        failure = string.Empty;
        var mrukWorldPosition = AagMrukSpaceCorrection.ObservedToMrukPosition(
            observedWorldPosition);
        var mrukWorldRotation = AagMrukSpaceCorrection.ObservedToMrukRotation(
            observedWorldRotation);
        if (!AagRigidPoseRecovery.IsFinite(mrukWorldPosition)
            || !AagRigidPoseRecovery.IsFinite(observedWorldRotation)
            || !TryFindContainingFloor(
                mrukWorldPosition, out var room, out var floor, out failure))
            return false;

        ToLocalPose(
            floor.transform,
            mrukWorldPosition,
            mrukWorldRotation,
            out localPosition,
            out localRotation);
        roomUuid = room.Anchor.Uuid.ToString();
        floorAnchorUuid = floor.Anchor.Uuid.ToString();
        return true;
    }

    private static bool TryFindContainingFloor(
        Vector3 worldPosition,
        out MRUKRoom selectedRoom,
        out MRUKAnchor selectedFloor,
        out string failure)
    {
        selectedRoom = null;
        selectedFloor = null;
        failure = string.Empty;
        var bestPlaneDistance = float.PositiveInfinity;
        var floorPlan = ExperimentSpaceRuntime.FloorPlan;
        if (floorPlan == null)
        {
            failure = "experiment_floor_plan_unavailable";
            return false;
        }
        foreach (var room in MRUK.Instance.Rooms)
        {
            if (room == null || room.Anchor == null || room.Anchor.Uuid == Guid.Empty
                || !floorPlan.ContainsRoom(room.Anchor.Uuid)
                || floorPlan.IsExcludedRoom(room.Anchor.Uuid))
                continue;

            foreach (var floor in room.FloorAnchors)
            {
                if (floor == null || floor.Anchor == null || floor.Anchor.Uuid == Guid.Empty
                    || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3)
                    continue;
                var local = floor.transform.InverseTransformPoint(worldPosition);
                if (!floor.IsPositionInBoundary(new Vector2(local.x, local.y))) continue;
                var planeDistance = Mathf.Abs(local.z);
                if (planeDistance >= bestPlaneDistance) continue;
                bestPlaneDistance = planeDistance;
                selectedRoom = room;
                selectedFloor = floor;
            }
        }
        if (selectedFloor != null) return true;
        failure = "outside_allowed_mruk_floor";
        return false;
    }

    private static bool TryLoadCatalog(out Catalog catalog, out string failure)
    {
        catalog = null;
        failure = string.Empty;
        try
        {
            if (File.Exists(CatalogPath))
            {
                catalog = JsonUtility.FromJson<Catalog>(File.ReadAllText(CatalogPath));
                if (IsStructurallyValidCatalog(catalog))
                    return true;
            }

            // A Space Setup rescan replaces MRUK room/floor UUIDs. The bundled,
            // audited v2 seed atomically supersedes the stale v1 device catalog
            // once, then the persistent copy remains authoritative on restarts.
            var seed = Resources.Load<TextAsset>(SeedResourcePath);
            catalog = seed == null
                ? null
                : JsonUtility.FromJson<Catalog>(seed.text.TrimStart('\uFEFF'));
            if (!IsStructurallyValidCatalog(catalog))
            {
                failure = "catalog_and_bundled_seed_invalid";
                catalog = null;
                return false;
            }

            if (!TryWriteCatalog(catalog, out var writeFailure))
                Debug.LogWarning($"[RoomLocalPlacement] using bundled seed without persistent copy: {writeFailure}");
            return true;
        }
        catch (Exception exception)
        {
            failure = $"catalog_read_{exception.GetType().Name}_{exception.Message}";
            catalog = null;
            return false;
        }
    }

    private static bool TryWriteCatalog(Catalog catalog, out string failure)
    {
        failure = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(CatalogPath);
            Directory.CreateDirectory(directory);
            var temporaryPath = CatalogPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(catalog, true));
            if (File.Exists(CatalogPath))
            {
                var backupPath = CatalogPath + ".bak";
                File.Copy(CatalogPath, backupPath, true);
                File.Copy(temporaryPath, CatalogPath, true);
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, CatalogPath);
            }
            return true;
        }
        catch (Exception exception)
        {
            failure = $"catalog_write_{exception.GetType().Name}_{exception.Message}";
            return false;
        }
    }

    private static Catalog NewCatalog() => new Catalog
    {
        updatedAtUtc = DateTime.UtcNow.ToString("O"),
        sets = new List<SetRecord>(),
    };

    private static bool IsStructurallyValidCatalog(Catalog catalog)
    {
        if (catalog == null
            || !string.Equals(catalog.schemaVersion, SchemaVersion, StringComparison.Ordinal)
            || catalog.sets == null
            || catalog.sets.Count == 0
            || catalog.sets.Count > ExperimentSpaceRuntime.SetIds.Count
            || catalog.sets.Any(value => !TryValidateSet(value, out _)))
            return false;

        var supported = new HashSet<string>(
            ExperimentSpaceRuntime.SetIds,
            StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return catalog.sets.All(value =>
            value != null
            && supported.Contains(value.setId)
            && ids.Add(value.setId));
    }
}
