using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class AagIncidentalAnchorManifest
{
    public string schema_version = "aag-fp1-incidental-anchor-sets/v1";
    public string floor_plan_id = "FP1";
    public string updated_at_utc = string.Empty;
    public string persistence_warning = "EXPORT BEFORE APP UNINSTALL OR CLEAR APP DATA";
    public List<AagIncidentalAnchorSet> sets = new List<AagIncidentalAnchorSet>();
}

[Serializable]
public sealed class AagIncidentalAnchorSet
{
    public string set_id = string.Empty;
    public List<AagIncidentalAnchorEntry> objects = new List<AagIncidentalAnchorEntry>();
}

[Serializable]
public sealed class AagIncidentalAnchorEntry
{
    public string set_id = string.Empty;
    public string object_id = string.Empty;
    public string prefab_resource_path = string.Empty;
    public string anchor_uuid = string.Empty;
    public float fallback_x;
    public float fallback_y;
    public float fallback_z;
    public float fallback_rotation_x;
    public float fallback_rotation_y;
    public float fallback_rotation_z;
    public float fallback_rotation_w = 1f;
    public string saved_at_utc = string.Empty;

    public Vector3 FallbackPosition => new Vector3(fallback_x, fallback_y, fallback_z);
    public Quaternion FallbackRotation => new Quaternion(
        fallback_rotation_x,
        fallback_rotation_y,
        fallback_rotation_z,
        fallback_rotation_w);
}

public static class AagIncidentalAnchorStore
{
    public const string ManifestFileName = "fp1_incidental_anchor_sets.json";
    public const string ResourceRoot = "IncidentalObjects";
    public const int RequiredObjectsPerSet = 5;

    public static string ManifestPath => Path.Combine(AagManualAnchorSetStore.FolderPath, ManifestFileName);
    public static string ExportFolderPath => AagManualAnchorSetStore.ExportFolderPath;

    public static AagIncidentalAnchorManifest LoadOrCreate()
    {
        AagIncidentalAnchorManifest manifest = null;
        try
        {
            if (File.Exists(ManifestPath))
                manifest = JsonUtility.FromJson<AagIncidentalAnchorManifest>(File.ReadAllText(ManifestPath));
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Incidental Store] load failed path={ManifestPath}; reason={exception.Message}");
        }

        manifest ??= new AagIncidentalAnchorManifest();
        manifest.sets ??= new List<AagIncidentalAnchorSet>();
        foreach (var setId in AagManualAnchorSetStore.SetIds)
        {
            var set = manifest.sets.FirstOrDefault(value =>
                string.Equals(value.set_id, setId, StringComparison.Ordinal));
            if (set == null)
            {
                set = new AagIncidentalAnchorSet { set_id = setId };
                manifest.sets.Add(set);
            }
            set.objects ??= new List<AagIncidentalAnchorEntry>();
        }
        return manifest;
    }

    public static AagIncidentalAnchorSet GetSet(AagIncidentalAnchorManifest manifest, string setId)
    {
        return manifest?.sets?.FirstOrDefault(value =>
            string.Equals(value.set_id, setId, StringComparison.Ordinal));
    }

    public static bool Save(AagIncidentalAnchorManifest manifest, out string failure)
    {
        try
        {
            Directory.CreateDirectory(AagManualAnchorSetStore.FolderPath);
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

    public static bool Export(AagIncidentalAnchorManifest manifest, out string path, out string failure)
    {
        path = string.Empty;
        try
        {
            Directory.CreateDirectory(ExportFolderPath);
            path = Path.Combine(ExportFolderPath,
                $"fp1_incidental_anchor_sets_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(path, JsonUtility.ToJson(manifest, true), Encoding.UTF8);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    public static string ResourceFolderForSet(string setId) => $"{ResourceRoot}/{setId}";

    /// <summary>
    /// FBX model assets and their editable prefab wrappers live in the same
    /// Resources folder. Only wrappers named 01_ through 05_ are spawn entries;
    /// this prevents the five source FBXs from being counted a second time.
    /// </summary>
    public static GameObject[] LoadPrefabCatalog(string setId)
    {
        return Resources.LoadAll<GameObject>(ResourceFolderForSet(setId))
            .Where(value => value != null && IsOrderedWrapperName(value.name))
            .OrderBy(value => value.name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsOrderedWrapperName(string name)
    {
        return !string.IsNullOrEmpty(name)
            && name.Length > 3
            && char.IsDigit(name[0])
            && char.IsDigit(name[1])
            && name[2] == '_';
    }
}
