using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class AagManualAnchorSetManifest
{
    public string schema_version = "aag-fp1-manual-anchor-sets/v1";
    public string floor_plan_id = "FP1";
    public string updated_at_utc = string.Empty;
    public string persistence_warning = "EXPORT BEFORE APP UNINSTALL OR CLEAR APP DATA";
    public List<AagManualAnchorSetRecord> sets = new List<AagManualAnchorSetRecord>();
}

[Serializable]
public sealed class AagManualAnchorSetRecord
{
    public string set_id = string.Empty;
    public bool locked;
    public string locked_at_utc = string.Empty;
    public List<AagManualAnchorEntry> anchors = new List<AagManualAnchorEntry>();
}

[Serializable]
public sealed class AagManualAnchorEntry
{
    public string floor_plan_id = "FP1";
    public string set_id = string.Empty;
    public string marker_id = string.Empty;
    public string color = string.Empty;
    public string anchor_uuid = string.Empty;
    public float world_x;
    public float world_y;
    public float world_z;
    public float rotation_x;
    public float rotation_y;
    public float rotation_z;
    public float rotation_w = 1f;
    public string saved_at_utc = string.Empty;

    public Vector3 WorldPosition => new Vector3(world_x, world_y, world_z);
    public Quaternion WorldRotation => new Quaternion(rotation_x, rotation_y, rotation_z, rotation_w);
}

public static class AagManualAnchorSetStore
{
    public const string FolderName = "AagManualAnchorSets";
    public const string ManifestFileName = "fp1_manual_anchor_sets.json";
    public static readonly string[] SetIds = { "FP1-S1", "FP1-S2", "FP1-S3" };
    public static readonly string[] Colors = { "Red", "Blue", "Green", "Yellow" };

    public static string FolderPath => Path.Combine(Application.persistentDataPath, FolderName);
    public static string ManifestPath => Path.Combine(FolderPath, ManifestFileName);
    public static string ExportFolderPath => Path.Combine(FolderPath, "Exports");

    public static AagManualAnchorSetManifest LoadOrCreate()
    {
        AagManualAnchorSetManifest manifest = null;
        try
        {
            if (File.Exists(ManifestPath))
                manifest = JsonUtility.FromJson<AagManualAnchorSetManifest>(File.ReadAllText(ManifestPath));
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Manual Sets] manifest load failed path={ManifestPath}; reason={exception.Message}");
        }

        manifest ??= new AagManualAnchorSetManifest();
        manifest.sets ??= new List<AagManualAnchorSetRecord>();
        foreach (var setId in SetIds)
        {
            if (manifest.sets.All(set => !string.Equals(set.set_id, setId, StringComparison.Ordinal)))
                manifest.sets.Add(new AagManualAnchorSetRecord { set_id = setId });
        }
        foreach (var set in manifest.sets)
            set.anchors ??= new List<AagManualAnchorEntry>();
        return manifest;
    }

    public static bool Save(AagManualAnchorSetManifest manifest, out string failure)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            manifest.updated_at_utc = DateTime.UtcNow.ToString("O");
            var json = JsonUtility.ToJson(manifest, true);
            var temporaryPath = ManifestPath + ".tmp";
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
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

    public static bool ExportJsonAndCsv(AagManualAnchorSetManifest manifest, out string jsonPath, out string csvPath, out string failure)
    {
        jsonPath = string.Empty;
        csvPath = string.Empty;
        try
        {
            Directory.CreateDirectory(ExportFolderPath);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            jsonPath = Path.Combine(ExportFolderPath, $"fp1_manual_anchor_sets_{stamp}.json");
            csvPath = Path.Combine(ExportFolderPath, $"fp1_manual_anchor_sets_{stamp}.csv");
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

    private static string BuildCsv(AagManualAnchorSetManifest manifest)
    {
        var builder = new StringBuilder("floor_plan_id,set_id,marker_id,color,anchor_uuid,world_x,world_y,world_z,rotation_x,rotation_y,rotation_z,rotation_w,saved_at_utc,set_locked\n");
        foreach (var set in manifest.sets.OrderBy(value => value.set_id, StringComparer.Ordinal))
        {
            foreach (var anchor in set.anchors)
            {
                builder.Append(anchor.floor_plan_id).Append(',').Append(anchor.set_id).Append(',')
                    .Append(anchor.marker_id).Append(',').Append(anchor.color).Append(',').Append(anchor.anchor_uuid).Append(',')
                    .Append(anchor.world_x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.world_y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.world_z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.rotation_x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.rotation_y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.rotation_z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.rotation_w.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.saved_at_utc).Append(',').Append(set.locked ? "true" : "false").Append('\n');
            }
        }
        return builder.ToString();
    }
}
