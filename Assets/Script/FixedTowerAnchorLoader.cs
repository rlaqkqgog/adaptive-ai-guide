using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Minimal Quest-store loader owned only by the four fixed tower anchors.
/// It does not share state, prefabs, or retries with AnchorLoader. When Quest
/// cannot return a tower UUID, it uses that tower's saved fallback pose.
/// </summary>
[DisallowMultipleComponent]
public sealed class FixedTowerAnchorLoader : MonoBehaviour
{
    private OVRSpatialAnchor anchorPrefab;
    private readonly Dictionary<Guid, OVRSpatialAnchor> loadedAnchors = new Dictionary<Guid, OVRSpatialAnchor>();
    private readonly Dictionary<Guid, GameObject> approximateRoots = new Dictionary<Guid, GameObject>();
    private readonly Dictionary<Guid, string> failures = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, string> approximateReasons = new Dictionary<Guid, string>();
    private int loadVersion;

    public bool IsLoading { get; private set; }
    public int LoadedCount => loadedAnchors.Count + approximateRoots.Count;
    public int RealLoadedCount => loadedAnchors.Count;
    public int ApproximateCount => approximateRoots.Count;
    public IReadOnlyDictionary<Guid, string> Failures => failures;
    public IReadOnlyDictionary<Guid, string> ApproximateReasons => approximateReasons;

    public void Initialize(OVRSpatialAnchor prefab)
    {
        anchorPrefab = prefab;
    }

    public void Load(IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid)
    {
        ClearLoadedAnchors();
        failures.Clear();

        var requestedByUuid = towersByUuid?
            .Where(pair => pair.Key != Guid.Empty && pair.Value != null)
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.First().Value)
            ?? new Dictionary<Guid, ExperimentTowerAnchor>();
        var requested = requestedByUuid.Keys.ToArray();
        if (requested.Length == 0) return;

        IsLoading = true;
        var version = ++loadVersion;
        LoadAsync(requested, requestedByUuid, version);
    }

    public bool TryGetTransform(Guid uuid, out Transform anchorTransform)
    {
        if (loadedAnchors.TryGetValue(uuid, out var realAnchor) && realAnchor != null)
        {
            anchorTransform = realAnchor.transform;
            return true;
        }
        if (approximateRoots.TryGetValue(uuid, out var approximateRoot) && approximateRoot != null)
        {
            anchorTransform = approximateRoot.transform;
            return true;
        }
        anchorTransform = null;
        return false;
    }

    public void ClearLoadedAnchors()
    {
        loadVersion++;
        IsLoading = false;
        foreach (var anchor in loadedAnchors.Values)
        {
            if (anchor == null) continue;
            anchor.gameObject.SetActive(false);
            Destroy(anchor.gameObject);
        }
        loadedAnchors.Clear();
        foreach (var root in approximateRoots.Values)
        {
            if (root != null) Destroy(root);
        }
        approximateRoots.Clear();
        failures.Clear();
        approximateReasons.Clear();
    }

    private async void LoadAsync(
        Guid[] requested,
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> requestedByUuid,
        int version)
    {
        try
        {
            var returnedAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            Debug.Log($"[FixedTower Load] start requested={requested.Length}", this);
            var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(requested, returnedAnchors);
            if (version != loadVersion) return;

            Debug.Log(
                $"[FixedTower Load] query status={result.Status} success={result.Success} " +
                $"returned={returnedAnchors.Count}/{requested.Length}", this);
            if (!result.Success)
            {
                foreach (var uuid in requested) failures[uuid] = $"load failed: {result.Status}";
            }
            else
            {
                var returnedUuids = new HashSet<Guid>(returnedAnchors.Select(anchor => anchor.Uuid));
                foreach (var uuid in requested)
                {
                    if (!returnedUuids.Contains(uuid)) failures[uuid] = "not returned";
                }

                foreach (var unboundAnchor in returnedAnchors)
                {
                    if (version != loadVersion) return;
                    var uuid = unboundAnchor.Uuid;
                    var localized = unboundAnchor.Localized || await unboundAnchor.LocalizeAsync();
                    if (version != loadVersion) return;
                    if (!localized)
                    {
                        failures[uuid] = "localize failed";
                        continue;
                    }
                    if (!unboundAnchor.TryGetPose(out var pose))
                    {
                        failures[uuid] = "pose unavailable";
                        continue;
                    }
                    if (anchorPrefab == null)
                    {
                        failures[uuid] = "anchor prefab missing";
                        continue;
                    }

                    var spatialAnchor = Instantiate(anchorPrefab, pose.position, pose.rotation);
                    spatialAnchor.gameObject.name = $"FixedTowerAnchor {uuid}";
                    unboundAnchor.BindTo(spatialAnchor);
                    loadedAnchors[uuid] = spatialAnchor;
                    failures.Remove(uuid);
                    Debug.Log($"[FixedTower Load] loaded uuid={uuid}", this);
                }
            }
        }
        catch (Exception exception)
        {
            if (version != loadVersion) return;
            foreach (var uuid in requested.Where(uuid => !loadedAnchors.ContainsKey(uuid)))
                failures[uuid] = $"exception: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (version == loadVersion)
            {
                SpawnApproximateRoots(requestedByUuid);
                IsLoading = false;
                Debug.Log(
                    $"[FixedTower Load] complete real={loadedAnchors.Count} approx={approximateRoots.Count} " +
                    $"ready={LoadedCount}/{requested.Length} failed={failures.Count}", this);
            }
        }
    }

    private void SpawnApproximateRoots(IReadOnlyDictionary<Guid, ExperimentTowerAnchor> requestedByUuid)
    {
        // Reconstruct missing tower poses relative to a localized tower. Raw
        // tracking-space fallback coordinates can shift between Quest sessions.
        var references = requestedByUuid
            .Where(pair => pair.Value != null
                && pair.Value.hasFallbackPose
                && loadedAnchors.TryGetValue(pair.Key, out var anchor)
                && anchor != null)
            .Select(pair => new
            {
                Definition = pair.Value,
                Transform = loadedAnchors[pair.Key].transform,
            })
            .ToArray();

        foreach (var pair in requestedByUuid)
        {
            var uuid = pair.Key;
            if (loadedAnchors.ContainsKey(uuid) || !failures.TryGetValue(uuid, out var reason)) continue;
            var definition = pair.Value;
            if (!definition.hasFallbackPose) continue;

            var position = definition.fallbackWorldPosition;
            var rotation = definition.fallbackWorldRotation;
            var referenceTowerId = "raw_fallback";
            if (references.Length > 0)
            {
                var reference = references
                    .OrderBy(candidate =>
                        (candidate.Definition.fallbackWorldPosition - definition.fallbackWorldPosition).sqrMagnitude)
                    .First();
                var deltaRotation = reference.Transform.rotation
                    * Quaternion.Inverse(reference.Definition.fallbackWorldRotation);
                position = reference.Transform.position
                    + deltaRotation * (definition.fallbackWorldPosition - reference.Definition.fallbackWorldPosition);
                rotation = deltaRotation * definition.fallbackWorldRotation;
                referenceTowerId = reference.Definition.towerId;
            }

            var root = new GameObject($"FixedTowerApproxAnchor {definition.towerId}");
            root.transform.SetPositionAndRotation(position, rotation);
            approximateRoots[uuid] = root;
            approximateReasons[uuid] = reason;
            failures.Remove(uuid);
            Debug.Log(
                $"[FixedTower Load] approximate tower={definition.towerId} uuid={uuid} reason={reason} " +
                $"reference={referenceTowerId} position={position}", this);
        }
    }
}
