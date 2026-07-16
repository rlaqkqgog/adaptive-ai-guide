using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Single runtime owner for the FP1 pilot. It intentionally owns the complete
/// session lifecycle so repeated Play runs cannot leave state in other managers.
/// The proven AnchorLoader remains the only anchor-localization service.
/// </summary>
[DisallowMultipleComponent]
public sealed class ExperimentMain : MonoBehaviour
{
    private const float VgMapHalfSpanMeters = 5f;

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

    private sealed class MetricSample
    {
        public float time;
        public float distance;
        public float headRotation;
        public string roomUuid;
    }

    private sealed class AesSnapshot
    {
        public float distance;
        public int uniqueRooms;
        public float headRotation;
        public float combinedScore;
        public bool gatePassed;
    }

    private sealed class ProxySnapshot
    {
        public bool hasValue;
        public float revisitSeconds;
        public float emptyHandSeconds;
        public float ratio;
        public bool coldStart;
    }

    private sealed class RoomChoice
    {
        public string roomUuid;
        public string roomId;
        public int remainingCount;
        public bool visited;
        public float lastVisitedAt;
        public int tieOrder;
        public string selectionRule;
    }

    [Serializable]
    private sealed class SessionStartLog
    {
        public string type = "session_start";
        public float t;
        public string schema = "fp1-pilot-jsonl/v1";
        public string sessionId;
        public string participantId;
        public string setId;
        public string guideMode;
        public string startedAtUtc;
        public string manifestPath;
        public float trackIntervalSeconds;
        public float roomMinDwellSeconds;
        public float aesWindowSeconds;
        public float aesMinimumDistanceMeters;
        public int aesMinimumUniqueRooms;
        public float aesMinimumHeadRotationDegrees;
        public float proxyWindowSeconds;
        public float proxyColdStartRatio;
        public float proxyLowBoundary;
        public float proxyHighBoundary;
        public float decisionIntervalSeconds;
        public float minimumUtteranceGapSeconds;
        public float timeLimitSeconds;
        public string coldStartLevel;
        public string underloadLevel;
        public string optimalLevel;
        public string overloadLevel;
    }

    [Serializable]
    private sealed class TrackLog
    {
        public string type = "track";
        public float t;
        public float x;
        public float y;
        public float z;
        public float yaw;
        public float pitch;
        public int carrying;
        public string roomUuid;
        public string roomId;
    }

    [Serializable]
    private sealed class RoomEventLog
    {
        public string type;
        public float t;
        public string roomUuid;
        public string roomId;
        public int carrying;
        public bool revisitAtEntry;
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
    private sealed class AesLog
    {
        public string type = "aes";
        public float t;
        public float windowSeconds;
        public float distanceMeters;
        public int uniqueRooms;
        public float headRotationDegrees;
        public float combinedScore;
        public bool gatePassed;
        public string guideMode;
    }

    [Serializable]
    private sealed class ProxyLog
    {
        public string type = "revisit_proxy";
        public float t;
        public float windowSeconds;
        public float totalAccumulatedEmptyHandSeconds;
        public float revisitEmptyHandSeconds;
        public float windowEmptyHandSeconds;
        public float proxyRatio;
        public bool hasValue;
        public bool coldStart;
        public int carrying;
        public string guideMode;
    }

    [Serializable]
    private sealed class UtteranceLog
    {
        public string type = "utterance";
        public float t;
        public bool aesGatePassed;
        public bool proxyHasValue;
        public float proxyRatio;
        public string level;
        public string roomUuid;
        public string roomId;
        public string selectionRule;
        public string clipId;
        public bool played;
        public string reason;
    }

    [Serializable]
    private sealed class SystemLog
    {
        public string type = "system";
        public float t;
        public string eventName;
        public string detail;
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

    [Header("Single configuration asset")]
    [SerializeField] private Fp1ExperimentConfig config;

    [Header("Existing scene services")]
    [SerializeField] private AnchorLoader anchorLoader;
    [SerializeField] private SpatialAnchorManager spatialAnchorManager;
    [SerializeField] private Transform headTransform;

