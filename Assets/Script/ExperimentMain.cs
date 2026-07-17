using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// FP1 session coordinator. Measurement, AAG decisions, and file ownership live
/// in their visible sibling managers under _Experiment.
/// </summary>
[DisallowMultipleComponent]
public sealed class ExperimentMain : MonoBehaviour
{
    private const float VgMapHalfSpanMeters = 7f; // 40% wider view than the previous 5 m half-span.

    private enum SessionState
    {
        Idle,
        Loading,
        Running,
        Ending,
    }

    private sealed class TargetState
    {
        public string objectId;
        public string color;
        public string roomUuid;
        public string roomId;
        public Transform transform;
        public bool approximate;
        public bool delivered;
    }

    [Serializable]
    private sealed class ObjectEventLog
    {
        public string type;
        public float t;
        public string objectId;
        public string towerId;
        public string color;
        public string roomUuid;
        public string roomId;
        public string sourceMode;
        public float x;
        public float y;
        public float z;
        public int carrying;
    }

    [Serializable]
    private sealed class SessionEndLog
    {
        public string type = "session_end";
        public float t;
        public string endedAtUtc;
        public string reason;
        public int deliveredCount;
        public int targetCount;
        public float runningSeconds;
    }

    [Header("Single Configuration Asset")]
    [SerializeField] private Fp1ExperimentConfig config;

    [Header("Existing Scene Services")]
    [SerializeField] private AnchorLoader anchorLoader;
    [SerializeField] private SpatialAnchorManager spatialAnchorManager;
    [SerializeField] private Transform headTransform;

    [Header("Visible Experiment Managers")]
    [SerializeField] private BehaviorMetrics behaviorMetrics;
    [SerializeField] private AAGGuide aagGuide;
    [SerializeField] private LoggingManager loggingManager;
    [SerializeField] private FixedTowerManager fixedTowerManager;
    [SerializeField] private FixedTowerAnchorLoader fixedTowerAnchorLoader;
    [SerializeField] private IncidentalObjectManager incidentalObjectManager;
    [SerializeField] private IncidentalAnchorLoader incidentalAnchorLoader;

