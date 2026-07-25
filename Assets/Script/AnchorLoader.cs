using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class AnchorLoader : MonoBehaviour
{
    private OVRSpatialAnchor anchorPrefab;
    private SpatialAnchorManager spatialAnchorManager;
    private readonly Dictionary<Guid, OVRSpatialAnchor> localizedAnchors = new Dictionary<Guid, OVRSpatialAnchor>();
    private readonly HashSet<OVRSpatialAnchor> loaderOwnedAnchors = new HashSet<OVRSpatialAnchor>();
    private readonly HashSet<Guid> requestedLocalization = new HashSet<Guid>();
    private readonly HashSet<Guid> pendingLocalization = new HashSet<Guid>();
    private readonly Dictionary<Guid, string> localizationFailures = new Dictionary<Guid, string>();
    private readonly HashSet<Guid> foundInQuestStore = new HashSet<Guid>();
    private readonly Dictionary<Guid, string> lastAttemptReasons = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, OVRSpatialAnchor> prefabByUuid = new Dictionary<Guid, OVRSpatialAnchor>();
    private int requestVersion;
    private bool startupReadinessCompleted;

    private const float StartupTrackingSettleSeconds = 2f;
    // Field evidence (2026-07-16): failures are deterministic per UUID — 24 retries over
    // 120s recovered zero anchors. A short window still covers startup timing, while the
    // approximate-offset fallback covers permanently missing anchors.

    public IReadOnlyDictionary<Guid, OVRSpatialAnchor> LocalizedAnchorsReadOnly => localizedAnchors;
    public IReadOnlyCollection<Guid> RequestedLocalizationReadOnly => requestedLocalization;
    public IReadOnlyDictionary<Guid, string> LocalizationFailuresReadOnly => localizationFailures;
    public bool IsReadOnlyLoadInProgress { get; private set; }
    public string ActiveLoadSource { get; private set; } = "NONE";
    public int LocalizedRequestedCount => requestedLocalization.Count(uuid => TryGetLocalizedAnchor(uuid, out _));
    public int MissingRequestedCount => requestedLocalization.Count - LocalizedRequestedCount;
    public int FoundInQuestStoreRequestedCount => foundInQuestStore.Count;
    public int LoadSucceededRequestedCount => foundInQuestStore.Count;
    public bool QuestStoreQueryCompleted { get; private set; }
    public bool ActiveForceQuestStoreQuery { get; private set; }

    private void Awake()
    {
        spatialAnchorManager = GetComponent<SpatialAnchorManager>();
        anchorPrefab = spatialAnchorManager != null ? spatialAnchorManager.anchorPrefab : null;
        Debug.Log($"[AAG Anchor Startup] App start utc={DateTime.UtcNow:O}");
    }

    public bool TryGetLocalizedAnchor(Guid uuid, out OVRSpatialAnchor anchor)
    {
        if (localizedAnchors.TryGetValue(uuid, out anchor) && anchor != null)
            return true;

        anchor = null;
        return false;
    }

    public void ClearPrefabOverrides()
    {
        prefabByUuid.Clear();
    }

    public void SetPrefabForUuid(Guid uuid, OVRSpatialAnchor prefab)
    {
        if (uuid == Guid.Empty || prefab == null) return;
        prefabByUuid[uuid] = prefab;
    }

    public void ForgetLocalizedAnchor(Guid uuid)
    {
        if (localizedAnchors.TryGetValue(uuid, out var anchor) && anchor != null)
            loaderOwnedAnchors.Remove(anchor);
        localizedAnchors.Remove(uuid);
    }

    public void ClearLoadedAnchors()
    {
        requestVersion++;
        foreach (var anchor in loaderOwnedAnchors.ToArray())
        {
            if (anchor == null) continue;
            var uuid = SafeUuid(anchor);
            localizedAnchors.Remove(uuid);
            anchor.gameObject.SetActive(false);
            Destroy(anchor.gameObject);
        }
        loaderOwnedAnchors.Clear();
        pendingLocalization.Clear();
        IsReadOnlyLoadInProgress = false;
        ActiveForceQuestStoreQuery = false;
    }

    public void LoadAnchorsByUuid()
    {
        Debug.LogWarning(
            "[AnchorLoader] Legacy parameterless/PlayerPrefs load is disabled. " +
            "Use LOAD ACTIVE SET, which reads only AagManualAnchorSets/fp1_manual_anchor_sets.json.");
    }

    public void LoadAnchorsByUuid(IEnumerable<Guid> requestedUuids, string source)
    {
        LoadAnchorsByUuid(requestedUuids, source, false);
    }

    public void LoadAnchorsByUuid(
        IEnumerable<Guid> requestedUuids,
        string source,
        bool forceQuestStoreQuery)
    {
        var normalized = requestedUuids?.Where(uuid => uuid != Guid.Empty).Distinct().ToArray()
            ?? Array.Empty<Guid>();

        var requestedSet = new HashSet<Guid>(normalized);
        if (!forceQuestStoreQuery)
            RetainOnlyRequestedLoaderObjects(requestedSet);
        var thisRequestVersion = ++requestVersion;

        requestedLocalization.Clear();
        pendingLocalization.Clear();
        localizationFailures.Clear();
        foundInQuestStore.Clear();
        lastAttemptReasons.Clear();
        QuestStoreQueryCompleted = false;
        ActiveLoadSource = string.IsNullOrWhiteSpace(source) ? "UNSPECIFIED" : source;
        ActiveForceQuestStoreQuery = forceQuestStoreQuery;
        foreach (var uuid in normalized) requestedLocalization.Add(uuid);

        if (normalized.Length == 0)
        {
            QuestStoreQueryCompleted = true;
            IsReadOnlyLoadInProgress = false;
            Debug.LogWarning($"[AnchorLoader] Failed source={ActiveLoadSource} reason=no UUIDs requested");
            return;
        }

        if (forceQuestStoreQuery)
        {
            IsReadOnlyLoadInProgress = true;
            var destroyedRuntimeObjects = DestroyRuntimeAnchorsForFreshQuery(requestedSet);
            Debug.Log(
                $"[AAG Anchor Fresh Load] source={ActiveLoadSource} requested={normalized.Length} " +
                $"destroyedRuntimeObjects={destroyedRuntimeObjects} existingAnchorReuse=false persistedAnchorErased=false");
            StartCoroutine(WaitForRuntimeCleanupThenLoad(normalized, thisRequestVersion));
            return;
        }

        foreach (var pair in FindExistingAnchors())
            localizedAnchors[pair.Key] = pair.Value;

        var unresolved = normalized.Where(uuid => !TryGetLocalizedAnchor(uuid, out _)).ToArray();
        foreach (var uuid in normalized.Where(uuid => TryGetLocalizedAnchor(uuid, out _)))
            foundInQuestStore.Add(uuid);
        if (unresolved.Length == 0)
        {
            QuestStoreQueryCompleted = true;
            IsReadOnlyLoadInProgress = false;
            Debug.Log($"[AnchorLoader] Loaded source={ActiveLoadSource} loaded={LocalizedRequestedCount}/{normalized.Length}");
            return;
        }

        IsReadOnlyLoadInProgress = true;
        if (startupReadinessCompleted)
        {
            BeginUuidLoad(unresolved, thisRequestVersion);
        }
        else
        {
            StartCoroutine(WaitForStartupReadinessThenLoad(unresolved, thisRequestVersion));
        }
    }

    private IEnumerator WaitForRuntimeCleanupThenLoad(Guid[] requested, int thisRequestVersion)
    {
        // Destroy() is applied at the end of the frame. Waiting for two frame boundaries
        // prevents a just-repaired in-memory anchor from satisfying the fresh store query.
        yield return new WaitForEndOfFrame();
        if (thisRequestVersion != requestVersion) yield break;
        yield return null;
        if (thisRequestVersion != requestVersion) yield break;

        var remainingRuntimeObjects = CountRuntimeAnchors(requested);
        Debug.Log(
            $"[AAG Anchor Fresh Load] Runtime cleanup complete source={ActiveLoadSource} " +
            $"remainingRequestedRuntimeObjects={remainingRuntimeObjects} utc={DateTime.UtcNow:O}");
        BeginUuidLoadWhenReady(requested, thisRequestVersion);
    }

    private IEnumerator WaitForStartupReadinessThenLoad(IReadOnlyCollection<Guid> requested, int thisRequestVersion)
    {
        Debug.Log($"[AAG Anchor Startup] Waiting for Input Focus source={ActiveLoadSource} requested={requested.Count}");
        while (!OVRManager.hasInputFocus)
        {
            if (thisRequestVersion != requestVersion) yield break;
            yield return null;
        }

        if (thisRequestVersion != requestVersion) yield break;
        Debug.Log($"[AAG Anchor Startup] Input Focus acquired utc={DateTime.UtcNow:O}");
        Debug.Log($"[AAG Anchor Startup] Stabilization wait start seconds={StartupTrackingSettleSeconds:F1}");
        var settleUntil = Time.realtimeSinceStartup + StartupTrackingSettleSeconds;
        while (Time.realtimeSinceStartup < settleUntil)
        {
            if (thisRequestVersion != requestVersion) yield break;
            yield return null;
        }

        if (thisRequestVersion != requestVersion) yield break;
        startupReadinessCompleted = true;
        Debug.Log($"[AAG Anchor Startup] Stabilization wait end utc={DateTime.UtcNow:O}");
        BeginUuidLoad(requested, thisRequestVersion);
    }

    private void BeginUuidLoad(IReadOnlyCollection<Guid> requested, int thisRequestVersion)
    {
        Debug.Log(
            $"[AAG Anchor Load] UUID Load start source={ActiveLoadSource} requested={requested.Count} " +
            $"forceQuestStoreQuery={ActiveForceQuestStoreQuery} existingAnchorReuse={!ActiveForceQuestStoreQuery} utc={DateTime.UtcNow:O}");
        LoadAndLocalizeAsync(requested, thisRequestVersion);
    }

    private void BeginUuidLoadWhenReady(IReadOnlyCollection<Guid> requested, int thisRequestVersion)
    {
        if (startupReadinessCompleted)
            BeginUuidLoad(requested, thisRequestVersion);
        else
            StartCoroutine(WaitForStartupReadinessThenLoad(requested, thisRequestVersion));
    }

    private async void LoadAndLocalizeAsync(IReadOnlyCollection<Guid> requested, int thisRequestVersion)
    {
        var total = requested.Count;
        try
        {
            var missing = requested.Where(uuid => !TryGetLocalizedAnchor(uuid, out _)).ToList();
            var completed = await TryLoadOnceAsync(missing, thisRequestVersion, total);
            if (!completed || thisRequestVersion != requestVersion) return;

            foreach (var uuid in requested.Where(value => !TryGetLocalizedAnchor(value, out _)))
            {
                var interim = lastAttemptReasons.TryGetValue(uuid, out var reason) ? reason : "not returned";
                RecordFailure(uuid, interim);
            }
        }
        catch (Exception exception)
        {
            if (thisRequestVersion != requestVersion) return;
            foreach (var uuid in requested.Where(value => !TryGetLocalizedAnchor(value, out _)
                && !localizationFailures.ContainsKey(value)))
                RecordFailure(uuid, $"load exception: {exception.GetType().Name}: {exception.Message}");
        }

        if (thisRequestVersion != requestVersion) return;
        IsReadOnlyLoadInProgress = false;
        var localizedFinal = LocalizedRequestedCount;
        Debug.Log(
            $"[AAG Anchor Load] Load complete source={ActiveLoadSource} attempts=1 " +
            $"localized={localizedFinal}/{total} spawned={localizedFinal}/{total} utc={DateTime.UtcNow:O}");
    }

    private async Task<bool> TryLoadOnceAsync(
        IReadOnlyCollection<Guid> missing, int thisRequestVersion, int totalRequested)
    {
        var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(missing, unboundAnchors);
        if (thisRequestVersion != requestVersion) return false;

        QuestStoreQueryCompleted = true;
        Debug.Log(
            $"[AAG Anchor Load] UUID query end source={ActiveLoadSource} attempt=1 " +
            $"returned={unboundAnchors.Count}/{missing.Count} status={result.Status} utc={DateTime.UtcNow:O}");
        if (!result.Success)
        {
            foreach (var uuid in missing)
                lastAttemptReasons[uuid] = $"load failed: {result.Status}";
            return true;
        }

        var returned = new HashSet<Guid>(unboundAnchors.Select(anchor => anchor.Uuid));
        foreach (var uuid in returned) foundInQuestStore.Add(uuid);
        foreach (var uuid in missing.Where(value => !returned.Contains(value)))
            lastAttemptReasons[uuid] = "not returned";

        foreach (var unboundAnchor in unboundAnchors)
            pendingLocalization.Add(unboundAnchor.Uuid);

        foreach (var unboundAnchor in unboundAnchors)
        {
            var localized = unboundAnchor.Localized || await unboundAnchor.LocalizeAsync();
            if (thisRequestVersion != requestVersion) return false;
            pendingLocalization.Remove(unboundAnchor.Uuid);
            if (!localized)
            {
                lastAttemptReasons[unboundAnchor.Uuid] = "localize failed";
                continue;
            }

            if (!unboundAnchor.TryGetPose(out var pose))
            {
                lastAttemptReasons[unboundAnchor.Uuid] = "pose unavailable";
                continue;
            }

            if (SpawnOrRefresh(unboundAnchor, pose))
            {
                lastAttemptReasons.Remove(unboundAnchor.Uuid);
                Debug.Log(
                    $"[AAG Anchor Load] Localized uuid={unboundAnchor.Uuid} attempt=1 " +
                    $"totalLocalized={LocalizedRequestedCount}/{totalRequested}");
            }
        }

        return true;
    }

    private bool SpawnOrRefresh(OVRSpatialAnchor.UnboundAnchor unboundAnchor, Pose pose)
    {
        try
        {
            if (TryGetLocalizedAnchor(unboundAnchor.Uuid, out var existing))
            {
                existing.transform.SetPositionAndRotation(pose.position, pose.rotation);
                return true;
            }

            var prefab = prefabByUuid.TryGetValue(unboundAnchor.Uuid, out var configuredPrefab)
                && configuredPrefab != null
                ? configuredPrefab
                : anchorPrefab;
            if (prefab == null)
                throw new InvalidOperationException($"No anchor prefab configured for UUID {unboundAnchor.Uuid}");

            var spatialAnchor = Instantiate(prefab, pose.position, pose.rotation);
            unboundAnchor.BindTo(spatialAnchor);
            localizedAnchors[unboundAnchor.Uuid] = spatialAnchor;
            loaderOwnedAnchors.Add(spatialAnchor);
            return true;
        }
        catch (Exception exception)
        {
            RecordFailure(unboundAnchor.Uuid, $"spawn failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    public void FinalizePendingAsTimedOut()
    {
        foreach (var uuid in pendingLocalization.ToArray())
            RecordFailure(uuid, "load incomplete");
        pendingLocalization.Clear();
        IsReadOnlyLoadInProgress = false;
        requestVersion++;
    }

    private void RetainOnlyRequestedLoaderObjects(HashSet<Guid> requested)
    {
        requestVersion++;
        foreach (var anchor in loaderOwnedAnchors.ToArray())
        {
            if (anchor == null)
            {
                loaderOwnedAnchors.Remove(anchor);
                continue;
            }

            var uuid = SafeUuid(anchor);
            if (requested.Contains(uuid)) continue;
            localizedAnchors.Remove(uuid);
            loaderOwnedAnchors.Remove(anchor);
            anchor.gameObject.SetActive(false);
            Destroy(anchor.gameObject);
        }
        pendingLocalization.Clear();
        IsReadOnlyLoadInProgress = false;
    }

    private int DestroyRuntimeAnchorsForFreshQuery(HashSet<Guid> requested)
    {
        spatialAnchorManager?.DetachRuntimeAnchorsForFreshLoad(requested);

        var destroyed = 0;
        foreach (var anchor in Resources.FindObjectsOfTypeAll<OVRSpatialAnchor>())
        {
            if (anchor == null || !anchor.gameObject.scene.IsValid()) continue;
            var uuid = SafeUuid(anchor);
            if (uuid == Guid.Empty || !requested.Contains(uuid)) continue;

            localizedAnchors.Remove(uuid);
            loaderOwnedAnchors.Remove(anchor);
            anchor.gameObject.SetActive(false);
            Destroy(anchor.gameObject);
            destroyed++;
            Debug.Log(
                $"[AAG Anchor Fresh Load] Runtime object removed uuid={uuid} " +
                $"object={anchor.gameObject.name} persistedAnchorErased=false");
        }

        pendingLocalization.Clear();
        return destroyed;
    }

    private static int CountRuntimeAnchors(IEnumerable<Guid> requested)
    {
        var requestedSet = requested as HashSet<Guid> ?? new HashSet<Guid>(requested);
        var count = 0;
        foreach (var anchor in Resources.FindObjectsOfTypeAll<OVRSpatialAnchor>())
        {
            if (anchor == null || !anchor.gameObject.scene.IsValid()) continue;
            var uuid = SafeUuid(anchor);
            if (uuid != Guid.Empty && requestedSet.Contains(uuid)) count++;
        }
        return count;
    }

    private void RecordFailure(Guid uuid, string reason)
    {
        localizationFailures[uuid] = reason;
        Debug.LogError($"[AnchorLoader] Failed uuid={uuid} source={ActiveLoadSource} reason={reason}");
    }

    private static Dictionary<Guid, OVRSpatialAnchor> FindExistingAnchors()
    {
        var result = new Dictionary<Guid, OVRSpatialAnchor>();
        foreach (var anchor in Resources.FindObjectsOfTypeAll<OVRSpatialAnchor>())
        {
            if (anchor == null || !anchor.gameObject.scene.IsValid() || !anchor.gameObject.activeInHierarchy) continue;
            var uuid = SafeUuid(anchor);
            if (uuid != Guid.Empty && !result.ContainsKey(uuid)) result[uuid] = anchor;
        }
        return result;
    }

    private static Guid SafeUuid(OVRSpatialAnchor anchor)
    {
        try { return anchor != null ? anchor.Uuid : Guid.Empty; }
        catch (Exception) { return Guid.Empty; }
    }
}