    private SessionState state = SessionState.Idle;
    [Header("Scene-owned outputs")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private GameObject hudRoot;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private RectTransform vgPanel;

    private int participantIndex;
    private int setIndex;
    private int guideIndex;
    private float sessionClockStart;
    private float runClockStart;
    private float sampleAccumulator;
    private float nextDecisionTime;
    private float nextFlushTime;
    private float lastUtteranceTime = float.NegativeInfinity;
    private bool hasPreviousPose;
    private Vector3 previousPosition;
    private float previousYaw;
    private float previousPitch;
    private int carrying;
    private string carriedObjectId = string.Empty;
    private string currentRoomUuid = string.Empty;
    private string currentRoomId = string.Empty;
    private bool currentRoomIsRevisit;
    private string candidateRoomUuid = string.Empty;
    private float candidateRoomDwell;
    private string sessionId = string.Empty;
    private string currentSetId = string.Empty;
    private ExperimentGuideMode currentGuideMode;
    private StreamWriter logWriter;

    private readonly List<MetricSample> metricSamples = new List<MetricSample>();
    private readonly List<GameObject> approximatedObjects = new List<GameObject>();
    private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
    private readonly HashSet<string> visitedRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastVisitedAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RectTransform> vgTargetMarkers = new Dictionary<string, RectTransform>(StringComparer.Ordinal);
    private readonly EmptyHandRevisitWindow revisitWindow = new EmptyHandRevisitWindow();

    private TextMeshProUGUI operatorText;
    private RectTransform vgUserMarker;

    private string SelectedParticipantId => SafeChoice(config != null ? config.participantIds : null, participantIndex, "P01");
    private string SelectedSetId => SafeChoice(config != null ? config.setIds : null, setIndex, AagExperimentSpaceCatalog.Fp1S1);
    private ExperimentGuideMode SelectedGuideMode => (ExperimentGuideMode)(guideIndex % 3);
    private float SessionTime => string.IsNullOrEmpty(sessionId) ? 0f : Time.realtimeSinceStartup - sessionClockStart;
    private float RunningTime => state == SessionState.Running ? Time.realtimeSinceStartup - runClockStart : 0f;

    private void Awake()
    {
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<Fp1ExperimentConfig>();
            Debug.LogWarning("[Experiment] FP1ExperimentConfig missing; using in-memory defaults.");
        }
        config.RebuildLookups();

        if (anchorLoader == null) anchorLoader = FindFirstObjectByType<AnchorLoader>();
        if (spatialAnchorManager == null) spatialAnchorManager = FindFirstObjectByType<SpatialAnchorManager>();
        if (headTransform == null)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) headTransform = rig.centerEyeAnchor;
        }

        if (audioSource == null) audioSource = GetComponentInChildren<AudioSource>(true);
        if (audioSource == null)
        {
            Debug.LogError("[Experiment] _Experiment/AudioSource reference is missing.", this);
            enabled = false;
            return;
        }
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;

        CreateHud();
        ResetMeasurementState();
        RefreshHud("Ready");
    }

