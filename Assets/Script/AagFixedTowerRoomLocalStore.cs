using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>Persistent FP2 destination-tower poses in MRUK floor-local frames.</summary>
public static class AagFixedTowerRoomLocalStore
{
    public const string SchemaVersion = "aag-fixed-tower-floor-local/v1";
    private static string SeedResourcePath =>
        $"AAG/{ExperimentSpaceRuntime.StorageKey}_fixed_tower_floor_local";

    [Serializable]
    private sealed class Catalog
    {
        public string schemaVersion = SchemaVersion;
        public string floorPlanId = string.Empty;
        public string updatedAtUtc = string.Empty;
        public List<Placement> placements = new List<Placement>();
    }

    [Serializable]
    private sealed class Placement
    {
        public string towerId = string.Empty;
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
        public PoseInput(string towerId, Vector3 position, Quaternion rotation)
        {
            TowerId = towerId;
            Position = position;
            Rotation = rotation;
        }

        public string TowerId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }

    public static string CatalogPath => Path.Combine(
        AagManualAnchorSetStore.FolderPath,
        ExperimentSpaceRuntime.NamespacedFileName("fixed_tower_floor_local"));

    public static bool TryCaptureAndSave(
        IReadOnlyCollection<PoseInput> poses,
        out string detail,
        out string failure)
    {
        detail = string.Empty;
        failure = string.Empty;
        if (poses == null || poses.Count != AagFixedTowerAnchorStore.RequiredTowerCount)
        {
            failure = $"tower_capture_requires_4_actual_{poses?.Count ?? 0}";
            return false;
        }

        var placements = new List<Placement>();
        foreach (var pose in poses.OrderBy(value => value.TowerId, StringComparer.Ordinal))
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
                failure = $"{failure}_{pose.TowerId}";
                return false;
            }

            placements.Add(new Placement
            {
                towerId = pose.TowerId,
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

        var expectedIds = new HashSet<string>(
            AagFixedTowerAnchorStore.TowerIds,
            StringComparer.Ordinal);
        if (!expectedIds.SetEquals(placements.Select(value => value.towerId)))
        {
            failure = "tower_ids_incomplete_or_duplicate";
            return false;
        }

        var catalog = new Catalog
        {
            floorPlanId = ExperimentSpaceRuntime.FloorPlanId,
            updatedAtUtc = DateTime.UtcNow.ToString("O"),
            placements = placements,
        };
        try
        {
            Directory.CreateDirectory(AagManualAnchorSetStore.FolderPath);
            var temporaryPath = CatalogPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(catalog, true));
            if (File.Exists(CatalogPath)) File.Delete(CatalogPath);
            File.Move(temporaryPath, CatalogPath);
        }
        catch (Exception exception)
        {
            failure = $"tower_catalog_write_{exception.GetType().Name}_{exception.Message}";
            return false;
        }

        detail = $"towers=4; path={CatalogPath}; rooms="
            + string.Join("|", placements.Select(value => value.roomUuid).Distinct());
        return true;
    }

    public static bool TryGet(
        string towerId,
        out AagFixedTowerRoomLocalCatalog.Entry entry,
        out string failure)
    {
        entry = null;
        failure = string.Empty;
        if (!TryLoad(out var catalog, out failure)) return false;
        var placement = catalog.placements.FirstOrDefault(value =>
            string.Equals(value.towerId, towerId, StringComparison.Ordinal));
        if (placement == null)
        {
            failure = $"tower_local_missing_{towerId}";
            return false;
        }
        entry = new AagFixedTowerRoomLocalCatalog.Entry(
            placement.towerId,
            placement.roomUuid,
            new Vector3(placement.localX, placement.localY, placement.localZ),
            new Quaternion(
                placement.localRotationX,
                placement.localRotationY,
                placement.localRotationZ,
                placement.localRotationW));
        return true;
    }

    public static bool HasCompleteCatalog => TryLoad(out _, out _);

    private static bool TryLoad(out Catalog catalog, out string failure)
    {
        catalog = null;
        failure = string.Empty;
        try
        {
            if (File.Exists(CatalogPath))
            {
                catalog = JsonUtility.FromJson<Catalog>(File.ReadAllText(CatalogPath));
                if (IsValid(catalog)) return true;
            }

            var seed = Resources.Load<TextAsset>(SeedResourcePath);
            catalog = seed == null
                ? null
                : JsonUtility.FromJson<Catalog>(seed.text.TrimStart('\uFEFF'));
            if (!IsValid(catalog))
            {
                failure = "tower_local_catalog_and_seed_invalid";
                catalog = null;
                return false;
            }

            if (!TryWriteCatalog(catalog, out var writeFailure))
                Debug.LogWarning(
                    $"[FixedTower RoomLocal] using bundled seed without persistent copy: {writeFailure}");
            return true;
        }
        catch (Exception exception)
        {
            failure = $"tower_catalog_read_{exception.GetType().Name}_{exception.Message}";
            catalog = null;
            return false;
        }
    }

    private static bool IsValid(Catalog catalog)
    {
        if (catalog == null
            || !string.Equals(catalog.schemaVersion, SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(
                catalog.floorPlanId,
                ExperimentSpaceRuntime.FloorPlanId,
                StringComparison.Ordinal)
            || catalog.placements == null
            || catalog.placements.Count != AagFixedTowerAnchorStore.RequiredTowerCount)
            return false;

        var expectedIds = new HashSet<string>(
            AagFixedTowerAnchorStore.TowerIds,
            StringComparer.Ordinal);
        if (!expectedIds.SetEquals(catalog.placements.Select(value => value.towerId)))
            return false;

        return catalog.placements.All(value =>
            value != null
            && Guid.TryParse(value.roomUuid, out var roomUuid)
            && roomUuid != Guid.Empty
            && Guid.TryParse(value.floorAnchorUuid, out var floorUuid)
            && floorUuid != Guid.Empty
            && IsFinite(value.localX)
            && IsFinite(value.localY)
            && IsFinite(value.localZ)
            && IsFinite(value.localRotationX)
            && IsFinite(value.localRotationY)
            && IsFinite(value.localRotationZ)
            && IsFinite(value.localRotationW));
    }

    private static bool TryWriteCatalog(Catalog catalog, out string failure)
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
            failure = $"tower_catalog_write_{exception.GetType().Name}_{exception.Message}";
            return false;
        }
    }

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}