    [Header("Scene-owned Outputs")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private GameObject hudRoot;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private RectTransform vgPanel;
    [SerializeField] private AudioClip correctDeliveryClip;

    private SessionState state = SessionState.Idle;
    private int participantIndex;
    private int setIndex;
    private int guideIndex;
    private float runClockStart;
    private float sampleAccumulator;
    private float nextDecisionTime;
    private string sessionId = string.Empty;
    private string currentSetId = string.Empty;
    private ExperimentGuideMode currentGuideMode;

    private readonly List<GameObject> approximatedObjects = new List<GameObject>();
    private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
    private readonly Dictionary<string, RectTransform> vgTargetMarkers = new Dictionary<string, RectTransform>(StringComparer.Ordinal);
    private Coroutine spawnConfirmationRoutine;

    private TextMeshProUGUI operatorText;
    private RectTransform vgUserMarker;
    private Sprite vgCircleSprite;
    private Texture2D vgCircleTexture;
    private bool ownsCorrectDeliveryClip;

    private string SelectedParticipantId => SafeChoice(config != null ? config.participantIds : null, participantIndex, "P01");
    private string SelectedSetId => SafeChoice(config != null ? config.setIds : null, setIndex, AagExperimentSpaceCatalog.Fp1S1);
    private ExperimentGuideMode SelectedGuideMode => (ExperimentGuideMode)(guideIndex % 3);
    private float SessionTime => loggingManager != null ? loggingManager.SessionTime : 0f;
    private float RunningTime => state == SessionState.Running ? Time.realtimeSinceStartup - runClockStart : 0f;
    public Fp1ExperimentConfig Configuration => config;

    private void Awake()
    {
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<Fp1ExperimentConfig>();
            Debug.LogWarning("[ExperimentMain] FP1ExperimentConfig missing; using in-memory defaults.", this);
        }
        config.RebuildLookups();

        if (anchorLoader == null) anchorLoader = FindFirstObjectByType<AnchorLoader>();
        if (spatialAnchorManager == null) spatialAnchorManager = FindFirstObjectByType<SpatialAnchorManager>();
        if (headTransform == null)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) headTransform = rig.centerEyeAnchor;
        }

        var experimentRoot = transform.parent != null ? transform.parent : transform;
        if (behaviorMetrics == null) behaviorMetrics = experimentRoot.GetComponentInChildren<BehaviorMetrics>(true);
        if (aagGuide == null) aagGuide = experimentRoot.GetComponentInChildren<AAGGuide>(true);
        if (loggingManager == null) loggingManager = experimentRoot.GetComponentInChildren<LoggingManager>(true);
        if (audioSource == null) audioSource = experimentRoot.GetComponentInChildren<AudioSource>(true);
        if (fixedTowerManager == null)
            fixedTowerManager = GetComponent<FixedTowerManager>() ?? gameObject.AddComponent<FixedTowerManager>();
        if (fixedTowerAnchorLoader == null)
            fixedTowerAnchorLoader = GetComponent<FixedTowerAnchorLoader>() ?? gameObject.AddComponent<FixedTowerAnchorLoader>();
        if (incidentalObjectManager == null)
            incidentalObjectManager = GetComponent<IncidentalObjectManager>() ?? gameObject.AddComponent<IncidentalObjectManager>();
        if (incidentalAnchorLoader == null)
            incidentalAnchorLoader = GetComponent<IncidentalAnchorLoader>() ?? gameObject.AddComponent<IncidentalAnchorLoader>();
        fixedTowerAnchorLoader.Initialize(spatialAnchorManager != null ? spatialAnchorManager.anchorPrefab : null);
        incidentalAnchorLoader.Initialize(spatialAnchorManager != null ? spatialAnchorManager.anchorPrefab : null);
        fixedTowerManager.Initialize(config, this);

        if (behaviorMetrics == null || aagGuide == null || loggingManager == null || audioSource == null)
        {
            Debug.LogError(
                "[ExperimentMain] _Experiment must contain BehaviorMetrics, AAGGuide, LoggingManager, and AudioSource.",
                this);
            enabled = false;
            return;
        }

        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        if (correctDeliveryClip == null)
        {
            correctDeliveryClip = CreateCorrectDeliveryClip();
            ownsCorrectDeliveryClip = correctDeliveryClip != null;
        }
        CreateHud();
        behaviorMetrics.ResetSession();
        aagGuide.StopAndReset();
        aagGuide.gameObject.SetActive(false);
        RefreshHud("Ready");
    }

    private void Update()
    {
        if (state == SessionState.Idle)
        {
            HandleOperatorInput();
            return;
        }

        loggingManager.Tick();
        if (state != SessionState.Running) return;

        sampleAccumulator += Time.unscaledDeltaTime;
        var interval = Mathf.Max(0.02f, config.trackIntervalSeconds);
        while (sampleAccumulator >= interval)
        {
            sampleAccumulator -= interval;
            behaviorMetrics.Sample(interval, currentGuideMode);
            if (currentGuideMode == ExperimentGuideMode.VG)
                UpdateVg(behaviorMetrics.LatestHeadPosition);
        }

        if (SessionTime >= nextDecisionTime)
        {
            nextDecisionTime += Mathf.Max(0.5f, config.decisionIntervalSeconds);
            var snapshot = behaviorMetrics.Evaluate(currentGuideMode);
            if (currentGuideMode == ExperimentGuideMode.AAG)
                aagGuide.Evaluate(snapshot, BuildAagTargets());
        }

        if (RunningTime >= config.sessionTimeLimitSeconds)
            EndSession("time_limit");

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Escape)) EndSession("operator_abort");
#endif
    }

    private void HandleOperatorInput()
    {
        if (OVRInput.GetDown(OVRInput.RawButton.X)) CycleParticipant();
        if (OVRInput.GetDown(OVRInput.RawButton.Y)) CycleSet();
        if (OVRInput.GetDown(OVRInput.RawButton.B)) CycleGuide();
        if (OVRInput.GetDown(OVRInput.RawButton.A)) StartSelectedSession();

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.P)) CycleParticipant();
        if (Input.GetKeyDown(KeyCode.S)) CycleSet();
        if (Input.GetKeyDown(KeyCode.G)) CycleGuide();
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)) StartSelectedSession();
#endif
    }

    public void CycleParticipant()
    {
        if (state != SessionState.Idle) return;
        participantIndex = NextIndex(participantIndex, config.participantIds);
        RefreshHud("Participant selected");
    }

    public void CycleSet()
    {
        if (state != SessionState.Idle) return;
        setIndex = NextIndex(setIndex, config.setIds);
        RefreshHud("Set selected");
    }

    public void CycleGuide()
    {
        if (state != SessionState.Idle) return;
        guideIndex = (guideIndex + 1) % 3;
        RefreshHud("Guide selected");
    }

    public void StartSelectedSession()
    {
        if (state != SessionState.Idle) return;
        StartCoroutine(StartSessionRoutine());
    }

    private IEnumerator StartSessionRoutine()
    {
        state = SessionState.Loading;
        PrepareForNewSession();

        currentSetId = SelectedSetId;
        currentGuideMode = SelectedGuideMode;
        sessionId = BuildSessionId(SelectedParticipantId, currentSetId, currentGuideMode);
        if (!loggingManager.OpenSession(sessionId, SelectedParticipantId, currentSetId, currentGuideMode, config))
        {
            sessionId = string.Empty;
            state = SessionState.Idle;
            RefreshHud("START BLOCKED: log_open_failed");
            yield break;
        }
        behaviorMetrics.BeginSession(config, headTransform, loggingManager);
        aagGuide.BeginSession(config, behaviorMetrics, loggingManager);
        RefreshHud("Loading anchors...");

        if (anchorLoader == null || spatialAnchorManager == null)
        {
            AbortLoading("missing_anchor_service");
            yield break;
        }

        var manifest = AagManualAnchorSetStore.LoadOrCreate();
        var set = manifest.sets.FirstOrDefault(value => string.Equals(value.set_id, currentSetId, StringComparison.Ordinal));
        if (set == null)
        {
            AbortLoading("set_not_found");
            yield break;
        }
        if (set.anchors == null || set.anchors.Count != 12)
        {
            AbortLoading($"set_requires_12_targets_actual_{set.anchors?.Count ?? 0}");
            yield break;
        }

        var entriesByUuid = new Dictionary<Guid, AagManualAnchorEntry>();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid) || uuid == Guid.Empty)
            {
                AbortLoading($"invalid_uuid_{entry.marker_id}");
                yield break;
            }
            entriesByUuid[uuid] = entry;
        }

        if (!fixedTowerManager.TryBuildAnchorMap(out var towersByUuid, out var towerConfigurationFailure))
        {
            AbortLoading(towerConfigurationFailure);
            yield break;
        }
        if (!incidentalObjectManager.TryBuildMap(
                currentSetId,
                out var incidentalsByUuid,
                out var incidentalConfigurationFailure))
        {
            AbortLoading(incidentalConfigurationFailure);
            yield break;
        }

        if (entriesByUuid.Keys.Any(towersByUuid.ContainsKey))
        {
            AbortLoading("fixed_tower_uuid_overlaps_target_uuid");
            yield break;
        }
        if (entriesByUuid.Keys.Any(incidentalsByUuid.ContainsKey)
            || towersByUuid.Keys.Any(incidentalsByUuid.ContainsKey))
        {
            AbortLoading("incidental_uuid_overlaps_stone_or_tower_uuid");
            yield break;
        }

        anchorLoader.ClearLoadedAnchors();
        anchorLoader.ClearPrefabOverrides();
        foreach (var pair in entriesByUuid)
            anchorLoader.SetPrefabForUuid(pair.Key, spatialAnchorManager.GetAnchorPrefabForColor(pair.Value.color));

        var requestedUuids = entriesByUuid.Keys.ToArray();
        WriteSystem("load_started",
            $"set={currentSetId}; targets={entriesByUuid.Count}; requested={requestedUuids.Length}; " +
            "loader=OBJECT_ONLY; forceQuestStoreQuery=true");
        anchorLoader.LoadAnchorsByUuid(requestedUuids, $"EXPERIMENT:{currentSetId}", true);
        while (anchorLoader.IsReadOnlyLoadInProgress)
        {
            RefreshHud($"Loading {anchorLoader.LocalizedRequestedCount}/{requestedUuids.Length}...");
            yield return null;
        }

        var missing = new List<AagManualAnchorEntry>();
        foreach (var pair in entriesByUuid)
        {
            if (anchorLoader.TryGetLocalizedAnchor(pair.Key, out var anchor)
                && !anchorLoader.LocalizationFailuresReadOnly.ContainsKey(pair.Key))
            {
                RegisterTarget(pair.Value, anchor.transform, false);
            }
            else
            {
                missing.Add(pair.Value);
                var detail = anchorLoader.LocalizationFailuresReadOnly.TryGetValue(pair.Key, out var reason)
                    ? reason
                    : "not_returned";
                WriteSystem("anchor_missing", $"object={pair.Value.marker_id}; uuid={pair.Key}; reason={detail}");
            }
        }

        if (missing.Count > 0 && set.HasOffsetCapture)
            SpawnApproximateTargets(set, missing);

        if (targets.Count != set.anchors.Count)
        {
            AbortLoading($"target_count_mismatch_ready_{targets.Count}_expected_{set.anchors.Count}");
            yield break;
        }

        WriteSystem("tower_load_started",
            $"fixedTowers={towersByUuid.Count}; requested={towersByUuid.Count}; loader=TOWER_ONLY");
        fixedTowerAnchorLoader.Load(towersByUuid);
        while (fixedTowerAnchorLoader.IsLoading)
        {
            RefreshHud($"Loading towers {fixedTowerAnchorLoader.LoadedCount}/{towersByUuid.Count}...");
            yield return null;
        }

        if (!fixedTowerManager.TrySpawnLocalizedTowers(fixedTowerAnchorLoader, towersByUuid, out var towerSpawnFailure))
        {
            foreach (var pair in towersByUuid)
            {
                if (!fixedTowerAnchorLoader.Failures.TryGetValue(pair.Key, out var reason)) continue;
                WriteSystem("tower_anchor_missing",
                    $"tower={pair.Value.towerId}; uuid={pair.Key}; reason={reason}");
            }
            AbortLoading(towerSpawnFailure);
            yield break;
        }

        foreach (var pair in towersByUuid)
        {
            var sourceMode = fixedTowerAnchorLoader.ApproximateReasons.TryGetValue(pair.Key, out var reason)
                ? $"APPROX; reason={reason}"
                : "REAL";
            WriteSystem("tower_loaded", $"tower={pair.Value.towerId}; mode={sourceMode}");
        }

        WriteSystem("incidental_load_started",
            $"set={currentSetId}; objects={incidentalsByUuid.Count}; requested={incidentalsByUuid.Count}; loader=INCIDENTAL_ONLY");
        incidentalAnchorLoader.Load(incidentalsByUuid);
        while (incidentalAnchorLoader.IsLoading)
        {
            RefreshHud($"Loading incidental objects {incidentalAnchorLoader.LoadedCount}/{incidentalsByUuid.Count}...");
            yield return null;
        }
        if (!incidentalObjectManager.TrySpawn(
                incidentalAnchorLoader,
                incidentalsByUuid,
                out var incidentalSpawnFailure))
        {
            foreach (var pair in incidentalsByUuid)
            {
                if (!incidentalAnchorLoader.Failures.TryGetValue(pair.Key, out var reason)) continue;
                WriteSystem("incidental_anchor_missing",
                    $"object={pair.Value.object_id}; uuid={pair.Key}; reason={reason}");
            }
            AbortLoading(incidentalSpawnFailure);
            yield break;
        }
        foreach (var pair in incidentalsByUuid)
        {
            var sourceMode = incidentalAnchorLoader.ApproximateReasons.TryGetValue(pair.Key, out var reason)
                ? $"APPROX; reason={reason}"
                : "REAL";
            WriteSystem("incidental_loaded",
                $"object={pair.Value.object_id}; prefab={pair.Value.prefab_resource_path}; mode={sourceMode}");
        }

        runClockStart = Time.realtimeSinceStartup;
        sampleAccumulator = 0f;
        nextDecisionTime = SessionTime + Mathf.Max(0.5f, config.decisionIntervalSeconds);
        state = SessionState.Running;
        ApplyGuideVisibility();
        ShowParticipantSpawnConfirmation(towersByUuid.Count);
        WriteSystem(
            "session_running",
            $"targets={targets.Count}; fixedTowers={towersByUuid.Count}; " +
            $"towerReal={fixedTowerAnchorLoader.RealLoadedCount}; towerApprox={fixedTowerAnchorLoader.ApproximateCount}; " +
            $"incidentals={incidentalsByUuid.Count}; incidentalReal={incidentalAnchorLoader.RealLoadedCount}; " +
            $"incidentalApprox={incidentalAnchorLoader.ApproximateCount}; " +
            $"guide={currentGuideMode}");
    }

    private void PrepareForNewSession()
    {
        StopGuideOutputs();
        loggingManager.CloseSession();
        ClearSpawnedObjects();
        behaviorMetrics.ResetSession();
        sampleAccumulator = 0f;
    }

    private void ClearSpawnedObjects()
    {
        foreach (var instance in approximatedObjects)
            if (instance != null) Destroy(instance);
        approximatedObjects.Clear();
        fixedTowerManager?.ClearRuntimeContents();
        fixedTowerAnchorLoader?.ClearLoadedAnchors();
        incidentalObjectManager?.ClearRuntimeContents();
        incidentalAnchorLoader?.ClearLoadedAnchors();
        targets.Clear();
        ClearVgMarkers();
        if (anchorLoader != null)
        {
            anchorLoader.ClearPrefabOverrides();
            anchorLoader.ClearLoadedAnchors();
        }
    }

    private void RegisterTarget(AagManualAnchorEntry entry, Transform targetTransform, bool approximate)
    {
        if (entry == null || targetTransform == null) return;
        var movableTransform = ConfigureStoneInteraction(targetTransform);
        var roomUuid = behaviorMetrics.ResolveRoomUuid(movableTransform.position);
        var mapping = config.FindRoom(roomUuid);
        var id = string.IsNullOrWhiteSpace(entry.marker_id) ? entry.anchor_uuid : entry.marker_id;
        var bridge = movableTransform.GetComponent<ExperimentObject>()
            ?? movableTransform.gameObject.AddComponent<ExperimentObject>();
        bridge.Initialize(this, id, false, entry.color);

        foreach (var canvas in targetTransform.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
        targetTransform.name = $"{currentSetId} {id} {(approximate ? "APPROX" : "REAL")}";
        targets[id] = new TargetState
        {
            objectId = id,
            color = entry.color ?? string.Empty,
            roomUuid = roomUuid,
            roomId = mapping != null ? mapping.roomId : RoomFallbackId(roomUuid),
            transform = movableTransform,
            approximate = approximate,
        };
        WriteObjectEvent("target_loaded", id, string.Empty);
        WriteSystem("target_loaded", $"object={id}; mode={(approximate ? "APPROX" : "REAL")}; roomUuid={roomUuid}");
    }

    // The old Cube child remains in the anchor prefab for backwards compatibility.
    // Select only the active, visible rock and reconnect every grab reference to it.
    private static Transform ConfigureStoneInteraction(Transform targetRoot)
    {
        var grabbables = targetRoot.GetComponentsInChildren<Grabbable>(true);
        var rockGrabbable = grabbables
            .Where(candidate => candidate != null && candidate.gameObject.activeInHierarchy)
            .OrderByDescending(candidate => candidate.GetComponent<Renderer>() != null)
            .FirstOrDefault();

        if (rockGrabbable == null)
        {
            var visibleCollider = targetRoot.GetComponentsInChildren<Collider>(true)
                .FirstOrDefault(candidate => candidate != null && candidate.gameObject.activeInHierarchy);
            return visibleCollider != null ? visibleCollider.transform : targetRoot;
        }

        var rockTransform = rockGrabbable.transform;
        var rockBody = rockTransform.GetComponent<Rigidbody>()
            ?? rockTransform.GetComponentInChildren<Rigidbody>(true);
        if (rockBody != null)
        {
            rockBody.useGravity = false;
            // Match the known-good blue stone: it moves only while selected and
            // returns to kinematic state at the release pose.
            rockBody.isKinematic = true;
            rockGrabbable.InjectOptionalRigidbody(rockBody);
            rockGrabbable.InjectOptionalTargetTransform(rockTransform);
            rockGrabbable.InjectOptionalKinematicWhileSelected(true);
            rockGrabbable.InjectOptionalThrowWhenUnselected(true);
        }

        foreach (var handGrab in targetRoot.GetComponentsInChildren<HandGrabInteractable>(true))
        {
            if (handGrab == null) continue;
            var isRockInteractable = handGrab.transform == rockTransform;
            handGrab.enabled = isRockInteractable;
            if (isRockInteractable)
            {
                handGrab.InjectOptionalPointableElement(rockGrabbable);
                if (rockBody != null) handGrab.InjectRigidbody(rockBody);
            }
        }

        foreach (var grabbable in grabbables)
        {
            if (grabbable != null && grabbable != rockGrabbable) grabbable.enabled = false;
        }

        // Never render or collide with the legacy cube, even when an older prefab
        // variant has it enabled.
        foreach (var child in targetRoot.GetComponentsInChildren<Transform>(true))
        {
            if (!string.Equals(child.name, "Cube", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var renderer in child.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            foreach (var collider in child.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        }

        EnvironmentDepthOcclusion.ApplyToRenderers(rockTransform);
        return rockTransform;
    }

    private void SpawnApproximateTargets(AagManualAnchorSetRecord set, IReadOnlyCollection<AagManualAnchorEntry> missing)
    {
        var references = new List<(AagManualAnchorEntry entry, Transform transform)>();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
            if (anchorLoader.TryGetLocalizedAnchor(uuid, out var anchor)
                && !anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid))
                references.Add((entry, anchor.transform));
        }
        if (references.Count == 0)
        {
            WriteSystem("approximation_failed", "no_localized_reference_anchor");
            return;
        }

        foreach (var entry in missing)
        {
            var reference = references
                .OrderBy(candidate => (candidate.entry.CapturedPosition - entry.CapturedPosition).sqrMagnitude)
                .First();
            var deltaRotation = reference.transform.rotation * Quaternion.Inverse(reference.entry.CapturedRotation);
            var position = reference.transform.position
                + deltaRotation * (entry.CapturedPosition - reference.entry.CapturedPosition);
            var rotation = deltaRotation * entry.CapturedRotation;
            var prefab = spatialAnchorManager.GetAnchorPrefabForColor(entry.color);
            if (prefab == null) continue;

            var prefabObject = prefab.gameObject;
            var wasActive = prefabObject.activeSelf;
            GameObject instance;
            try
            {
                prefabObject.SetActive(false);
                instance = Instantiate(prefabObject, position, rotation);
            }
            finally
            {
                prefabObject.SetActive(wasActive);
            }
            var anchorComponent = instance.GetComponent<OVRSpatialAnchor>();
            if (anchorComponent != null) DestroyImmediate(anchorComponent);
            instance.SetActive(true);
            approximatedObjects.Add(instance);
            RegisterTarget(entry, instance.transform, true);
        }
    }

    private IReadOnlyCollection<AagGuideTarget> BuildAagTargets()
    {
        return targets.Values.Select(target => new AagGuideTarget
        {
            objectId = target.objectId,
            roomUuid = target.roomUuid,
            roomId = target.roomId,
            delivered = target.delivered,
        }).ToArray();
    }

    public void NotifyObjectGrabbed(ExperimentObject experimentObject)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        if (!behaviorMetrics.TryBeginCarry(experimentObject.ObjectId, out var rejectionReason))
        {
            WriteSystem("grab_rejected", $"object={experimentObject.ObjectId}; reason={rejectionReason}");
            return;
        }
        WriteObjectEvent("grab", experimentObject.ObjectId, string.Empty);
    }

    public void NotifyObjectDropped(ExperimentObject experimentObject)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        behaviorMetrics.EndCarry(experimentObject.ObjectId);
        WriteObjectEvent("drop", experimentObject.ObjectId, string.Empty);
    }

    public void NotifyObjectDelivered(ExperimentObject experimentObject, string towerId)
    {
        TryNotifyObjectDelivered(experimentObject, towerId);
    }

    public void NotifyWrongTower(ExperimentObject experimentObject, string towerId)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        WriteObjectEvent("wrong_tower", experimentObject.ObjectId, towerId);
    }

    public bool TryNotifyObjectDelivered(ExperimentObject experimentObject, string towerId)
    {
        if (state != SessionState.Running || experimentObject == null) return false;
        if (!targets.TryGetValue(experimentObject.ObjectId, out var target) || target.delivered) return false;
        target.delivered = true;
        behaviorMetrics.ForceClearCarry();
        WriteObjectEvent("delivered", experimentObject.ObjectId, towerId);
        if (audioSource != null && correctDeliveryClip != null)
            audioSource.PlayOneShot(correctDeliveryClip, 0.9f);
        if (targets.Values.All(value => value.delivered)) EndSession("all_targets_delivered");
        return true;
    }

    private void WriteObjectEvent(string eventType, string objectId, string towerId)
    {
        targets.TryGetValue(objectId, out var target);
        var position = target?.transform != null ? target.transform.position : Vector3.zero;
        loggingManager.Write(new ObjectEventLog
        {
            type = eventType,
            t = SessionTime,
            objectId = objectId,
            towerId = towerId,
            color = target?.color ?? string.Empty,
            roomUuid = target?.roomUuid ?? string.Empty,
            roomId = target?.roomId ?? string.Empty,
            sourceMode = target == null ? string.Empty : target.approximate ? "APPROX" : "REAL",
            x = position.x,
            y = position.y,
            z = position.z,
            carrying = behaviorMetrics.Carrying,
        }, eventType);
    }

    public void EndSession(string reason)
    {
        if (state != SessionState.Running) return;
        state = SessionState.Ending;
        behaviorMetrics.EndSession();
        WriteSessionEnd(reason, Time.realtimeSinceStartup - runClockStart);
        loggingManager.CloseSession();
        StopGuideOutputs();
        ClearSpawnedObjects();
        behaviorMetrics.ResetSession();
        sessionId = string.Empty;
        state = SessionState.Idle;
        RefreshHud($"Previous session ended: {reason}");
    }

    private void AbortLoading(string reason)
    {
        WriteSystem("session_aborted", reason);
        behaviorMetrics.EndSession();
        WriteSessionEnd(reason, 0f);
        loggingManager.CloseSession();
        StopGuideOutputs();
        ClearSpawnedObjects();
        behaviorMetrics.ResetSession();
        sessionId = string.Empty;
        state = SessionState.Idle;
        RefreshHud($"START BLOCKED: {reason}");
    }

    private void WriteSessionEnd(string reason, float runningSeconds)
    {
        loggingManager.Write(new SessionEndLog
        {
            t = SessionTime,
            endedAtUtc = DateTime.UtcNow.ToString("O"),
            reason = reason ?? "unknown",
            deliveredCount = targets.Values.Count(target => target.delivered),
            targetCount = targets.Count,
            runningSeconds = runningSeconds,
        }, "session_end");
        loggingManager.FlushNow();
    }

    private void WriteSystem(string eventName, string detail)
    {
        loggingManager?.WriteSystem(eventName, detail);
    }

    private void OnApplicationPause(bool paused)
    {
        if (!paused || loggingManager == null || !loggingManager.WriterOpen) return;
        WriteSystem("application_pause", "paused=true");
        loggingManager.FlushNow();
    }

    private void OnApplicationQuit()
    {
        if (loggingManager == null || !loggingManager.WriterOpen) return;
        WriteSystem("application_quit", "quit=true");
        behaviorMetrics?.EndSession();
        WriteSessionEnd("application_quit", RunningTime);
        loggingManager.CloseSession();
    }

    private void OnDestroy()
    {
        if (loggingManager != null) loggingManager.CloseSession();
        if (vgCircleSprite != null) Destroy(vgCircleSprite);
        if (vgCircleTexture != null) Destroy(vgCircleTexture);
        if (ownsCorrectDeliveryClip && correctDeliveryClip != null) Destroy(correctDeliveryClip);
    }

    private void CreateHud()
    {
        if (headTransform == null || hudRoot == null || lobbyPanel == null || vgPanel == null)
        {
            Debug.LogError("[ExperimentMain] Scene hierarchy must contain ExperimentCanvas/LobbyPanel/VGPanel.", this);
            enabled = false;
            return;
        }

        var rootRect = hudRoot.GetComponent<RectTransform>();
        var canvas = hudRoot.GetComponent<Canvas>();
        var scaler = hudRoot.GetComponent<CanvasScaler>();
        if (rootRect == null || canvas == null || scaler == null)
        {
            Debug.LogError("[ExperimentMain] ExperimentCanvas needs RectTransform, Canvas and CanvasScaler.", hudRoot);
            enabled = false;
            return;
        }

        rootRect.SetParent(headTransform, false);
        rootRect.localPosition = new Vector3(0f, -0.02f, 0.85f);
        rootRect.localRotation = Quaternion.identity;
        rootRect.localScale = Vector3.one * 0.001f;
        rootRect.sizeDelta = new Vector2(920f, 620f);
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = 250;
        scaler.dynamicPixelsPerUnit = 10f;

        var lobbyImage = lobbyPanel.GetComponent<Image>() ?? lobbyPanel.AddComponent<Image>();
        lobbyImage.color = new Color(0.02f, 0.03f, 0.05f, 0.94f);
        var vgImage = vgPanel.GetComponent<Image>() ?? vgPanel.gameObject.AddComponent<Image>();
        vgImage.color = new Color(0.015f, 0.02f, 0.025f, 0.72f);
        vgCircleSprite = CreateCircleSprite(128, out vgCircleTexture);
        vgImage.sprite = vgCircleSprite;
        vgImage.type = Image.Type.Simple;
        vgImage.preserveAspect = true;
        var vgMask = vgPanel.GetComponent<Mask>() ?? vgPanel.gameObject.AddComponent<Mask>();
        vgMask.showMaskGraphic = true;

        operatorText = lobbyPanel.GetComponentInChildren<TextMeshProUGUI>(true)
            ?? CreateText(lobbyPanel.GetComponent<RectTransform>(), "OperatorText", 36f, TextAlignmentOptions.Center);

        vgPanel.anchorMin = new Vector2(1f, 0f);
        vgPanel.anchorMax = new Vector2(1f, 0f);
        vgPanel.pivot = new Vector2(1f, 0f);
        vgPanel.anchoredPosition = new Vector2(-18f, 18f);
        vgPanel.sizeDelta = new Vector2(240f, 240f); // 80% of the previous 300 px map.
        vgUserMarker = CreateMapSymbol(vgPanel, "CameraDirection", "▲", new Color(0.1f, 1f, 0.3f, 1f), 36.4f);
        vgPanel.gameObject.SetActive(false);
    }

    private static TextMeshProUGUI CreateText(RectTransform parent, string name, float size, TextAlignmentOptions alignment)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        var rect = obj.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0.04f, 0.04f);
        rect.anchorMax = new Vector2(0.96f, 0.96f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var text = obj.GetComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.color = Color.white;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        return text;
    }

    private static RectTransform CreateMapSymbol(RectTransform parent, string name, string symbol, Color color, float size)
    {
        var text = CreateText(parent, name, size, TextAlignmentOptions.Center);
        var rect = text.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(size * 1.5f, size * 1.5f);
        text.text = symbol;
        text.color = color;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return rect;
    }

    private void RefreshHud(string status)
    {
        if (operatorText == null) return;
        lobbyPanel.SetActive(true);
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
        operatorText.text = "FP1 PILOT\n\n"
            + $"PARTICIPANT  {SelectedParticipantId}\n"
            + $"SET          {SelectedSetId}\n"
            + $"GUIDE        {SelectedGuideMode}\n\n"
            + $"{status}\n\n"
            + "X: PARTICIPANT   Y: SET   B: GUIDE   A: PLAY\n"
            + "Keyboard: P / S / G / Enter";
    }

    private void ShowParticipantSpawnConfirmation(int towerCount)
    {
        if (operatorText == null || lobbyPanel == null) return;
        var realCount = targets.Values.Count(target => !target.approximate);
        var approximateCount = targets.Values.Count(target => target.approximate);
        lobbyPanel.SetActive(true);
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
        operatorText.text = "READY\n\n"
            + $"OBJECTS SPAWNED  {targets.Count}/12\n"
            + $"REAL {realCount}   APPROX {approximateCount}\n"
            + $"PAGODAS SPAWNED  {towerCount}/4\n\n"
            + "Carry each stone to the pagoda with the matching color.";
        if (spawnConfirmationRoutine != null) StopCoroutine(spawnConfirmationRoutine);
        spawnConfirmationRoutine = StartCoroutine(HideSpawnConfirmationAfterDelay());
    }

    private IEnumerator HideSpawnConfirmationAfterDelay()
    {
        yield return new WaitForSecondsRealtime(4f);
        spawnConfirmationRoutine = null;
        if (state != SessionState.Running) yield break;
        if (lobbyPanel != null) lobbyPanel.SetActive(false);
        if (vgPanel != null) vgPanel.gameObject.SetActive(currentGuideMode == ExperimentGuideMode.VG);
    }

    private void ApplyGuideVisibility()
    {
        if (lobbyPanel != null) lobbyPanel.SetActive(false);
        if (aagGuide != null) aagGuide.gameObject.SetActive(currentGuideMode == ExperimentGuideMode.AAG);
        if (vgPanel != null) vgPanel.gameObject.SetActive(currentGuideMode == ExperimentGuideMode.VG);
        if (currentGuideMode == ExperimentGuideMode.VG) EnsureVgTargetMarkers();
    }

    private void StopGuideOutputs()
    {
        if (spawnConfirmationRoutine != null)
        {
            StopCoroutine(spawnConfirmationRoutine);
            spawnConfirmationRoutine = null;
        }
        if (aagGuide != null)
        {
            aagGuide.StopAndReset();
            aagGuide.gameObject.SetActive(false);
        }
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
    }

    private void UpdateVg(Vector3 headPosition)
    {
        if (vgPanel == null || headTransform == null) return;
        EnsureVgTargetMarkers();
        var userYaw = headTransform.eulerAngles.y;
        SetMapMarker(vgUserMarker, headPosition, headPosition, userYaw);
        // The player arrow always faces the map's north/up direction. The map
        // content below is rotated by the player's current facing direction.
        vgUserMarker.localRotation = Quaternion.identity;
        foreach (var pair in vgTargetMarkers)
        {
            if (targets.TryGetValue(pair.Key, out var target) && target.transform != null)
                SetMapMarker(pair.Value, target.transform.position, headPosition, userYaw);
        }
    }

    private void EnsureVgTargetMarkers()
    {
        foreach (var target in targets.Values)
        {
            if (target.transform == null || vgTargetMarkers.ContainsKey(target.objectId)) continue;
            vgTargetMarkers[target.objectId] = CreateMapSymbol(
                vgPanel,
                $"Target {target.objectId}",
                "●",
                new Color(0.82f, 0.82f, 0.82f, 1f),
                22f);
        }
    }

    private void SetMapMarker(RectTransform marker, Vector3 worldPosition, Vector3 centerPosition, float userYaw)
    {
        if (marker == null) return;
        var worldOffset = worldPosition - centerPosition;
        var mapOffset = Quaternion.Euler(0f, -userYaw, 0f) * worldOffset;
        var offset = new Vector2(mapOffset.x, mapOffset.z);
        var visible = offset.sqrMagnitude <= VgMapHalfSpanMeters * VgMapHalfSpanMeters;
        marker.gameObject.SetActive(visible);
        if (!visible) return;

        var width = Mathf.Max(1f, vgPanel.rect.width - 36f);
        var height = Mathf.Max(1f, vgPanel.rect.height - 36f);
        marker.anchoredPosition = new Vector2(
            offset.x / VgMapHalfSpanMeters * width * 0.5f,
            offset.y / VgMapHalfSpanMeters * height * 0.5f);
    }

    private void ClearVgMarkers()
    {
        foreach (var marker in vgTargetMarkers.Values)
            if (marker != null) Destroy(marker.gameObject);
        vgTargetMarkers.Clear();
    }

    private static Color ColorForName(string color)
    {
        if (string.Equals(color, "Red", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.15f, 0.1f);
        if (string.Equals(color, "Blue", StringComparison.OrdinalIgnoreCase)) return new Color(0.1f, 0.45f, 1f);
        if (string.Equals(color, "Green", StringComparison.OrdinalIgnoreCase)) return new Color(0.15f, 1f, 0.25f);
        if (string.Equals(color, "Yellow", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.85f, 0.1f);
        return Color.white;
    }

    private static Sprite CreateCircleSprite(int size, out Texture2D texture)
    {
        size = Mathf.Max(32, size);
        texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "VG Circular Background",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        var pixels = new Color32[size * size];
        var center = (size - 1) * 0.5f;
        var radius = center - 1f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                var alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(radius + 1f - distance) * 255f);
                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);

        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect);
        sprite.name = "VG Circular Background";
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static AudioClip CreateCorrectDeliveryClip()
    {
        const int sampleRate = 24000;
        const float durationSeconds = 0.28f;
        var sampleCount = Mathf.CeilToInt(sampleRate * durationSeconds);
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var time = index / (float)sampleRate;
            var fadeIn = Mathf.Clamp01(time / 0.012f);
            var envelope = fadeIn * Mathf.Exp(-8f * time);
            var firstTone = Mathf.Sin(2f * Mathf.PI * 880f * time);
            var secondTone = Mathf.Sin(2f * Mathf.PI * 1320f * time);
            samples[index] = (firstTone * 0.65f + secondTone * 0.35f) * envelope * 0.5f;
        }

        var clip = AudioClip.Create("Correct Stone Delivery", sampleCount, 1, sampleRate, false);
        return clip.SetData(samples, 0) ? clip : null;
    }

    private static int NextIndex(int current, Array values)
    {
        return values == null || values.Length == 0 ? 0 : (current + 1) % values.Length;
    }

    private static string SafeChoice(string[] values, int index, string fallback)
    {
        if (values == null || values.Length == 0) return fallback;
        return values[Mathf.Clamp(index, 0, values.Length - 1)];
    }

    private static string BuildSessionId(string participant, string setId, ExperimentGuideMode guide)
    {
        return $"{participant}_{setId}_{guide}_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static string RoomFallbackId(string roomUuid)
    {
        return string.IsNullOrEmpty(roomUuid) ? string.Empty : roomUuid.Substring(0, Mathf.Min(8, roomUuid.Length));
    }
}

