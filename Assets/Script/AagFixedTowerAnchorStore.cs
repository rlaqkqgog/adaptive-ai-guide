using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class AagFixedTowerAnchorManifest
{
    public string schema_version = "aag-fp1-fixed-tower-anchors/v3";
    public string floor_plan_id = "FP1";
    public string updated_at_utc = string.Empty;
    public List<AagFixedTowerAnchorEntry> towers = new List<AagFixedTowerAnchorEntry>();
}

[Serializable]
public sealed class AagFixedTowerAnchorEntry
{
    public string tower_id = string.Empty;
    public string anchor_uuid = string.Empty;
    // Used only when Quest does not return this saved UUID at load time.
    public bool has_fallback_pose;
    public float fallback_x;
    public float fallback_y;
    public float fallback_z;
    public float fallback_rotation_x;
    public float fallback_rotation_y;
    public float fallback_rotation_z;
    public float fallback_rotation_w = 1f;
}

/// <summary>
/// Authoritative runtime store for the four destination tower anchors. It is
/// intentionally independent from S1/S2/S3 so every experiment set reuses the
/// same physical tower locations.
/// </summary>
public static class AagFixedTowerAnchorStore
{
    public const int RequiredTowerCount = 4;
    public static string ManifestFileName =>
        ExperimentSpaceRuntime.NamespacedFileName("fixed_tower_anchors");
    public static readonly string[] TowerIds = { "Tower-1", "Tower-2", "Tower-3", "Tower-4" };
    public static readonly string[] TowerColors = { "Red", "Blue", "Yellow", "Green" };

    public static string ManifestPath => Path.Combine(AagManualAnchorSetStore.FolderPath, ManifestFileName);

    public static string ColorForTowerId(string towerId)
    {
        for (var index = 0; index < TowerIds.Length; index++)
        {
            if (string.Equals(TowerIds[index], towerId, StringComparison.Ordinal))
                return TowerColors[index];
        }
        return string.Empty;
    }

    public static AagFixedTowerAnchorManifest LoadOrCreate()
    {
        AagFixedTowerAnchorManifest manifest = null;
        try
        {
            if (File.Exists(ManifestPath))
                manifest = JsonUtility.FromJson<AagFixedTowerAnchorManifest>(File.ReadAllText(ManifestPath));
        }
        catch (Exception exception)
        {
            Debug.LogError($"[FixedTower Store] load failed path={ManifestPath}; reason={exception.Message}");
        }

        manifest ??= new AagFixedTowerAnchorManifest();
        manifest.schema_version = $"aag-{ExperimentSpaceRuntime.StorageKey}-fixed-tower-anchors/v3";
        manifest.floor_plan_id = ExperimentSpaceRuntime.FloorPlanId;
        manifest.towers ??= new List<AagFixedTowerAnchorEntry>();
        return manifest;
    }

    public static bool Save(AagFixedTowerAnchorManifest manifest, out string failure)
    {
        try
        {
            Directory.CreateDirectory(AagManualAnchorSetStore.FolderPath);
            manifest.schema_version = $"aag-{ExperimentSpaceRuntime.StorageKey}-fixed-tower-anchors/v3";
            manifest.floor_plan_id = ExperimentSpaceRuntime.FloorPlanId;
            manifest.updated_at_utc = DateTime.UtcNow.ToString("O");
            var temporaryPath = ManifestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(manifest, true), Encoding.UTF8);
            if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
            File.Move(temporaryPath, ManifestPath);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    public static bool ExportJsonAndCsv(
        AagFixedTowerAnchorManifest manifest,
        out string jsonPath,
        out string csvPath,
        out string failure)
    {
        jsonPath = string.Empty;
        csvPath = string.Empty;
        try
        {
            Directory.CreateDirectory(AagManualAnchorSetStore.ExportFolderPath);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            jsonPath = Path.Combine(AagManualAnchorSetStore.ExportFolderPath,
                $"{ExperimentSpaceRuntime.StorageKey}_fixed_tower_anchors_{stamp}.json");
            csvPath = Path.Combine(AagManualAnchorSetStore.ExportFolderPath,
                $"{ExperimentSpaceRuntime.StorageKey}_fixed_tower_anchors_{stamp}.csv");
            File.WriteAllText(jsonPath, JsonUtility.ToJson(manifest, true), Encoding.UTF8);
            File.WriteAllText(csvPath, BuildCsv(manifest), Encoding.UTF8);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    private static string BuildCsv(AagFixedTowerAnchorManifest manifest)
    {
        var builder = new StringBuilder(
            "floor_plan_id,tower_id,anchor_uuid,has_fallback_pose,fallback_x,fallback_y,fallback_z,fallback_rotation_x,fallback_rotation_y,fallback_rotation_z,fallback_rotation_w\n");
        foreach (var tower in manifest.towers.OrderBy(value => value.tower_id, StringComparer.Ordinal))
        {
            builder.Append(manifest.floor_plan_id).Append(',').Append(tower.tower_id).Append(',')
                .Append(tower.anchor_uuid).Append(',')
                .Append(tower.has_fallback_pose).Append(',')
                .Append(tower.fallback_x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(tower.fallback_y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(tower.fallback_z.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(tower.fallback_rotation_x.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(tower.fallback_rotation_y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(tower.fallback_rotation_z.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(tower.fallback_rotation_w.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return builder.ToString();
    }
}
