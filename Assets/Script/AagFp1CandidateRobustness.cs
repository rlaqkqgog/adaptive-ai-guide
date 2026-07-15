using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.AI;

public sealed partial class AagFp1PlacementAuthoring
{
    private const string CandidateBankSchemaVersion = "aag-fp1-candidate-bank/v3-bundled-manual-hotspots";
    private const string GeneratorVersion = "aag-fp1-geometry-aware/v6-bundled-manual-hotspots";
    private const string CandidateBankJsonFileName = "fp1_candidate_bank.json";
    private const string RouteMetricUnavailable = "ROUTE_METRIC_UNAVAILABLE";

    private enum CandidateLifecycleState
    {
        Draft,
        Ready,
        Locked,
        Stale,
        Invalidated,
    }

    [Serializable]
    private sealed class CandidateDependencyFingerprint
    {
        public string fingerprint;
        public string candidate_id;
        public int seed;
        public List<string> previous_locked_candidate_ids = new List<string>();
        public string referenced_world_coordinates_hash;
        public string placement_settings_hash;
        public List<string> fp1_room_uuids = new List<string>();
        public string mruk_export_identifier;
        public string mruk_geometry_hash;
        public string hotspot_source_anchor_hash;
        public string hotspot_catalog_hash;
        public string generator_version;
    }

    [Serializable]
    private sealed class CandidateMetrics
    {
        public string route_metric_status = RouteMetricUnavailable;
        public float route_length;
        public float mean_pairwise_distance;
        public float mean_nearest_neighbor_distance;
        public float maximum_pairwise_distance;
        public float convex_hull_coverage_report_only;
        public int corner_count;
        public int wall_band_count;
        public int peripheral_count;
        public int entrance_visible_marker_count;
        public float mean_discoverable_observation_count;
        public int max_visible_in_one_view;
        public float quality_score;
        public int manual_hotspot_count;
        public int procedural_count;
        public int unique_hotspot_count;
        public int reused_hotspot_count;
        public float mean_hotspot_distance;
        public List<StringIntMetric> room_counts = new List<StringIntMetric>();
        public List<StringIntMetric> zone_group_counts = new List<StringIntMetric>();
    }

    [Serializable]
    private sealed class StringIntMetric
    {
        public string key;
        public int value;
    }

    [Serializable]
    private sealed class CandidateSnapshot
    {
        public string candidate_id;
        public string set_id;
        public int seed;
        public string state;
        public bool individual_ready;
        public string state_reason;
        public CandidateDependencyFingerprint dependency = new CandidateDependencyFingerprint();
        public CandidateMetrics metrics = new CandidateMetrics();
        public List<AagPlacementRecord> placements = new List<AagPlacementRecord>();
    }

    [Serializable]
    private sealed class CandidateBankFile
    {
        public string schema_version = CandidateBankSchemaVersion;
        public string generator_version = GeneratorVersion;
        public bool triplet_ready;
        public string triplet_reason;
        public List<string> recommended_candidate_ids = new List<string>();
        public List<CandidateSnapshot> candidates = new List<CandidateSnapshot>();
    }

    private sealed class TripletEvaluation
    {
        public CandidateSnapshot s1;
        public CandidateSnapshot s2;
        public CandidateSnapshot s3;
        public bool diversityPassed;
        public bool equivalencePassed;
        public string diversityReason;
        public string equivalenceReason;
        public float qualitySpread;
        public int hotspotReuseCount;
    }

    [Header("PROVISIONAL candidate bank / triplet search")]
    [SerializeField, Range(1, 8)] private int candidateBankTopNPerSet = 4;
    [SerializeField, Min(0f)] private float maxQualityDropRatio = 0.15f;

    [Header("PROVISIONAL simplified cross-set diversity gate")]
    [SerializeField, Min(0f)] private float crossSetExclusionRadiusMeters = 0.35f;
    [SerializeField, Min(1)] private int maximumCrossSetSlotReuse = 2;
    [SerializeField, Min(0f)] private float sameColorCrossSetExclusionRadiusMeters = 0.6f;

    [Header("PROVISIONAL triplet equivalence tolerances")]
    [SerializeField, Min(0f)] private float maxRouteLengthDifferenceRatio = 0.15f;
    [SerializeField] private AagDifficultyDistanceMetric equivalenceDispersionMetric = AagDifficultyDistanceMetric.MeanNearestNeighborDistance;
    [SerializeField, Min(0f)] private float maxDispersionDifferenceRatio = 0.15f;
    [SerializeField, Min(0)] private int maxRoomCountDifferencePerUuid = 1;
    [SerializeField, Min(0)] private int maxZoneCountDifference = 1;
    [SerializeField, Range(0f, 1f)] private float maxPlacementTypeRatioDifference = 0.2f;
    [SerializeField, Min(0)] private int maxEntranceVisibleMarkerDifference = 2;
    [SerializeField, Min(0f)] private float maxMeanDiscoverabilityDifferenceRatio = 0.25f;
    [SerializeField, Min(0)] private int maxVisibleCountDifference = 0;

    [Header("Optional navigable route metric")]
    [Tooltip("Leave null until the experiment start point is calibrated. No route value is invented while null.")]
    [SerializeField] private Transform experimentStartPoint;
    [SerializeField, Min(0.01f)] private float navMeshSampleRadiusMeters = 0.5f;

    [Header("Unlock confirmation")]
    [SerializeField, Min(1f)] private float unlockConfirmationWindowSeconds = 10f;

    private CandidateBankFile candidateBank = new CandidateBankFile();
    private readonly Dictionary<string, CandidateSnapshot> recommendedTripletBySet = new Dictionary<string, CandidateSnapshot>(StringComparer.Ordinal);
    private bool tripletReady;
    private string tripletStatusReason = "Triplet not evaluated";
    private string pendingUnlockSetId;
    private float pendingUnlockExpiresAt;
    private float nextCandidateAllowedTime;

    private string CandidateBankJsonPath => Path.Combine(StorageFolderPath, CandidateBankJsonFileName);
    public bool IsTripletReadyForRuntime => tripletReady
        && SetIds.All(setId => recommendedTripletBySet.TryGetValue(setId, out var candidate) && CandidateMatchesConfirmed(candidate));

    private static string StateText(CandidateLifecycleState state)
    {
        return state.ToString().ToUpperInvariant();
    }

    private static CandidateLifecycleState ParseState(string value)
    {
        return Enum.TryParse(value, true, out CandidateLifecycleState parsed)
            ? parsed
            : CandidateLifecycleState.Stale;
    }

    private void LoadCandidateRobustnessState()
    {
        candidateBank = new CandidateBankFile();
        if (File.Exists(CandidateBankJsonPath))
        {
            try
            {
                var loaded = JsonUtility.FromJson<CandidateBankFile>(File.ReadAllText(CandidateBankJsonPath));
                if (loaded != null && loaded.candidates != null)
                {
                    candidateBank = loaded;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[AAG Candidate] Candidate bank load failed: {exception}");
            }
        }

        candidateBank.candidates ??= new List<CandidateSnapshot>();
        candidateBank.recommended_candidate_ids ??= new List<string>();
        candidateBank.schema_version = CandidateBankSchemaVersion;
        candidateBank.generator_version = GeneratorVersion;
        foreach (var candidate in candidateBank.candidates.Where(candidate => candidate != null))
        {
            candidate.placements ??= new List<AagPlacementRecord>();
            candidate.metrics ??= new CandidateMetrics();
            candidate.dependency ??= new CandidateDependencyFingerprint();
            candidate.dependency.previous_locked_candidate_ids ??= new List<string>();
            candidate.dependency.fp1_room_uuids ??= new List<string>();
        }
        candidateBank.candidates.RemoveAll(candidate => candidate == null);
        ImportLegacyLockedPlacementsAsStaleCandidates();
        RefreshCandidateFreshness();
        EvaluateTripletBank();
        RestoreCandidatePreviewsFromBank();
        SaveCandidateBank();
    }

    private void RestoreCandidatePreviewsFromBank()
    {
        foreach (var setId in SetIds)
        {
            if (confirmedBySet.ContainsKey(setId)) continue;
            var candidate = recommendedTripletBySet.TryGetValue(setId, out var recommended)
                ? recommended
                : candidateBank.candidates
                    .Where(item => string.Equals(item.set_id, setId, StringComparison.Ordinal)
                        && item.placements != null
                        && item.placements.Count == MarkerCountPerSet)
                    .OrderBy(item => PreviewStateOrder(ParseState(item.state)))
                    .ThenByDescending(item => item.metrics?.quality_score ?? 0f)
                    .ThenBy(item => item.candidate_id, StringComparer.Ordinal)
                    .FirstOrDefault();
            if (candidate != null)
            {
                unconfirmedBySet[setId] = CloneRecords(candidate.placements);
            }
        }
    }

    private static int PreviewStateOrder(CandidateLifecycleState state)
    {
        if (state == CandidateLifecycleState.Ready) return 0;
        if (state == CandidateLifecycleState.Stale) return 1;
        if (state == CandidateLifecycleState.Invalidated) return 2;
        if (state == CandidateLifecycleState.Draft) return 3;
        return 4;
    }

    private void ImportLegacyLockedPlacementsAsStaleCandidates()
    {
        foreach (var pair in confirmedBySet)
        {
            if (FindCandidateByPlacements(pair.Key, pair.Value) != null)
            {
                continue;
            }

            var seed = pair.Value.Count > 0 ? pair.Value[0].seed : GetSeed(pair.Key);
            var snapshot = CreateCandidateSnapshot(pair.Key, seed, pair.Value, false);
            snapshot.state = StateText(CandidateLifecycleState.Stale);
            snapshot.state_reason = "STALE: locked coordinates predate dependency fingerprint metadata; explicit revalidation is required";
            candidateBank.candidates.Add(snapshot);
        }
    }

    private CandidateSnapshot RegisterGeneratedCandidate(
        string setId,
        int seed,
        List<AagPlacementRecord> placements,
        bool individualReady,
        string reason)
    {
        var candidateId = BuildCandidateId(setId, seed, placements);
        var existing = candidateBank.candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.candidate_id, candidateId, StringComparison.Ordinal));
        var snapshot = existing ?? CreateCandidateSnapshot(setId, seed, placements, individualReady);
        snapshot.individual_ready = individualReady;
        snapshot.state = StateText(individualReady ? CandidateLifecycleState.Ready : CandidateLifecycleState.Draft);
        snapshot.state_reason = individualReady ? "Individual READY validation passed" : reason;
        snapshot.placements = CloneRecords(placements);
        snapshot.metrics = BuildCandidateMetrics(snapshot.placements);
        snapshot.dependency = BuildDependencyFingerprint(snapshot);
        if (existing == null)
        {
            candidateBank.candidates.Add(snapshot);
        }

