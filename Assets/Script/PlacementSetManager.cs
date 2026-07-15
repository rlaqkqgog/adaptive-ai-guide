using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class PlacementSetManager : MonoBehaviour
{
    [Serializable]
    public sealed class LocalizedMarkerPose
    {
        public string MarkerId { get; internal set; }
        public string Color { get; internal set; }
        public Guid AnchorUuid { get; internal set; }
        public Transform Pose { get; internal set; }
    }

    [SerializeField] private string initialSet = "FP1-S1";
    [SerializeField] private GameObject markerPrefab;
    [SerializeField, Min(1f)] private float localizationTimeoutSeconds = 15f;
    [SerializeField] private bool loadInitialSetOnStart = true;

    private AnchorLoader anchorLoader;
    private readonly List<GameObject> spawnedMarkers = new List<GameObject>();
    private readonly List<LocalizedMarkerPose> localizedMarkerPoses = new List<LocalizedMarkerPose>();

    public string CurrentSet { get; private set; } = string.Empty;
    public IReadOnlyList<LocalizedMarkerPose> LocalizedMarkerPoses => localizedMarkerPoses;
    public event Action<string, IReadOnlyList<LocalizedMarkerPose>> CurrentSetLocalized;

    private void Awake()
    {
        anchorLoader = GetComponent<AnchorLoader>();
    }

    private IEnumerator Start()
    {
        if (loadInitialSetOnStart) yield return LoadSet(initialSet);
    }

    public Coroutine SelectSet(string setId)
    {
        return StartCoroutine(LoadSet(setId));
    }

    private IEnumerator LoadSet(string setId)
    {
        ClearSpawnedMarkers();
        CurrentSet = string.Empty;
        var manifest = AagManualAnchorSetStore.LoadOrCreate();
        var set = manifest.sets.FirstOrDefault(value => string.Equals(value.set_id, setId, StringComparison.Ordinal));
        if (set == null || !set.locked || set.anchors.Count != 12
            || AagManualAnchorSetStore.Colors.Any(color => set.anchors.Count(entry => entry.color == color) != 3))
        {
            Debug.LogError($"[PlacementSetManager] set={setId} load blocked: manifest set must be locked with 12 anchors and 3 per color");
            yield break;
        }
        if (anchorLoader == null)
        {
            Debug.LogError("[PlacementSetManager] AnchorLoader component missing");
            yield break;
        }
        var entriesByUuid = set.anchors.ToDictionary(entry => Guid.Parse(entry.anchor_uuid));
        anchorLoader.LoadAnchorsByUuid(entriesByUuid.Keys, $"PLACEMENT_SET:{setId}");
        var expires = Time.unscaledTime + localizationTimeoutSeconds;
        while (anchorLoader.IsReadOnlyLoadInProgress && Time.unscaledTime < expires) yield return null;
        if (anchorLoader.IsReadOnlyLoadInProgress) anchorLoader.FinalizePendingAsTimedOut();

        foreach (var pair in entriesByUuid)
        {
            if (!anchorLoader.LocalizedAnchorsReadOnly.TryGetValue(pair.Key, out var localizedAnchor) || localizedAnchor == null)
            {
                var reason = anchorLoader.LocalizationFailuresReadOnly.TryGetValue(pair.Key, out var failure) ? failure : "NOT_LOCALIZED";
                Debug.LogError($"[PlacementSetManager] set={setId} marker={pair.Value.marker_id} uuid={pair.Key} unavailable reason={reason}");
                continue;
            }
            if (markerPrefab == null)
            {
                Debug.LogError($"[PlacementSetManager] markerPrefab missing; localized uuid={pair.Key} was not spawned");
                continue;
            }
            var marker = Instantiate(markerPrefab, localizedAnchor.transform.position, localizedAnchor.transform.rotation);
            marker.name = $"{setId} {pair.Value.marker_id} {pair.Key}";
            spawnedMarkers.Add(marker);
            localizedMarkerPoses.Add(new LocalizedMarkerPose
            {
                MarkerId = pair.Value.marker_id,
                Color = pair.Value.color,
                AnchorUuid = pair.Key,
                Pose = marker.transform,
            });
        }
        CurrentSet = setId;
        Debug.Log($"[PlacementSetManager] CurrentSet={CurrentSet}; requested=12 localized={anchorLoader.LocalizedRequestedCount} spawned={localizedMarkerPoses.Count}");
        CurrentSetLocalized?.Invoke(CurrentSet, LocalizedMarkerPoses);
    }

    private void ClearSpawnedMarkers()
    {
        foreach (var marker in spawnedMarkers)
            if (marker != null) Destroy(marker);
        spawnedMarkers.Clear();
        localizedMarkerPoses.Clear();
    }
}