    private void Update()
    {
        if (state == SessionState.Idle)
        {
            HandleOperatorInput();
            return;
        }

        FlushIfDue();
        if (state != SessionState.Running) return;

        sampleAccumulator += Time.unscaledDeltaTime;
        var interval = Mathf.Max(0.02f, config.trackIntervalSeconds);
        while (sampleAccumulator >= interval)
        {
            sampleAccumulator -= interval;
            SampleRuntime(interval);
        }

        if (SessionTime >= nextDecisionTime)
        {
            nextDecisionTime += Mathf.Max(0.5f, config.decisionIntervalSeconds);
            EvaluateMetricsAndGuide();
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
        sessionClockStart = Time.realtimeSinceStartup;
        OpenLog();
        WriteSessionStart();
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

        anchorLoader.ClearLoadedAnchors();
        anchorLoader.ClearPrefabOverrides();
        foreach (var pair in entriesByUuid)
            anchorLoader.SetPrefabForUuid(pair.Key, spatialAnchorManager.GetAnchorPrefabForColor(pair.Value.color));

        WriteSystem("load_started", $"set={currentSetId}; requested={entriesByUuid.Count}; forceQuestStoreQuery=true");
        anchorLoader.LoadAnchorsByUuid(entriesByUuid.Keys, $"EXPERIMENT:{currentSetId}", true);
        while (anchorLoader.IsReadOnlyLoadInProgress)
        {
            RefreshHud($"Loading {anchorLoader.LocalizedRequestedCount}/{entriesByUuid.Count}...");
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

        runClockStart = Time.realtimeSinceStartup;
        sampleAccumulator = 0f;
        nextDecisionTime = SessionTime + Mathf.Max(0.5f, config.decisionIntervalSeconds);
        state = SessionState.Running;
        ApplyGuideVisibility();
        WriteSystem("session_running", $"targets={targets.Count}; guide={currentGuideMode}");
    }

    private void PrepareForNewSession()
    {
        StopGuideOutputs();
        CloseLog();
        ClearSpawnedObjects();
        ResetMeasurementState();
    }

    private void ResetMeasurementState()
    {
        metricSamples.Clear();
        revisitWindow.Reset();
        visitedRooms.Clear();
        lastVisitedAt.Clear();
        ClearVgMarkers();
        carrying = 0;
        carriedObjectId = string.Empty;
        currentRoomUuid = string.Empty;
        currentRoomId = string.Empty;
        currentRoomIsRevisit = false;
        candidateRoomUuid = string.Empty;
        candidateRoomDwell = 0f;
        sampleAccumulator = 0f;
        hasPreviousPose = false;
        previousPosition = Vector3.zero;
        previousYaw = 0f;
        previousPitch = 0f;
        lastUtteranceTime = float.NegativeInfinity;
    }

    private void ClearSpawnedObjects()
    {
        foreach (var instance in approximatedObjects)
            if (instance != null) Destroy(instance);
        approximatedObjects.Clear();
        targets.Clear();
        if (anchorLoader != null)
        {
            anchorLoader.ClearPrefabOverrides();
            anchorLoader.ClearLoadedAnchors();
        }
    }

    private void RegisterTarget(AagManualAnchorEntry entry, Transform targetTransform, bool approximate)
    {
        if (entry == null || targetTransform == null) return;
        var visualCollider = targetTransform.GetComponentsInChildren<Collider>(true)
            .FirstOrDefault(candidate => candidate != null && !(candidate is MeshCollider mesh && !mesh.convex));
        var movableTransform = visualCollider != null ? visualCollider.transform : targetTransform;
        var roomUuid = GetContainingRoomUuid(movableTransform.position);
        var mapping = config.FindRoom(roomUuid);
        var id = string.IsNullOrWhiteSpace(entry.marker_id) ? entry.anchor_uuid : entry.marker_id;
        var bridge = movableTransform.GetComponent<ExperimentObject>()
            ?? movableTransform.gameObject.AddComponent<ExperimentObject>();
        bridge.Initialize(this, id, false);

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

    private void SampleRuntime(float sampleDelta)
    {
        if (headTransform == null)
        {
            WriteSystem("tracking_lost", "headTransform_missing");
            return;
        }

        var position = headTransform.position;
        var euler = headTransform.eulerAngles;
        var yaw = NormalizeAngle(euler.y);
        var pitch = NormalizeAngle(euler.x);
        var rawRoomUuid = GetContainingRoomUuid(position);
        UpdateStableRoom(rawRoomUuid, sampleDelta);

        var distance = 0f;
        var rotation = 0f;
        if (hasPreviousPose)
        {
            var delta = position - previousPosition;
            distance = new Vector2(delta.x, delta.z).magnitude;
            rotation = Mathf.Abs(Mathf.DeltaAngle(previousYaw, yaw)) + Mathf.Abs(Mathf.DeltaAngle(previousPitch, pitch));
        }
        previousPosition = position;
        previousYaw = yaw;
        previousPitch = pitch;
        hasPreviousPose = true;

        metricSamples.Add(new MetricSample
        {
            time = SessionTime,
            distance = distance,
            headRotation = rotation,
            roomUuid = currentRoomUuid,
        });
        TrimMetricWindow();

        // Critical invariant: this call advances neither time nor expiry while carrying.
        revisitWindow.Sample(sampleDelta, carrying == 0, currentRoomIsRevisit);

        WriteLog(new TrackLog
        {
            t = SessionTime,
            x = position.x,
            y = position.y,
            z = position.z,
            yaw = yaw,
            pitch = pitch,
            carrying = carrying,
            roomUuid = currentRoomUuid,
            roomId = currentRoomId,
        });

        if (currentGuideMode == ExperimentGuideMode.VG) UpdateVg(position);
    }

    private void UpdateStableRoom(string rawRoomUuid, float sampleDelta)
    {
        rawRoomUuid ??= string.Empty;
        if (string.Equals(rawRoomUuid, currentRoomUuid, StringComparison.OrdinalIgnoreCase))
        {
            candidateRoomUuid = string.Empty;
            candidateRoomDwell = 0f;
            return;
        }

        if (!string.Equals(rawRoomUuid, candidateRoomUuid, StringComparison.OrdinalIgnoreCase))
        {
            candidateRoomUuid = rawRoomUuid;
            candidateRoomDwell = sampleDelta;
            return;
        }

        candidateRoomDwell += sampleDelta;
        if (candidateRoomDwell < config.roomMinDwellSeconds) return;

        if (!string.IsNullOrEmpty(currentRoomUuid))
        {
            WriteLog(new RoomEventLog
            {
                type = "room_exit",
                t = SessionTime,
                roomUuid = currentRoomUuid,
                roomId = currentRoomId,
                carrying = carrying,
                revisitAtEntry = currentRoomIsRevisit,
            });
        }

        currentRoomUuid = candidateRoomUuid;
        var mapping = config.FindRoom(currentRoomUuid);
        currentRoomId = mapping != null ? mapping.roomId : RoomFallbackId(currentRoomUuid);
        currentRoomIsRevisit = !string.IsNullOrEmpty(currentRoomUuid) && visitedRooms.Contains(currentRoomUuid);
        if (!string.IsNullOrEmpty(currentRoomUuid))
        {
            visitedRooms.Add(currentRoomUuid);
            lastVisitedAt[currentRoomUuid] = SessionTime;
            WriteLog(new RoomEventLog
            {
                type = "room_enter",
                t = SessionTime,
                roomUuid = currentRoomUuid,
                roomId = currentRoomId,
                carrying = carrying,
                revisitAtEntry = currentRoomIsRevisit,
            });
        }
        candidateRoomUuid = string.Empty;
        candidateRoomDwell = 0f;
    }

    private void EvaluateMetricsAndGuide()
    {
        var aes = CalculateAes();
        var proxy = CalculateProxy();
        WriteLog(new AesLog
        {
            t = SessionTime,
            windowSeconds = config.aesWindowSeconds,
            distanceMeters = aes.distance,
            uniqueRooms = aes.uniqueRooms,
            headRotationDegrees = aes.headRotation,
            combinedScore = aes.combinedScore,
            gatePassed = aes.gatePassed,
            guideMode = currentGuideMode.ToString(),
        });
        WriteLog(new ProxyLog
        {
            t = SessionTime,
            windowSeconds = config.proxyWindowSeconds,
            totalAccumulatedEmptyHandSeconds = revisitWindow.TotalAccumulatedEmptyHandSeconds,
            revisitEmptyHandSeconds = proxy.revisitSeconds,
            windowEmptyHandSeconds = proxy.emptyHandSeconds,
            proxyRatio = proxy.ratio,
            hasValue = proxy.hasValue,
            coldStart = proxy.coldStart,
            carrying = carrying,
            guideMode = currentGuideMode.ToString(),
        });

        // AES/RevisitProxy are always computed above. Only output is conditional.
        if (currentGuideMode == ExperimentGuideMode.AAG) RunAagDecision(aes, proxy);
    }

    private AesSnapshot CalculateAes()
    {
        var snapshot = new AesSnapshot();
        var rooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in metricSamples)
        {
            snapshot.distance += sample.distance;
            snapshot.headRotation += sample.headRotation;
            if (!string.IsNullOrEmpty(sample.roomUuid)) rooms.Add(sample.roomUuid);
        }
        snapshot.uniqueRooms = rooms.Count;
        snapshot.combinedScore = snapshot.distance + snapshot.uniqueRooms + snapshot.headRotation / 180f;
        snapshot.gatePassed = snapshot.distance >= config.aesMinimumDistanceMeters
            && snapshot.uniqueRooms >= config.aesMinimumUniqueRooms
            && snapshot.headRotation >= config.aesMinimumHeadRotationDegrees;
        return snapshot;
    }

    private ProxySnapshot CalculateProxy()
    {
        revisitWindow.GetWindow(config.proxyWindowSeconds, out var revisitSeconds, out var emptyHandSeconds);
        var coldThreshold = config.proxyWindowSeconds * config.proxyColdStartRatio;
        var hasValue = revisitWindow.TotalAccumulatedEmptyHandSeconds >= coldThreshold && emptyHandSeconds > 0f;
        return new ProxySnapshot
        {
            hasValue = hasValue,
            revisitSeconds = revisitSeconds,
            emptyHandSeconds = emptyHandSeconds,
            ratio = emptyHandSeconds > 0f ? revisitSeconds / emptyHandSeconds : 0f,
            coldStart = !hasValue,
        };
    }

    private void RunAagDecision(AesSnapshot aes, ProxySnapshot proxy)
    {
        var level = ResolveSupportLevel(proxy);
        var choice = ChooseRemainingRoom();
        var clipId = !aes.gatePassed ? "GF-01" : BuildClipId(level, choice);
        var log = new UtteranceLog
        {
            t = SessionTime,
            aesGatePassed = aes.gatePassed,
            proxyHasValue = proxy.hasValue,
            proxyRatio = proxy.ratio,
            level = level.ToString(),
            roomUuid = choice?.roomUuid ?? string.Empty,
            roomId = choice?.roomId ?? string.Empty,
            selectionRule = choice?.selectionRule ?? "none",
            clipId = clipId,
        };

        if (SessionTime - lastUtteranceTime < config.minimumUtteranceGapSeconds)
        {
            log.reason = "minimum_gap";
            WriteLog(log);
            return;
        }
        if (audioSource.isPlaying)
        {
            log.reason = "audio_busy";
            WriteLog(log);
            return;
        }
        var clip = config.FindClip(clipId);
        if (clip == null)
        {
            log.reason = "clip_missing";
            WriteLog(log);
            return;
        }

        audioSource.clip = clip;
        audioSource.Play();
        lastUtteranceTime = SessionTime;
        log.played = true;
        log.reason = "played";
        WriteLog(log);
    }

    private AagSupportLevel ResolveSupportLevel(ProxySnapshot proxy)
    {
        if (!proxy.hasValue) return config.coldStartLevel;
        if (proxy.ratio < config.proxyLowBoundary) return config.underloadLevel;
        if (proxy.ratio < config.proxyHighBoundary) return config.optimalLevel;
        return config.overloadLevel;
    }

    private RoomChoice ChooseRemainingRoom()
    {
        var choices = targets.Values
            .Where(target => !target.delivered && !string.IsNullOrEmpty(target.roomUuid))
            .GroupBy(target => target.roomUuid, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var mapping = config.FindRoom(group.Key);
                var visited = visitedRooms.Contains(group.Key);
                return new RoomChoice
                {
                    roomUuid = group.Key,
                    roomId = mapping != null ? mapping.roomId : first.roomId,
                    remainingCount = group.Count(),
                    visited = visited,
                    lastVisitedAt = lastVisitedAt.TryGetValue(group.Key, out var time) ? time : float.NegativeInfinity,
                    tieOrder = mapping?.tieOrder ?? int.MaxValue,
                    selectionRule = visited ? "stalest_visited" : "unvisited_first",
                };
            })
            .ToList();
        if (choices.Count == 0) return null;

        return choices
            .OrderBy(choice => choice.visited ? 1 : 0)
            .ThenBy(choice => choice.visited ? choice.lastVisitedAt : float.NegativeInfinity)
            .ThenByDescending(choice => choice.remainingCount)
            .ThenBy(choice => choice.tieOrder)
            .First();
    }

    private static string BuildClipId(AagSupportLevel level, RoomChoice choice)
    {
        return level switch
        {
            AagSupportLevel.VeryEasy when choice != null && !choice.visited => $"VE-U-{choice.roomId}",
            AagSupportLevel.VeryEasy when choice != null => $"VE-V-{choice.roomId}",
            AagSupportLevel.Easy when choice != null => $"E-{choice.roomId}",
            AagSupportLevel.Normal => "N-01",
            AagSupportLevel.Hard when choice != null && !choice.visited => "H-01",
            AagSupportLevel.Hard => "N-01",
            _ => "VH-01",
        };
    }

    public void NotifyObjectGrabbed(ExperimentObject experimentObject)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        if (carrying > 0 && !string.Equals(carriedObjectId, experimentObject.ObjectId, StringComparison.Ordinal))
        {
            WriteSystem("grab_rejected", $"object={experimentObject.ObjectId}; carrying={carriedObjectId}");
            return;
        }
        carrying = 1;
        carriedObjectId = experimentObject.ObjectId;
        WriteObjectEvent("grab", experimentObject.ObjectId, string.Empty);
    }

