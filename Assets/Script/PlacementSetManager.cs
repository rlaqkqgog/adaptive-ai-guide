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
    [SerializeField] private bool loadInitialSetOnStart = false;

    private AnchorLoader anchorLoader;
    private int selectionVersion;
    private readonly List<GameObject> spawnedMarkers = new List<GameObject>();
    private readonly List<LocalizedMarkerPose> localizedMarkerPoses = new List<LocalizedMarkerPose>();

    public string CurrentSet { get; private set; } = string.Empty;
    public IReadOnlyList<LocalizedMarkerPose> LocalizedMarkerPoses => localizedMarkerPoses;
    public event Action<string, IReadOnlyList<LocalizedMarkerPose>> CurrentSetLocalized;

    private void Awake()
    {
        anchorLoader = GetComponent<AnchorLoader>();
        if (!ExperimentSpaceRuntime.SetIds.Contains(initialSet)
            && ExperimentSpaceRuntime.SetIds.Count > 0)
            initialSet = ExperimentSpaceRuntime.SetIds[0];
    }

    private IEnumerator Start()
    {
        if (GetComponent<AagManualAnchorSetAuthoring>() != null)
        {
            Debug.Log("[PlacementSetManager] Initial set load skipped because manual anchor authoring requires an explicit LOAD ACTIVE SET trigger.");
            yield break;
        }
        if (loadInitialSetOnStart) yield return LoadSet(initialSet, ++selectionVersion);
    }

    public Coroutine SelectSet(string setId)
    {
        return StartCoroutine(LoadSet(setId, ++selectionVersion));
    }

    private IEnumerator LoadSet(string setId, int loadVersion)
    {
        ClearSpawnedMarkers();
        anchorLoader?.ClearLoadedAnchors();
        CurrentSet = string.Empty;
        var manifest = AagManualAnchorSetStore.LoadOrCreate();
        Debug.Log(
            $"[PlacementSetManager] manifestSource=FIXED_RUNTIME_FILE path={AagManualAnchorSetStore.ManifestPath} " +
            "exportsSearched=false backupsSearched=false playerPrefsUsed=false legacyAnchorLogUsed=false");
        var set = manifest.sets.FirstOrDefault(value => string.Equals(value.set_id, setId, StringComparison.Ordinal));
        if (set == null)
        {
            Debug.LogError($"[PlacementSetManager] Failed set={setId} reason=set not found in manual manifest");
            yield break;
        }
        if (anchorLoader == null)
        {
            Debug.LogError("[PlacementSetManager] AnchorLoader component missing");
            yield break;
        }
        var entriesByUuid = set.anchors.ToDictionary(entry => Guid.Parse(entry.anchor_uuid));
        anchorLoader.LoadAnchorsByUuid(entriesByUuid.Keys, $"PLACEMENT_SET:{setId}", true);
        while (anchorLoader.IsReadOnlyLoadInProgress)
        {
            if (loadVersion != selectionVersion) yield break;
            yield return null;
        }
        if (loadVersion != selectionVersion) yield break;

        foreach (var pair in entriesByUuid)
        {
            if (!anchorLoader.TryGetLocalizedAnchor(pair.Key, out var localizedAnchor)
                || anchorLoader.LocalizationFailuresReadOnly.ContainsKey(pair.Key))
            {
                var reason = anchorLoader.LocalizationFailuresReadOnly.TryGetValue(pair.Key, out var failure) ? failure : "NOT_LOCALIZED";
                Debug.LogError($"[PlacementSetManager] Failed marker={pair.Value.marker_id} uuid={pair.Key} reason={reason}");
                continue;
            }
            if (markerPrefab == null)
            {
                Debug.LogError($"[PlacementSetManager] Failed marker={pair.Value.marker_id} uuid={pair.Key} reason=marker prefab missing");
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
            Debug.Log($"[PlacementSetManager] Loaded marker={pair.Value.marker_id} uuid={pair.Key}");
        }
        CurrentSet = setId;
        Debug.Log($"[PlacementSetManager] Load complete set={CurrentSet} loaded={localizedMarkerPoses.Count}/{set.anchors.Count}");
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
