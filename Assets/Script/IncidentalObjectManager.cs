using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using UnityEngine;

/// <summary>Spawns visual-only incidental prefabs under their dedicated anchor roots.</summary>
[DisallowMultipleComponent]
public sealed class IncidentalObjectManager : MonoBehaviour
{
    private readonly List<GameObject> spawnedContents = new List<GameObject>();
    private readonly Dictionary<string, GameObject> spawnedByObjectId =
        new Dictionary<string, GameObject>(StringComparer.Ordinal);

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
            if (ExperimentSpaceRuntime.IsFp2)
                RaiseVisualBottomToHeight(instance, anchorTransform.position.y);
            spawnedContents.Add(instance);
            spawnedByObjectId[pair.Value.object_id] = instance;
        }
        return true;
    }

    private static void RaiseVisualBottomToHeight(GameObject instance, float desiredBottomWorldY)
    {
        var renderers = instance.GetComponentsInChildren<Renderer>(true)
            .Where(value => value != null && value.enabled)
            .ToArray();
        if (renderers.Length == 0) return;
        var minimumWorldY = renderers.Min(value => value.bounds.min.y);
        var lift = desiredBottomWorldY - minimumWorldY;
        if (!float.IsNaN(lift) && !float.IsInfinity(lift) && Mathf.Abs(lift) >= 0.001f)
            instance.transform.position += Vector3.up * lift;
    }

    public int ResolveHorizontalWallPenetrations(Action<string, string> writeCorrection)
    {
        const float minimumCorrectionMeters = 0.005f;
        const float clearanceMeters = 0.01f;
        const float maximumTotalCorrectionMeters = 0.35f;
        const int maximumPasses = 6;

        var correctedCount = 0;
        Physics.SyncTransforms();
        foreach (var pair in spawnedByObjectId.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var instance = pair.Value;
            if (instance == null) continue;
            var start = instance.transform.position;
            var totalCorrection = Vector3.zero;

            for (var pass = 0; pass < maximumPasses; pass++)
            {
                if (!TryFindLargestHorizontalWallPenetration(
                        instance, minimumCorrectionMeters, out var direction, out var distance))
                    break;

                var remaining = maximumTotalCorrectionMeters - totalCorrection.magnitude;
                if (remaining <= 0f) break;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.0001f) break;
                direction.Normalize();
                var correction = direction * Mathf.Min(distance + clearanceMeters, remaining);
                var candidate = instance.transform.position + correction;
                if (!AagRecoveryPlacementValidator.TryValidate(candidate, out _, out _)) break;

                instance.transform.position = candidate;
                totalCorrection += correction;
                Physics.SyncTransforms();
            }

            if (totalCorrection.sqrMagnitude < minimumCorrectionMeters * minimumCorrectionMeters) continue;
            correctedCount++;
            writeCorrection?.Invoke(
                pair.Key,
                $"distance={totalCorrection.magnitude:F4}; "
                + $"from=({start.x:F4},{start.y:F4},{start.z:F4}); "
                + $"to=({instance.transform.position.x:F4},{instance.transform.position.y:F4},"
                + $"{instance.transform.position.z:F4})");
        }
        return correctedCount;
    }

    public bool TryConstrainFp1ToWalkablePaths(
        Action<string, string> writeCorrection,
        out int correctedCount,
        out string failure)
    {
        correctedCount = 0;
        failure = string.Empty;
        if (ExperimentSpaceRuntime.IsFp2) return true;

        foreach (var pair in spawnedByObjectId.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var instance = pair.Value;
            if (instance == null)
            {
                failure = $"fp1_incidental_instance_missing_{pair.Key}";
                return false;
            }
            var original = instance.transform.position;
            if (AagFp1FieldSafetyOverrides.TryGetIncidentalRoom(
                    pair.Key, out var overrideRoomUuid))
            {
                if (!AagFp1WalkablePath.TryResolveObservedRoomCenter(
                        overrideRoomUuid,
                        original,
                        out var centered,
                        out var centralPoint,
                        out var centralClearance,
                        out var centerMove,
                        out var centerFailure))
                {
                    failure = $"fp1_incidental_room_center_{pair.Key}_{centerFailure}";
                    return false;
                }
                instance.transform.position = centered;
                if (centerMove >= 0.01f) correctedCount++;
                writeCorrection?.Invoke(
                    pair.Key,
                    $"mode=field_reported_room_center; room={overrideRoomUuid}; "
                    + $"localCenter=({centralPoint.x:F3},{centralPoint.y:F3}); "
                    + $"clearance={centralClearance:F3}; moved={centerMove:F3}; "
                    + $"from=({original.x:F3},{original.y:F3},{original.z:F3}); "
                    + $"to=({centered.x:F3},{centered.y:F3},{centered.z:F3})");
                continue;
            }
            if (!AagFp1WalkablePath.TryConstrainObservedWorldPosition(
                    original,
                    out var resolved,
                    out var roomUuid,
                    out var distanceToPath,
                    out var movedMeters,
                    out var pathFailure))
            {
                failure = $"fp1_incidental_walkable_path_{pair.Key}_{pathFailure}";
                return false;
            }
            if (movedMeters < 0.01f) continue;
            instance.transform.position = resolved;
            correctedCount++;
            writeCorrection?.Invoke(
                pair.Key,
                $"room={roomUuid}; distanceToPath={distanceToPath:F3}; moved={movedMeters:F3}; "
                + $"from=({original.x:F3},{original.y:F3},{original.z:F3}); "
                + $"to=({resolved.x:F3},{resolved.y:F3},{resolved.z:F3})");
        }
        return true;
    }

    public bool TryGetSpawnedTransform(string objectId, out Transform result)
    {
        if (!string.IsNullOrEmpty(objectId)
            && spawnedByObjectId.TryGetValue(objectId, out var instance)
            && instance != null)
        {
            result = instance.transform;
            return true;
        }
        result = null;
        return false;
    }

    public void ClearRuntimeContents()
    {
        foreach (var instance in spawnedContents)
            if (instance != null) Destroy(instance);
        spawnedContents.Clear();
        spawnedByObjectId.Clear();
    }

    private static bool TryFindLargestHorizontalWallPenetration(
        GameObject instance,
        float minimumDistance,
        out Vector3 bestDirection,
        out float bestDistance)
    {
        bestDirection = Vector3.zero;
        bestDistance = 0f;
        var renderers = instance.GetComponentsInChildren<Renderer>(true)
            .Where(value => value != null && value.enabled && value.gameObject.activeInHierarchy)
            .ToArray();
        foreach (var renderer in renderers)
        {
            var probe = new GameObject("IncidentalWallProbe")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = 2,
            };
            try
            {
                probe.transform.SetPositionAndRotation(renderer.transform.position, renderer.transform.rotation);
                var scale = renderer.transform.lossyScale;
                probe.transform.localScale = new Vector3(
                    Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                var box = probe.AddComponent<BoxCollider>();
                box.center = renderer.localBounds.center;
                box.size = renderer.localBounds.size;
                Physics.SyncTransforms();

                var nearby = Physics.OverlapBox(
                    box.bounds.center,
                    box.bounds.extents + Vector3.one * 0.01f,
                    Quaternion.identity,
                    Physics.AllLayers,
                    QueryTriggerInteraction.Ignore);
                foreach (var environmentCollider in nearby)
                {
                    if (environmentCollider == null
                        || environmentCollider == box
                        || environmentCollider.transform.IsChildOf(instance.transform)
                        || environmentCollider.GetComponentInParent<MRUKAnchor>() == null
                        || !Physics.ComputePenetration(
                            box,
                            box.transform.position,
                            box.transform.rotation,
                            environmentCollider,
                            environmentCollider.transform.position,
                            environmentCollider.transform.rotation,
                            out var direction,
                            out var distance)
                        || distance < minimumDistance
                        || Mathf.Abs(direction.y) >= 0.55f
                        || distance <= bestDistance)
                        continue;

                    bestDirection = direction;
                    bestDistance = distance;
                }
            }
            finally
            {
                DestroyImmediate(probe);
            }
        }
        return bestDistance >= minimumDistance;
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