    public void NotifyObjectDropped(ExperimentObject experimentObject)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        if (string.Equals(carriedObjectId, experimentObject.ObjectId, StringComparison.Ordinal))
        {
            carrying = 0;
            carriedObjectId = string.Empty;
        }
        WriteObjectEvent("drop", experimentObject.ObjectId, string.Empty);
    }

    public void NotifyObjectDelivered(ExperimentObject experimentObject, string towerId)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        if (!targets.TryGetValue(experimentObject.ObjectId, out var target) || target.delivered) return;
        target.delivered = true;
        carrying = 0;
        carriedObjectId = string.Empty;
        WriteObjectEvent("delivered", experimentObject.ObjectId, towerId);
        if (targets.Values.All(value => value.delivered)) EndSession("all_targets_delivered");
    }

    private void WriteObjectEvent(string eventType, string objectId, string towerId)
    {
        targets.TryGetValue(objectId, out var target);
        var position = target?.transform != null ? target.transform.position : Vector3.zero;
        WriteLog(new ObjectEventLog
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
            carrying = carrying,
        });
    }

    public void EndSession(string reason)
    {
        if (state != SessionState.Running) return;
        state = SessionState.Ending;
        var delivered = targets.Values.Count(target => target.delivered);
        WriteLog(new SessionEndLog
        {
            t = SessionTime,
            endedAtUtc = DateTime.UtcNow.ToString("O"),
            reason = reason ?? "unknown",
            deliveredCount = delivered,
            targetCount = targets.Count,
            runningSeconds = Time.realtimeSinceStartup - runClockStart,
        });
        CloseLog();
        StopGuideOutputs();
        ClearSpawnedObjects();
        ResetMeasurementState();
        sessionId = string.Empty;
        state = SessionState.Idle;
        RefreshHud($"Previous session ended: {reason}");
    }

    private void AbortLoading(string reason)
    {
        WriteSystem("session_aborted", reason);
        WriteLog(new SessionEndLog
        {
            t = SessionTime,
            endedAtUtc = DateTime.UtcNow.ToString("O"),
            reason = reason,
            deliveredCount = 0,
            targetCount = targets.Count,
        });
        CloseLog();
        StopGuideOutputs();
        ClearSpawnedObjects();
        ResetMeasurementState();
        sessionId = string.Empty;
        state = SessionState.Idle;
        RefreshHud($"START BLOCKED: {reason}");
    }

    private void OpenLog()
    {
        var folder = Path.Combine(Application.persistentDataPath, "ExperimentLogs", SanitizePathPart(sessionId));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.jsonl");
        logWriter = new StreamWriter(path, false, new UTF8Encoding(false));
        nextFlushTime = Time.realtimeSinceStartup + config.logFlushIntervalSeconds;
        Debug.Log($"[Experiment] log={path}");
    }

    private void WriteSessionStart()
    {
        WriteLog(new SessionStartLog
        {
            t = 0f,
            sessionId = sessionId,
            participantId = SelectedParticipantId,
            setId = currentSetId,
            guideMode = currentGuideMode.ToString(),
            startedAtUtc = DateTime.UtcNow.ToString("O"),
            manifestPath = AagManualAnchorSetStore.ManifestPath,
            trackIntervalSeconds = config.trackIntervalSeconds,
            roomMinDwellSeconds = config.roomMinDwellSeconds,
            aesWindowSeconds = config.aesWindowSeconds,
            aesMinimumDistanceMeters = config.aesMinimumDistanceMeters,
            aesMinimumUniqueRooms = config.aesMinimumUniqueRooms,
            aesMinimumHeadRotationDegrees = config.aesMinimumHeadRotationDegrees,
            proxyWindowSeconds = config.proxyWindowSeconds,
            proxyColdStartRatio = config.proxyColdStartRatio,
            proxyLowBoundary = config.proxyLowBoundary,
            proxyHighBoundary = config.proxyHighBoundary,
            decisionIntervalSeconds = config.decisionIntervalSeconds,
            minimumUtteranceGapSeconds = config.minimumUtteranceGapSeconds,
            timeLimitSeconds = config.sessionTimeLimitSeconds,
            coldStartLevel = config.coldStartLevel.ToString(),
            underloadLevel = config.underloadLevel.ToString(),
            optimalLevel = config.optimalLevel.ToString(),
            overloadLevel = config.overloadLevel.ToString(),
        });
        logWriter?.Flush();
    }

    private void WriteSystem(string eventName, string detail)
    {
        WriteLog(new SystemLog { t = SessionTime, eventName = eventName, detail = detail ?? string.Empty });
    }

    private void WriteLog(object record)
    {
        if (logWriter == null || record == null) return;
        try
        {
            logWriter.WriteLine(JsonUtility.ToJson(record));
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Experiment] log write failed: {exception.Message}");
        }
    }

    private void FlushIfDue()
    {
        if (logWriter == null || Time.realtimeSinceStartup < nextFlushTime) return;
        try { logWriter.Flush(); }
        catch (Exception exception) { Debug.LogError($"[Experiment] log flush failed: {exception.Message}"); }
        nextFlushTime = Time.realtimeSinceStartup + config.logFlushIntervalSeconds;
    }

    private void CloseLog()
    {
        if (logWriter == null) return;
        try
        {
            logWriter.Flush();
            logWriter.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Experiment] log close failed: {exception.Message}");
        }
        finally
        {
            logWriter = null;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && logWriter != null)
        {
            WriteSystem("application_pause", "paused=true");
            try { logWriter.Flush(); } catch (Exception exception) { Debug.LogError(exception.Message); }
        }
    }

    private void OnApplicationQuit()
    {
        if (logWriter != null)
        {
            WriteSystem("application_quit", "quit=true");
            WriteLog(new SessionEndLog
            {
                t = SessionTime,
                endedAtUtc = DateTime.UtcNow.ToString("O"),
                reason = "application_quit",
                deliveredCount = targets.Values.Count(target => target.delivered),
                targetCount = targets.Count,
                runningSeconds = state == SessionState.Running ? Time.realtimeSinceStartup - runClockStart : 0f,
            });
        }
        CloseLog();
    }

    private void OnDestroy()
    {
        CloseLog();
    }

    private void CreateHud()
    {
        if (headTransform == null || hudRoot == null || lobbyPanel == null || vgPanel == null)
        {
            Debug.LogError("[Experiment] Scene hierarchy must contain ExperimentCanvas/LobbyPanel/VGPanel.", this);
            enabled = false;
            return;
        }

        var rootRect = hudRoot.GetComponent<RectTransform>();
        var canvas = hudRoot.GetComponent<Canvas>();
        var scaler = hudRoot.GetComponent<CanvasScaler>();
        if (rootRect == null || canvas == null || scaler == null)
        {
            Debug.LogError("[Experiment] ExperimentCanvas needs RectTransform, Canvas and CanvasScaler.", hudRoot);
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

        operatorText = lobbyPanel.GetComponentInChildren<TextMeshProUGUI>(true)
            ?? CreateText(lobbyPanel.GetComponent<RectTransform>(), "OperatorText", 36f, TextAlignmentOptions.Center);

        vgPanel.anchorMin = Vector2.one;
        vgPanel.anchorMax = Vector2.one;
        vgPanel.pivot = Vector2.one;
        vgPanel.anchoredPosition = new Vector2(-18f, -18f);
        vgPanel.sizeDelta = new Vector2(300f, 300f);
        vgUserMarker = CreateMapSymbol(vgPanel, "CameraDirection", "▲", new Color(0.1f, 1f, 0.3f, 1f), 52f);
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

    private void ApplyGuideVisibility()
    {
        if (lobbyPanel != null) lobbyPanel.SetActive(false);
        if (vgPanel != null) vgPanel.gameObject.SetActive(currentGuideMode == ExperimentGuideMode.VG);
        if (currentGuideMode == ExperimentGuideMode.VG) EnsureVgTargetMarkers();
    }

    private void StopGuideOutputs()
    {
        if (audioSource != null) audioSource.Stop();
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
    }

    private void UpdateVg(Vector3 headPosition)
    {
        if (vgPanel == null || headTransform == null) return;
        EnsureVgTargetMarkers();
        SetMapMarker(vgUserMarker, headPosition, headPosition);
        vgUserMarker.localRotation = Quaternion.Euler(0f, 0f, -headTransform.eulerAngles.y);
        foreach (var pair in vgTargetMarkers)
        {
            if (targets.TryGetValue(pair.Key, out var target) && target.transform != null)
                SetMapMarker(pair.Value, target.transform.position, headPosition);
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
                ColorForName(target.color),
                22f);
        }
    }

    private void SetMapMarker(RectTransform marker, Vector3 worldPosition, Vector3 centerPosition)
    {
        if (marker == null) return;
        var offset = new Vector2(worldPosition.x - centerPosition.x, worldPosition.z - centerPosition.z);
        var visible = Mathf.Abs(offset.x) <= VgMapHalfSpanMeters && Mathf.Abs(offset.y) <= VgMapHalfSpanMeters;
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

    private void TrimMetricWindow()
    {
        var minimumTime = SessionTime - config.aesWindowSeconds;
        while (metricSamples.Count > 0 && metricSamples[0].time < minimumTime) metricSamples.RemoveAt(0);
    }

    private string GetContainingRoomUuid(Vector3 worldPosition)
    {
        if (MRUK.Instance == null) return string.Empty;
        MRUKRoom containing = null;
        foreach (var room in MRUK.Instance.Rooms)
        {
            var inside = false;
            foreach (var floor in room.FloorAnchors)
            {
                if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3) continue;
                var local = floor.transform.InverseTransformPoint(worldPosition);
                if (!floor.IsPositionInBoundary(new Vector2(local.x, local.y))) continue;
                inside = true;
                break;
            }
            if (!inside) continue;
            if (containing != null) return string.Empty;
            containing = room;
        }
        if (containing == null || containing.Anchor == null || containing.Anchor.Uuid == Guid.Empty) return string.Empty;
        return containing.Anchor.Uuid.ToString();
    }

    private static void Encapsulate(ref Vector2 min, ref Vector2 max, Vector2 point)
    {
        min = Vector2.Min(min, point);
        max = Vector2.Max(max, point);
    }

    private static Color ColorForName(string color)
    {
        if (string.Equals(color, "Red", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.15f, 0.1f);
        if (string.Equals(color, "Blue", StringComparison.OrdinalIgnoreCase)) return new Color(0.1f, 0.45f, 1f);
        if (string.Equals(color, "Green", StringComparison.OrdinalIgnoreCase)) return new Color(0.15f, 1f, 0.25f);
        if (string.Equals(color, "Yellow", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.85f, 0.1f);
        return Color.white;
    }

    private static float NormalizeAngle(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
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

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "session";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static string RoomFallbackId(string roomUuid)
    {
        return string.IsNullOrEmpty(roomUuid) ? string.Empty : roomUuid.Substring(0, Mathf.Min(8, roomUuid.Length));
    }
}