        TrimCandidateBank(setId, snapshot.candidate_id);
        EvaluateTripletBank();
        SaveCandidateBank();
        Debug.Log($"[AAG Candidate] id={snapshot.candidate_id}; set={setId}; seed={seed}; state={snapshot.state}; "
            + $"fingerprint={ShortHash(snapshot.dependency.fingerprint)}; quality={snapshot.metrics.quality_score:F6}");
        return snapshot;
    }

    private CandidateSnapshot CreateCandidateSnapshot(
        string setId,
        int seed,
        List<AagPlacementRecord> placements,
        bool individualReady)
    {
        var snapshot = new CandidateSnapshot
        {
            candidate_id = BuildCandidateId(setId, seed, placements),
            set_id = setId,
            seed = seed,
            state = StateText(individualReady ? CandidateLifecycleState.Ready : CandidateLifecycleState.Draft),
            individual_ready = individualReady,
            placements = CloneRecords(placements),
        };
        snapshot.metrics = BuildCandidateMetrics(snapshot.placements);
        snapshot.dependency = BuildDependencyFingerprint(snapshot);
        return snapshot;
    }

    private void TrimCandidateBank(string setId, string preservedCandidateId)
    {
        var candidates = candidateBank.candidates
            .Where(candidate => string.Equals(candidate.set_id, setId, StringComparison.Ordinal))
            .OrderByDescending(candidate => ParseState(candidate.state) == CandidateLifecycleState.Locked)
            .ThenByDescending(candidate => candidate.metrics?.quality_score ?? 0f)
            .ThenBy(candidate => candidate.candidate_id, StringComparer.Ordinal)
            .ToList();
        var protectedIds = new HashSet<string>(candidates
            .Where(candidate => ParseState(candidate.state) == CandidateLifecycleState.Locked
                || string.Equals(candidate.candidate_id, preservedCandidateId, StringComparison.Ordinal))
            .Select(candidate => candidate.candidate_id), StringComparer.Ordinal);
        var keepCount = Mathf.Max(candidateBankTopNPerSet, protectedIds.Count);
        foreach (var candidate in candidates.Where(candidate => !protectedIds.Contains(candidate.candidate_id)).Take(Mathf.Max(0, keepCount - protectedIds.Count)))
        {
            protectedIds.Add(candidate.candidate_id);
        }
        foreach (var candidate in candidates.Where(candidate => !protectedIds.Contains(candidate.candidate_id)))
        {
            candidateBank.candidates.Remove(candidate);
        }
    }

    private int GetNextCandidateSeed(string setId)
    {
        var existingSeeds = new HashSet<int>(candidateBank.candidates
            .Where(candidate => string.Equals(candidate.set_id, setId, StringComparison.Ordinal))
            .Select(candidate => candidate.seed));
        unchecked
        {
            var seed = GetSeed(setId);
            while (existingSeeds.Contains(seed)) seed += 104729;
            return seed;
        }
    }

    public void RequestNextCandidate(string input, string controller, string button)
    {
        var setId = SelectedSetId;
        TryGetSetPlacements(setId, out var currentPlacements, out var currentConfirmed, out _);
        var current = currentPlacements == null ? null : FindCandidateByPlacements(setId, currentPlacements);
        var fromId = current?.candidate_id ?? "NONE";
        if (!isReady || !spaceValidator.IsValidationPassed)
        {
            LogNextCandidateResult(input, controller, button, setId, fromId, "NONE", "NONE", 0, 0, false, "FP1 authoring is not ready");
            return;
        }
        if (isGenerating)
        {
            LogNextCandidateResult(input, controller, button, setId, fromId, "NONE", "NONE", 0, 0, false, "another generate/switch operation is in progress");
            return;
        }
        if (Time.unscaledTime < nextCandidateAllowedTime)
        {
            LogNextCandidateResult(input, controller, button, setId, fromId, fromId, "BLOCKED", 0, 0, false, "Next Candidate debounce window is active");
            return;
        }
        if (currentConfirmed || ParseState(current?.state) == CandidateLifecycleState.Locked)
        {
            var reason = $"current candidate {fromId} is LOCKED; unlock {setId} before candidate switching or generation";
            SetHud($"{BuildCandidateHudHeader()}\nNEXT CANDIDATE BLOCKED\nUNLOCK CURRENT SET FIRST");
            LogNextCandidateResult(input, controller, button, setId, fromId, fromId, "BLOCKED", 0, 0, false, reason);
            LogBlockedUiInteraction("NEXT_CANDIDATE_LOCKED", reason);
            return;
        }

        nextCandidateAllowedTime = Time.unscaledTime + nextCandidateDebounceSeconds;

        var ordered = GetOrderedCandidates(setId);
        var currentIndex = current == null
            ? -1
            : ordered.FindIndex(candidate => string.Equals(candidate.candidate_id, current.candidate_id, StringComparison.Ordinal));
        if (currentIndex + 1 < ordered.Count)
        {
            StartCoroutine(ApplyBankCandidateRoutine(input, controller, button, current, ordered[currentIndex + 1]));
            return;
        }

        StartCoroutine(GenerateAndApplyNextCandidate(input, controller, button, current));
    }

    private IEnumerator ApplyBankCandidateRoutine(
        string input,
        string controller,
        string button,
        CandidateSnapshot current,
        CandidateSnapshot target)
    {
        BeginUiInteractionOperation("NEXT_CANDIDATE_BANK");
        try
        {
            yield return null;
            ApplyCandidatePreview(input, controller, button, current, target, "BANK");
        }
        finally
        {
            EndUiInteractionOperation("NEXT_CANDIDATE_BANK");
        }
    }

    private List<CandidateSnapshot> GetOrderedCandidates(string setId)
    {
        return candidateBank.candidates
            .Where(candidate => string.Equals(candidate.set_id, setId, StringComparison.Ordinal)
                && candidate.placements != null
                && candidate.placements.Count == MarkerCountPerSet)
            .OrderBy(candidate => candidate.seed)
            .ThenBy(candidate => candidate.candidate_id, StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerator GenerateAndApplyNextCandidate(
        string input,
        string controller,
        string button,
        CandidateSnapshot current)
    {
        BeginUiInteractionOperation("NEXT_CANDIDATE_GENERATE");
        var setId = SelectedSetId;
        var fromId = current?.candidate_id ?? "NONE";
        var seed = GetNextCandidateSeed(setId);
        try
        {
            SetHud($"{BuildCandidateHudHeader()}\nGENERATING NEXT CANDIDATE\nseed={seed}");
            Debug.Log($"[AAG Candidate] NextCandidate generation requested input={input} controller={controller} button={button} set={setId} fromCandidate={fromId} newSeed={seed}");
            yield return null;

            LogProvisionalSettings();
            if (!TryGenerateSet(setId, seed, out var placements, out var summary))
            {
                lastValidationSummary = summary;
                UpdateHud(false);
                LogNextCandidateResult(input, controller, button, setId, fromId, "NONE", "GENERATED", 0, 0, false, summary);
                RecordUiInteractionOperationResult(0, 0, false, summary);
                yield break;
            }

            var generated = FindCandidateByPlacements(setId, placements);
            if (generated == null)
            {
                lastValidationSummary = "generated coordinates were not retained in the candidate bank";
                UpdateHud(false);
                LogNextCandidateResult(input, controller, button, setId, fromId, "NONE", "GENERATED", 0, 0, false, lastValidationSummary);
                RecordUiInteractionOperationResult(0, 0, false, lastValidationSummary);
                yield break;
            }

            ApplyCandidatePreview(input, controller, button, current, generated, "GENERATED", summary);
        }
        finally
        {
            EndUiInteractionOperation("NEXT_CANDIDATE_GENERATE");
        }
    }

    private void ApplyCandidatePreview(
        string input,
        string controller,
        string button,
        CandidateSnapshot previous,
        CandidateSnapshot target,
        string source,
        string generatedSummary = null)
    {
        var setId = SelectedSetId;
        var fromId = previous?.candidate_id ?? "NONE";
        if (target == null || target.placements == null || target.placements.Count != MarkerCountPerSet)
        {
            LogNextCandidateResult(input, controller, button, setId, fromId, target?.candidate_id ?? "NONE", source, 0, 0, false, "target candidate does not contain 12 markers");
            RecordUiInteractionOperationResult(0, 0, false, "target candidate does not contain 12 markers");
            return;
        }

        InvalidateDependentSetsAfterChange(setId, true);
        unconfirmedBySet[setId] = CloneRecords(target.placements);
        EvaluateTripletBank();
        SaveCandidateBank();

        LogMarkerUiIdentity("before-next-candidate-marker-change");
        var removed = DestroyPreviewObjects();
        var spawned = 0;
        foreach (var placement in target.placements)
        {
            CreatePreviewMarker(placement);
            spawned++;
        }
        LogMarkerUiIdentity("after-next-candidate-marker-change");

        lastValidationSummary = string.IsNullOrWhiteSpace(generatedSummary)
            ? $"candidate={target.candidate_id}; seed={target.seed}; state={target.state}; {target.state_reason}"
            : generatedSummary;
        UpdateHud(false);
        LogNextCandidateResult(input, controller, button, setId, fromId, target.candidate_id, source, removed, spawned, true, target.state_reason);
        RecordUiInteractionOperationResult(removed, spawned, true, target.state_reason);
    }

    private static void LogNextCandidateResult(
        string input,
        string controller,
        string button,
        string setId,
        string fromId,
        string toId,
        string source,
        int removed,
        int spawned,
        bool success,
        string reason)
    {
        Debug.Log($"[AAG Candidate] NextCandidate input={input} controller={controller} button={button} set={setId} "
            + $"fromCandidate={fromId} toCandidate={toId} source={source} removed={removed} spawned={spawned} "
            + $"success={BoolText(success)} reason=\"{SanitizeLogReason(reason)}\"");
    }

    private CandidateSnapshot FindCandidateByPlacements(string setId, List<AagPlacementRecord> placements)
    {
        var positionHash = HashPlacementCoordinates(placements);
        return candidateBank.candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.set_id, setId, StringComparison.Ordinal)
            && string.Equals(HashPlacementCoordinates(candidate.placements), positionHash, StringComparison.Ordinal));
    }

    private CandidateSnapshot MarkCandidateLocked(string setId, List<AagPlacementRecord> placements)
    {
        var snapshot = FindCandidateByPlacements(setId, placements)
            ?? RegisterGeneratedCandidate(setId, placements[0].seed, placements, true, "Registered during lock");
        foreach (var candidate in candidateBank.candidates.Where(candidate =>
                     string.Equals(candidate.set_id, setId, StringComparison.Ordinal)
                     && ParseState(candidate.state) == CandidateLifecycleState.Locked))
        {
            candidate.state = StateText(candidate.individual_ready ? CandidateLifecycleState.Ready : CandidateLifecycleState.Draft);
            candidate.state_reason = "Replaced by another locked candidate";
        }

        snapshot.state = StateText(CandidateLifecycleState.Locked);
        snapshot.state_reason = "LOCKED by explicit user confirmation";
        snapshot.dependency = BuildDependencyFingerprint(snapshot);
        InvalidateDependentSetsAfterChange(setId, false);
        EvaluateTripletBank();
        SaveCandidateBank();
        return snapshot;
    }

    public void RequestUnlockSelectedSet()
    {
        if (!confirmedBySet.ContainsKey(SelectedSetId))
        {
            Debug.LogWarning($"[AAG Unlock] {SelectedSetId} is not LOCKED.");
            return;
        }

        var affected = string.Equals(SelectedSetId, AagExperimentSpaceCatalog.Fp1S1, StringComparison.Ordinal)
            ? "FP1-S2, FP1-S3 and the current triplet"
            : string.Equals(SelectedSetId, AagExperimentSpaceCatalog.Fp1S2, StringComparison.Ordinal)
                ? "FP1-S3 and the current triplet"
                : "the current triplet";
        var warning = $"Unlocking {SelectedSetId} will invalidate {affected}. Continue?";
        if (!string.Equals(pendingUnlockSetId, SelectedSetId, StringComparison.Ordinal)
            || Time.unscaledTime > pendingUnlockExpiresAt)
        {
            pendingUnlockSetId = SelectedSetId;
            pendingUnlockExpiresAt = Time.unscaledTime + unlockConfirmationWindowSeconds;
            SetHud($"PROVISIONAL {SelectedSetId}\n{warning}\nRepeat UNLOCK within {unlockConfirmationWindowSeconds:F0}s to continue");
            Debug.LogWarning($"[AAG Unlock] {warning} Repeat the explicit unlock action to confirm.");
            return;
        }

        pendingUnlockSetId = null;
        ExecuteUnlockCascade(SelectedSetId);
    }

    private void ExecuteUnlockCascade(string setId)
    {
        if (!confirmedBySet.TryGetValue(setId, out var unlockedPlacements))
        {
            return;
        }

        unconfirmedBySet[setId] = CloneRecords(unlockedPlacements);
        confirmedBySet.Remove(setId);
        var unlockedCandidate = FindCandidateByPlacements(setId, unlockedPlacements);
        if (unlockedCandidate != null)
        {
            unlockedCandidate.state = StateText(unlockedCandidate.individual_ready
                ? CandidateLifecycleState.Ready
                : CandidateLifecycleState.Draft);
            unlockedCandidate.state_reason = "Explicitly unlocked; coordinates preserved in candidate bank";
        }

        InvalidateDependentSetsAfterChange(setId, true);
        EvaluateTripletBank();
        SaveCandidateBank();
        SaveConfirmedPlacementsAndVerifyRoundTrip();
        ShowSelectedSet();
        Debug.LogWarning($"[AAG Unlock] {setId} unlocked; dependent coordinates were preserved but states were INVALIDATED; tripletReady=false");
        LogUiInteractionState("unlock-complete", "UNLOCK");
    }

    private void InvalidateDependentSetsAfterChange(string changedSetId, bool demoteLockedCoordinates)
    {
        tripletReady = false;
        tripletStatusReason = $"INVALIDATED because {changedSetId} changed";
        candidateBank.triplet_ready = false;
        candidateBank.triplet_reason = tripletStatusReason;
        recommendedTripletBySet.Clear();
        candidateBank.recommended_candidate_ids.Clear();
        var changedIndex = Array.IndexOf(SetIds, changedSetId);
        for (var index = changedIndex + 1; index < SetIds.Length; index++)
        {
            var dependentSetId = SetIds[index];
            foreach (var candidate in candidateBank.candidates.Where(candidate =>
                         string.Equals(candidate.set_id, dependentSetId, StringComparison.Ordinal)))
            {
                candidate.state = StateText(CandidateLifecycleState.Invalidated);
                candidate.state_reason = $"INVALIDATED because dependency {changedSetId} changed or unlocked";
            }

            if (demoteLockedCoordinates && confirmedBySet.TryGetValue(dependentSetId, out var coordinates))
            {
                unconfirmedBySet[dependentSetId] = CloneRecords(coordinates);
                confirmedBySet.Remove(dependentSetId);
            }
        }
    }

    public void RevalidateSelectedCandidate()
    {
        if (!TryGetSetPlacements(SelectedSetId, out var placements, out _, out _))
        {
            Debug.LogWarning($"[AAG Candidate] {SelectedSetId} has no candidate to revalidate.");
            return;
        }

        if (!EnsureActiveZoneGroupMap(out var zoneFailure))
        {
            Debug.LogError($"[AAG Candidate] {SelectedSetId} revalidation blocked: {zoneFailure}");
            return;
        }

        var snapshot = FindCandidateByPlacements(SelectedSetId, placements);
        if (snapshot == null)
        {
            snapshot = CreateCandidateSnapshot(SelectedSetId, placements[0].seed, placements, false);
            candidateBank.candidates.Add(snapshot);
        }

        var valid = ValidatePlacementSet(placements, out var reason, true);
        var remainsLocked = valid
            && confirmedBySet.TryGetValue(SelectedSetId, out var lockedCoordinates)
            && string.Equals(HashPlacementCoordinates(lockedCoordinates), HashPlacementCoordinates(placements), StringComparison.Ordinal);
        snapshot.individual_ready = valid;
        snapshot.state = StateText(valid
            ? remainsLocked ? CandidateLifecycleState.Locked : CandidateLifecycleState.Ready
            : CandidateLifecycleState.Draft);
        snapshot.state_reason = valid
            ? remainsLocked ? "Explicit revalidation passed; existing locked coordinates remain LOCKED" : "Explicit revalidation passed"
            : reason;
        snapshot.metrics = BuildCandidateMetrics(placements);
        snapshot.dependency = BuildDependencyFingerprint(snapshot);
        RefreshCandidateFreshness();
        EvaluateTripletBank();
        SaveCandidateBank();
        Debug.Log($"[AAG Candidate] Revalidate id={snapshot.candidate_id}; state={snapshot.state}; reason=\"{SanitizeLogReason(snapshot.state_reason)}\"");
        ShowSelectedSet();
    }

    private void RefreshCandidateFreshness()
    {
        foreach (var candidate in candidateBank.candidates)
        {
            if (candidate.dependency == null || string.IsNullOrEmpty(candidate.dependency.fingerprint))
            {
                candidate.state = StateText(CandidateLifecycleState.Stale);
                candidate.state_reason = "STALE: dependency fingerprint is missing";
                continue;
            }

            if (ParseState(candidate.state) == CandidateLifecycleState.Invalidated)
            {
                continue;
            }

            var current = BuildDependencyFingerprint(candidate);
            if (!string.Equals(current.fingerprint, candidate.dependency.fingerprint, StringComparison.Ordinal))
            {
                candidate.state = StateText(CandidateLifecycleState.Stale);
                candidate.state_reason = BuildFingerprintMismatchReason(candidate.dependency, current);
            }
        }
    }

    private static string BuildFingerprintMismatchReason(
        CandidateDependencyFingerprint generated,
        CandidateDependencyFingerprint current)
    {
        if (generated == null)
        {
            return "STALE: dependency fingerprint is missing; revalidate or regenerate";
        }
        var reasons = new List<string>();
        if (!string.Equals(generated.placement_settings_hash, current.placement_settings_hash, StringComparison.Ordinal)) reasons.Add("placement settings changed");
        if (!string.Equals(generated.mruk_export_identifier, current.mruk_export_identifier, StringComparison.Ordinal)) reasons.Add("MRUK export identifier changed");
        if (!string.Equals(generated.mruk_geometry_hash, current.mruk_geometry_hash, StringComparison.Ordinal)) reasons.Add("MRUK geometry changed");
        if (!string.Equals(generated.hotspot_source_anchor_hash, current.hotspot_source_anchor_hash, StringComparison.Ordinal)) reasons.Add("saved Spatial Anchor UUID/pose list changed");
        if (!string.Equals(generated.hotspot_catalog_hash, current.hotspot_catalog_hash, StringComparison.Ordinal)) reasons.Add("preferred hotspot catalog changed");
        if (!string.Equals(generated.referenced_world_coordinates_hash, current.referenced_world_coordinates_hash, StringComparison.Ordinal)) reasons.Add("referenced locked coordinates changed");
        if (!(generated.previous_locked_candidate_ids ?? new List<string>()).SequenceEqual(current.previous_locked_candidate_ids ?? new List<string>(), StringComparer.Ordinal)) reasons.Add("previous locked candidate IDs changed");
        if (!string.Equals(generated.generator_version, current.generator_version, StringComparison.Ordinal)) reasons.Add("generator version changed");
        return $"STALE: {string.Join(", ", reasons.DefaultIfEmpty("dependency fingerprint changed"))}; revalidate or regenerate";
    }

    private CandidateDependencyFingerprint BuildDependencyFingerprint(CandidateSnapshot snapshot)
    {
        RefreshPreferredHotspotCatalog("dependency-fingerprint", false);
        var previousSetIds = SetIds.Take(Mathf.Max(0, Array.IndexOf(SetIds, snapshot.set_id))).ToList();
        var lockedReferences = candidateBank.candidates
            .Where(candidate => previousSetIds.Contains(candidate.set_id, StringComparer.Ordinal)
                && ParseState(candidate.state) == CandidateLifecycleState.Locked)
            .OrderBy(candidate => candidate.set_id, StringComparer.Ordinal)
            .ToList();
        var dependency = new CandidateDependencyFingerprint
        {
            candidate_id = snapshot.candidate_id,
            seed = snapshot.seed,
            previous_locked_candidate_ids = lockedReferences.Select(candidate => candidate.candidate_id).ToList(),
            referenced_world_coordinates_hash = Sha256(string.Join("|", lockedReferences.Select(candidate => HashPlacementCoordinates(candidate.placements)))),
            placement_settings_hash = BuildPlacementSettingsHash(),
            fp1_room_uuids = AagExperimentSpaceCatalog.Fp1.RoomIds.Select(id => id.ToString()).OrderBy(value => value, StringComparer.Ordinal).ToList(),
            mruk_export_identifier = GetLatestMrukExportIdentifier(),
            mruk_geometry_hash = BuildMrukGeometryHash(),
            hotspot_source_anchor_hash = preferredHotspotSourceHash,
            hotspot_catalog_hash = preferredHotspotCatalogHash,
            generator_version = GeneratorVersion,
        };
        dependency.fingerprint = Sha256(string.Join("|",
            dependency.candidate_id,
            dependency.seed.ToString(CultureInfo.InvariantCulture),
            string.Join(",", dependency.previous_locked_candidate_ids),
            dependency.referenced_world_coordinates_hash,
            dependency.placement_settings_hash,
            string.Join(",", dependency.fp1_room_uuids),
            dependency.mruk_export_identifier,
            dependency.mruk_geometry_hash,
            dependency.hotspot_source_anchor_hash,
            dependency.hotspot_catalog_hash,
            dependency.generator_version));
        return dependency;
    }

    private string BuildPlacementSettingsHash()
    {
        var values = new[]
        {
            minimumWallDistanceMeters, minimumObjectDistanceMeters, floorGapMeters, floorAlignmentToleranceMeters,
            minimumObstacleDistanceMeters, generationSafetyMarginMeters, validationEpsilonMeters,
            minimumCornerDistanceMeters, maximumCornerDistanceMeters, maximumCornerAngleDegrees,
            minimumCornerAdjacentEdgeLengthMeters, minimumDoorwayDistanceMeters, minimumWalkingPathDistanceMeters,
            minimumAngularSeparationDegrees, candidateGridSpacingMeters, preferredWallBandWidthMeters,
            narrowHallAspectRatioThreshold, deterministicVariationWeight, cornerProximityWeight, wallBandWeight,
            doorwayDistanceWeight, walkingPathDistanceWeight, entranceVisibilityPenaltyWeight,
            sameRoomCoVisibilityPenaltyWeight, objectDistanceWeight, sameWallSegmentPenaltyWeight,
            zoneReusePenaltyWeight, roomDistributionWeight, placementTypeDiversityWeight,
            easyDiscoverabilityPenaltyWeight, previewMarkerDiameterMeters,
            manualHotspotAdjacencyMeters, hotspotAnchorLoadTimeoutSeconds,
        };
        var text = string.Join("|", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))
            + $"|{minimumRoomsUsed}|{zoneGridColumns}|{zoneGridRows}|{maximumMarkersPerZone}"
            + $"|{maxVisibleObjectsPerView}|{minimumDiscoverableObservationCount}|{observationGridSize}"
            + $"|{fp1S1Seed}|{fp1S2Seed}|{fp1S3Seed}|{SerializeZoneGroupOverrides()}";
        text += $"|manual={ManualMarkersPerSet}:{ProceduralMarkersPerSet}|room2Max={room2MaximumMarkers}|room3Max={room3MaximumMarkers}"
            + $"|catalog={preferredHotspotCatalogHash}|source={activeSourceCatalogKind}|recovery=SPATIAL_ANCHOR_LOCALIZED>MRUK_ROOM_LOCAL_RECOVERY>UNAVAILABLE|ignoreLegacyTest={BoolText(ignoreLegacyPlayerPrefsForPersistenceTest)}";
        return Sha256(text);
    }

    private string SerializeZoneGroupOverrides()
    {
        return string.Join(";", (zoneGroupOverrides ?? new List<ZoneGroupDefinition>())
            .Where(group => group != null)
            .OrderBy(group => group.zoneGroupId, StringComparer.Ordinal)
            .Select(group => $"{group.zoneGroupId}:{string.Join(",", (group.roomUuids ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal))}"));
    }

    private string GetLatestMrukExportIdentifier()
    {
        try
        {
            var folder = Path.Combine(Application.persistentDataPath, "AagRoomExports");
            if (!Directory.Exists(folder)) return "UNAVAILABLE";
            var file = new DirectoryInfo(folder).GetFiles("aag_room_coordinates_*.json")
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .ThenByDescending(candidate => candidate.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            return file == null ? "UNAVAILABLE" : $"{file.Name}:{Sha256(File.ReadAllBytes(file.FullName))}";
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[AAG Candidate] MRUK export identifier unavailable: {exception.Message}");
            return "UNAVAILABLE";
        }
    }

    private string BuildMrukGeometryHash()
    {
        var builder = new StringBuilder();
        foreach (var room in GetLoadedAllowedRooms())
        {
            builder.Append(room.Anchor.Uuid).Append('|');
            foreach (var floor in room.FloorAnchors.Where(floor => floor != null).OrderBy(floor => floor.Anchor.Uuid.ToString(), StringComparer.Ordinal))
            {
                builder.Append(floor.Anchor.Uuid).Append(':');
                foreach (var point in floor.PlaneBoundary2D ?? new List<Vector2>())
                {
                    var world = floor.transform.TransformPoint(new Vector3(point.x, point.y, 0f));
                    builder.Append(world.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(world.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(world.z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                }
            }
        }
        return Sha256(builder.ToString());
    }

    private static string BuildCandidateId(string setId, int seed, List<AagPlacementRecord> placements)
    {
        return $"{setId}-C{seed}-{ShortHash(HashPlacementCoordinates(placements))}";
    }

    private static string HashPlacementCoordinates(List<AagPlacementRecord> placements)
    {
        return Sha256(string.Join("|", placements
            .OrderBy(record => record.answer_marker_id, StringComparer.Ordinal)
            .Select(record => string.Format(CultureInfo.InvariantCulture,
                "{0}:{1:R},{2:R},{3:R}:{4}:{5}:{6}:{7:R}:{8:R}:{9}",
                record.answer_marker_id,
                record.world_x,
                record.world_y,
                record.world_z,
                record.room_uuid,
                record.placementSource,
                record.hotspot_uuid,
                record.hotspot_offset_meters,
                record.hotspot_sector_angle,
                record.spatial_slot_key))));
    }

    private static string Sha256(string text)
    {
        return Sha256(Encoding.UTF8.GetBytes(text ?? string.Empty));
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ShortHash(string value)
    {
        return string.IsNullOrEmpty(value) ? "none" : value.Substring(0, Mathf.Min(12, value.Length));
    }

    private CandidateMetrics BuildCandidateMetrics(List<AagPlacementRecord> placements)
    {
        var metrics = new CandidateMetrics();
        if (placements == null || placements.Count == 0)
        {
            return metrics;
        }

        var measured = CloneRecords(placements);
        var observationCache = new Dictionary<Guid, List<Vector3>>();
        if (!PopulatePlacementMetrics(measured, observationCache, out var metricFailure))
        {
            Debug.LogWarning($"[AAG Equivalence] Candidate metrics unavailable: {metricFailure}");
            return metrics;
        }

        metrics.mean_pairwise_distance = CalculateMeanPairwiseDistance(measured);
        metrics.mean_nearest_neighbor_distance = CalculateMeanNearestNeighborDistance(measured);
        metrics.maximum_pairwise_distance = CalculateMaximumPairwiseDistance(measured);
        metrics.convex_hull_coverage_report_only = CalculateHorizontalConvexHullArea(measured);
        metrics.room_counts = measured.GroupBy(record => record.room_uuid, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StringIntMetric { key = group.Key, value = group.Count() })
            .ToList();
        metrics.zone_group_counts = measured.GroupBy(record => ResolveZoneGroupId(record.room_uuid), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StringIntMetric { key = group.Key, value = group.Count() })
            .ToList();
        var manualRecords = measured.Where(IsManualHotspotRecord).ToList();
        metrics.manual_hotspot_count = manualRecords.Count;
        metrics.procedural_count = measured.Count(record => string.Equals(record.placementSource, ProceduralPlacementSource, StringComparison.Ordinal));
        metrics.unique_hotspot_count = manualRecords.Select(record => record.hotspot_uuid).Distinct(StringComparer.Ordinal).Count();
        metrics.reused_hotspot_count = metrics.manual_hotspot_count - metrics.unique_hotspot_count;
        metrics.mean_hotspot_distance = manualRecords.Count == 0 ? 0f : manualRecords.Average(record => record.hotspot_offset_meters);

        foreach (var record in measured)
        {
            if (!Guid.TryParse(record.room_uuid, out var roomId)
                || FindLoadedRoom(roomId) is not MRUKRoom room
                || !TryFindContainingFloor(room, ToVector3(record), out var floor, out var floorPoint))
            {
                continue;
            }

            var validCorners = GetValidCornerIndices(floor);
            var cornerIndex = GetNearestValidCornerIndex(floor, floorPoint, validCorners, out var cornerDistance);
            var type = ClassifyPlacementType(cornerIndex, cornerDistance, MeasureClearances(room, floor, floorPoint).wall);
            if (type == AagPlacementType.Corner) metrics.corner_count++;
            else if (type == AagPlacementType.WallBand) metrics.wall_band_count++;
            else metrics.peripheral_count++;
        }

        var visibility = EvaluateGlobalVisibilityAudit(measured, observationCache);
        metrics.entrance_visible_marker_count = visibility.entranceVisibleMarkerCount;
        metrics.max_visible_in_one_view = visibility.maximumVisible;
        metrics.mean_discoverable_observation_count = (float)measured.Average(record => record.discoverable_observation_count);
        if (TryCalculateMinimumNavigableRouteLength(measured, out var routeLength, out var routeReason))
        {
            metrics.route_metric_status = "AVAILABLE_NAVMESH_SHORTEST_PATH";
            metrics.route_length = routeLength;
        }
        else
        {
            metrics.route_metric_status = RouteMetricUnavailable + ":" + routeReason;
            metrics.route_length = 0f;
        }

        var entranceRate = metrics.entrance_visible_marker_count / (float)Mathf.Max(1, measured.Count);
        var visibilityExcess = Mathf.Max(0, metrics.max_visible_in_one_view - maxVisibleObjectsPerView);
        var dominantTypeRatio = Mathf.Max(metrics.corner_count, Mathf.Max(metrics.wall_band_count, metrics.peripheral_count))
            / (float)Mathf.Max(1, measured.Count);
        var discoveryPenalty = metrics.mean_discoverable_observation_count / Mathf.Max(1f, observationGridSize * observationGridSize * 8f);
        metrics.quality_score = 1f / (1f + visibilityExcess * 2f + entranceRate + dominantTypeRatio * 0.5f + discoveryPenalty * 0.25f);
        return metrics;
    }

    private string ResolveZoneGroupId(string roomUuid)
    {
        if (Guid.TryParse(roomUuid, out var roomId) && activeZoneGroupByRoom.TryGetValue(roomId, out var active))
        {
            return active;
        }

        var configured = (zoneGroupOverrides ?? new List<ZoneGroupDefinition>())
            .FirstOrDefault(group => group != null && (group.roomUuids ?? new List<string>())
                .Any(value => string.Equals(value, roomUuid, StringComparison.OrdinalIgnoreCase)));
        return configured != null && !string.IsNullOrWhiteSpace(configured.zoneGroupId)
            ? configured.zoneGroupId
            : "ROOM-" + (roomUuid?.Substring(0, Mathf.Min(8, roomUuid.Length)) ?? "unknown");
    }

    private bool TryCalculateMinimumNavigableRouteLength(
        List<AagPlacementRecord> placements,
        out float routeLength,
        out string reason)
    {
        routeLength = 0f;
        reason = string.Empty;
        if (experimentStartPoint == null)
        {
            reason = "START_POINT_UNAVAILABLE";
            return false;
        }

        var sourcePositions = new List<Vector3> { experimentStartPoint.position };
        sourcePositions.AddRange(placements.Select(ToVector3));
        var navPositions = new List<Vector3>(sourcePositions.Count);
        foreach (var source in sourcePositions)
        {
            if (!NavMesh.SamplePosition(source, out var hit, navMeshSampleRadiusMeters, NavMesh.AllAreas))
            {
                reason = "NAVMESH_SAMPLE_FAILED";
                return false;
            }
            navPositions.Add(hit.position);
        }

        var count = placements.Count;
        var distances = new float[count + 1, count + 1];
        for (var left = 0; left <= count; left++)
        {
            for (var right = left + 1; right <= count; right++)
            {
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(navPositions[left], navPositions[right], NavMesh.AllAreas, path)
                    || path.status != NavMeshPathStatus.PathComplete
                    || path.corners == null
                    || path.corners.Length < 2)
                {
                    reason = "NAVIGABLE_PATH_INCOMPLETE";
                    return false;
                }

                var length = 0f;
                for (var corner = 1; corner < path.corners.Length; corner++)
                {
                    length += Vector3.Distance(path.corners[corner - 1], path.corners[corner]);
                }
                distances[left, right] = distances[right, left] = length;
            }
        }

        var stateCount = 1 << count;
        var dynamic = new float[stateCount, count];
        for (var state = 0; state < stateCount; state++)
        {
            for (var marker = 0; marker < count; marker++) dynamic[state, marker] = float.PositiveInfinity;
        }
        for (var marker = 0; marker < count; marker++) dynamic[1 << marker, marker] = distances[0, marker + 1];
        for (var state = 1; state < stateCount; state++)
        {
            for (var last = 0; last < count; last++)
            {
                if ((state & (1 << last)) == 0 || float.IsPositiveInfinity(dynamic[state, last])) continue;
                for (var next = 0; next < count; next++)
                {
                    if ((state & (1 << next)) != 0) continue;
                    var nextState = state | (1 << next);
                    dynamic[nextState, next] = Mathf.Min(dynamic[nextState, next], dynamic[state, last] + distances[last + 1, next + 1]);
                }
            }
        }

        routeLength = float.PositiveInfinity;
        for (var last = 0; last < count; last++) routeLength = Mathf.Min(routeLength, dynamic[stateCount - 1, last]);
        if (float.IsInfinity(routeLength))
        {
            reason = "ROUTE_SOLVER_FAILED";
            return false;
        }
        return true;
    }

    private void EvaluateTripletBank()
    {
        RefreshCandidateFreshness();
        recommendedTripletBySet.Clear();
        candidateBank.recommended_candidate_ids.Clear();
        tripletReady = false;

        var eligible = new Dictionary<string, List<CandidateSnapshot>>(StringComparer.Ordinal);
        foreach (var setId in SetIds)
        {
            eligible[setId] = candidateBank.candidates
                .Where(candidate => string.Equals(candidate.set_id, setId, StringComparison.Ordinal)
                    && candidate.individual_ready
                    && (ParseState(candidate.state) == CandidateLifecycleState.Ready
                        || ParseState(candidate.state) == CandidateLifecycleState.Locked))
                .OrderByDescending(candidate => candidate.metrics?.quality_score ?? 0f)
                .ThenBy(candidate => candidate.candidate_id, StringComparer.Ordinal)
                .Take(candidateBankTopNPerSet)
                .ToList();
        }

        TripletEvaluation best = null;
        foreach (var s1 in eligible[SetIds[0]])
        foreach (var s2 in eligible[SetIds[1]])
        foreach (var s3 in eligible[SetIds[2]])
        {
            var evaluation = EvaluateTriplet(s1, s2, s3);
            if (!evaluation.diversityPassed || !evaluation.equivalencePassed) continue;
            if (best == null || evaluation.hotspotReuseCount < best.hotspotReuseCount
                || (evaluation.hotspotReuseCount == best.hotspotReuseCount && evaluation.qualitySpread < best.qualitySpread)
                || (evaluation.hotspotReuseCount == best.hotspotReuseCount && Mathf.Approximately(evaluation.qualitySpread, best.qualitySpread)
                    && string.CompareOrdinal(TripletKey(evaluation), TripletKey(best)) < 0))
            {
                best = evaluation;
            }
        }

        if (best != null)
        {
            foreach (var candidate in new[] { best.s1, best.s2, best.s3 })
            {
                recommendedTripletBySet[candidate.set_id] = candidate;
                candidateBank.recommended_candidate_ids.Add(candidate.candidate_id);
            }
            var allLocked = recommendedTripletBySet.Values.All(candidate =>
                ParseState(candidate.state) == CandidateLifecycleState.Locked
                && CandidateMatchesConfirmed(candidate));
            tripletReady = allLocked;
            tripletStatusReason = allLocked
                ? "TRIPLET_READY: diversity/equivalence/individual hard validation passed and all recommended candidates are LOCKED"
                : "Recommended triplet passes gates but TRIPLET_READY=false until all three recommended candidates are LOCKED";
            LogTripletEvaluation(best, tripletReady);
        }
        else
        {
            var haveAllIndividual = SetIds.All(setId => eligible[setId].Count > 0);
            tripletStatusReason = haveAllIndividual
                ? "No feasible triplet although individual READY candidates exist"
                : "At least one set has no current READY/LOCKED candidate";
            Debug.LogWarning($"[AAG Equivalence] routeLength={RouteMetricUnavailable}(report-only), dispersion=NOT_EVALUATED, roomDistribution=NOT_EVALUATED, visibility=NOT_EVALUATED, tripletReady=false; reason=\"{tripletStatusReason}\"");
            if (haveAllIndividual)
            {
                Debug.LogWarning("[AAG Triplet] feasibleTriplet=false while individualReadyCandidates=true; generate/revalidate alternative candidates instead of accepting a greedy sequence.");
            }
        }

        LogCandidateBankQualityWarnings(eligible);
        candidateBank.triplet_ready = tripletReady;
        candidateBank.triplet_reason = tripletStatusReason;
    }

    private TripletEvaluation EvaluateTriplet(CandidateSnapshot s1, CandidateSnapshot s2, CandidateSnapshot s3)
    {
        var evaluation = new TripletEvaluation { s1 = s1, s2 = s2, s3 = s3 };
        var triplet = new[] { s1, s2, s3 };
        evaluation.diversityPassed = EvaluateSimplifiedDiversityGate(triplet, out evaluation.diversityReason);
        evaluation.equivalencePassed = EvaluateEquivalenceGate(triplet, out evaluation.equivalenceReason);
        var qualities = triplet.Select(candidate => candidate.metrics?.quality_score ?? 0f).ToList();
        evaluation.qualitySpread = qualities.Max() - qualities.Min();
        var manualHotspots = triplet.SelectMany(candidate => candidate.placements).Where(IsManualHotspotRecord).ToList();
        evaluation.hotspotReuseCount = manualHotspots.Count
            - manualHotspots.Select(record => record.hotspot_uuid).Distinct(StringComparer.Ordinal).Count();
        return evaluation;
    }

    private bool EvaluateSimplifiedDiversityGate(CandidateSnapshot[] triplet, out string reason)
    {
        var failures = new List<string>();
        var minimumCrossSet = float.PositiveInfinity;
        var minimumSameColor = float.PositiveInfinity;
        for (var leftSet = 0; leftSet < triplet.Length; leftSet++)
        for (var rightSet = leftSet + 1; rightSet < triplet.Length; rightSet++)
        {
            foreach (var left in triplet[leftSet].placements)
            foreach (var right in triplet[rightSet].placements)
            {
                var distance = HorizontalDistance(ToVector3(left), ToVector3(right));
                minimumCrossSet = Mathf.Min(minimumCrossSet, distance);
                if (string.Equals(left.color, right.color, StringComparison.OrdinalIgnoreCase))
                {
                    minimumSameColor = Mathf.Min(minimumSameColor, distance);
                }
            }
        }

        if (minimumCrossSet + validationEpsilonMeters < crossSetExclusionRadiusMeters)
            failures.Add($"crossSetExclusion={minimumCrossSet:F6}<{crossSetExclusionRadiusMeters:F6}");
        if (minimumSameColor + validationEpsilonMeters < sameColorCrossSetExclusionRadiusMeters)
            failures.Add($"sameColorExclusion={minimumSameColor:F6}<{sameColorCrossSetExclusionRadiusMeters:F6}");

        var maximumSlotReuse = triplet.SelectMany(candidate => candidate.placements)
            .Select(GetPlacementSlotKey)
            .Where(slot => !string.IsNullOrEmpty(slot))
            .GroupBy(slot => slot, StringComparer.Ordinal)
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();
        if (maximumSlotReuse > maximumCrossSetSlotReuse)
            failures.Add($"slotReuse={maximumSlotReuse}>{maximumCrossSetSlotReuse}");
        if (!EvaluateMixedTripletGate(triplet, out var mixedReason))
            failures.Add(mixedReason);

        var meanNearest = triplet.Select(candidate => candidate.metrics.mean_nearest_neighbor_distance).ToArray();
        var cellOverlap = CalculateSpatialCellOverlapReportOnly(triplet);
        var locationSimilarity = CalculateGreedyLocationSimilarityReportOnly(triplet);
        var hungarianDistance = CalculateHungarianAssignmentDistanceReportOnly(triplet);
        var typeDifference = CalculatePlacementTypeDifferenceReportOnly(triplet);
        Debug.Log($"[AAG Diversity Report] gateOnly=[crossSetExclusion,slotReuse,sameColorExclusion]; "
            + $"meanNearestNeighbor=[{string.Join(",", meanNearest.Select(value => value.ToString("F6", CultureInfo.InvariantCulture)))}](report-only); "
            + $"spatialCellOverlap={cellOverlap:F6}(report-only); overallLocationSimilarity={locationSimilarity:F6}(report-only); "
            + $"hungarianAssignmentDistance={hungarianDistance:F6}(report-only); "
            + $"placementTypeDifference={typeDifference:F6}(report-only); reportOnlyMetricsBlockReady=false");
        reason = failures.Count == 0
            ? $"passed(minCrossSet={minimumCrossSet:F6},minSameColor={minimumSameColor:F6},maxSlotReuse={maximumSlotReuse},{mixedReason})"
            : string.Join(",", failures);
        return failures.Count == 0;
    }

    private string GetPlacementSlotKey(AagPlacementRecord record)
    {
        if (!Guid.TryParse(record.room_uuid, out var roomId)
            || FindLoadedRoom(roomId) is not MRUKRoom room
            || !TryFindContainingFloor(room, ToVector3(record), out var floor, out var floorPoint))
            return null;
        var validCorners = GetValidCornerIndices(floor);
        var corner = GetNearestValidCornerIndex(floor, floorPoint, validCorners, out var cornerDistance);
        var type = ClassifyPlacementType(corner, cornerDistance, MeasureClearances(room, floor, floorPoint).wall);
        var segment = GetNearestBoundarySegmentIndex(floor.PlaneBoundary2D, floorPoint);
        if (type == AagPlacementType.Corner) return $"{floor.Anchor.Uuid}:CORNER:{corner}";
        if (type == AagPlacementType.WallBand) return $"{floor.Anchor.Uuid}:WALL:SEGMENT:{segment}";
        return null;
    }

    private bool EvaluateEquivalenceGate(CandidateSnapshot[] triplet, out string reason)
    {
        var failures = new List<string>();
        var routeAvailable = triplet.All(candidate => candidate.metrics.route_metric_status.StartsWith("AVAILABLE", StringComparison.Ordinal));
        if (routeAvailable)
        {
            var routes = triplet.Select(candidate => candidate.metrics.route_length).ToArray();
            if (RelativeSpread(routes) > maxRouteLengthDifferenceRatio)
                failures.Add($"routeLengthRatio={RelativeSpread(routes):F6}>{maxRouteLengthDifferenceRatio:F6}");
        }

        var dispersion = triplet.Select(candidate => GetRepresentativeDispersion(candidate.metrics))
            .ToArray();
        if (RelativeSpread(dispersion) > maxDispersionDifferenceRatio)
            failures.Add($"dispersionRatio={RelativeSpread(dispersion):F6}>{maxDispersionDifferenceRatio:F6}");

        foreach (var candidate in triplet)
        {
            var counts = candidate.metrics.room_counts.ToDictionary(item => item.key, item => item.value, StringComparer.OrdinalIgnoreCase);
            counts.TryGetValue(Room2Uuid.ToString(), out var room2Count);
            counts.TryGetValue(Room3Uuid.ToString(), out var room3Count);
            if (room2Count > room2MaximumMarkers)
                failures.Add($"{candidate.set_id}:Room2={room2Count}>{room2MaximumMarkers}");
            if (room3Count > room3MaximumMarkers)
                failures.Add($"{candidate.set_id}:Room3={room3Count}>{room3MaximumMarkers}");
        }
        CompareIntegerMetricMaps(triplet.Select(candidate => candidate.metrics.room_counts).ToArray(), maxRoomCountDifferencePerUuid, "room", failures);
        CompareIntegerMetricMaps(triplet.Select(candidate => candidate.metrics.zone_group_counts).ToArray(), maxZoneCountDifference, "zone", failures);

        var total = (float)MarkerCountPerSet;
        foreach (var selector in new Func<CandidateMetrics, int>[] { metric => metric.corner_count, metric => metric.wall_band_count, metric => metric.peripheral_count })
        {
            var ratios = triplet.Select(candidate => selector(candidate.metrics) / total).ToArray();
            if (ratios.Max() - ratios.Min() > maxPlacementTypeRatioDifference)
                failures.Add($"placementTypeRatioDiff={ratios.Max() - ratios.Min():F6}>{maxPlacementTypeRatioDifference:F6}");
        }

        var entrances = triplet.Select(candidate => candidate.metrics.entrance_visible_marker_count).ToArray();
        if (entrances.Max() - entrances.Min() > maxEntranceVisibleMarkerDifference)
            failures.Add($"entranceVisibleDiff={entrances.Max() - entrances.Min()}>{maxEntranceVisibleMarkerDifference}");
        var discoverability = triplet.Select(candidate => candidate.metrics.mean_discoverable_observation_count).ToArray();
        if (RelativeSpread(discoverability) > maxMeanDiscoverabilityDifferenceRatio)
            failures.Add($"discoverabilityRatio={RelativeSpread(discoverability):F6}>{maxMeanDiscoverabilityDifferenceRatio:F6}");
        var maximumVisible = triplet.Select(candidate => candidate.metrics.max_visible_in_one_view).ToArray();
        if (maximumVisible.Max() - maximumVisible.Min() > maxVisibleCountDifference)
            failures.Add($"maxVisibleDiff={maximumVisible.Max() - maximumVisible.Min()}>{maxVisibleCountDifference}");
        foreach (var candidate in triplet)
        {
            if (candidate.metrics.manual_hotspot_count != ManualMarkersPerSet || candidate.metrics.procedural_count != ProceduralMarkersPerSet)
                failures.Add($"{candidate.set_id}:manualMetric={candidate.metrics.manual_hotspot_count}:{candidate.metrics.procedural_count}");
        }

        reason = failures.Count == 0 ? "passed" : string.Join(",", failures.Distinct());
        return failures.Count == 0;
    }

    private float GetRepresentativeDispersion(CandidateMetrics metrics)
    {
        if (equivalenceDispersionMetric == AagDifficultyDistanceMetric.MeanPairwiseDistance)
            return metrics.mean_pairwise_distance;
        if (equivalenceDispersionMetric == AagDifficultyDistanceMetric.MaximumPairwiseDistance)
            return metrics.maximum_pairwise_distance;
        return metrics.mean_nearest_neighbor_distance;
    }

    private static void CompareIntegerMetricMaps(
        List<StringIntMetric>[] maps,
        int tolerance,
        string label,
        List<string> failures)
    {
        var keys = maps.SelectMany(map => map).Select(item => item.key).Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var values = maps.Select(map => map.FirstOrDefault(item => string.Equals(item.key, key, StringComparison.Ordinal))?.value ?? 0).ToArray();
            if (values.Max() - values.Min() > tolerance)
                failures.Add($"{label}={key}:diff={values.Max() - values.Min()}>{tolerance}");
        }
    }

    private void LogTripletEvaluation(TripletEvaluation evaluation, bool ready)
    {
        var triplet = new[] { evaluation.s1, evaluation.s2, evaluation.s3 };
        var routes = triplet.All(candidate => candidate.metrics.route_metric_status.StartsWith("AVAILABLE", StringComparison.Ordinal))
            ? string.Join(",", triplet.Select(candidate => $"{candidate.set_id}:{candidate.metrics.route_length:F6}"))
            : RouteMetricUnavailable + "(report-only)";
        var dispersion = string.Join(",", triplet.Select(candidate => $"{candidate.set_id}:pairwise={candidate.metrics.mean_pairwise_distance:F6}/nearest={candidate.metrics.mean_nearest_neighbor_distance:F6}/hull={candidate.metrics.convex_hull_coverage_report_only:F6}(report-only)"));
        var distribution = string.Join(",", triplet.Select(candidate => $"{candidate.set_id}:rooms={FormatMetricMap(candidate.metrics.room_counts)}/zones={FormatMetricMap(candidate.metrics.zone_group_counts)}/types={candidate.metrics.corner_count}|{candidate.metrics.wall_band_count}|{candidate.metrics.peripheral_count}"));
        var visibility = string.Join(",", triplet.Select(candidate => $"{candidate.set_id}:entrance={candidate.metrics.entrance_visible_marker_count}/meanObs={candidate.metrics.mean_discoverable_observation_count:F3}/maxView={candidate.metrics.max_visible_in_one_view}"));
        var mixed = string.Join(",", triplet.Select(candidate => $"{candidate.set_id}:manual={candidate.metrics.manual_hotspot_count}/procedural={candidate.metrics.procedural_count}/unique={candidate.metrics.unique_hotspot_count}/reused={candidate.metrics.reused_hotspot_count}/meanHotspotDistance={candidate.metrics.mean_hotspot_distance:F3}"));
        Debug.Log($"[AAG Equivalence] routeLength={routes}, dispersion=[{dispersion}], roomDistribution=[{distribution}], visibility=[{visibility}], mixed=[{mixed}], "
            + $"diversity={evaluation.diversityReason}, equivalence={evaluation.equivalenceReason}, hotspotReuse={evaluation.hotspotReuseCount}, qualitySpread={evaluation.qualitySpread:F6}, tripletReady={BoolText(ready)}");
    }

    private static string FormatMetricMap(List<StringIntMetric> metrics)
    {
        return string.Join("|", metrics.Select(item => $"{item.key}:{item.value}"));
    }

    private void LogCandidateBankQualityWarnings(Dictionary<string, List<CandidateSnapshot>> eligible)
    {
        var averages = SetIds.ToDictionary(
            setId => setId,
            setId => eligible[setId].Count == 0 ? 0f : eligible[setId].Average(candidate => candidate.metrics.quality_score),
            StringComparer.Ordinal);
        var priorAverage = (averages[SetIds[0]] + averages[SetIds[1]]) * 0.5f;
        var dropRatio = priorAverage <= 0f ? 0f : (priorAverage - averages[SetIds[2]]) / priorAverage;
        if (dropRatio > maxQualityDropRatio)
        {
            Debug.LogWarning($"[AAG Triplet] S3 quality warning: S1={averages[SetIds[0]]:F6}, S2={averages[SetIds[1]]:F6}, "
                + $"S3={averages[SetIds[2]]:F6}, dropRatio={dropRatio:F6}, maxQualityDropRatio={maxQualityDropRatio:F6}");
        }

        var maximum = averages.Values.Max();
        foreach (var pair in averages.Where(pair => maximum > 0f && pair.Value < maximum))
        {
            Debug.Log($"[AAG Triplet] set={pair.Key}; quality={pair.Value:F6}; bestSetQuality={maximum:F6}; scoreDifference={maximum - pair.Value:F6}; ratioDifference={(maximum - pair.Value) / maximum:F6}");
        }

        if (SetIds.All(setId => eligible[setId].Count > 0))
        {
            var meanMetrics = SetIds.ToDictionary(
                setId => setId,
                setId => new
                {
                    routeAvailable = eligible[setId].All(candidate => candidate.metrics.route_metric_status.StartsWith("AVAILABLE", StringComparison.Ordinal)),
                    route = eligible[setId].Average(candidate => candidate.metrics.route_length),
                    dispersion = eligible[setId].Average(candidate => GetRepresentativeDispersion(candidate.metrics)),
                    entrance = eligible[setId].Average(candidate => candidate.metrics.entrance_visible_marker_count),
                    maxView = eligible[setId].Average(candidate => candidate.metrics.max_visible_in_one_view),
                    observations = eligible[setId].Average(candidate => candidate.metrics.mean_discoverable_observation_count),
                },
                StringComparer.Ordinal);
            if (meanMetrics.Values.All(value => value.routeAvailable))
            {
                var worstRoute = meanMetrics.OrderByDescending(pair => pair.Value.route).First();
                var bestRoute = meanMetrics.Values.Min(value => value.route);
                Debug.Log($"[AAG Triplet] route bank comparison: worstSet={worstRoute.Key}; meanRoute={worstRoute.Value.route:F6}; bestMeanRoute={bestRoute:F6}; difference={worstRoute.Value.route - bestRoute:F6}");
            }

            var centerDispersion = meanMetrics.Values.Average(value => value.dispersion);
            var largestDispersionDeviation = meanMetrics.OrderByDescending(pair => Math.Abs(pair.Value.dispersion - centerDispersion)).First();
            Debug.Log($"[AAG Triplet] dispersion bank comparison: set={largestDispersionDeviation.Key}; mean={largestDispersionDeviation.Value.dispersion:F6}; allSetMean={centerDispersion:F6}; absoluteDifference={Math.Abs(largestDispersionDeviation.Value.dispersion - centerDispersion):F6}");
            var worstVisibility = meanMetrics.OrderByDescending(pair => pair.Value.maxView).ThenByDescending(pair => pair.Value.entrance).First();
            Debug.Log($"[AAG Triplet] visibility bank comparison: worstSet={worstVisibility.Key}; meanMaxView={worstVisibility.Value.maxView:F6}; meanEntranceVisible={worstVisibility.Value.entrance:F6}; meanDiscoverableObservations={worstVisibility.Value.observations:F6}");
        }
    }

    private bool CanLockSelectedCandidate(List<AagPlacementRecord> placements, out string reason)
    {
        if (!EnsureActiveZoneGroupMap(out reason))
        {
            reason = "Physical zone-group validation unavailable: " + reason;
            return false;
        }

        var candidate = FindCandidateByPlacements(SelectedSetId, placements);
        if (candidate == null)
        {
            reason = "Candidate metadata is missing; regenerate or explicitly revalidate before LOCKED";
            return false;
        }

        var current = BuildDependencyFingerprint(candidate);
        if (!string.Equals(candidate.dependency?.fingerprint, current.fingerprint, StringComparison.Ordinal))
        {
            candidate.state = StateText(CandidateLifecycleState.Stale);
            candidate.state_reason = BuildFingerprintMismatchReason(candidate.dependency, current);
            SaveCandidateBank();
        }

        var state = ParseState(candidate.state);
        if (!candidate.individual_ready || state != CandidateLifecycleState.Ready)
        {
            reason = $"state={candidate.state}; {candidate.state_reason}; STALE/INVALIDATED/DRAFT candidates cannot be LOCKED or used at runtime";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private bool EnsureActiveZoneGroupMap(out string failure)
    {
        failure = string.Empty;
        var rooms = GetLoadedAllowedRooms();
        if (activeZoneGroupByRoom.Count == rooms.Count && rooms.Count == AagExperimentSpaceCatalog.Fp1.RoomIds.Count)
            return true;
        if (rooms.Count != AagExperimentSpaceCatalog.Fp1.RoomIds.Count)
        {
            failure = $"loaded allowed rooms={rooms.Count}, expected={AagExperimentSpaceCatalog.Fp1.RoomIds.Count}";
            return false;
        }

        if (!TryBuildManualHotspotZoneMap(rooms, out var map, out failure))
            return false;
        activeZoneGroupByRoom.Clear();
        foreach (var pair in map) activeZoneGroupByRoom[pair.Key] = pair.Value;
        Debug.Log($"[AAG Manual Hotspot Zone Diagnosis] rebuilt catalog zoneGroup map for lock/revalidation; rooms={activeZoneGroupByRoom.Count}");
        return true;
    }

    private string GetSelectedCandidateStateText()
    {
        return GetSelectedCandidateLifecycleState();
    }

    private string GetSelectedCandidateLifecycleState()
    {
        if (!TryGetSetPlacements(SelectedSetId, out var placements, out _, out _)) return "EMPTY";
        var candidate = FindCandidateByPlacements(SelectedSetId, placements);
        return candidate?.state ?? "UNTRACKED";
    }

    private string BuildCandidateHudHeader()
    {
        TryGetSetPlacements(SelectedSetId, out var placements, out _, out _);
        var candidate = placements == null ? null : FindCandidateByPlacements(SelectedSetId, placements);
        var candidateId = candidate?.candidate_id ?? "NONE";
        var seed = candidate != null
            ? candidate.seed.ToString(CultureInfo.InvariantCulture)
            : placements != null && placements.Count > 0
                ? placements[0].seed.ToString(CultureInfo.InvariantCulture)
                : "NONE";
        var state = candidate?.state ?? (placements == null ? "EMPTY" : "UNTRACKED");
        return $"SET ID: {SelectedSetId} | TRIPLET_READY={BoolText(tripletReady)}\n"
            + $"CANDIDATE ID: {candidateId}\nSEED: {seed} | STATE: {state}";
    }

    private void RollbackCandidateLock(CandidateSnapshot candidate)
    {
        if (candidate == null) return;
        candidate.state = StateText(candidate.individual_ready ? CandidateLifecycleState.Ready : CandidateLifecycleState.Draft);
        candidate.state_reason = "LOCKED rollback after JSON/CSV round-trip failure";
        EvaluateTripletBank();
        SaveCandidateBank();
    }

    private static float RelativeSpread(IEnumerable<float> source)
    {
        var values = source.ToArray();
        if (values.Length == 0) return 0f;
        var maximum = values.Max();
        var minimum = values.Min();
        return maximum <= 0.000001f ? 0f : (maximum - minimum) / maximum;
    }

    private static string TripletKey(TripletEvaluation evaluation)
    {
        return $"{evaluation.s1.candidate_id}|{evaluation.s2.candidate_id}|{evaluation.s3.candidate_id}";
    }

    private float CalculateSpatialCellOverlapReportOnly(CandidateSnapshot[] triplet)
    {
        var cellSize = Mathf.Max(0.1f, crossSetExclusionRadiusMeters);
        var sets = triplet.Select(candidate => new HashSet<string>(candidate.placements.Select(record =>
            $"{Mathf.FloorToInt(record.world_x / cellSize)}:{Mathf.FloorToInt(record.world_z / cellSize)}"), StringComparer.Ordinal)).ToArray();
        var overlaps = new List<float>();
        for (var left = 0; left < sets.Length; left++)
        for (var right = left + 1; right < sets.Length; right++)
        {
            var union = new HashSet<string>(sets[left], StringComparer.Ordinal);
            union.UnionWith(sets[right]);
            var intersection = new HashSet<string>(sets[left], StringComparer.Ordinal);
            intersection.IntersectWith(sets[right]);
            overlaps.Add(union.Count == 0 ? 0f : intersection.Count / (float)union.Count);
        }
        return overlaps.Count == 0 ? 0f : overlaps.Average();
    }

    private static float CalculateGreedyLocationSimilarityReportOnly(CandidateSnapshot[] triplet)
    {
        var similarities = new List<float>();
        for (var left = 0; left < triplet.Length; left++)
        for (var right = left + 1; right < triplet.Length; right++)
        {
            similarities.Add(triplet[left].placements.Average(record => triplet[right].placements.Min(other =>
                HorizontalDistance(ToVector3(record), ToVector3(other)))));
        }
        return similarities.Count == 0 ? 0f : similarities.Average();
    }

    private static float CalculateHungarianAssignmentDistanceReportOnly(CandidateSnapshot[] triplet)
    {
        var pairDistances = new List<float>();
        for (var leftSet = 0; leftSet < triplet.Length; leftSet++)
        for (var rightSet = leftSet + 1; rightSet < triplet.Length; rightSet++)
        {
            var left = triplet[leftSet].placements;
            var right = triplet[rightSet].placements;
            if (left.Count != right.Count || left.Count == 0) continue;
            var states = 1 << right.Count;
            var dynamic = Enumerable.Repeat(float.PositiveInfinity, states).ToArray();
            dynamic[0] = 0f;
            for (var mask = 0; mask < states; mask++)
            {
                if (float.IsPositiveInfinity(dynamic[mask])) continue;
                var leftIndex = CountBits(mask);
                if (leftIndex >= left.Count) continue;
                for (var rightIndex = 0; rightIndex < right.Count; rightIndex++)
                {
                    if ((mask & (1 << rightIndex)) != 0) continue;
                    var next = mask | (1 << rightIndex);
                    dynamic[next] = Mathf.Min(dynamic[next], dynamic[mask]
                        + HorizontalDistance(ToVector3(left[leftIndex]), ToVector3(right[rightIndex])));
                }
            }
            pairDistances.Add(dynamic[states - 1] / left.Count);
        }
        return pairDistances.Count == 0 ? 0f : pairDistances.Average();
    }

    private static int CountBits(int value)
    {
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }

    private static float CalculatePlacementTypeDifferenceReportOnly(CandidateSnapshot[] triplet)
    {
        var values = new List<float>();
        for (var left = 0; left < triplet.Length; left++)
        for (var right = left + 1; right < triplet.Length; right++)
        {
            values.Add((Mathf.Abs(triplet[left].metrics.corner_count - triplet[right].metrics.corner_count)
                + Mathf.Abs(triplet[left].metrics.wall_band_count - triplet[right].metrics.wall_band_count)
                + Mathf.Abs(triplet[left].metrics.peripheral_count - triplet[right].metrics.peripheral_count)) / (float)(MarkerCountPerSet * 2));
        }
        return values.Count == 0 ? 0f : values.Average();
    }

    private static float CalculateHorizontalConvexHullArea(List<AagPlacementRecord> placements)
    {
        var points = placements.Select(record => new Vector2(record.world_x, record.world_z)).Distinct().OrderBy(point => point.x).ThenBy(point => point.y).ToList();
        if (points.Count < 3) return 0f;
        var hull = new List<Vector2>();
        foreach (var point in points)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0f) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        var lowerCount = hull.Count;
        for (var index = points.Count - 2; index >= 0; index--)
        {
            var point = points[index];
            while (hull.Count > lowerCount && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0f) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        hull.RemoveAt(hull.Count - 1);
        var area = 0f;
        for (var index = 0; index < hull.Count; index++)
        {
            var next = hull[(index + 1) % hull.Count];
            area += hull[index].x * next.y - next.x * hull[index].y;
        }
        return Mathf.Abs(area) * 0.5f;
    }

    private static float Cross(Vector2 origin, Vector2 left, Vector2 right)
    {
        return (left.x - origin.x) * (right.y - origin.y) - (left.y - origin.y) * (right.x - origin.x);
    }

    private CandidateSnapshot GetExportCandidate(string setId)
    {
        if (recommendedTripletBySet.TryGetValue(setId, out var recommended)
            && confirmedBySet.TryGetValue(setId, out var recommendedCoordinates)
            && string.Equals(HashPlacementCoordinates(recommended.placements), HashPlacementCoordinates(recommendedCoordinates), StringComparison.Ordinal))
        {
            return recommended;
        }

        return candidateBank.candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.set_id, setId, StringComparison.Ordinal)
            && ParseState(candidate.state) == CandidateLifecycleState.Locked
            && confirmedBySet.TryGetValue(setId, out var coordinates)
            && string.Equals(HashPlacementCoordinates(candidate.placements), HashPlacementCoordinates(coordinates), StringComparison.Ordinal));
    }

    private bool CandidateMatchesConfirmed(CandidateSnapshot candidate)
    {
        return candidate != null
            && confirmedBySet.TryGetValue(candidate.set_id, out var coordinates)
            && coordinates.Count == MarkerCountPerSet
            && string.Equals(HashPlacementCoordinates(candidate.placements), HashPlacementCoordinates(coordinates), StringComparison.Ordinal);
    }

    private string GetPlacementExportStatus()
    {
        if (tripletReady && GetConfirmedRowCount() == MarkerCountPerSet * SetIds.Length)
        {
            return "TRIPLET_READY_RUNTIME_ONLY_PLAN_COORDINATES_UNAVAILABLE";
        }

        return "AUTHORING_ONLY_NOT_FOR_RUNTIME_OR_FINAL";
    }

    private bool ValidateCandidateRobustnessSettings()
    {
        return candidateBankTopNPerSet >= 1
            && maxQualityDropRatio >= 0f
            && crossSetExclusionRadiusMeters >= 0f
            && maximumCrossSetSlotReuse >= 1
            && sameColorCrossSetExclusionRadiusMeters >= 0f
            && maxRouteLengthDifferenceRatio >= 0f
            && maxDispersionDifferenceRatio >= 0f
            && maxRoomCountDifferencePerUuid >= 0
            && maxZoneCountDifference >= 0
            && maxPlacementTypeRatioDifference >= 0f
            && maxEntranceVisibleMarkerDifference >= 0
            && maxMeanDiscoverabilityDifferenceRatio >= 0f
            && maxVisibleCountDifference >= 0
            && navMeshSampleRadiusMeters > 0f
            && unlockConfirmationWindowSeconds >= 1f
            && nextCandidateDebounceSeconds >= 0f;
    }

    private void LogCandidateRobustnessSettings()
    {
        Debug.Log($"[AAG Equivalence] PROVISIONAL settings: topN={candidateBankTopNPerSet}; maxQualityDropRatio={maxQualityDropRatio:F6}; "
            + $"diversity=[crossSetExclusion={crossSetExclusionRadiusMeters:F6}m,slotReuseMax={maximumCrossSetSlotReuse},sameColorExclusion={sameColorCrossSetExclusionRadiusMeters:F6}m]; "
            + $"equivalence=[routeRatio={maxRouteLengthDifferenceRatio:F6},dispersionMetric={equivalenceDispersionMetric},dispersionRatio={maxDispersionDifferenceRatio:F6},"
            + $"roomDiff={maxRoomCountDifferencePerUuid},zoneDiff={maxZoneCountDifference},typeRatioDiff={maxPlacementTypeRatioDifference:F6},"
            + $"entranceDiff={maxEntranceVisibleMarkerDifference},discoverabilityRatio={maxMeanDiscoverabilityDifferenceRatio:F6},maxViewDiff={maxVisibleCountDifference}]; "
            + $"routeStart={(experimentStartPoint == null ? RouteMetricUnavailable : experimentStartPoint.name)}; navMeshSampleRadius={navMeshSampleRadiusMeters:F6}m; "
            + $"unlockConfirmationWindow={unlockConfirmationWindowSeconds:F3}s; nextCandidateDebounce={nextCandidateDebounceSeconds:F3}s");
    }

    private void SaveCandidateBank()
    {
        try
        {
            Directory.CreateDirectory(StorageFolderPath);
            candidateBank.schema_version = CandidateBankSchemaVersion;
            candidateBank.generator_version = GeneratorVersion;
            candidateBank.triplet_ready = tripletReady;
            candidateBank.triplet_reason = tripletStatusReason;
            File.WriteAllText(CandidateBankJsonPath, JsonUtility.ToJson(candidateBank, true), Encoding.UTF8);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Candidate] Candidate bank save failed: {exception}");
        }
    }
}
