using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class AnchorLoader : MonoBehaviour
{
    private OVRSpatialAnchor anchorPrefab;
    private SpatialAnchorManager spatialAnchorManager;
    private Action<OVRSpatialAnchor.UnboundAnchor, bool> onLoadAnchor;
    private readonly Dictionary<Guid, OVRSpatialAnchor> localizedAnchors = new Dictionary<Guid, OVRSpatialAnchor>();
    private readonly HashSet<Guid> requestedLocalization = new HashSet<Guid>();
    private readonly HashSet<Guid> pendingLocalization = new HashSet<Guid>();
    private readonly Dictionary<Guid, string> localizationFailures = new Dictionary<Guid, string>();

    public IReadOnlyDictionary<Guid, OVRSpatialAnchor> LocalizedAnchorsReadOnly => localizedAnchors;
    public IReadOnlyCollection<Guid> RequestedLocalizationReadOnly => requestedLocalization;
    public IReadOnlyDictionary<Guid, string> LocalizationFailuresReadOnly => localizationFailures;
    public bool IsReadOnlyLoadInProgress { get; private set; }
    public string ActiveLoadSource { get; private set; } = "NONE";
    public int LocalizedRequestedCount => requestedLocalization.Count(uuid => localizedAnchors.ContainsKey(uuid));
    public int MissingRequestedCount => requestedLocalization.Count - LocalizedRequestedCount;
    public int FoundInQuestStoreRequestedCount { get; private set; }
    public int LoadSucceededRequestedCount { get; private set; }
    public bool QuestStoreQueryCompleted { get; private set; }

    public bool TryGetLocalizedAnchor(Guid uuid, out OVRSpatialAnchor anchor)
    {
        if (localizedAnchors.TryGetValue(uuid, out anchor) && anchor != null)
            return true;

        anchor = null;
        return false;
    }

    public void ForgetLocalizedAnchor(Guid uuid)
    {
        localizedAnchors.Remove(uuid);
    }

    private void Awake()
    {
        spatialAnchorManager = GetComponent<SpatialAnchorManager>();
        anchorPrefab = spatialAnchorManager.anchorPrefab;
        onLoadAnchor = OnLocalized;
    }

    public void LoadAnchorsByUuid()
    {
        var uuids = spatialAnchorManager != null
            ? spatialAnchorManager.GetSavedAnchorUuidsReadOnly()
            : new List<Guid>();
        LoadAnchorsByUuid(uuids, "PLAYERPREFS_LEGACY");
    }

    public void LoadAnchorsByUuid(IEnumerable<Guid> requestedUuids, string source)
    {
        requestedLocalization.Clear();
        pendingLocalization.Clear();
        localizationFailures.Clear();
        FoundInQuestStoreRequestedCount = 0;
        LoadSucceededRequestedCount = 0;
        QuestStoreQueryCompleted = false;
        ActiveLoadSource = string.IsNullOrWhiteSpace(source) ? "UNSPECIFIED" : source;
        foreach (var uuid in requestedUuids?.Where(value => value != Guid.Empty).Distinct() ?? Enumerable.Empty<Guid>())
            requestedLocalization.Add(uuid);

        if (requestedLocalization.Count == 0)
        {
            IsReadOnlyLoadInProgress = false;
            Debug.LogWarning($"[AAG Hotspot Persistence] source={ActiveLoadSource} loadRequested=0 localized=0 missing=0");
            return;
        }

        var existingByUuid = FindExistingLocalizedAnchors();
        foreach (var pair in existingByUuid)
            localizedAnchors[pair.Key] = pair.Value;
        var unresolved = requestedLocalization.Where(uuid => !localizedAnchors.ContainsKey(uuid)).ToArray();
        if (unresolved.Length == 0)
        {
            FoundInQuestStoreRequestedCount = requestedLocalization.Count;
            LoadSucceededRequestedCount = requestedLocalization.Count;
            QuestStoreQueryCompleted = true;
            IsReadOnlyLoadInProgress = false;
            return;
        }

        IsReadOnlyLoadInProgress = true;
        Load(new OVRSpatialAnchor.LoadOptions
        {
            Timeout = 0,
            StorageLocation = OVRSpace.StorageLocation.Local,
            Uuids = unresolved,
        });
    }

    public void FinalizePendingAsTimedOut()
    {
        foreach (var uuid in pendingLocalization.ToArray())
            RecordFailure(uuid, "LOCALIZATION_TIMEOUT");
        pendingLocalization.Clear();
        IsReadOnlyLoadInProgress = false;
    }

    private void Load(OVRSpatialAnchor.LoadOptions options)
    {
        OVRSpatialAnchor.LoadUnboundAnchors(options, anchors =>
        {
            if (anchors == null)
            {
                QuestStoreQueryCompleted = true;
                foreach (var uuid in requestedLocalization.Where(uuid => !localizedAnchors.ContainsKey(uuid)))
                    RecordFailure(uuid, "LOCAL_STORE_LOAD_RETURNED_NULL");
                IsReadOnlyLoadInProgress = false;
                return;
            }

            var returned = new HashSet<Guid>(anchors.Select(anchor => anchor.Uuid));
            QuestStoreQueryCompleted = true;
            FoundInQuestStoreRequestedCount = requestedLocalization.Count(uuid => localizedAnchors.ContainsKey(uuid) || returned.Contains(uuid));
            LoadSucceededRequestedCount = FoundInQuestStoreRequestedCount;
            foreach (var uuid in requestedLocalization.Where(uuid => !localizedAnchors.ContainsKey(uuid) && !returned.Contains(uuid)))
                RecordFailure(uuid, "UUID_NOT_FOUND_IN_QUEST_LOCAL_STORAGE");

            foreach (var anchor in anchors)
                pendingLocalization.Add(anchor.Uuid);
            foreach (var anchor in anchors)
            {
                if (anchor.Localized)
                    onLoadAnchor(anchor, true);
                else if (!anchor.Localizing)
                    anchor.Localize(onLoadAnchor);
            }
            if (pendingLocalization.Count == 0)
                IsReadOnlyLoadInProgress = false;
        });
    }

    private void OnLocalized(OVRSpatialAnchor.UnboundAnchor unboundAnchor, bool success)
    {
        pendingLocalization.Remove(unboundAnchor.Uuid);
        if (!success)
        {
            RecordFailure(unboundAnchor.Uuid, "LOCALIZE_CALLBACK_FAILED");
            CompleteIfFinished();
            return;
        }

        try
        {
            if (localizedAnchors.TryGetValue(unboundAnchor.Uuid, out var existing) && existing != null)
            {
                Debug.Log($"[Anchor] Read-only load deduplicated UUID={unboundAnchor.Uuid}");
                CompleteIfFinished();
                return;
            }
            if (FindExistingLocalizedAnchors().TryGetValue(unboundAnchor.Uuid, out existing) && existing != null)
            {
                localizedAnchors[unboundAnchor.Uuid] = existing;
                Debug.Log($"[Anchor] Read-only load reused existing gray cube UUID={unboundAnchor.Uuid}");
                CompleteIfFinished();
                return;
            }

            var pose = unboundAnchor.Pose;
            var spatialAnchor = Instantiate(anchorPrefab, pose.position, pose.rotation);
            unboundAnchor.BindTo(spatialAnchor);
            if (!spatialAnchor.TryGetComponent<OVRSpatialAnchor>(out var anchor))
            {
                Destroy(spatialAnchor.gameObject);
                RecordFailure(unboundAnchor.Uuid, "BOUND_PREFAB_HAS_NO_OVR_SPATIAL_ANCHOR");
                CompleteIfFinished();
                return;
            }

            localizedAnchors[unboundAnchor.Uuid] = anchor;
            var labels = spatialAnchor.GetComponentsInChildren<TextMeshProUGUI>();
            if (labels.Length > 0) labels[0].text = "UUID: " + unboundAnchor.Uuid;
            if (labels.Length > 1) labels[1].text = "Loaded from Device";
            Debug.Log($"[AAG Hotspot Localization] uuid={unboundAnchor.Uuid}; source={ActiveLoadSource}; localized=true");
        }
        catch (Exception exception)
        {
            RecordFailure(unboundAnchor.Uuid, $"BIND_OR_INSTANTIATE_FAILED:{exception.GetType().Name}:{exception.Message}");
        }
        CompleteIfFinished();
    }

    private void CompleteIfFinished()
    {
        if (pendingLocalization.Count == 0)
            IsReadOnlyLoadInProgress = false;
    }

    private void RecordFailure(Guid uuid, string reason)
    {
        localizationFailures[uuid] = reason;
        Debug.LogError($"[AAG Hotspot Localization Failure] uuid={uuid}; source={ActiveLoadSource}; reason=\"{reason}\"");
    }

    private static Dictionary<Guid, OVRSpatialAnchor> FindExistingLocalizedAnchors()
    {
        var result = new Dictionary<Guid, OVRSpatialAnchor>();
        foreach (var sceneAnchor in Resources.FindObjectsOfTypeAll<OVRSpatialAnchor>())
        {
            if (sceneAnchor == null || !sceneAnchor.gameObject.scene.IsValid()) continue;
            try
            {
                if (sceneAnchor.Uuid != Guid.Empty && !result.ContainsKey(sceneAnchor.Uuid))
                    result[sceneAnchor.Uuid] = sceneAnchor;
            }
            catch (Exception)
            {
                // Ignore unbound prefab components.
            }
        }
        return result;
    }
}
