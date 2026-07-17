using System;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

/// <summary>Spawns visual-only incidental prefabs under their dedicated anchor roots.</summary>
[DisallowMultipleComponent]
public sealed class IncidentalObjectManager : MonoBehaviour
{
    private readonly List<GameObject> spawnedContents = new List<GameObject>();

    public bool TryBuildMap(
        string setId,
        out Dictionary<Guid, AagIncidentalAnchorEntry> entries,
        out string failure,
        bool requireComplete = true)
    {
        entries = new Dictionary<Guid, AagIncidentalAnchorEntry>();
        failure = string.Empty;
        var set = AagIncidentalAnchorStore.GetSet(AagIncidentalAnchorStore.LoadOrCreate(), setId);
        var catalog = AagIncidentalAnchorStore.LoadPrefabCatalog(setId);
        if (catalog.Length != AagIncidentalAnchorStore.RequiredObjectsPerSet)
        {
            failure = $"incidental_catalog_requires_{AagIncidentalAnchorStore.RequiredObjectsPerSet}_actual_{catalog.Length}_{setId}";
            return false;
        }
        if (set?.objects == null)
        {
            failure = $"incidental_store_missing_{setId}";
            return false;
        }
        if (requireComplete && set.objects.Count != AagIncidentalAnchorStore.RequiredObjectsPerSet)
        {
            failure = $"incidental_set_requires_{AagIncidentalAnchorStore.RequiredObjectsPerSet}_saved_actual_{set.objects.Count}_{setId}";
            return false;
        }

        var allowedPaths = new HashSet<string>(
            catalog.Select(value => $"{AagIncidentalAnchorStore.ResourceFolderForSet(setId)}/{value.name}"),
            StringComparer.Ordinal);

        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in set.objects)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.object_id)
                || !objectIds.Add(entry.object_id))
            {
                failure = "incidental_object_id_missing_or_duplicate";
                return false;
            }
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid) || uuid == Guid.Empty)
            {
                failure = $"incidental_invalid_uuid_{entry.object_id}";
                return false;
            }
            if (entries.ContainsKey(uuid))
            {
                failure = $"incidental_duplicate_uuid_{entry.object_id}";
                return false;
            }
            if (string.IsNullOrWhiteSpace(entry.prefab_resource_path)
                || !allowedPaths.Contains(entry.prefab_resource_path)
                || Resources.Load<GameObject>(entry.prefab_resource_path) == null)
            {
                failure = $"incidental_prefab_missing_{entry.object_id}";
                return false;
            }
            entries.Add(uuid, entry);
        }
        return true;
    }

    public bool TrySpawn(
        IncidentalAnchorLoader loader,
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries,
        out string failure)
    {
        ClearRuntimeContents();
        failure = string.Empty;
        foreach (var pair in entries)
        {
            if (loader == null || !loader.TryGetTransform(pair.Key, out var anchorTransform))
            {
                failure = $"incidental_anchor_missing_{pair.Value.object_id}";
                ClearRuntimeContents();
                return false;
            }
            var prefab = Resources.Load<GameObject>(pair.Value.prefab_resource_path);
            if (prefab == null)
            {
                failure = $"incidental_prefab_missing_{pair.Value.object_id}";
                ClearRuntimeContents();
                return false;
            }

            PrepareInvisibleAnchorShell(anchorTransform.gameObject);
            // Instantiate inactive so a prefab that accidentally contains an
            // OVRSpatialAnchor cannot create or save another anchor on enable.
            var prefabWasActive = prefab.activeSelf;
            GameObject instance;
            try
            {
                prefab.SetActive(false);
                instance = Instantiate(prefab, anchorTransform, false);
            }
            finally
            {
                prefab.SetActive(prefabWasActive);
            }
            foreach (var nestedAnchor in instance.GetComponentsInChildren<OVRSpatialAnchor>(true))
                DestroyImmediate(nestedAnchor);
            instance.name = $"Incidental {pair.Value.object_id}";
            DisableAllInteraction(instance);
            EnvironmentDepthOcclusion.ApplyToRenderers(instance.transform);
            instance.SetActive(true);
            spawnedContents.Add(instance);
        }
        return true;
    }

    public void ClearRuntimeContents()
    {
        foreach (var instance in spawnedContents)
            if (instance != null) Destroy(instance);
        spawnedContents.Clear();
    }

    private static void PrepareInvisibleAnchorShell(GameObject root)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
        foreach (var canvas in root.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
        foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var grabbable in root.GetComponentsInChildren<Grabbable>(true)) grabbable.enabled = false;
        foreach (var handGrab in root.GetComponentsInChildren<HandGrabInteractable>(true)) handGrab.enabled = false;
        foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
        {
            body.useGravity = false;
            body.isKinematic = true;
        }
    }

    private static void DisableAllInteraction(GameObject instance)
    {
        foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var grabbable in instance.GetComponentsInChildren<Grabbable>(true)) grabbable.enabled = false;
        foreach (var handGrab in instance.GetComponentsInChildren<HandGrabInteractable>(true)) handGrab.enabled = false;
        foreach (var experimentObject in instance.GetComponentsInChildren<ExperimentObject>(true))
            experimentObject.enabled = false;
        foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true))
        {
            body.useGravity = false;
            body.isKinematic = true;
        }
    }
}