/// <summary>
/// Gives runtime-spawned task content the Meta Environment Depth shader while
/// preserving its original texture and colour properties.
/// </summary>
public static class EnvironmentDepthOcclusion
{
    private const string ObjectShaderName = "Meta/Depth/URP/Occlusion Simple Lit";
    private const string TextShaderName = "FP1/TextMeshPro/Environment Depth Mobile";

    public static void ApplyToRenderers(Transform root)
    {
        if (root == null) return;
        var occlusionShader = Shader.Find(ObjectShaderName);
        if (occlusionShader == null)
        {
            Debug.LogWarning($"[EnvironmentDepthOcclusion] Missing shader: {ObjectShaderName}");
            return;
        }

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer is ParticleSystemRenderer) continue;
            var sourceMaterials = renderer.sharedMaterials;
            if (sourceMaterials == null || sourceMaterials.Length == 0) continue;

            var occludedMaterials = new Material[sourceMaterials.Length];
            for (var index = 0; index < sourceMaterials.Length; index++)
            {
                var source = sourceMaterials[index];
                if (source == null || source.shader == occlusionShader)
                {
                    occludedMaterials[index] = source;
                    continue;
                }

                var converted = new Material(source) { name = $"{source.name} (Environment Depth)" };
                converted.shader = occlusionShader;
                occludedMaterials[index] = converted;
            }
            renderer.sharedMaterials = occludedMaterials;
        }
    }

    public static void ApplyToText(TMP_Text text)
    {
        if (text == null) return;
        var occlusionShader = Shader.Find(TextShaderName);
        if (occlusionShader == null)
        {
            Debug.LogWarning($"[EnvironmentDepthOcclusion] Missing shader: {TextShaderName}", text);
            return;
        }

        var source = text.fontSharedMaterial;
        if (source == null) return;
        var converted = new Material(source) { name = $"{source.name} (Environment Depth)" };
        converted.shader = occlusionShader;
        text.fontMaterial = converted;
    }
}
