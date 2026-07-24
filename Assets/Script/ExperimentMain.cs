using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// FP1 session coordinator. Measurement, AAG decisions, and file ownership live
/// in their visible sibling managers under _Experiment.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public sealed class ExperimentMain : MonoBehaviour
{
    private const float VgMapHalfSpanMeters = 7f; // 40% wider view than the previous 5 m half-span.
    private const float OperatorWindowHoldSeconds = 0.8f;
    private const float OperatorAbortHoldSeconds = 0.8f;
    private const float S3RigidRecoveryStabilizationSeconds = 4f;
    private const float S3RigidRecoveryRetryIntervalSeconds = 0.25f;
    private const float SpaceReadinessTimeoutSeconds = 12f;
    private const float SpaceReadinessStableSeconds = 1.5f;
    private const float SpaceReadinessPollSeconds = 0.25f;
    // Quest/MRUK World Lock legitimately accumulates tracking-space corrections
    // during a long walk. Integrity must therefore be judged per rendered frame,
    // not against the session-start pose. These limits remain well below the
    // multi-metre origin replacement this guard is intended to catch.
    private const float MaximumTrackingSpaceStepMeters = 0.75f;
    private const float MaximumTrackingSpaceStepRotationDegrees = 10f;
    private const int InventoryCapacity = 3;
    private const float InventoryDeliveryDwellSeconds = 3f;
    private const int RequiredRoomInteriorGridSize = 11;
    private const float RequiredRoomMinimumMarkerSeparationMeters = 1.5f;
    private static readonly Vector3 HudHeadLockedLocalPosition = new Vector3(0f, -0.02f, 0.85f);
    private static readonly Vector3 HudHeadLockedLocalScale = Vector3.one * 0.001f;

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
        public string sourceMode;
        public Transform transform;
        public bool approximate;
        public bool delivered;
        public bool pocketed;
        public bool collected;
    }

    private sealed class RecoveredTargetPlacement
    {
        public AagManualAnchorEntry entry;
        public OVRSpatialAnchor prefab;
        public Vector3 position;
        public Quaternion rotation;
        public string roomIds;
    }

    private readonly struct S3CanonicalTargetPlacement
    {
        public readonly Vector3 Position;
        public readonly string RoomUuid;

        public S3CanonicalTargetPlacement(Vector3 position, string roomUuid)
        {
            Position = position;
            RoomUuid = roomUuid;
        }
    }

    // Validated FP1-S3 constellation from the last physically correct field run
    // (2026-07-20 16:41).  These poses are transformed as one rigid layout from
    // persistent tower/incidental references; live per-room MRUK floor origins
    // are deliberately not used because Meta can replace them mid-session.
    private static readonly IReadOnlyDictionary<string, S3CanonicalTargetPlacement>
        S3CanonicalTargets = new Dictionary<string, S3CanonicalTargetPlacement>(StringComparer.Ordinal)
        {
            ["blue_1"] = new(new Vector3(-19.5796976f, 0.8613052f, -5.8403312f), "7e4d3e1f-3247-602b-3822-c21df384e947"),
            ["blue_2"] = new(new Vector3(2.1814880f, 1.8638211f, -3.1760836f), "7423d1c6-d1e7-3316-6714-6a9b282a396e"),
            ["blue_3"] = new(new Vector3(3.6865795f, 1.2555922f, 5.7449489f), "ce00a9e3-c3dc-48cd-a503-e928584055f2"),
            ["green_1"] = new(new Vector3(-8.6749134f, 1.7837031f, 4.2364044f), "ad306342-a794-cffd-be87-d9aea02c5823"),
            ["green_2"] = new(new Vector3(-13.6698999f, 0.4892681f, -0.8269613f), "5faa1907-d2e2-7605-7b01-5149a34a4c6d"),
            ["green_3"] = new(new Vector3(8.8090744f, 2.5344079f, 5.1913123f), "ce00a9e3-c3dc-48cd-a503-e928584055f2"),
            ["red_1"] = new(new Vector3(-26.6888847f, 0.6086617f, -4.9870124f), "98332012-20ba-e0ba-78ec-8440113270cd"),
            ["red_2"] = new(new Vector3(-17.8106403f, 0.7588592f, -7.3271031f), "b4131884-79b1-7db8-5529-4875ea40bdd5"),
            ["red_3"] = new(new Vector3(-11.9031324f, 1.8741175f, -6.1516155f), "b4131884-79b1-7db8-5529-4875ea40bdd5"),
            ["yellow_1"] = new(new Vector3(-17.5012245f, 0.2500426f, -1.7263687f), "5faa1907-d2e2-7605-7b01-5149a34a4c6d"),
            ["yellow_2"] = new(new Vector3(6.2087975f, 2.2959590f, 3.5084310f), "ce00a9e3-c3dc-48cd-a503-e928584055f2"),
            ["yellow_3"] = new(new Vector3(7.9531116f, 0.9614943f, 0.1868193f), "880b5e63-6438-a8d5-c156-2ddffc48a6d4"),
        };

    // Floor-anchor origins in the same validated world frame as
    // S3CanonicalTargets. Meta can rebase the complete multi-room scene after
    // a room is removed from Space Setup. Matching the remaining room UUIDs
    // recovers that single global transform without depending on the deleted
    // Unmapped room or on any individual object anchor.
    private static readonly IReadOnlyDictionary<Guid, Vector3> S3CanonicalRoomFloorOrigins =
        new Dictionary<Guid, Vector3>
        {
            [Guid.Parse("5faa1907-d2e2-7605-7b01-5149a34a4c6d")] = new(-13.106f, 0.036f, -0.403f),
            [Guid.Parse("98332012-20ba-e0ba-78ec-8440113270cd")] = new(-26.704f, -0.002f, -3.716f),
            [Guid.Parse("0d537c33-3e47-2606-3ea9-897c2bc9f1ce")] = new(1.133f, 0.079f, 2.906f),
            [Guid.Parse("b4131884-79b1-7db8-5529-4875ea40bdd5")] = new(-12.484f, -0.003f, -5.448f),
            [Guid.Parse("ce00a9e3-c3dc-48cd-a503-e928584055f2")] = new(6.964f, 0.018f, 3.705f),
            [Guid.Parse("7e4d3e1f-3247-602b-3822-c21df384e947")] = new(-22.695f, -0.005f, -7.886f),
            [Guid.Parse("880b5e63-6438-a8d5-c156-2ddffc48a6d4")] = new(10.916f, 0.006f, -0.139f),
            [Guid.Parse("7423d1c6-d1e7-3316-6714-6a9b282a396e")] = new(-0.681f, -0.049f, -2.859f),
        };

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
        public bool wasCarried;
    }

    [Serializable]
    private sealed class GrabRejectedLog
    {
        public string type = "grab_rejected";
        public float t;
        public string objectId;
        public string reason;
        public string carriedObjectId;
    }

    [Serializable]
    private sealed class InventoryEventLog
    {
        public string type = "inventory";
        public float t;
        public string action;
        public string inputSource;
        public string objectId;
        public string color;
        public int totalCount;
        public int capacity;
    }

    [Serializable]
    private sealed class InventoryDwellLog
    {
        public string type = "inventory_delivery_dwell";
        public float t;
        public string state;
        public string towerId;
        public string color;
        public float elapsedSeconds;
        public float requiredSeconds;
        public int matchingCount;
        public int deliveredCount;
        public string reason;
        public string objectIds;
    }

    [Serializable]
    private sealed class VgGuideTargetSnapshot
    {
        public string objectId;
        public string color;
        public string roomUuid;
        public string roomId;
        public float targetX;
        public float targetY;
        public float targetZ;
        public float directionX;
        public float directionY;
        public float directionZ;
        public float distanceMeters;
        public float markerX;
        public float markerY;
        public bool visible;
        public bool delivered;
    }

    [Serializable]
    private sealed class VgGuideSnapshotLog
    {
        public string type = "vg_guide_snapshot";
        public float t;
        public bool panelVisible;
        public string projectionRule = "head_relative_yaw_rotated_top_down";
        public float headX;
        public float headY;
        public float headZ;
        public float headYawDegrees;
        public float visibleRadiusMeters;
        public int guideCount;
        public VgGuideTargetSnapshot[] vgGuides;
    }

    [Serializable]
    private sealed class VgGuideStateLog
    {
        public string type = "vg_guide_state";
        public float t;
        public string scope;
        public string objectId;
        public string state;
        public bool visible;
        public string reason;
        public float targetX;
        public float targetY;
        public float targetZ;
        public float markerX;
        public float markerY;
    }

    [Serializable]
    private sealed class SessionEndLog
    {
        public string type = "session_end";
        public float t;
        public string endedAtUtc;
        public string reason;
        public string abortNote;
        public int deliveredCount;
        public int targetCount;
        public float runningSeconds;
    }

    [Header("Single Configuration Asset")]
    [SerializeField] private ExperimentConfig config;

    [Header("Existing Scene Services")]
    [SerializeField] private AnchorLoader anchorLoader;
    [SerializeField] private SpatialAnchorManager spatialAnchorManager;
    [SerializeField] private AagExperimentSpaceValidator experimentSpaceValidator;
    [SerializeField] private Transform headTransform;

    [Header("Visible Experiment Managers")]
    [SerializeField] private BehaviorMetrics behaviorMetrics;
    [SerializeField] private AAGGuide aagGuide;
    [SerializeField] private LoggingManager loggingManager;
    [SerializeField] private FixedTowerManager fixedTowerManager;
    [SerializeField] private FixedTowerAnchorLoader fixedTowerAnchorLoader;
    [SerializeField] private IncidentalObjectManager incidentalObjectManager;
    [SerializeField] private IncidentalAnchorLoader incidentalAnchorLoader;
    [SerializeField] private AagFiducialZoneAlignment fiducialZoneAlignment;
    [SerializeField] private AagAprilTagTranslationAligner aprilTagTranslationAligner;
    [SerializeField] private AagFixedSpaceOffset fixedSpaceOffset;
    [SerializeField] private AagSpaceOffsetDiagnostic spaceOffsetDiagnostic;

    [Header("Scene-owned Outputs")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private GameObject hudRoot;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private RectTransform vgPanel;
    [SerializeField] private AudioClip correctDeliveryClip;
    [SerializeField] private AudioClip deliveryZoneEntryClip;

    [Header("Experimenter Abort")]
    [SerializeField, TextArea(1, 2)] private string experimenterAbortNote = string.Empty;

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
    private readonly List<string> pocketedObjectIds = new List<string>();
    private readonly Dictionary<string, TargetState> targets = new Dictionary<string, TargetState>(StringComparer.Ordinal);
    private readonly Dictionary<string, RectTransform> vgTargetMarkers = new Dictionary<string, RectTransform>(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> vgMarkerVisibility = new Dictionary<string, bool>(StringComparer.Ordinal);
    private Coroutine spawnConfirmationRoutine;

    private TextMeshProUGUI operatorText;
    private TextMeshProUGUI inventoryText;
    private TextMeshProUGUI inventoryStoreProgressText;
    private TextMeshProUGUI deliveryProgressText;
    private GameObject abortButtonRoot;
    private TextMeshProUGUI abortButtonText;
    private float abortConfirmationExpiresAt;
    private bool operatorWindowOpen;
    private float operatorWindowHoldStartedAt = -1f;
    private bool operatorWindowHoldTriggered;
    private float operatorAbortHoldStartedAt = -1f;
    private bool operatorAbortHoldTriggered;
    private RectTransform vgUserMarker;
    private Sprite vgCircleSprite;
    private Texture2D vgCircleTexture;
    private bool ownsCorrectDeliveryClip;
    private bool ownsDeliveryZoneEntryClip;
    private string activeDeliveryTowerId = string.Empty;
    private string activeDeliveryColor = string.Empty;
    private string mismatchFeedbackTowerId = string.Empty;
    private float activeDeliveryDwellStartedAt = -1f;
    private float transientInventoryFeedbackUntil = -1f;
    private bool inventoryCompletionEndScheduled;
    private bool sessionTrackingSpaceLockActive;
    private Transform lockedTrackingSpace;
    private Vector3 lockedTrackingSpaceLocalPosition;
    private Quaternion lockedTrackingSpaceLocalRotation;
    private string pendingSpatialIntegrityFailure = string.Empty;
    private bool spaceReadinessPassed;
    private string spaceReadinessFailure = string.Empty;
    private string activeInventoryStoreObjectId = string.Empty;
    private string currentTowerZoneId = string.Empty;
    private string currentTowerZoneColor = string.Empty;
    private float currentTowerZoneEnteredAt = -1f;
    private bool deliveryCompletionFeedbackActive;
    private bool fiducialStartGatePassed;
    private string lastFiducialHudStatus = string.Empty;
    private string lastAprilTagHudStatus = string.Empty;

    private string SelectedParticipantId => SafeChoice(config != null ? config.participantIds : null, participantIndex, "P01");
    private string SelectedSetId => SafeChoice(
        config != null ? config.setIds : null,
        setIndex,
        ExperimentSpaceRuntime.SetIds.Count > 0
            ? ExperimentSpaceRuntime.SetIds[0]
            : AagExperimentSpaceCatalog.Fp1S1);
    private ExperimentGuideMode SelectedGuideMode => (ExperimentGuideMode)(guideIndex % 3);
    private float SessionTime => loggingManager != null ? loggingManager.SessionTime : 0f;
    private float RunningTime => state == SessionState.Running ? Time.realtimeSinceStartup - runClockStart : 0f;
    public ExperimentConfig Configuration => config;
    private Guid ExpectedStartRoomUuid =>
        config != null && config.TryGetStartRoomUuid(out var roomUuid)
            ? roomUuid
            : Guid.Empty;

    private void Awake()
    {
        if (config == null)
        {
            config = ScriptableObject.CreateInstance<Fp1ExperimentConfig>();
            Debug.LogWarning("[ExperimentMain] FP1ExperimentConfig missing; using in-memory defaults.", this);
        }
        config.RebuildLookups();
        ExperimentSpaceRuntime.Configure(config);

        if (anchorLoader == null) anchorLoader = FindFirstObjectByType<AnchorLoader>();
        if (spatialAnchorManager == null) spatialAnchorManager = FindFirstObjectByType<SpatialAnchorManager>();
        if (experimentSpaceValidator == null)
            experimentSpaceValidator = FindFirstObjectByType<AagExperimentSpaceValidator>();
        experimentSpaceValidator?.Configure(config.floorPlanId);
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
        if (fiducialZoneAlignment == null)
            fiducialZoneAlignment = GetComponent<AagFiducialZoneAlignment>()
                ?? gameObject.AddComponent<AagFiducialZoneAlignment>();
        if (fixedSpaceOffset == null)
            fixedSpaceOffset = GetComponent<AagFixedSpaceOffset>()
                ?? gameObject.AddComponent<AagFixedSpaceOffset>();
        if (aprilTagTranslationAligner == null)
            aprilTagTranslationAligner = GetComponent<AagAprilTagTranslationAligner>();
        if (spaceOffsetDiagnostic == null)
            spaceOffsetDiagnostic = GetComponent<AagSpaceOffsetDiagnostic>()
                ?? gameObject.AddComponent<AagSpaceOffsetDiagnostic>();
        fixedTowerAnchorLoader.Initialize(spatialAnchorManager != null ? spatialAnchorManager.anchorPrefab : null);
        incidentalAnchorLoader.Initialize(spatialAnchorManager != null ? spatialAnchorManager.anchorPrefab : null);
        fixedTowerManager.Initialize(config, this);
        fiducialZoneAlignment.Initialize(config, WriteSystem);
        fiducialZoneAlignment.enabled = config.fiducialMarkerAlignmentEnabled;
        fixedSpaceOffset.Configure(
            config.fixedSpaceOffsetEnabled,
            config.fixedSpaceOffsetMeters,
            config.fixedSpaceOffsetHorizontalOnly);
        spaceOffsetDiagnostic.SetFixedSpaceOffset(fixedSpaceOffset);
        behaviorMetrics?.SetPhysicalRoomProvider(
            fiducialZoneAlignment.GetCurrentPhysicalRoomUuid);

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
        if (deliveryZoneEntryClip == null)
        {
            deliveryZoneEntryClip = CreateDeliveryZoneEntryClip();
            ownsDeliveryZoneEntryClip = deliveryZoneEntryClip != null;
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
            if (fiducialZoneAlignment != null
                && fiducialZoneAlignment.HandleIdleCalibrationInput(out var markerStatus))
            {
                lastFiducialHudStatus = fiducialZoneAlignment.StatusLine;
                RefreshHud(markerStatus);
            }
            else if (fiducialZoneAlignment != null
                && !string.Equals(
                    lastFiducialHudStatus,
                    fiducialZoneAlignment.StatusLine,
                    StringComparison.Ordinal))
            {
                lastFiducialHudStatus = fiducialZoneAlignment.StatusLine;
                RefreshHud("Ready");
            }
            if (aprilTagTranslationAligner != null
                && !string.Equals(
                    lastAprilTagHudStatus,
                    aprilTagTranslationAligner.StatusLine,
                    StringComparison.Ordinal))
            {
                lastAprilTagHudStatus = aprilTagTranslationAligner.StatusLine;
                RefreshHud("Ready");
            }
            return;
        }

        loggingManager.Tick();
        if (state != SessionState.Running) return;
        HandleRunningOperatorWindowInput();
        UpdateInventoryDeliveryDwell();

        if (abortConfirmationExpiresAt > 0f && Time.unscaledTime > abortConfirmationExpiresAt)
        {
            abortConfirmationExpiresAt = 0f;
            if (abortButtonText != null) abortButtonText.text = "HOLD LEFT X\nEND FAILED";
        }

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

    private void LateUpdate()
    {
        // MRUK World Lock updates TrackingSpace in Update(). Compare with the
        // last accepted frame so normal gradual correction can accumulate without
        // being mistaken for an origin jump.
        if (!sessionTrackingSpaceLockActive || lockedTrackingSpace == null) return;

        var positionStep = Vector3.Distance(
            lockedTrackingSpace.localPosition, lockedTrackingSpaceLocalPosition);
        var rotationStep = Quaternion.Angle(
            lockedTrackingSpace.localRotation, lockedTrackingSpaceLocalRotation);
        if (positionStep <= MaximumTrackingSpaceStepMeters
            && rotationStep <= MaximumTrackingSpaceStepRotationDegrees)
        {
            lockedTrackingSpaceLocalPosition = lockedTrackingSpace.localPosition;
            lockedTrackingSpaceLocalRotation = lockedTrackingSpace.localRotation;
            return;
        }

        // A single-frame change this large is an unsafe relocalization. Restore
        // the immediately preceding accepted pose and fail closed.
        lockedTrackingSpace.SetLocalPositionAndRotation(
            lockedTrackingSpaceLocalPosition,
            lockedTrackingSpaceLocalRotation);

        pendingSpatialIntegrityFailure =
            $"tracking_space_step_position_{positionStep:F3}m_rotation_{rotationStep:F2}deg";
        if (state != SessionState.Running) return;

        WriteSystem(
            "spatial_integrity_breach",
            $"reason={pendingSpatialIntegrityFailure}; action=session_failed_closed");
        EndSession("spatial_integrity_abort", pendingSpatialIntegrityFailure);
    }

    private void HandleRunningOperatorWindowInput()
    {
        // X+Y is not reported reliably as a simultaneous chord by every Quest
        // controller/runtime combination. Y is already proven to work in the
        // idle operator menu, and has no participant action while a run is active.
        var rawYHeld = OVRInput.Get(OVRInput.RawButton.Y, OVRInput.Controller.LTouch);
        var logicalYHeld = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
        var operatorButtonHeld = rawYHeld || logicalYHeld;
        if (!operatorButtonHeld)
        {
            operatorWindowHoldStartedAt = -1f;
            operatorWindowHoldTriggered = false;
        }
        else if (operatorWindowHoldStartedAt < 0f)
        {
            operatorWindowHoldStartedAt = Time.unscaledTime;
            WriteSystem("operator_window_hold_started",
                $"rawY={rawYHeld}; logicalY={logicalYHeld}; requiredSeconds={OperatorWindowHoldSeconds:F1}");
        }
        else if (!operatorWindowHoldTriggered
            && Time.unscaledTime - operatorWindowHoldStartedAt >= OperatorWindowHoldSeconds)
        {
            operatorWindowHoldTriggered = true;
            ToggleRunningOperatorWindow();
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.F10)) ToggleRunningOperatorWindow();
#endif

        HandleOperatorAbortInput();
    }

    private void HandleOperatorAbortInput()
    {
        if (!operatorWindowOpen)
        {
            operatorAbortHoldStartedAt = -1f;
            operatorAbortHoldTriggered = false;
            return;
        }

        var rawXHeld = OVRInput.Get(OVRInput.RawButton.X, OVRInput.Controller.LTouch);
        var logicalXHeld = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);
        var abortButtonHeld = rawXHeld || logicalXHeld;
        if (!abortButtonHeld)
        {
            operatorAbortHoldStartedAt = -1f;
            operatorAbortHoldTriggered = false;
            return;
        }

        if (operatorAbortHoldStartedAt < 0f)
        {
            operatorAbortHoldStartedAt = Time.unscaledTime;
            WriteSystem(
                "experimenter_abort_hold_started",
                $"stage={(abortConfirmationExpiresAt > Time.unscaledTime ? "confirm" : "arm")}; "
                + $"rawX={rawXHeld}; logicalX={logicalXHeld}; requiredSeconds={OperatorAbortHoldSeconds:F1}");
            return;
        }

        if (operatorAbortHoldTriggered
            || Time.unscaledTime - operatorAbortHoldStartedAt < OperatorAbortHoldSeconds)
            return;

        operatorAbortHoldTriggered = true;
        AbortSessionFromUi();
    }

    private void TryPocketSelectedStone(string objectId, string inputSource)
    {
        if (string.IsNullOrEmpty(objectId)
            || !targets.TryGetValue(objectId, out var target)
            || target == null
            || target.transform == null
            || target.delivered
            || target.pocketed)
            return;

        if (pocketedObjectIds.Count >= InventoryCapacity)
        {
            WriteInventoryEvent("store_rejected_full", target, inputSource);
            ShowInventoryFeedback("INVENTORY FULL", 1.2f);
            return;
        }

        foreach (var body in target.transform.GetComponentsInChildren<Rigidbody>(true))
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
        target.pocketed = true;
        target.collected = true;
        if (behaviorMetrics.NotifyTargetFound(objectId, "inventory"))
            aagGuide?.NotifyTargetFound();
        pocketedObjectIds.Add(objectId);
        behaviorMetrics.ForceClearCarry();
        target.transform.gameObject.SetActive(false);
        if (vgTargetMarkers.TryGetValue(objectId, out var marker) && marker != null)
            marker.gameObject.SetActive(false);
        if (currentGuideMode == ExperimentGuideMode.VG)
            WriteVgGuideState("marker", objectId, "pocketed", false, "inventory_store");
        WriteInventoryEvent("stored", target, inputSource);
        ShowInventoryFeedback($"{target.color.ToUpperInvariant()} STORED\n{pocketedObjectIds.Count}/{InventoryCapacity}", 1.0f);
        RefreshInventoryHud();
    }

    public void NotifyInventoryStoreHoldStarted(ExperimentObject experimentObject, float requiredSeconds)
    {
        if (!CanTrackInventoryStoreHold(experimentObject)) return;
        activeInventoryStoreObjectId = experimentObject.ObjectId;
        WriteSystem("inventory_store_hold_started",
            $"object={experimentObject.ObjectId}; input=left_hand_pinch_hold; requiredSeconds={requiredSeconds:F1}");
        SetInventoryStoreProgress(0f, requiredSeconds, true);
    }

    public void NotifyInventoryHandResolution(
        ExperimentObject experimentObject,
        int pointerIdentifier,
        bool leftHandResolved,
        string resolution)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        WriteSystem("inventory_hand_resolution",
            $"object={experimentObject.ObjectId}; pointerId={pointerIdentifier}; "
            + $"leftHandResolved={leftHandResolved}; resolution={resolution ?? "unknown"}");
    }

    public void NotifyInventoryStoreHoldProgress(
        ExperimentObject experimentObject,
        float elapsedSeconds,
        float requiredSeconds)
    {
        if (!CanTrackInventoryStoreHold(experimentObject)) return;
        activeInventoryStoreObjectId = experimentObject.ObjectId;
        SetInventoryStoreProgress(elapsedSeconds, requiredSeconds, true);
    }

    public void NotifyInventoryStoreHoldCancelled(ExperimentObject experimentObject, string reason)
    {
        if (experimentObject == null
            || !string.Equals(activeInventoryStoreObjectId, experimentObject.ObjectId, StringComparison.Ordinal))
            return;
        WriteSystem("inventory_store_hold_cancelled",
            $"object={experimentObject.ObjectId}; reason={reason ?? "unknown"}");
        activeInventoryStoreObjectId = string.Empty;
        SetInventoryStoreProgress(0f, 1f, false);
    }

    public void NotifyInventoryStoreHoldCompleted(ExperimentObject experimentObject, float requiredSeconds)
    {
        if (!CanTrackInventoryStoreHold(experimentObject))
        {
            NotifyInventoryStoreHoldCancelled(experimentObject, "session_or_carry_state_changed");
            return;
        }

        WriteSystem("inventory_store_hold_completed",
            $"object={experimentObject.ObjectId}; input=left_hand_pinch_hold; elapsedSeconds={requiredSeconds:F1}");
        activeInventoryStoreObjectId = string.Empty;
        SetInventoryStoreProgress(0f, 1f, false);
        TryPocketSelectedStone(experimentObject.ObjectId, "left_hand_pinch_hold");
    }

    private bool CanTrackInventoryStoreHold(ExperimentObject experimentObject)
    {
        return state == SessionState.Running
            && !operatorWindowOpen
            && experimentObject != null
            && behaviorMetrics.Carrying != 0
            && string.Equals(behaviorMetrics.CarriedObjectId, experimentObject.ObjectId, StringComparison.Ordinal);
    }

    private void SetInventoryStoreProgress(float elapsedSeconds, float requiredSeconds, bool visible)
    {
        if (inventoryStoreProgressText == null) return;
        var show = visible && state == SessionState.Running && !operatorWindowOpen;
        inventoryStoreProgressText.gameObject.SetActive(show);
        if (!show) return;

        var progress = Mathf.Clamp01(elapsedSeconds / Mathf.Max(0.01f, requiredSeconds));
        const int segmentCount = 10;
        var filled = Mathf.Clamp(Mathf.FloorToInt(progress * segmentCount), 0, segmentCount);
        var gauge = $"[{new string('#', filled)}{new string('-', segmentCount - filled)}]";
        inventoryStoreProgressText.text = $"STORE IN INVENTORY\n{gauge}  {Mathf.RoundToInt(progress * 100f)}%";
    }

    private void WriteInventoryEvent(string action, TargetState target, string inputSource = "")
    {
        loggingManager.Write(new InventoryEventLog
        {
            t = SessionTime,
            action = action ?? string.Empty,
            inputSource = inputSource ?? string.Empty,
            objectId = target?.objectId ?? string.Empty,
            color = target?.color ?? string.Empty,
            totalCount = pocketedObjectIds.Count,
            capacity = InventoryCapacity,
        }, "inventory");
    }

    private void WriteInventoryDwellEvent(
        string dwellState,
        string towerId,
        string color,
        float elapsedSeconds,
        int matchingCount,
        int deliveredCount,
        string reason = "",
        string objectIds = "")
    {
        loggingManager.Write(new InventoryDwellLog
        {
            t = SessionTime,
            state = dwellState ?? string.Empty,
            towerId = towerId ?? string.Empty,
            color = color ?? string.Empty,
            elapsedSeconds = Mathf.Max(0f, elapsedSeconds),
            requiredSeconds = InventoryDeliveryDwellSeconds,
            matchingCount = matchingCount,
            deliveredCount = deliveredCount,
            reason = reason ?? string.Empty,
            objectIds = objectIds ?? string.Empty,
        }, "inventory_delivery_dwell");
    }

    private void UpdateInventoryDeliveryDwell()
    {
        if (operatorWindowOpen || headTransform == null || fixedTowerManager == null)
        {
            ResetInventoryDeliveryDwell(true, operatorWindowOpen ? "operator_window" : "tracking_or_tower_missing");
            UpdateTowerZoneTransition(string.Empty, string.Empty,
                operatorWindowOpen ? "operator_window" : "tracking_or_tower_missing");
            UpdateTransientInventoryFeedback();
            return;
        }

        if (!fixedTowerManager.TryGetDwellTower(
                headTransform.position,
                out var towerId,
                out var towerColor))
        {
            ResetInventoryDeliveryDwell(true, "left_tower_zone");
            UpdateTowerZoneTransition(string.Empty, string.Empty, "left_tower_zone");
            mismatchFeedbackTowerId = string.Empty;
            UpdateTransientInventoryFeedback();
            return;
        }
        UpdateTowerZoneTransition(towerId, towerColor, "entered_or_changed_tower_zone");

        if (deliveryCompletionFeedbackActive)
        {
            if (Time.unscaledTime < transientInventoryFeedbackUntil)
            {
                UpdateTransientInventoryFeedback();
                return;
            }
            deliveryCompletionFeedbackActive = false;
        }

        var matchingCount = pocketedObjectIds.Count(objectId =>
            targets.TryGetValue(objectId, out var target)
            && target != null
            && target.pocketed
            && !target.delivered
            && string.Equals(target.color, towerColor, StringComparison.OrdinalIgnoreCase));
        if (matchingCount == 0)
        {
            ResetInventoryDeliveryDwell(true, "no_matching_inventory_stone");
            if (!string.Equals(mismatchFeedbackTowerId, towerId, StringComparison.Ordinal))
            {
                mismatchFeedbackTowerId = towerId;
                if (audioSource != null && deliveryZoneEntryClip != null)
                    audioSource.PlayOneShot(deliveryZoneEntryClip, 0.55f);
                WriteSystem(
                    "inventory_delivery_mismatch",
                    $"tower={towerId}; requiredColor={towerColor}; reason=no_matching_inventory_stone");
                WriteInventoryDwellEvent(
                    "mismatch", towerId, towerColor, 0f, 0, 0, "no_matching_inventory_stone");
            }
            SetDeliveryProgressText($"NO MATCHING\n{towerColor.ToUpperInvariant()} STONE", true);
            return;
        }

        if (!string.Equals(activeDeliveryTowerId, towerId, StringComparison.Ordinal))
        {
            ResetInventoryDeliveryDwell(true, "changed_tower_zone");
            mismatchFeedbackTowerId = string.Empty;
            activeDeliveryTowerId = towerId;
            activeDeliveryColor = towerColor;
            activeDeliveryDwellStartedAt = Time.unscaledTime;
            transientInventoryFeedbackUntil = -1f;
            if (audioSource != null && deliveryZoneEntryClip != null)
                audioSource.PlayOneShot(deliveryZoneEntryClip, 0.75f);
            WriteSystem(
                "inventory_delivery_dwell_started",
                $"tower={towerId}; color={towerColor}; matchingCount={matchingCount}; requiredSeconds={InventoryDeliveryDwellSeconds:F1}");
            WriteInventoryDwellEvent("started", towerId, towerColor, 0f, matchingCount, 0);
        }

        var elapsed = Mathf.Max(0f, Time.unscaledTime - activeDeliveryDwellStartedAt);
        var remaining = Mathf.Max(0, Mathf.CeilToInt(InventoryDeliveryDwellSeconds - elapsed));
        SetDeliveryProgressText($"DELIVERY PROCESSING...\n{remaining}", true);
        if (elapsed < InventoryDeliveryDwellSeconds) return;
        CompleteInventoryDeliveryDwell(towerId, towerColor, elapsed);
    }

    private void CompleteInventoryDeliveryDwell(string towerId, string towerColor, float elapsed)
    {
        var matchingIds = pocketedObjectIds
            .Where(objectId => targets.TryGetValue(objectId, out var target)
                && target != null
                && target.pocketed
                && !target.delivered
                && string.Equals(target.color, towerColor, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        activeDeliveryTowerId = string.Empty;
        activeDeliveryColor = string.Empty;
        mismatchFeedbackTowerId = string.Empty;
        activeDeliveryDwellStartedAt = -1f;
        var deliveredCount = 0;
        foreach (var objectId in matchingIds)
        {
            if (TryMarkTargetDelivered(objectId, towerId, false, false)) deliveredCount++;
        }
        fixedTowerManager.RecordPocketDeliveries(towerId, deliveredCount);
        if (deliveredCount <= 0)
        {
            ShowInventoryFeedback("NO MATCHING STONE", 1.2f);
            return;
        }

        if (audioSource != null && correctDeliveryClip != null)
            audioSource.PlayOneShot(correctDeliveryClip, 0.9f);
        deliveryCompletionFeedbackActive = true;
        ShowInventoryFeedback($"DELIVERY COMPLETE\n{towerColor.ToUpperInvariant()} x{deliveredCount}", 1.2f);
        WriteSystem(
            "inventory_delivery_dwell_completed",
            $"tower={towerId}; color={towerColor}; elapsedSeconds={elapsed:F3}; "
            + $"deliveredCount={deliveredCount}; objects={string.Join("|", matchingIds)}");
        WriteInventoryDwellEvent(
            "completed",
            towerId,
            towerColor,
            elapsed,
            matchingIds.Length,
            deliveredCount,
            string.Empty,
            string.Join("|", matchingIds));
        RefreshInventoryHud();

        if (targets.Count > 0 && targets.Values.All(value => value.delivered) && !inventoryCompletionEndScheduled)
        {
            inventoryCompletionEndScheduled = true;
            StartCoroutine(EndSessionAfterInventoryCompletionFeedback());
        }
    }

    private IEnumerator EndSessionAfterInventoryCompletionFeedback()
    {
        yield return new WaitForSecondsRealtime(1.2f);
        inventoryCompletionEndScheduled = false;
        if (state == SessionState.Running && targets.Count > 0 && targets.Values.All(value => value.delivered))
            EndSession("all_targets_delivered");
    }

    private void ResetInventoryDeliveryDwell(bool writeLog, string reason)
    {
        if (string.IsNullOrEmpty(activeDeliveryTowerId)) return;
        if (writeLog)
        {
            var elapsed = Mathf.Max(0f, Time.unscaledTime - activeDeliveryDwellStartedAt);
            WriteSystem(
                "inventory_delivery_dwell_cancelled",
                $"tower={activeDeliveryTowerId}; color={activeDeliveryColor}; elapsedSeconds={elapsed:F3}; reason={reason}");
            WriteInventoryDwellEvent(
                "cancelled", activeDeliveryTowerId, activeDeliveryColor, elapsed, 0, 0, reason);
        }
        activeDeliveryTowerId = string.Empty;
        activeDeliveryColor = string.Empty;
        activeDeliveryDwellStartedAt = -1f;
        UpdateTransientInventoryFeedback();
    }

    private void UpdateTowerZoneTransition(string towerId, string towerColor, string reason)
    {
        if (string.Equals(currentTowerZoneId, towerId, StringComparison.Ordinal)) return;

        if (!string.IsNullOrEmpty(currentTowerZoneId))
        {
            var elapsed = Mathf.Max(0f, Time.unscaledTime - currentTowerZoneEnteredAt);
            WriteSystem(
                "inventory_delivery_zone_exit",
                $"tower={currentTowerZoneId}; color={currentTowerZoneColor}; elapsedSeconds={elapsed:F3}; reason={reason}");
            WriteInventoryDwellEvent(
                "zone_exit", currentTowerZoneId, currentTowerZoneColor, elapsed, 0, 0, reason);
        }

        currentTowerZoneId = towerId ?? string.Empty;
        currentTowerZoneColor = towerColor ?? string.Empty;
        currentTowerZoneEnteredAt = string.IsNullOrEmpty(currentTowerZoneId) ? -1f : Time.unscaledTime;
        if (string.IsNullOrEmpty(currentTowerZoneId)) return;

        WriteSystem(
            "inventory_delivery_zone_enter",
            $"tower={currentTowerZoneId}; color={currentTowerZoneColor}; "
            + $"radiusMeters={FixedTowerManager.TowerDwellMaximumDistanceMeters:F2}");
        WriteInventoryDwellEvent(
            "zone_enter", currentTowerZoneId, currentTowerZoneColor, 0f, 0, 0);
    }

    private void ShowInventoryFeedback(string message, float seconds)
    {
        transientInventoryFeedbackUntil = Time.unscaledTime + Mathf.Max(0.1f, seconds);
        SetDeliveryProgressText(message, true);
    }

    private void UpdateTransientInventoryFeedback()
    {
        if (!string.IsNullOrEmpty(activeDeliveryTowerId)) return;
        var visible = state == SessionState.Running
            && !operatorWindowOpen
            && Time.unscaledTime < transientInventoryFeedbackUntil;
        SetDeliveryProgressText(deliveryProgressText != null ? deliveryProgressText.text : string.Empty, visible);
    }

    private void SetDeliveryProgressText(string message, bool visible)
    {
        if (deliveryProgressText == null) return;
        if (!string.IsNullOrEmpty(message)) deliveryProgressText.text = message;
        var show = visible && state == SessionState.Running && !operatorWindowOpen;
        if (show)
        {
            // Delivery feedback is intentionally text-only.  The lobby is a
            // full-screen, opaque operator surface on the same head-locked
            // canvas; never let a delayed/loading transition leave it behind
            // the participant-facing progress text.
            if (lobbyPanel != null && lobbyPanel.gameObject.activeSelf)
                lobbyPanel.gameObject.SetActive(false);
            DisableUnexpectedDeliveryBackgrounds();
        }
        deliveryProgressText.gameObject.SetActive(show);
    }

    private void DisableUnexpectedDeliveryBackgrounds()
    {
        if (deliveryProgressText == null) return;

        // CreateText adds only TMP text, but keep this fail-safe so a future
        // scene/prefab edit cannot silently reintroduce a rectangular Image,
        // RawImage, or mask on the delivery feedback object.
        foreach (var graphic in deliveryProgressText.GetComponents<Graphic>())
        {
            if (graphic != null && graphic != deliveryProgressText)
                graphic.enabled = false;
        }
        foreach (var mask in deliveryProgressText.GetComponents<Mask>())
            if (mask != null) mask.enabled = false;
        foreach (var mask in deliveryProgressText.GetComponents<RectMask2D>())
            if (mask != null) mask.enabled = false;
    }

    private void HandleOperatorInput()
    {
        if (OVRInput.GetDown(OVRInput.RawButton.X, OVRInput.Controller.LTouch)
            || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) CycleParticipant();
        if (OVRInput.GetDown(OVRInput.RawButton.Y, OVRInput.Controller.LTouch)
            || OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) CycleSet();
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

    private IEnumerator WaitForExperimentSpaceReadiness()
    {
        spaceReadinessPassed = false;
        spaceReadinessFailure = "space_readiness_timeout";
        var deadline = Time.realtimeSinceStartup + SpaceReadinessTimeoutSeconds;
        var stableSince = -1f;
        var nextPollAt = 0f;

        while (Time.realtimeSinceStartup < deadline)
        {
            var now = Time.realtimeSinceStartup;
            if (now < nextPollAt)
            {
                yield return null;
                continue;
            }
            nextPollAt = now + SpaceReadinessPollSeconds;

            if (experimentSpaceValidator == null)
            {
                spaceReadinessFailure = "space_validator_missing";
                stableSince = -1f;
            }
            else if (experimentSpaceValidator.TryValidateSessionStart(
                headTransform,
                ExpectedStartRoomUuid,
                fiducialStartGatePassed,
                out var failure))
            {
                if (stableSince < 0f) stableSince = now;
                var stableSeconds = now - stableSince;
                RefreshHud($"Checking experiment space {stableSeconds:F1}/{SpaceReadinessStableSeconds:F1}s...");
                if (stableSeconds >= SpaceReadinessStableSeconds)
                {
                    spaceReadinessPassed = true;
                    spaceReadinessFailure = string.Empty;
                    yield break;
                }
            }
            else
            {
                stableSince = -1f;
                spaceReadinessFailure = failure;
                RefreshHud($"SPACE NOT READY: {failure}");
            }

            yield return null;
        }

        Debug.LogError(
            $"[AAG Space Gate] Start blocked after {SpaceReadinessTimeoutSeconds:F1}s: {spaceReadinessFailure}");
    }

    private IEnumerator StartSessionRoutine()
    {
        state = SessionState.Loading;
        PrepareForNewSession();

        var aprilTagAlignmentApplied = aprilTagTranslationAligner != null
            && aprilTagTranslationAligner.IsApplied;
        if ((config.fixedSpaceOffsetEnabled || aprilTagAlignmentApplied)
            && config.fiducialMarkerAlignmentEnabled)
        {
            state = SessionState.Idle;
            RefreshHud("START BLOCKED: fixed_offset_and_fiducial_are_mutually_exclusive");
            Debug.LogError(
                "[AAG Fixed Offset] Disable either Fixed Global Translation or Fiducial Marker Alignment. "
                + "Applying both would double-correct experiment content.",
                this);
            yield break;
        }
        if (!aprilTagAlignmentApplied)
        {
            fixedSpaceOffset.Configure(
                config.fixedSpaceOffsetEnabled,
                config.fixedSpaceOffsetMeters,
                config.fixedSpaceOffsetHorizontalOnly);
        }

        fiducialStartGatePassed = false;
        if (aprilTagTranslationAligner != null
            && aprilTagTranslationAligner.RequireAppliedAlignmentBeforeSession)
        {
            RefreshHud("Checking Room3 AprilTag translation...");
            if (!aprilTagTranslationAligner.TryValidateSessionStart(out var aprilTagFailure))
            {
                state = SessionState.Idle;
                RefreshHud($"START BLOCKED: {aprilTagFailure}");
                yield break;
            }
            fiducialStartGatePassed = true;
        }
        if (config.fiducialMarkerAlignmentEnabled)
        {
            RefreshHud("Checking Room3 QR marker...");
            var fiducialFailure = "fiducial_manager_missing";
            if (fiducialZoneAlignment == null
                || !fiducialZoneAlignment.TryValidateSessionStart("room3", out fiducialFailure))
            {
                state = SessionState.Idle;
                RefreshHud($"START BLOCKED: {fiducialFailure}");
                yield break;
            }
            fiducialStartGatePassed = true;
            fiducialZoneAlignment.BeginSession("room3");
        }

        RefreshHud("Checking experiment space...");
        yield return WaitForExperimentSpaceReadiness();
        if (!spaceReadinessPassed)
        {
            fiducialZoneAlignment?.EndSession();
            state = SessionState.Idle;
            RefreshHud($"START BLOCKED: {spaceReadinessFailure}");
            yield break;
        }

        currentSetId = SelectedSetId;
        currentGuideMode = SelectedGuideMode;
        sessionId = BuildSessionId(SelectedParticipantId, currentSetId, currentGuideMode);
        if (!loggingManager.OpenSession(sessionId, SelectedParticipantId, currentSetId, currentGuideMode, config))
        {
            fiducialZoneAlignment?.EndSession();
            sessionId = string.Empty;
            state = SessionState.Idle;
            RefreshHud("START BLOCKED: log_open_failed");
            yield break;
        }
        behaviorMetrics.BeginSession(config, headTransform, loggingManager);
        aagGuide.BeginSession(config, behaviorMetrics, loggingManager);
        if (!BeginSessionTrackingSpaceLock(out var trackingLockFailure))
        {
            AbortLoading(trackingLockFailure);
            yield break;
        }
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

        if (!fixedTowerManager.TryBuildAnchorMap(sessionId, true, out var towersByUuid, out var towerConfigurationFailure))
        {
            AbortLoading(towerConfigurationFailure);
            yield break;
        }
        WriteSystem("tower_color_assignment", fixedTowerManager.ActiveColorAssignment);
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

        var isS3 = string.Equals(
            set.set_id, AagExperimentSpaceCatalog.Fp1S3, StringComparison.Ordinal);
        var loadedTargetsFromS3Global = false;
        if (!TrySolveS3RoomConstellation(
                out var roomConstellationSolution,
                out var roomConstellationFailure,
                out var roomConstellationDetail))
        {
            var strictDetail = roomConstellationDetail;
            if (TrySolveS3RoomConstellation(
                    out roomConstellationSolution,
                    out var bestEffortWarning,
                    out var bestEffortDetail,
                    true))
            {
                WriteSystem(
                    "room_constellation_warning",
                    $"{strictDetail}; action=diagnostic_only_continue_best_effort; "
                    + $"qualityWarning={bestEffortWarning}; fallback={bestEffortDetail}");
            }
            else
            {
                // UUID and floor validation remain fail-closed in the space
                // validator. Constellation geometry is diagnostic only because
                // Meta may independently relocalize overlapping room floors.
                roomConstellationSolution = new AagRigidPoseRecovery.Solution(
                    Quaternion.identity,
                    Vector3.zero,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    float.PositiveInfinity,
                    float.PositiveInfinity);
                WriteSystem(
                    "room_constellation_warning",
                    $"{strictDetail}; action=diagnostic_only_continue_identity_rotation; "
                    + $"bestEffortFailure={bestEffortWarning}; fallback={bestEffortDetail}");
            }
        }
        else
        {
            WriteSystem("room_constellation_solved", roomConstellationDetail);
        }

        // Target and S3 incidental positions are resolved in individual MRUK
        // floor frames. The constellation solution supplies only a best-effort
        // legacy orientation/fallback for content that has not yet migrated.
        AagRigidPoseRecovery.Solution? s3RecoverySolution = roomConstellationSolution;

        if (!TryResolveFixedTowersFromRoomFloors(
                towersByUuid,
                roomConstellationSolution,
                out var towerRoomLocalDetail,
                out var towerRoomLocalFailure))
        {
            WriteSystem("tower_room_local_failed", towerRoomLocalFailure);
            AbortLoading($"fixed_tower_room_local_failed_{towerRoomLocalFailure}");
            yield break;
        }
        WriteSystem("tower_room_local_resolved", towerRoomLocalDetail);

        var roomLocalLoadDetail = string.Empty;
        var roomLocalLoadFailure = string.Empty;
        var loadedTargetsFromRoomLocal = TrySpawnRoomLocalTargets(
            set, out roomLocalLoadDetail, out roomLocalLoadFailure);
        if (loadedTargetsFromRoomLocal)
        {
            WriteSystem(
                isS3 ? "s3_room_local_load_succeeded" : "room_local_load_succeeded",
                roomLocalLoadDetail);
        }
        else
        {
            WriteSystem("room_local_load_failed", roomLocalLoadFailure);
            AbortLoading($"room_local_catalog_failed_{roomLocalLoadFailure}");
            yield break;
        }

        if (!loadedTargetsFromRoomLocal && !loadedTargetsFromS3Global)
        {
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
                if (TryGetRequiredRoomUuid(set.set_id, pair.Value.marker_id, out var requiredRoomUuid)
                    && !string.Equals(
                        behaviorMetrics.ResolveRoomUuid(anchor.transform.position),
                        requiredRoomUuid.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    anchor.gameObject.SetActive(false);
                    missing.Add(pair.Value);
                    WriteSystem(
                        "anchor_room_mismatch",
                        $"object={pair.Value.marker_id}; uuid={pair.Key}; requiredRoom={requiredRoomUuid}; action=room_constrained_approx");
                }
                else
                {
                    RegisterTarget(pair.Value, anchor.transform, false);
                }
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

        if (string.Equals(set.set_id, AagExperimentSpaceCatalog.Fp1S3, StringComparison.Ordinal)
            && set.HasOffsetCapture)
        {
            var solveFailure = string.Empty;
            var recoveryAttemptCount = 0;
            var recoveryStartedAt = Time.unscaledTime;
            var usedValidatedPairRecovery = false;

            // Quest spatial anchors can report their first localized transform before
            // the complete batch has settled into one tracking frame. S3 needs the
            // recovered transform for missing stones, so retry the unchanged strict
            // three-reference consensus for a short, S3-only stabilization window.
            if (missing.Count > 0)
            {
                RefreshHud("Stabilizing S3 anchor poses...");
                yield return new WaitForSecondsRealtime(S3RigidRecoveryRetryIntervalSeconds);
            }

            do
            {
                recoveryAttemptCount++;
                if (TrySolveRigidRecovery(set, out var solvedRecovery, out solveFailure))
                {
                    s3RecoverySolution = solvedRecovery;
                    break;
                }

                if (missing.Count == 0
                    || Time.unscaledTime - recoveryStartedAt >= S3RigidRecoveryStabilizationSeconds)
                    break;

                RefreshHud($"Stabilizing S3 anchor poses... ({recoveryAttemptCount})");
                yield return new WaitForSecondsRealtime(S3RigidRecoveryRetryIntervalSeconds);
            }
            while (true);

            // Some Quest room relocalizations preserve a long reference axis
            // exactly while one valid third anchor settles a few centimetres
            // beyond the strict least-squares RMS limit. Do not widen that
            // limit. Instead, require the existing two-reference solver plus
            // an independent, non-collinear third point that preserves all
            // three triangle edges within the existing inlier tolerance.
            var validatedPairFailure = "not_attempted";
            if (!s3RecoverySolution.HasValue
                && missing.Count > 0
                && TrySolveValidatedPairRecovery(
                    set,
                    out var validatedPairRecovery,
                    out var validatedPairDetail,
                    out validatedPairFailure))
            {
                s3RecoverySolution = validatedPairRecovery;
                usedValidatedPairRecovery = true;
                WriteSystem("rigid_recovery_validated_pair", validatedPairDetail);
            }
            else if (!s3RecoverySolution.HasValue && missing.Count > 0)
            {
                solveFailure = $"{solveFailure}; validatedPair={validatedPairFailure}";
            }

            if (s3RecoverySolution.HasValue)
            {
                WriteSystem(
                    usedValidatedPairRecovery
                        ? "rigid_recovery_validated_pair_accepted"
                        : "rigid_recovery_stabilized",
                    $"attempts={recoveryAttemptCount}; elapsedSeconds={Time.unscaledTime - recoveryStartedAt:F2}; "
                    + $"strictThresholdsUnchanged=true");
            }
            else if (missing.Count > 0)
            {
                WriteSystem(
                    "rigid_recovery_failed",
                    $"{solveFailure}; attempts={recoveryAttemptCount}; "
                    + $"elapsedSeconds={Time.unscaledTime - recoveryStartedAt:F2}; "
                    + DescribeRigidRecoveryReferences(set));
                AbortLoading($"s3_rigid_recovery_failed_{solveFailure}");
                yield break;
            }
            else
            {
                WriteSystem("rigid_recovery_unavailable", solveFailure);
            }
        }

        if (missing.Count > 0 && set.HasOffsetCapture)
        {
            // FP1-S1 and FP1-S2 deliberately retain their field-tested legacy
            // fallback. The rigid recovery is scoped to S3 so this repair cannot
            // change either already-completed condition.
            if (string.Equals(set.set_id, AagExperimentSpaceCatalog.Fp1S3, StringComparison.Ordinal))
            {
                var recoveryFailure = "solution_unavailable";
                if (!s3RecoverySolution.HasValue
                    || !TrySpawnRigidRecoveredTargets(missing, s3RecoverySolution.Value, out recoveryFailure))
                {
                    WriteSystem("rigid_recovery_failed", recoveryFailure);
                    AbortLoading($"s3_rigid_recovery_failed_{recoveryFailure}");
                    yield break;
                }
            }
            else
            {
                if (!SpawnApproximateTargets(set, missing, out var approximationFailure))
                {
                    WriteSystem("approximation_failed", approximationFailure);
                    AbortLoading($"approximation_failed_{approximationFailure}");
                    yield break;
                }
            }
        }
        }

        if (targets.Count != set.anchors.Count)
        {
            AbortLoading($"target_count_mismatch_ready_{targets.Count}_expected_{set.anchors.Count}");
            yield break;
        }

        FreezeRealTargetAnchors();
        ResolveHorizontalWallPenetrations();
        if (!loadedTargetsFromRoomLocal && !loadedTargetsFromS3Global)
        {
            if (TryCaptureRoomLocalTargets(currentSetId, out var roomLocalCaptureDetail, out var roomLocalCaptureFailure))
                WriteSystem("room_local_calibration_saved", roomLocalCaptureDetail);
            else
                WriteSystem("room_local_calibration_skipped", roomLocalCaptureFailure);
        }

        WriteSystem("tower_load_started",
            $"fixedTowers={towersByUuid.Count}; requested={towersByUuid.Count}; loader=TOWER_ONLY");
        fixedTowerAnchorLoader.LoadCurrentWorldPoses(towersByUuid);
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
        if (isS3 || config.fiducialMarkerAlignmentEnabled || config.fixedSpaceOffsetEnabled)
        {
            var frozenTowerAnchors = fixedTowerAnchorLoader.FreezeRealAnchorPoses();
            var towerFrame = config.fiducialMarkerAlignmentEnabled
                ? "fiducial_zone"
                : config.fixedSpaceOffsetEnabled
                    ? "fixed_global_translation"
                    : "session_global";
            WriteSystem(
                "tower_poses_frozen",
                $"realAnchors={frozenTowerAnchors}; total={towersByUuid.Count}; "
                + $"frame={towerFrame}");
        }

        WriteSystem("incidental_load_started",
            $"set={currentSetId}; objects={incidentalsByUuid.Count}; requested={incidentalsByUuid.Count}; loader=INCIDENTAL_ONLY");
        incidentalAnchorLoader.Load(incidentalsByUuid, s3RecoverySolution, true, currentSetId);
        while (incidentalAnchorLoader.IsLoading)
        {
            RefreshHud($"Loading incidental objects {incidentalAnchorLoader.LoadedCount}/{incidentalsByUuid.Count}...");
            yield return null;
        }
        if (!string.IsNullOrEmpty(incidentalAnchorLoader.LastRecoverySummary))
            WriteSystem("incidental_rigid_recovery", incidentalAnchorLoader.LastRecoverySummary);
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
        var frozenIncidentalAnchors = incidentalAnchorLoader.FreezeRealAnchorPoses();
        WriteSystem("incidental_poses_frozen",
            $"realAnchors={frozenIncidentalAnchors}; total={incidentalsByUuid.Count}");
        var correctedIncidentalWalls = incidentalObjectManager.ResolveHorizontalWallPenetrations(
            (objectId, detail) => WriteSystem("incidental_wall_depenetrated", $"object={objectId}; {detail}"));
        WriteSystem("incidental_wall_validation_complete", $"corrected={correctedIncidentalWalls}");
        foreach (var pair in incidentalsByUuid)
        {
            var sourceMode = incidentalAnchorLoader.ApproximateReasons.TryGetValue(pair.Key, out var reason)
                ? $"APPROX; reason={reason}"
                : "REAL";
            if (!incidentalObjectManager.TryGetSpawnedTransform(pair.Value.object_id, out var incidentalTransform))
                incidentalAnchorLoader.TryGetTransform(pair.Key, out incidentalTransform);
            var incidentalPosition = incidentalTransform != null ? incidentalTransform.position : Vector3.zero;
            WriteSystem("incidental_loaded",
                $"object={pair.Value.object_id}; prefab={pair.Value.prefab_resource_path}; mode={sourceMode}; "
                + $"position=({incidentalPosition.x:F4},{incidentalPosition.y:F4},{incidentalPosition.z:F4})");
        }

        if (!TryRegisterFixedOffsetContent(
                towersByUuid,
                incidentalsByUuid,
                out var fixedOffsetFailure))
        {
            AbortLoading($"fixed_space_offset_failed_{fixedOffsetFailure}");
            yield break;
        }

        if (!TryRegisterFiducialZoneContent(
                towersByUuid,
                incidentalsByUuid,
                out var fiducialRegistrationFailure))
        {
            AbortLoading($"fiducial_content_registration_failed_{fiducialRegistrationFailure}");
            yield break;
        }

        if (!string.IsNullOrEmpty(pendingSpatialIntegrityFailure))
        {
            AbortLoading($"spatial_integrity_changed_during_loading_{pendingSpatialIntegrityFailure}");
            yield break;
        }
        var finalSpaceFailure = experimentSpaceValidator == null ? "validator_missing" : string.Empty;
        if (experimentSpaceValidator == null
            || !experimentSpaceValidator.TryValidateSessionStart(
                headTransform,
                ExpectedStartRoomUuid,
                fiducialStartGatePassed,
                out finalSpaceFailure))
        {
            AbortLoading($"final_space_validation_failed_{finalSpaceFailure}");
            yield break;
        }

        runClockStart = Time.realtimeSinceStartup;
        sampleAccumulator = 0f;
        nextDecisionTime = SessionTime + Mathf.Max(0.5f, config.decisionIntervalSeconds);
        state = SessionState.Running;
        ApplyGuideVisibility();
        ShowParticipantSpawnConfirmation(towersByUuid.Count);
        SetAbortButtonVisible(false);
        if (currentGuideMode == ExperimentGuideMode.VG)
        {
            UpdateVg(headTransform != null ? headTransform.position : Vector3.zero, false);
            WriteVgGuideSnapshot();
        }
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
        ReleaseSessionTrackingSpaceLock();
        CloseRunningOperatorWindow(false);
        StopGuideOutputs();
        loggingManager.CloseSession();
        ClearSpawnedObjects();
        behaviorMetrics.ResetSession();
        sampleAccumulator = 0f;
    }

    private void ClearSpawnedObjects()
    {
        fiducialZoneAlignment?.EndSession();
        fiducialZoneAlignment?.ReleaseAllContent();
        fixedSpaceOffset?.ClearRegisteredContent();
        fiducialStartGatePassed = false;
        ResetInventoryDeliveryDwell(false, "session_clear");
        mismatchFeedbackTowerId = string.Empty;
        currentTowerZoneId = string.Empty;
        currentTowerZoneColor = string.Empty;
        currentTowerZoneEnteredAt = -1f;
        deliveryCompletionFeedbackActive = false;
        transientInventoryFeedbackUntil = -1f;
        inventoryCompletionEndScheduled = false;
        activeInventoryStoreObjectId = string.Empty;
        SetInventoryStoreProgress(0f, 1f, false);
        SetDeliveryProgressText(string.Empty, false);
        pocketedObjectIds.Clear();
        foreach (var instance in approximatedObjects)
            if (instance != null) Destroy(instance);
        approximatedObjects.Clear();
        fixedTowerManager?.ClearRuntimeContents();
        fixedTowerAnchorLoader?.ClearLoadedAnchors();
        incidentalObjectManager?.ClearRuntimeContents();
        incidentalAnchorLoader?.ClearLoadedAnchors();
        targets.Clear();
        RefreshInventoryHud();
        ClearVgMarkers();
        if (anchorLoader != null)
        {
            anchorLoader.ClearPrefabOverrides();
            anchorLoader.ClearLoadedAnchors();
        }
    }

    private bool TryRegisterFixedOffsetContent(
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid,
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> incidentalsByUuid,
        out string failure)
    {
        failure = string.Empty;
        var useAprilTagTranslation = aprilTagTranslationAligner != null
            && aprilTagTranslationAligner.IsApplied;
        if (config == null || (!config.fixedSpaceOffsetEnabled && !useAprilTagTranslation)) return true;
        if (fixedSpaceOffset == null)
        {
            failure = "manager_missing";
            return false;
        }

        var correction = fixedSpaceOffset.CorrectionOffsetMeters;
        if (!AagSpaceOffsetSolver.IsFinite(correction))
        {
            failure = "correction_non_finite";
            return false;
        }
        if (correction.magnitude > AagFixedSpaceOffset.MaximumAcceptedCorrectionMeters)
        {
            failure = $"correction_too_large_{correction.magnitude:F3}m";
            return false;
        }

        fixedSpaceOffset.ClearRegisteredContent();
        var registeredRoots = new HashSet<Transform>();
        foreach (var target in targets.Values.OrderBy(value => value.objectId, StringComparer.Ordinal))
        {
            var root = ResolveTargetRegistrationRoot(target?.transform);
            if (root == null)
            {
                failure = $"target_root_missing_{target?.objectId ?? "null"}";
                return false;
            }
            if (!registeredRoots.Add(root)) continue;
            if (!fixedSpaceOffset.TryRegisterContent(root, $"target:{target.objectId}", out failure))
                return false;
        }

        foreach (var pair in towersByUuid.OrderBy(value => value.Value.towerId, StringComparer.Ordinal))
        {
            if (!fixedTowerAnchorLoader.TryGetTransform(pair.Key, out var towerRoot)
                || towerRoot == null)
            {
                failure = $"tower_root_missing_{pair.Value.towerId}";
                return false;
            }
            if (!registeredRoots.Add(towerRoot)) continue;
            if (!fixedSpaceOffset.TryRegisterContent(
                    towerRoot, $"tower:{pair.Value.towerId}", out failure))
                return false;
        }

        foreach (var pair in incidentalsByUuid.OrderBy(value => value.Value.object_id, StringComparer.Ordinal))
        {
            if (!incidentalAnchorLoader.TryGetTransform(pair.Key, out var incidentalRoot)
                || incidentalRoot == null)
            {
                failure = $"incidental_root_missing_{pair.Value.object_id}";
                return false;
            }
            if (!registeredRoots.Add(incidentalRoot)) continue;
            if (!fixedSpaceOffset.TryRegisterContent(
                    incidentalRoot, $"incidental:{pair.Value.object_id}", out failure))
                return false;
        }

        WriteSystem(
            "fixed_space_offset_applied",
            $"roots={fixedSpaceOffset.RegisteredRootCount}; "
            + $"offset=({correction.x:F4},{correction.y:F4},{correction.z:F4}); "
            + $"horizontalOnly={fixedSpaceOffset.HorizontalOnly}; "
            + $"source={(useAprilTagTranslation ? "ROOM3_APRILTAG" : "FP1ExperimentConfig")}");
        return true;
    }

    private void RegisterTarget(
        AagManualAnchorEntry entry,
        Transform targetTransform,
        bool approximate,
        string sourceModeOverride = null,
        string roomUuidOverride = null)
    {
        if (entry == null || targetTransform == null) return;
        var movableTransform = ConfigureStoneInteraction(targetTransform);
        var roomUuid = string.IsNullOrWhiteSpace(roomUuidOverride)
            ? behaviorMetrics.ResolveRoomUuid(movableTransform.position)
            : roomUuidOverride.Trim();
        var mapping = config.FindRoom(roomUuid);
        var id = string.IsNullOrWhiteSpace(entry.marker_id) ? entry.anchor_uuid : entry.marker_id;
        var bridge = movableTransform.GetComponent<ExperimentObject>()
            ?? movableTransform.gameObject.AddComponent<ExperimentObject>();
        bridge.Initialize(this, id, false, entry.color);

        foreach (var canvas in targetTransform.GetComponentsInChildren<Canvas>(true)) canvas.enabled = false;
        var sourceMode = string.IsNullOrWhiteSpace(sourceModeOverride)
            ? approximate ? "APPROX" : "REAL"
            : sourceModeOverride.Trim();
        targetTransform.name = $"{currentSetId} {id} {sourceMode}";
        targets[id] = new TargetState
        {
            objectId = id,
            color = entry.color ?? string.Empty,
            roomUuid = roomUuid,
            roomId = mapping != null ? mapping.roomId : RoomFallbackId(roomUuid),
            sourceMode = sourceMode,
            transform = movableTransform,
            approximate = approximate,
        };
        WriteObjectEvent("target_loaded", id, string.Empty);
        WriteSystem("target_loaded", $"object={id}; mode={sourceMode}; roomUuid={roomUuid}");
    }

    private bool TryRegisterFiducialZoneContent(
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid,
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> incidentalsByUuid,
        out string failure)
    {
        failure = string.Empty;
        if (config == null || !config.fiducialMarkerAlignmentEnabled) return true;
        if (fiducialZoneAlignment == null)
        {
            failure = "manager_missing";
            return false;
        }

        var registeredRoots = new HashSet<Transform>();
        foreach (var target in targets.Values.OrderBy(value => value.objectId, StringComparer.Ordinal))
        {
            if (target?.transform == null || string.IsNullOrWhiteSpace(target.roomUuid))
            {
                failure = $"target_identity_missing_{target?.objectId ?? "null"}";
                return false;
            }

            var root = ResolveTargetRegistrationRoot(target.transform);
            if (root == null)
            {
                failure = $"target_root_missing_{target.objectId}";
                return false;
            }
            if (!registeredRoots.Add(root)) continue;
            if (!fiducialZoneAlignment.RegisterContent(
                    target.roomUuid,
                    root,
                    $"target:{target.objectId}",
                    out failure))
                return false;
        }

        foreach (var pair in towersByUuid.OrderBy(value => value.Value.towerId, StringComparer.Ordinal))
        {
            if (!AagFixedTowerRoomLocalCatalog.TryGet(pair.Value.towerId, out var placement)
                || !fixedTowerAnchorLoader.TryGetTransform(pair.Key, out var towerRoot)
                || towerRoot == null)
            {
                failure = $"tower_root_or_room_missing_{pair.Value.towerId}";
                return false;
            }
            if (!registeredRoots.Add(towerRoot)) continue;
            if (!fiducialZoneAlignment.RegisterContent(
                    placement.RoomUuid.ToString(),
                    towerRoot,
                    $"tower:{pair.Value.towerId}",
                    out failure))
                return false;
        }

        foreach (var pair in incidentalsByUuid.OrderBy(value => value.Value.object_id, StringComparer.Ordinal))
        {
            if (!incidentalAnchorLoader.TryGetTransform(pair.Key, out var incidentalRoot)
                || incidentalRoot == null)
            {
                failure = $"incidental_root_missing_{pair.Value.object_id}";
                return false;
            }
            var roomUuid = behaviorMetrics.ResolveRoomUuid(incidentalRoot.position);
            if (string.IsNullOrWhiteSpace(roomUuid))
            {
                failure = $"incidental_room_unresolved_{pair.Value.object_id}";
                return false;
            }
            if (!registeredRoots.Add(incidentalRoot)) continue;
            if (!fiducialZoneAlignment.RegisterContent(
                    roomUuid,
                    incidentalRoot,
                    $"incidental:{pair.Value.object_id}",
                    out failure))
                return false;
        }

        WriteSystem(
            "fiducial_content_registration_complete",
            $"roots={registeredRoots.Count}; targets={targets.Count}; "
            + $"towers={towersByUuid.Count}; incidentals={incidentalsByUuid.Count}");
        return true;
    }

    private Transform ResolveTargetRegistrationRoot(Transform targetTransform)
    {
        if (targetTransform == null) return null;
        var approximateRoot = approximatedObjects.FirstOrDefault(value =>
            value != null
            && (value.transform == targetTransform || targetTransform.IsChildOf(value.transform)));
        if (approximateRoot != null) return approximateRoot.transform;

        var spatialAnchor = targetTransform.GetComponentInParent<OVRSpatialAnchor>(true);
        return spatialAnchor != null ? spatialAnchor.transform : targetTransform;
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

    private void FreezeRealTargetAnchors()
    {
        foreach (var target in targets.Values.Where(value => !value.approximate && value.transform != null))
        {
            var spatialAnchor = target.transform.GetComponentInParent<OVRSpatialAnchor>(true);
            if (spatialAnchor == null || !spatialAnchor.enabled) continue;

            // Once the initial pose has been accepted, the task object must
            // stop following late spatial-anchor refinements in every set.
            // A kinematic stone embedded in a wall would otherwise be expelled
            // abruptly as soon as grabbing temporarily enables physics.
            spatialAnchor.enabled = false;
            WriteSystem(
                "target_pose_frozen",
                $"object={target.objectId}; mode=REAL; position=({target.transform.position.x:F4},"
                + $"{target.transform.position.y:F4},{target.transform.position.z:F4})");
        }
    }

    private void ResolveHorizontalWallPenetrations()
    {
        const float minimumCorrectionMeters = 0.01f;
        const float clearanceMeters = 0.01f;
        const float maximumTotalCorrectionMeters = 0.25f;
        const int maximumPasses = 4;

        Physics.SyncTransforms();
        foreach (var target in targets.Values.Where(value => value.transform != null && !value.delivered))
        {
            var start = target.transform.position;
            var totalCorrection = Vector3.zero;
            var stoneColliders = target.transform.GetComponentsInChildren<Collider>(true)
                .Where(value => value != null && value.enabled && !value.isTrigger)
                .ToArray();

            for (var pass = 0; pass < maximumPasses; pass++)
            {
                var correctedThisPass = false;
                foreach (var stoneCollider in stoneColliders)
                {
                    var nearby = Physics.OverlapBox(
                        stoneCollider.bounds.center,
                        stoneCollider.bounds.extents + Vector3.one * clearanceMeters,
                        Quaternion.identity,
                        Physics.AllLayers,
                        QueryTriggerInteraction.Ignore);
                    foreach (var environmentCollider in nearby)
                    {
                        if (environmentCollider == null
                            || environmentCollider == stoneCollider
                            || environmentCollider.transform.IsChildOf(target.transform)
                            || target.transform.IsChildOf(environmentCollider.transform)
                            || environmentCollider.GetComponentInParent<MRUKAnchor>() == null)
                            continue;

                        if (!Physics.ComputePenetration(
                                stoneCollider,
                                stoneCollider.transform.position,
                                stoneCollider.transform.rotation,
                                environmentCollider,
                                environmentCollider.transform.position,
                                environmentCollider.transform.rotation,
                                out var direction,
                                out var distance)
                            || distance < minimumCorrectionMeters
                            || Mathf.Abs(direction.y) >= 0.55f)
                            continue;

                        var remaining = maximumTotalCorrectionMeters - totalCorrection.magnitude;
                        if (remaining <= 0f) break;
                        var correction = direction * Mathf.Min(distance + clearanceMeters, remaining);
                        target.transform.position += correction;
                        totalCorrection += correction;
                        Physics.SyncTransforms();
                        correctedThisPass = true;
                    }
                }
                if (!correctedThisPass || totalCorrection.magnitude >= maximumTotalCorrectionMeters) break;
            }

            if (totalCorrection.sqrMagnitude < minimumCorrectionMeters * minimumCorrectionMeters) continue;
            WriteSystem(
                "target_wall_depenetrated",
                $"object={target.objectId}; distance={totalCorrection.magnitude:F4}; "
                + $"from=({start.x:F4},{start.y:F4},{start.z:F4}); "
                + $"to=({target.transform.position.x:F4},{target.transform.position.y:F4},{target.transform.position.z:F4})");
        }
    }

    private bool TrySpawnRoomLocalTargets(
        AagManualAnchorSetRecord set,
        out string detail,
        out string failure)
    {
        detail = string.Empty;
        failure = string.Empty;
        if (set?.anchors == null || set.anchors.Count != 12)
        {
            failure = "set_requires_12_room_local_targets";
            return false;
        }
        if (!MrukRoomLocalPlacementStore.TryGetSet(set.set_id, out var savedSet, out failure))
            return false;

        var entries = set.anchors.ToDictionary(value => value.marker_id, StringComparer.Ordinal);
        var savedIds = new HashSet<string>(StringComparer.Ordinal);
        var placements = new List<RecoveredTargetPlacement>();
        foreach (var saved in savedSet.placements.OrderBy(value => value.objectId, StringComparer.Ordinal))
        {
            if (!savedIds.Add(saved.objectId)
                || !entries.TryGetValue(saved.objectId, out var entry)
                || !string.Equals(saved.color, entry.color, StringComparison.OrdinalIgnoreCase))
            {
                failure = $"catalog_identity_mismatch_{saved.objectId}";
                return false;
            }
            if (TryGetRequiredRoomUuid(set.set_id, saved.objectId, out var requiredRoomUuid)
                && !string.Equals(saved.roomUuid, requiredRoomUuid.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                failure = $"catalog_required_room_mismatch_{saved.objectId}_{saved.roomUuid}_{requiredRoomUuid}";
                return false;
            }
            if (!MrukRoomLocalPlacementStore.TryResolveWorldPose(
                    saved, out var position, out var rotation, out failure))
                return false;
            if (!AagRecoveryPlacementValidator.TryValidate(position, out var roomIds, out var placementFailure))
            {
                if (!TryRecoverS3VolumePlacement(
                        set.set_id,
                        saved,
                        position,
                        placements,
                        placementFailure,
                        out position,
                        out roomIds,
                        out var recoveryDetail))
                {
                    failure = $"{placementFailure}_{saved.objectId}";
                    return false;
                }

                WriteSystem("target_room_local_volume_rehomed", recoveryDetail);
            }
            if (!roomIds.Split('|').Contains(saved.roomUuid, StringComparer.OrdinalIgnoreCase))
            {
                failure = $"resolved_room_mismatch_{saved.objectId}";
                return false;
            }
            var prefab = spatialAnchorManager.GetAnchorPrefabForColor(entry.color);
            if (prefab == null)
            {
                failure = $"prefab_missing_{saved.objectId}_{entry.color}";
                return false;
            }
            placements.Add(new RecoveredTargetPlacement
            {
                entry = entry,
                prefab = prefab,
                position = position,
                rotation = rotation,
                roomIds = roomIds,
            });
        }
        if (placements.Count != 12 || savedIds.Count != entries.Count)
        {
            failure = $"catalog_target_count_{placements.Count}_expected_{entries.Count}";
            return false;
        }

        foreach (var placement in placements)
        {
            var prefabObject = placement.prefab.gameObject;
            var wasActive = prefabObject.activeSelf;
            GameObject instance;
            try
            {
                prefabObject.SetActive(false);
                instance = Instantiate(prefabObject, placement.position, placement.rotation);
            }
            finally
            {
                prefabObject.SetActive(wasActive);
            }
            foreach (var anchor in instance.GetComponentsInChildren<OVRSpatialAnchor>(true))
                DestroyImmediate(anchor);
            instance.SetActive(true);
            approximatedObjects.Add(instance);
            RegisterTarget(placement.entry, instance.transform, true, "MRUK_ROOM_LOCAL");
            WriteSystem(
                "target_room_local_loaded",
                $"object={placement.entry.marker_id}; rooms={placement.roomIds}; "
                + $"position=({placement.position.x:F4},{placement.position.y:F4},{placement.position.z:F4})");
        }

        detail = $"set={set.set_id}; targets={placements.Count}; calibratedAt={savedSet.calibratedAtUtc}; "
            + $"source={savedSet.calibrationSource}; path={MrukRoomLocalPlacementStore.CatalogPath}";
        return true;
    }

    private static bool TryRecoverS3VolumePlacement(
        string setId,
        MrukRoomLocalPlacementStore.Placement saved,
        Vector3 originalPosition,
        IReadOnlyCollection<RecoveredTargetPlacement> existingPlacements,
        string validationFailure,
        out Vector3 resolvedPosition,
        out string roomIds,
        out string detail)
    {
        resolvedPosition = originalPosition;
        roomIds = string.Empty;
        detail = string.Empty;

        // A rescan can classify a previously valid S3 floor-local pose inside a
        // newly measured table/storage volume. Rehome only volume collisions in
        // the same required room; all other invalid poses remain fail-closed.
        if (!string.Equals(setId, AagExperimentSpaceCatalog.Fp1S3, StringComparison.Ordinal)
            || saved == null
            || !validationFailure.StartsWith("inside_mruk_volume_", StringComparison.Ordinal)
            || !Guid.TryParse(saved.roomUuid, out var roomUuid)
            || !Guid.TryParse(saved.floorAnchorUuid, out var floorUuid))
            return false;

        var room = MRUK.Instance?.Rooms?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == roomUuid);
        var floor = room?.FloorAnchors?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == floorUuid
            && value.PlaneBoundary2D != null && value.PlaneBoundary2D.Count >= 3);
        if (floor == null) return false;

        var localHeight = floor.transform.InverseTransformPoint(originalPosition).z;
        const float minimumSeparationMeters = 1.5f;
        foreach (var candidate in BuildRequiredRoomInteriorCandidates(floor))
        {
            var candidateWorld = floor.transform.TransformPoint(
                new Vector3(candidate.point.x, candidate.point.y, localHeight));
            if (!AagRecoveryPlacementValidator.TryValidate(candidateWorld, out var candidateRoomIds, out _)
                || !candidateRoomIds.Split('|').Contains(roomUuid.ToString(), StringComparer.OrdinalIgnoreCase))
                continue;

            var overlapsExistingTarget = existingPlacements.Any(existing =>
            {
                var delta = candidateWorld - existing.position;
                delta.y = 0f;
                return delta.sqrMagnitude < minimumSeparationMeters * minimumSeparationMeters;
            });
            if (overlapsExistingTarget) continue;

            resolvedPosition = candidateWorld;
            roomIds = candidateRoomIds;
            detail = $"object={saved.objectId}; reason={validationFailure}; room={roomUuid}; "
                + $"floor={floorUuid}; from=({originalPosition.x:F4},{originalPosition.y:F4},{originalPosition.z:F4}); "
                + $"to=({candidateWorld.x:F4},{candidateWorld.y:F4},{candidateWorld.z:F4})";
            return true;
        }

        return false;
    }

    private bool TryCaptureRoomLocalTargets(
        string setId,
        out string detail,
        out string failure)
    {
        var poses = targets.Values
            .Where(value => value != null && value.transform != null && !value.delivered)
            .Select(value => new MrukRoomLocalPlacementStore.PoseInput(
                value.objectId,
                value.color,
                value.transform.position,
                value.transform.rotation))
            .ToArray();
        return MrukRoomLocalPlacementStore.TryCaptureAndSave(setId, poses, out detail, out failure);
    }

    private bool SpawnApproximateTargets(
        AagManualAnchorSetRecord set,
        IReadOnlyCollection<AagManualAnchorEntry> missing,
        out string failure)
    {
        failure = string.Empty;
        var missingUuids = new HashSet<Guid>(missing
            .Select(entry => Guid.TryParse(entry.anchor_uuid, out var uuid) ? uuid : Guid.Empty)
            .Where(uuid => uuid != Guid.Empty));
        var references = new List<(AagManualAnchorEntry entry, Transform transform)>();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
            if (anchorLoader.TryGetLocalizedAnchor(uuid, out var anchor)
                && !anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid)
                && !missingUuids.Contains(uuid))
                references.Add((entry, anchor.transform));
        }
        if (references.Count == 0)
        {
            failure = "no_localized_reference_anchor";
            return false;
        }

        foreach (var entry in missing)
        {
            var reference = references
                .OrderBy(candidate => (candidate.entry.CapturedPosition - entry.CapturedPosition).sqrMagnitude)
                .First();
            var deltaRotation = reference.transform.rotation * Quaternion.Inverse(reference.entry.CapturedRotation);
            var position = reference.transform.position
                + deltaRotation * ((entry.CapturedPosition - reference.entry.CapturedPosition)
                    * AagManualAnchorSetStore.ApproximateOffsetScale);
            var rotation = deltaRotation * entry.CapturedRotation;
            if (!TryApplyRequiredRoomPolicy(set.set_id, entry, position, out position, out var roomPolicyDetail))
            {
                failure = roomPolicyDetail;
                return false;
            }
            var prefab = spatialAnchorManager.GetAnchorPrefabForColor(entry.color);
            if (prefab == null)
            {
                failure = $"prefab_missing_{entry.marker_id}_{entry.color}";
                return false;
            }

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
            if (!string.IsNullOrEmpty(roomPolicyDetail))
                WriteSystem("target_required_room_rehomed", roomPolicyDetail);
        }
        return true;
    }

    private bool TryApplyRequiredRoomPolicy(
        string setId,
        AagManualAnchorEntry entry,
        Vector3 proposedPosition,
        out Vector3 resolvedPosition,
        out string detail)
    {
        resolvedPosition = proposedPosition;
        detail = string.Empty;
        if (!TryGetRequiredRoomUuid(setId, entry?.marker_id, out var requiredRoomUuid)) return true;

        var proposedRoomUuid = behaviorMetrics.ResolveRoomUuid(proposedPosition);
        // The RoomUuid returned immediately after an anchor approximation can be
        // stale.  S2 yellow_2 and green_3 must therefore always be rebuilt from
        // Room3's current floor geometry, rather than trusting that first result.
        // This policy is intentionally scoped to the two explicitly constrained
        // S2 markers; all other stones retain their existing placement behavior.

        var room = MRUK.Instance?.Rooms?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == requiredRoomUuid);
        if (room == null)
        {
            detail = $"required_room_not_loaded_{entry.marker_id}_{requiredRoomUuid}";
            return false;
        }

        var slot = string.Equals(entry.marker_id, "green_3", StringComparison.Ordinal) ? 1 : 0;
        foreach (var floor in room.FloorAnchors
                     .Where(value => value != null && value.Anchor != null
                         && value.PlaneBoundary2D != null && value.PlaneBoundary2D.Count >= 3)
                     .OrderBy(value => value.Anchor.Uuid.ToString(), StringComparer.Ordinal))
        {
            var localHeight = floor.transform.InverseTransformPoint(proposedPosition).z;
            var candidates = BuildRequiredRoomInteriorCandidates(floor);
            var primary = candidates.FirstOrDefault();
            var ordered = slot == 0 || candidates.Count < 2
                ? candidates
                : candidates
                    .OrderByDescending(value => (value.point - primary.point).sqrMagnitude
                        >= RequiredRoomMinimumMarkerSeparationMeters * RequiredRoomMinimumMarkerSeparationMeters)
                    .ThenByDescending(value => value.clearance)
                    .ThenBy(value => value.point.x)
                    .ThenBy(value => value.point.y)
                    .ToList();

            foreach (var candidate in ordered)
            {
                var world = floor.transform.TransformPoint(
                    new Vector3(candidate.point.x, candidate.point.y, localHeight));
                if (!AagRecoveryPlacementValidator.TryValidate(world, out var roomIds, out _)
                    || !roomIds.Split('|').Contains(requiredRoomUuid.ToString(), StringComparer.OrdinalIgnoreCase))
                    continue;

                resolvedPosition = world;
                detail = $"object={entry.marker_id}; requiredRoom={requiredRoomUuid}; "
                    + $"previousRoom={proposedRoomUuid}; floor={floor.Anchor.Uuid}; slot={slot}; "
                    + $"position=({world.x:F4},{world.y:F4},{world.z:F4})";
                return true;
            }
        }

        detail = $"required_room_safe_position_unavailable_{entry.marker_id}_{requiredRoomUuid}";
        return false;
    }

    private static List<(Vector2 point, float clearance)> BuildRequiredRoomInteriorCandidates(MRUKAnchor floor)
    {
        var boundary = floor.PlaneBoundary2D;
        var minimum = boundary[0];
        var maximum = boundary[0];
        foreach (var point in boundary)
        {
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }

        var candidates = new List<(Vector2 point, float clearance)>();
        for (var row = 1; row < RequiredRoomInteriorGridSize; row++)
        {
            for (var column = 1; column < RequiredRoomInteriorGridSize; column++)
            {
                var point = new Vector2(
                    Mathf.Lerp(minimum.x, maximum.x, column / (float)RequiredRoomInteriorGridSize),
                    Mathf.Lerp(minimum.y, maximum.y, row / (float)RequiredRoomInteriorGridSize));
                if (!floor.IsPositionInBoundary(point)) continue;
                candidates.Add((point, DistanceToBoundary(point, boundary)));
            }
        }

        return candidates
            .OrderByDescending(value => value.clearance)
            .ThenBy(value => value.point.x)
            .ThenBy(value => value.point.y)
            .ToList();
    }

    private static float DistanceToBoundary(Vector2 point, IReadOnlyList<Vector2> boundary)
    {
        var minimumSquared = float.PositiveInfinity;
        for (var index = 0; index < boundary.Count; index++)
        {
            var start = boundary[index];
            var end = boundary[(index + 1) % boundary.Count];
            var segment = end - start;
            var denominator = segment.sqrMagnitude;
            var t = denominator <= Mathf.Epsilon
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(point - start, segment) / denominator);
            minimumSquared = Mathf.Min(minimumSquared, (point - (start + segment * t)).sqrMagnitude);
        }
        return Mathf.Sqrt(minimumSquared);
    }

    private static bool TryGetRequiredRoomUuid(string setId, string markerId, out Guid roomUuid)
    {
        if (string.Equals(setId, AagExperimentSpaceCatalog.Fp1S2, StringComparison.Ordinal)
            && (string.Equals(markerId, "yellow_2", StringComparison.Ordinal)
                || string.Equals(markerId, "green_3", StringComparison.Ordinal)))
        {
            roomUuid = AagExperimentSpaceCatalog.Fp1Room3Uuid;
            return true;
        }

        roomUuid = Guid.Empty;
        return false;
    }

    private List<AagRigidPoseRecovery.Reference> CollectS3GlobalReferences(
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid,
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> incidentalsByUuid)
    {
        var references = new List<AagRigidPoseRecovery.Reference>();
        foreach (var pair in towersByUuid.OrderBy(value => value.Value.towerId, StringComparer.Ordinal))
        {
            if (pair.Value == null || !pair.Value.hasFallbackPose
                || !anchorLoader.TryGetLocalizedAnchor(pair.Key, out var anchor)
                || anchorLoader.LocalizationFailuresReadOnly.ContainsKey(pair.Key))
                continue;
            references.Add(new AagRigidPoseRecovery.Reference(
                $"tower:{pair.Value.towerId}",
                pair.Value.fallbackWorldPosition,
                anchor.transform.position));
        }

        foreach (var pair in incidentalsByUuid.OrderBy(value => value.Value.object_id, StringComparer.Ordinal))
        {
            if (pair.Value == null
                || !anchorLoader.TryGetLocalizedAnchor(pair.Key, out var anchor)
                || anchorLoader.LocalizationFailuresReadOnly.ContainsKey(pair.Key))
                continue;
            references.Add(new AagRigidPoseRecovery.Reference(
                $"incidental:{pair.Value.object_id}",
                pair.Value.FallbackPosition,
                anchor.transform.position));
        }
        return references;
    }

    private bool TrySolveS3RoomConstellation(
        out AagRigidPoseRecovery.Solution solution,
        out string failure,
        out string detail,
        bool allowRmsWarning = false)
    {
        solution = default;
        failure = string.Empty;
        detail = string.Empty;
        var references = new List<AagRigidPoseRecovery.Reference>();
        var rooms = MRUK.Instance?.Rooms;
        if (rooms == null)
        {
            failure = "mruk_rooms_unavailable";
            detail = "reason=mruk_rooms_unavailable";
            return false;
        }

        foreach (var pair in S3CanonicalRoomFloorOrigins.OrderBy(value => value.Key))
        {
            var room = rooms.FirstOrDefault(value =>
                value != null && value.Anchor != null && value.Anchor.Uuid == pair.Key);
            var floor = room?.FloorAnchors?.FirstOrDefault(value => value != null);
            if (floor == null) continue;
            references.Add(new AagRigidPoseRecovery.Reference(
                $"room:{pair.Key}",
                pair.Value,
                floor.transform.position));
        }

        var solved = allowRmsWarning
            ? AagRigidPoseRecovery.TrySolveBestEffort(references, out solution, out failure)
            : AagRigidPoseRecovery.TrySolve(references, out solution, out failure);
        detail = $"matchedRooms={references.Count}; loadedRooms={rooms.Count}; "
            + $"deletedUnmappedRequired=false; mode={(allowRmsWarning ? "best_effort" : "strict")}; "
            + $"reason={(string.IsNullOrEmpty(failure) ? "none" : failure)}; "
            + (solved
                ? $"inliers={string.Join("|", solution.InlierIds)}; "
                    + $"outliers={string.Join("|", solution.OutlierIds)}; "
                    + $"rms={solution.RmsResidualMeters:F4}; maxResidual={solution.MaxResidualMeters:F4}; "
                    + $"yaw={solution.Rotation.eulerAngles.y:F3}; "
                    + $"translation=({solution.Translation.x:F4},{solution.Translation.y:F4},{solution.Translation.z:F4})"
                : DescribeReferences(references));
        return solved;
    }

    private bool TryResolveFixedTowersFromRoomFloors(
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid,
        AagRigidPoseRecovery.Solution roomConstellationSolution,
        out string detail,
        out string failure)
    {
        detail = string.Empty;
        failure = string.Empty;
        if (towersByUuid == null
            || towersByUuid.Count != FixedTowerManager.RequiredTowerCount
            || AagFixedTowerRoomLocalCatalog.Count != FixedTowerManager.RequiredTowerCount)
        {
            failure = $"catalog_count_{AagFixedTowerRoomLocalCatalog.Count}_runtime_{towersByUuid?.Count ?? 0}";
            return false;
        }

        var rooms = MRUK.Instance?.Rooms;
        if (rooms == null)
        {
            failure = "mruk_rooms_unavailable";
            return false;
        }

        var rows = new List<string>();
        foreach (var definition in towersByUuid.Values.OrderBy(value => value.towerId, StringComparer.Ordinal))
        {
            if (definition == null
                || !AagFixedTowerRoomLocalCatalog.TryGet(definition.towerId, out var placement))
            {
                failure = $"catalog_entry_missing_{definition?.towerId ?? "null"}";
                return false;
            }

            var room = rooms.FirstOrDefault(value =>
                value != null && value.Anchor != null && value.Anchor.Uuid == placement.RoomUuid);
            var floor = room?.FloorAnchors?.FirstOrDefault(value => value != null);
            if (floor == null)
            {
                failure = $"floor_missing_{definition.towerId}_{placement.RoomUuid}";
                return false;
            }

            var local = placement.FloorLocalPosition;
            if (!floor.IsPositionInBoundary(new Vector2(local.x, local.y)))
            {
                failure = $"outside_floor_{definition.towerId}_{placement.RoomUuid}";
                return false;
            }

            var worldPosition = floor.transform.TransformPoint(local);
            var worldRotation = roomConstellationSolution.TransformRotation(
                placement.CanonicalRotation.normalized);
            if (!AagRigidPoseRecovery.IsFinite(worldPosition)
                || !AagRigidPoseRecovery.IsFinite(worldRotation))
            {
                failure = $"non_finite_pose_{definition.towerId}";
                return false;
            }

            definition.hasFallbackPose = true;
            definition.fallbackWorldPosition = worldPosition;
            definition.fallbackWorldRotation = worldRotation;
            rows.Add(
                $"{definition.towerId}:room={placement.RoomUuid},"
                + $"local=({local.x:F3},{local.y:F3},{local.z:F3}),"
                + $"world=({worldPosition.x:F3},{worldPosition.y:F3},{worldPosition.z:F3})");
        }

        detail = string.Join("|", rows);
        return true;
    }

    private static string DescribeReferences(
        IReadOnlyCollection<AagRigidPoseRecovery.Reference> references)
    {
        var rows = (references ?? Array.Empty<AagRigidPoseRecovery.Reference>())
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(value =>
                $"{value.Id}:captured=({value.CapturedPosition.x:F3},{value.CapturedPosition.y:F3},{value.CapturedPosition.z:F3})"
                + $",current=({value.CurrentPosition.x:F3},{value.CurrentPosition.y:F3},{value.CurrentPosition.z:F3})");
        return $"references={string.Join("|", rows)}";
    }

    private bool BeginSessionTrackingSpaceLock(out string failure)
    {
        failure = string.Empty;
        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        var trackingSpace = cameraRig != null ? cameraRig.trackingSpace : null;
        if (trackingSpace == null)
        {
            failure = "session_tracking_space_unavailable";
            return false;
        }

        lockedTrackingSpace = trackingSpace;
        lockedTrackingSpaceLocalPosition = trackingSpace.localPosition;
        lockedTrackingSpaceLocalRotation = trackingSpace.localRotation;
        pendingSpatialIntegrityFailure = string.Empty;
        sessionTrackingSpaceLockActive = true;
        WriteSystem(
            "session_tracking_space_locked",
            $"worldLockRemainsEnabled={MRUK.Instance != null && MRUK.Instance.EnableWorldLock}; "
            + $"localPosition=({lockedTrackingSpaceLocalPosition.x:F4},{lockedTrackingSpaceLocalPosition.y:F4},{lockedTrackingSpaceLocalPosition.z:F4}); "
            + $"localRotation=({lockedTrackingSpaceLocalRotation.x:F5},{lockedTrackingSpaceLocalRotation.y:F5},"
            + $"{lockedTrackingSpaceLocalRotation.z:F5},{lockedTrackingSpaceLocalRotation.w:F5})");
        return true;
    }

    private bool TrySpawnS3CanonicalTargets(
        AagManualAnchorSetRecord set,
        AagRigidPoseRecovery.Solution solution,
        out string failure)
    {
        failure = string.Empty;
        if (set?.anchors == null || set.anchors.Count != 12 || S3CanonicalTargets.Count != 12)
        {
            failure = "canonical_catalog_count_mismatch";
            return false;
        }

        var placements = new List<RecoveredTargetPlacement>();
        foreach (var entry in set.anchors.OrderBy(value => value.marker_id, StringComparer.Ordinal))
        {
            if (!S3CanonicalTargets.TryGetValue(entry.marker_id, out var canonical))
            {
                failure = $"canonical_pose_missing_{entry.marker_id}";
                return false;
            }
            var prefab = spatialAnchorManager.GetAnchorPrefabForColor(entry.color);
            if (prefab == null)
            {
                failure = $"prefab_missing_{entry.marker_id}_{entry.color}";
                return false;
            }
            var position = solution.TransformPoint(canonical.Position);
            var rotation = solution.TransformRotation(entry.CapturedRotation);
            if (!AagRigidPoseRecovery.IsFinite(position) || !AagRigidPoseRecovery.IsFinite(rotation))
            {
                failure = $"non_finite_pose_{entry.marker_id}";
                return false;
            }
            if (!AagRecoveryPlacementValidator.TryValidate(position, out var roomIds, out var placementFailure)
                || !roomIds.Split('|').Contains(canonical.RoomUuid, StringComparer.OrdinalIgnoreCase))
            {
                failure = $"canonical_pose_invalid_{entry.marker_id}_{placementFailure}";
                return false;
            }
            placements.Add(new RecoveredTargetPlacement
            {
                entry = entry,
                prefab = prefab,
                position = position,
                rotation = rotation,
                roomIds = canonical.RoomUuid,
            });
        }

        foreach (var placement in placements)
        {
            var prefabObject = placement.prefab.gameObject;
            var wasActive = prefabObject.activeSelf;
            GameObject instance;
            try
            {
                prefabObject.SetActive(false);
                instance = Instantiate(prefabObject, placement.position, placement.rotation);
            }
            finally
            {
                prefabObject.SetActive(wasActive);
            }
            foreach (var anchor in instance.GetComponentsInChildren<OVRSpatialAnchor>(true))
                DestroyImmediate(anchor);
            instance.SetActive(true);
            approximatedObjects.Add(instance);
            RegisterTarget(
                placement.entry,
                instance.transform,
                true,
                "S3_SESSION_GLOBAL",
                placement.roomIds);
            WriteSystem(
                "target_session_global_loaded",
                $"object={placement.entry.marker_id}; roomUuid={placement.roomIds}; "
                + $"position=({placement.position.x:F4},{placement.position.y:F4},{placement.position.z:F4})");
        }
        return true;
    }

    private bool TrySolveRigidRecovery(
        AagManualAnchorSetRecord set,
        out AagRigidPoseRecovery.Solution solution,
        out string failure)
    {
        var references = CollectRigidRecoveryReferences(set);

        if (!AagRigidPoseRecovery.TrySolve(references, out solution, out failure))
            return false;

        WriteSystem(
            "rigid_recovery_transform",
            $"set={set.set_id}; references={references.Count}; inliers={string.Join("|", solution.InlierIds)}; "
            + $"outliers={string.Join("|", solution.OutlierIds)}; rms={solution.RmsResidualMeters:F4}; "
            + $"maxResidual={solution.MaxResidualMeters:F4}; yaw={solution.Rotation.eulerAngles.y:F3}; "
            + $"translation=({solution.Translation.x:F4},{solution.Translation.y:F4},{solution.Translation.z:F4})");
        return true;
    }

    private bool TrySolveValidatedPairRecovery(
        AagManualAnchorSetRecord set,
        out AagRigidPoseRecovery.Solution solution,
        out string detail,
        out string failure)
    {
        var references = CollectRigidRecoveryReferences(set);
        if (!AagRigidPoseRecovery.TrySolveValidatedPair(
                references,
                out solution,
                out var referencePair,
                out var validatorId,
                out var maximumTriangleDistanceDelta,
                out failure))
        {
            detail = string.Empty;
            return false;
        }

        detail = $"set={set.set_id}; references={references.Count}; pair={referencePair}; "
            + $"validator={validatorId}; triangleMaxDistanceDelta={maximumTriangleDistanceDelta:F4}; "
            + $"pairRms={solution.RmsResidualMeters:F4}; pairMaxResidual={solution.MaxResidualMeters:F4}; "
            + $"outliers={string.Join("|", solution.OutlierIds)}; yaw={solution.Rotation.eulerAngles.y:F3}; "
            + $"translation=({solution.Translation.x:F4},{solution.Translation.y:F4},{solution.Translation.z:F4}); "
            + $"pairTolerance={AagRigidPoseRecovery.PairDistanceToleranceMeters:F4}; "
            + $"validationTolerance={AagRigidPoseRecovery.InlierToleranceMeters:F4}; "
            + $"rmsTolerance={AagRigidPoseRecovery.MaximumRmsResidualMeters:F4}";
        return true;
    }

    private List<AagRigidPoseRecovery.Reference> CollectRigidRecoveryReferences(
        AagManualAnchorSetRecord set)
    {
        var references = new List<AagRigidPoseRecovery.Reference>();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
            if (!anchorLoader.TryGetLocalizedAnchor(uuid, out var anchor)
                || anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid))
                continue;

            references.Add(new AagRigidPoseRecovery.Reference(
                entry.marker_id,
                entry.CapturedPosition,
                anchor.transform.position));
        }
        return references;
    }

    private string DescribeRigidRecoveryReferences(AagManualAnchorSetRecord set)
    {
        var references = new List<string>();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid)
                || !anchorLoader.TryGetLocalizedAnchor(uuid, out var anchor)
                || anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid))
                continue;

            var position = anchor.transform.position;
            references.Add($"{entry.marker_id}=({position.x:F3},{position.y:F3},{position.z:F3})");
        }

        return $"referenceSnapshot={string.Join("|", references)}";
    }

    private bool TrySpawnRigidRecoveredTargets(
        IReadOnlyCollection<AagManualAnchorEntry> missing,
        AagRigidPoseRecovery.Solution solution,
        out string failure)
    {
        failure = string.Empty;

        var placements = new List<RecoveredTargetPlacement>();
        foreach (var entry in missing.OrderBy(value => value.marker_id, StringComparer.Ordinal))
        {
            var position = solution.TransformPoint(entry.CapturedPosition);
            var rotation = solution.TransformRotation(entry.CapturedRotation);
            if (!AagRigidPoseRecovery.IsFinite(position) || !AagRigidPoseRecovery.IsFinite(rotation))
            {
                failure = $"non_finite_pose_{entry.marker_id}";
                return false;
            }

            if (!AagRecoveryPlacementValidator.TryValidate(position, out var roomIds, out var placementFailure))
            {
                failure = $"{placementFailure}_{entry.marker_id}";
                return false;
            }

            var prefab = spatialAnchorManager.GetAnchorPrefabForColor(entry.color);
            if (prefab == null)
            {
                failure = $"prefab_missing_{entry.marker_id}_{entry.color}";
                return false;
            }

            placements.Add(new RecoveredTargetPlacement
            {
                entry = entry,
                prefab = prefab,
                position = position,
                rotation = rotation,
                roomIds = roomIds,
            });
        }

        foreach (var placement in placements)
        {
            var prefabObject = placement.prefab.gameObject;
            var wasActive = prefabObject.activeSelf;
            GameObject instance;
            try
            {
                prefabObject.SetActive(false);
                instance = Instantiate(prefabObject, placement.position, placement.rotation);
            }
            finally
            {
                prefabObject.SetActive(wasActive);
            }

            var anchorComponent = instance.GetComponent<OVRSpatialAnchor>();
            if (anchorComponent != null) DestroyImmediate(anchorComponent);
            instance.SetActive(true);
            approximatedObjects.Add(instance);
            RegisterTarget(placement.entry, instance.transform, true);
            WriteSystem(
                "target_rigid_recovered",
                $"object={placement.entry.marker_id}; rooms={placement.roomIds}; "
                + $"position=({placement.position.x:F4},{placement.position.y:F4},{placement.position.z:F4})");
        }

        return true;
    }

    private IReadOnlyCollection<AagGuideTarget> BuildAagTargets()
    {
        return targets.Values.Select(target => new AagGuideTarget
        {
            objectId = target.objectId,
            roomUuid = target.roomUuid,
            roomId = target.roomId,
            delivered = target.delivered || target.pocketed || target.collected,
        }).ToArray();
    }

    public void NotifyObjectGrabbed(ExperimentObject experimentObject)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        if (!behaviorMetrics.TryBeginCarry(experimentObject.ObjectId, out var rejectionReason))
        {
            loggingManager.Write(new GrabRejectedLog
            {
                t = SessionTime,
                objectId = experimentObject.ObjectId,
                reason = rejectionReason,
                carriedObjectId = behaviorMetrics.CarriedObjectId,
            }, "grab_rejected");
            // Retain the legacy system row so existing pilot parsers keep working.
            WriteSystem("grab_rejected", $"object={experimentObject.ObjectId}; reason={rejectionReason}");
            return;
        }
        fiducialZoneAlignment?.DetachRegisteredAncestor(
            experimentObject.transform,
            "participant_grabbed");
        WriteObjectEvent("grab", experimentObject.ObjectId, string.Empty);
    }

    public void NotifyObjectDropped(ExperimentObject experimentObject)
    {
        if (state != SessionState.Running || experimentObject == null) return;
        var wasCarried = behaviorMetrics.Carrying != 0
            && string.Equals(behaviorMetrics.CarriedObjectId, experimentObject.ObjectId, StringComparison.Ordinal);
        behaviorMetrics.EndCarry(experimentObject.ObjectId);
        WriteObjectEvent("drop", experimentObject.ObjectId, string.Empty, wasCarried);
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
        return TryMarkTargetDelivered(experimentObject.ObjectId, towerId, true, true);
    }

    private bool TryMarkTargetDelivered(
        string objectId,
        string towerId,
        bool playCompletionAudio,
        bool endSessionIfComplete)
    {
        if (state != SessionState.Running
            || !targets.TryGetValue(objectId, out var target)
            || target.delivered)
            return false;
        target.delivered = true;
        target.pocketed = false;
        target.collected = true;
        if (behaviorMetrics.NotifyTargetFound(objectId, "delivery"))
            aagGuide?.NotifyTargetFound();
        pocketedObjectIds.Remove(objectId);
        behaviorMetrics.ForceClearCarry();
        WriteObjectEvent("delivered", objectId, towerId);
        if (currentGuideMode == ExperimentGuideMode.VG)
            WriteVgGuideState("marker", objectId, "delivered", IsVgMarkerVisible(objectId), "target_delivered");
        if (playCompletionAudio && audioSource != null && correctDeliveryClip != null)
            audioSource.PlayOneShot(correctDeliveryClip, 0.9f);
        RefreshInventoryHud();
        if (endSessionIfComplete && targets.Values.All(value => value.delivered))
            EndSession("all_targets_delivered");
        return true;
    }

    private void WriteObjectEvent(string eventType, string objectId, string towerId, bool wasCarried = false)
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
            sourceMode = target?.sourceMode ?? string.Empty,
            x = position.x,
            y = position.y,
            z = position.z,
            carrying = behaviorMetrics.Carrying,
            wasCarried = wasCarried,
        }, eventType);
    }

    public void EndSession(string reason)
    {
        EndSession(reason, string.Empty);
    }

    public void EndSession(string reason, string abortNote)
    {
        if (state != SessionState.Running) return;
        ResetInventoryDeliveryDwell(true, $"session_end:{reason}");
        UpdateTowerZoneTransition(string.Empty, string.Empty, $"session_end:{reason}");
        state = SessionState.Ending;
        SetAbortButtonVisible(false);
        RestoreHudToHead();
        operatorWindowOpen = false;
        behaviorMetrics.EndSession();
        StopGuideOutputs($"session_end:{reason}");
        WriteSessionEnd(reason, Time.realtimeSinceStartup - runClockStart, abortNote);
        loggingManager.CloseSession();
        ClearSpawnedObjects();
        behaviorMetrics.ResetSession();
        ReleaseSessionTrackingSpaceLock();
        sessionId = string.Empty;
        state = SessionState.Idle;
        RefreshHud($"Previous session ended: {reason}");
    }

    public void AbortSessionFromUi()
    {
        if (state != SessionState.Running) return;
        if (Time.unscaledTime > abortConfirmationExpiresAt)
        {
            abortConfirmationExpiresAt = Time.unscaledTime + 3f;
            if (abortButtonText != null) abortButtonText.text = "RELEASE, HOLD X\nTO CONFIRM";
            WriteSystem("experimenter_abort_armed", "confirmationWindowSeconds=3");
            return;
        }

        EndSession("experimenter_abort", experimenterAbortNote);
    }

    public void SetExperimenterAbortNote(string note)
    {
        experimenterAbortNote = note ?? string.Empty;
    }

    private void AbortLoading(string reason)
    {
        SetAbortButtonVisible(false);
        RestoreHudToHead();
        operatorWindowOpen = false;
        WriteSystem("session_aborted", reason);
        behaviorMetrics.EndSession();
        WriteSessionEnd(reason, 0f, string.Empty);
        loggingManager.CloseSession();
        StopGuideOutputs();
        ClearSpawnedObjects();
        behaviorMetrics.ResetSession();
        ReleaseSessionTrackingSpaceLock();
        sessionId = string.Empty;
        state = SessionState.Idle;
        RefreshHud($"START BLOCKED: {reason}");
    }

    private void ReleaseSessionTrackingSpaceLock()
    {
        sessionTrackingSpaceLockActive = false;
        lockedTrackingSpace = null;
        pendingSpatialIntegrityFailure = string.Empty;
    }

    private void WriteSessionEnd(string reason, float runningSeconds, string abortNote = "")
    {
        loggingManager.Write(new SessionEndLog
        {
            t = SessionTime,
            endedAtUtc = DateTime.UtcNow.ToString("O"),
            reason = reason ?? "unknown",
            abortNote = abortNote?.Trim() ?? string.Empty,
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
        if (!paused) return;

        pendingSpatialIntegrityFailure = "application_paused_or_virtual_lobby_entered";
        if (loggingManager != null && loggingManager.WriterOpen)
        {
            WriteSystem("application_pause", "paused=true; action=fail_closed");
            loggingManager.FlushNow();
        }

        // Entering the Quest lobby or losing immersive focus can cause MRUK to
        // relocalize into a different world frame on return.  Never continue a
        // participant session across that boundary.
        if (state == SessionState.Running)
            EndSession("spatial_integrity_abort", pendingSpatialIntegrityFailure);
    }

    private void OnApplicationQuit()
    {
        if (loggingManager == null || !loggingManager.WriterOpen) return;
        WriteSystem("application_quit", "quit=true");
        behaviorMetrics?.EndSession();
        aagGuide?.StopAndReset("application_quit");
        WriteSessionEnd("application_quit", RunningTime);
        loggingManager.CloseSession();
    }

    private void OnDestroy()
    {
        if (loggingManager != null) loggingManager.CloseSession();
        if (vgCircleSprite != null) Destroy(vgCircleSprite);
        if (vgCircleTexture != null) Destroy(vgCircleTexture);
        if (ownsCorrectDeliveryClip && correctDeliveryClip != null) Destroy(correctDeliveryClip);
        if (ownsDeliveryZoneEntryClip && deliveryZoneEntryClip != null) Destroy(deliveryZoneEntryClip);
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
        rootRect.localPosition = HudHeadLockedLocalPosition;
        rootRect.localRotation = Quaternion.identity;
        rootRect.localScale = HudHeadLockedLocalScale;
        rootRect.sizeDelta = new Vector2(920f, 620f);
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.sortingOrder = 250;
        scaler.dynamicPixelsPerUnit = 10f;
        if (hudRoot.GetComponent<GraphicRaycaster>() == null)
            hudRoot.AddComponent<GraphicRaycaster>();
        if (hudRoot.GetComponent<TrackedDeviceRaycaster>() == null)
            hudRoot.AddComponent<TrackedDeviceRaycaster>();

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
        CreateInventoryHud(rootRect);
        CreateInventoryStoreProgressHud(rootRect);
        CreateDeliveryProgressHud(rootRect);

        vgPanel.anchorMin = new Vector2(1f, 0f);
        vgPanel.anchorMax = new Vector2(1f, 0f);
        vgPanel.pivot = new Vector2(1f, 0f);
        vgPanel.anchoredPosition = new Vector2(-18f, 18f);
        vgPanel.sizeDelta = new Vector2(240f, 240f); // 80% of the previous 300 px map.
        vgUserMarker = CreateMapSymbol(vgPanel, "CameraDirection", "▲", new Color(0.1f, 1f, 0.3f, 1f), 36.4f);
        vgPanel.gameObject.SetActive(false);
        CreateAbortButton(lobbyPanel.GetComponent<RectTransform>());
    }

    private void CreateInventoryHud(RectTransform parent)
    {
        inventoryText = CreateText(parent, "PocketInventory", 27f, TextAlignmentOptions.TopLeft);
        var rect = inventoryText.rectTransform;
        rect.anchorMin = new Vector2(0.02f, 0.62f);
        rect.anchorMax = new Vector2(0.24f, 0.98f);
        rect.pivot = new Vector2(0f, 1f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        inventoryText.color = Color.white;
        inventoryText.textWrappingMode = TextWrappingModes.NoWrap;
        inventoryText.raycastTarget = false;
        inventoryText.gameObject.SetActive(false);
        RefreshInventoryHud();
    }

    private void CreateDeliveryProgressHud(RectTransform parent)
    {
        deliveryProgressText = CreateText(parent, "DeliveryProgress", 64f, TextAlignmentOptions.Center);
        var rect = deliveryProgressText.rectTransform;
        rect.anchorMin = new Vector2(0.25f, 0.40f);
        rect.anchorMax = new Vector2(0.75f, 0.60f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        deliveryProgressText.color = Color.white;
        deliveryProgressText.fontStyle = FontStyles.Bold;
        deliveryProgressText.outlineColor = Color.black;
        deliveryProgressText.outlineWidth = 0.22f;
        deliveryProgressText.textWrappingMode = TextWrappingModes.NoWrap;
        deliveryProgressText.raycastTarget = false;
        DisableUnexpectedDeliveryBackgrounds();
        deliveryProgressText.gameObject.SetActive(false);
    }

    private void CreateInventoryStoreProgressHud(RectTransform parent)
    {
        inventoryStoreProgressText = CreateText(parent, "InventoryStoreProgress", 48f, TextAlignmentOptions.Center);
        var rect = inventoryStoreProgressText.rectTransform;
        rect.anchorMin = new Vector2(0.25f, 0.62f);
        rect.anchorMax = new Vector2(0.75f, 0.78f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        inventoryStoreProgressText.color = Color.white;
        inventoryStoreProgressText.fontStyle = FontStyles.Bold;
        inventoryStoreProgressText.outlineColor = Color.black;
        inventoryStoreProgressText.outlineWidth = 0.22f;
        inventoryStoreProgressText.textWrappingMode = TextWrappingModes.NoWrap;
        inventoryStoreProgressText.raycastTarget = false;
        inventoryStoreProgressText.gameObject.SetActive(false);
    }

    private void RefreshInventoryHud()
    {
        if (inventoryText == null) return;
        inventoryText.gameObject.SetActive(state == SessionState.Running && !operatorWindowOpen);

        var colors = new[] { "red", "blue", "green", "yellow" };
        var lines = new List<string>(colors.Length);
        foreach (var color in colors)
        {
            var delivered = targets.Values.Count(target => target.delivered
                && string.Equals(target.color, color, StringComparison.OrdinalIgnoreCase));
            if (delivered >= 3)
            {
                lines.Add($"{color} done");
                continue;
            }

            var pocketed = targets.Values.Count(target => target.pocketed
                && !target.delivered
                && string.Equals(target.color, color, StringComparison.OrdinalIgnoreCase));
            lines.Add($"{color} {delivered}/3 ({pocketed})");
        }
        inventoryText.text = string.Join("\n", lines);
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

    private void CreateAbortButton(RectTransform parent)
    {
        abortButtonRoot = new GameObject("ExperimenterAbortButton", typeof(RectTransform), typeof(Image), typeof(Button));
        var rect = abortButtonRoot.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, 30f);
        rect.sizeDelta = new Vector2(280f, 78f);
        var image = abortButtonRoot.GetComponent<Image>();
        image.color = new Color(0.7f, 0.04f, 0.04f, 0.94f);
        var button = abortButtonRoot.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(AbortSessionFromUi);
        abortButtonText = CreateText(rect, "Label", 22f, TextAlignmentOptions.Center);
        abortButtonText.text = "HOLD LEFT X\nEND FAILED";
        abortButtonRoot.SetActive(false);
    }

    private void SetAbortButtonVisible(bool visible)
    {
        abortConfirmationExpiresAt = 0f;
        operatorAbortHoldStartedAt = -1f;
        operatorAbortHoldTriggered = false;
        if (abortButtonText != null) abortButtonText.text = "HOLD LEFT X\nEND FAILED";
        if (abortButtonRoot == null) return;
        abortButtonRoot.SetActive(visible);
        if (visible) abortButtonRoot.transform.SetAsLastSibling();
    }

    public void ToggleRunningOperatorWindow()
    {
        if (state != SessionState.Running) return;
        if (operatorWindowOpen)
            CloseRunningOperatorWindow(true);
        else
            OpenRunningOperatorWindow();
    }

    private void OpenRunningOperatorWindow()
    {
        if (state != SessionState.Running || hudRoot == null || lobbyPanel == null) return;
        if (spawnConfirmationRoutine != null)
        {
            StopCoroutine(spawnConfirmationRoutine);
            spawnConfirmationRoutine = null;
        }

        var vgWasVisible = currentGuideMode == ExperimentGuideMode.VG
            && vgPanel != null
            && vgPanel.gameObject.activeSelf;
        // Keep the on-demand operator panel head-locked while it is open. The
        // panel remains completely hidden during the participant run, but when
        // explicitly requested it must stay visible and reachable by the ray.
        RestoreHudToHead();

        operatorWindowOpen = true;
        ResetInventoryDeliveryDwell(true, "operator_window_opened");
        SetDeliveryProgressText(string.Empty, false);
        RefreshInventoryHud();
        lobbyPanel.SetActive(true);
        lobbyPanel.transform.SetAsLastSibling();
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
        SetAbortButtonVisible(true);
        if (operatorText != null)
        {
            operatorText.text = "FP1 OPERATOR\n\n"
                + $"PARTICIPANT  {SelectedParticipantId}\n"
                + $"SET          {currentSetId}\n"
                + $"GUIDE        {currentGuideMode}\n"
                + $"DELIVERED    {targets.Values.Count(target => target.delivered)}/{targets.Count}\n"
                + $"TIME         {RunningTime:F1} s\n\n"
                + "End only if this run has failed.\n"
                + "Hold left X for 0.8 s to arm FAILED END.\n"
                + "Release, then hold X again within 3 s to confirm.\n"
                + "Hold left Y again to close this window.";
        }

        if (vgWasVisible)
            WriteVgGuideState("panel", string.Empty, "hidden", false, "operator_window_opened");
        WriteSystem("operator_window", "state=opened; input=left_Y_hold_0.8s; placement=head_locked_on_demand");
    }

    private void CloseRunningOperatorWindow(bool writeLog)
    {
        if (!operatorWindowOpen)
        {
            SetAbortButtonVisible(false);
            RestoreHudToHead();
            return;
        }

        operatorWindowOpen = false;
        RefreshInventoryHud();
        SetAbortButtonVisible(false);
        if (lobbyPanel != null) lobbyPanel.SetActive(false);
        RestoreHudToHead();
        var showVg = state == SessionState.Running && currentGuideMode == ExperimentGuideMode.VG;
        if (vgPanel != null) vgPanel.gameObject.SetActive(showVg);
        if (!writeLog) return;
        if (showVg)
            WriteVgGuideState("panel", string.Empty, "shown", true, "operator_window_closed");
        WriteSystem("operator_window", "state=closed; input=left_Y_hold_0.8s");
    }

    private void RestoreHudToHead()
    {
        if (hudRoot == null || headTransform == null) return;
        var rootRect = hudRoot.GetComponent<RectTransform>();
        if (rootRect == null) return;
        rootRect.SetParent(headTransform, false);
        rootRect.localPosition = HudHeadLockedLocalPosition;
        rootRect.localRotation = Quaternion.identity;
        rootRect.localScale = HudHeadLockedLocalScale;
    }

    private void RefreshHud(string status)
    {
        if (operatorText == null) return;
        operatorWindowOpen = false;
        SetDeliveryProgressText(string.Empty, false);
        RefreshInventoryHud();
        SetAbortButtonVisible(false);
        RestoreHudToHead();
        lobbyPanel.SetActive(true);
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
        operatorText.text = "FP1 PILOT\n\n"
            + $"PARTICIPANT  {SelectedParticipantId}\n"
            + $"SET          {SelectedSetId}\n"
            + $"GUIDE        {SelectedGuideMode}\n\n"
            + $"{status}\n\n"
            + "X: PARTICIPANT   Y: SET   B: GUIDE   A: PLAY\n"
            + "Keyboard: P / S / G / Enter\n"
            + (config != null && config.fiducialMarkerAlignmentEnabled
                ? $"{fiducialZoneAlignment?.StatusLine ?? "QR MARKERS UNAVAILABLE"}\n"
                    + "Idle setup: look at one room QR, hold RIGHT STICK 1.2 s\n"
                : string.Empty)
            + (aprilTagTranslationAligner != null
                ? $"APRILTAG: {aprilTagTranslationAligner.StatusLine}\n"
                : string.Empty)
            + "During run: hold left Y for operator window\n"
            + "Hold a left-hand pinch for 1.2 s: store stone";
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
            + "Pinch a stone with the left hand to store it (max 3).\n"
            + "Stand by its matching pagoda for 3 seconds to deliver.";
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
        if (currentGuideMode == ExperimentGuideMode.VG)
            WriteVgGuideState("panel", string.Empty, "shown", true, "spawn_confirmation_complete");
        if (abortButtonRoot != null && abortButtonRoot.activeSelf)
            abortButtonRoot.transform.SetAsLastSibling();
    }

    private void ApplyGuideVisibility()
    {
        if (lobbyPanel != null) lobbyPanel.SetActive(false);
        if (aagGuide != null) aagGuide.gameObject.SetActive(currentGuideMode == ExperimentGuideMode.AAG);
        if (vgPanel != null) vgPanel.gameObject.SetActive(currentGuideMode == ExperimentGuideMode.VG);
        if (currentGuideMode == ExperimentGuideMode.VG) EnsureVgTargetMarkers();
        RefreshInventoryHud();
    }

    private void StopGuideOutputs(string interruptionReason = "guide_reset")
    {
        if (spawnConfirmationRoutine != null)
        {
            StopCoroutine(spawnConfirmationRoutine);
            spawnConfirmationRoutine = null;
        }
        if (aagGuide != null)
        {
            aagGuide.StopAndReset(interruptionReason);
            aagGuide.gameObject.SetActive(false);
        }
        if (vgPanel != null) vgPanel.gameObject.SetActive(false);
    }

    private void UpdateVg(Vector3 headPosition, bool logVisibilityChanges = true)
    {
        if (vgPanel == null || headTransform == null) return;
        EnsureVgTargetMarkers();
        var userYaw = headTransform.eulerAngles.y;
        SetMapMarker(vgUserMarker, headPosition, headPosition, userYaw, string.Empty, false);
        // The player arrow always faces the map's north/up direction. The map
        // content below is rotated by the player's current facing direction.
        vgUserMarker.localRotation = Quaternion.identity;
        foreach (var pair in vgTargetMarkers)
        {
            if (!targets.TryGetValue(pair.Key, out var target) || target.transform == null) continue;
            if (target.collected)
            {
                pair.Value.gameObject.SetActive(false);
                vgMarkerVisibility[pair.Key] = false;
            }
            else
                SetMapMarker(pair.Value, target.transform.position, headPosition, userYaw, pair.Key, logVisibilityChanges);
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

    private void SetMapMarker(
        RectTransform marker,
        Vector3 worldPosition,
        Vector3 centerPosition,
        float userYaw,
        string objectId,
        bool logVisibilityChange)
    {
        if (marker == null) return;
        var worldOffset = worldPosition - centerPosition;
        var mapOffset = Quaternion.Euler(0f, -userYaw, 0f) * worldOffset;
        var offset = new Vector2(mapOffset.x, mapOffset.z);
        var visible = offset.sqrMagnitude <= VgMapHalfSpanMeters * VgMapHalfSpanMeters;
        marker.gameObject.SetActive(visible);
        if (!string.IsNullOrEmpty(objectId))
        {
            if (logVisibilityChange
                && vgMarkerVisibility.TryGetValue(objectId, out var previousVisible)
                && previousVisible != visible)
            {
                WriteVgGuideState(
                    "marker",
                    objectId,
                    visible ? "shown" : "hidden",
                    visible,
                    visible ? "entered_visible_radius" : "left_visible_radius");
            }
            vgMarkerVisibility[objectId] = visible;
        }
        if (!visible) return;

        var width = Mathf.Max(1f, vgPanel.rect.width - 36f);
        var height = Mathf.Max(1f, vgPanel.rect.height - 36f);
        marker.anchoredPosition = new Vector2(
            offset.x / VgMapHalfSpanMeters * width * 0.5f,
            offset.y / VgMapHalfSpanMeters * height * 0.5f);
    }

    private void WriteVgGuideSnapshot()
    {
        if (loggingManager == null || !loggingManager.WriterOpen || headTransform == null) return;
        var headPosition = headTransform.position;
        var guides = targets.Values
            .OrderBy(target => target.objectId, StringComparer.Ordinal)
            .Where(target => target.transform != null)
            .Select(target =>
            {
                var offset = target.transform.position - headPosition;
                var direction = offset.sqrMagnitude > 0.000001f ? offset.normalized : Vector3.zero;
                vgTargetMarkers.TryGetValue(target.objectId, out var marker);
                var visible = marker != null && marker.gameObject.activeSelf;
                vgMarkerVisibility[target.objectId] = visible;
                return new VgGuideTargetSnapshot
                {
                    objectId = target.objectId,
                    color = target.color,
                    roomUuid = target.roomUuid,
                    roomId = target.roomId,
                    targetX = target.transform.position.x,
                    targetY = target.transform.position.y,
                    targetZ = target.transform.position.z,
                    directionX = direction.x,
                    directionY = direction.y,
                    directionZ = direction.z,
                    distanceMeters = offset.magnitude,
                    markerX = marker != null ? marker.anchoredPosition.x : 0f,
                    markerY = marker != null ? marker.anchoredPosition.y : 0f,
                    visible = visible,
                    delivered = target.delivered,
                };
            })
            .ToArray();

        loggingManager.Write(new VgGuideSnapshotLog
        {
            t = SessionTime,
            panelVisible = vgPanel != null && vgPanel.gameObject.activeSelf,
            headX = headPosition.x,
            headY = headPosition.y,
            headZ = headPosition.z,
            headYawDegrees = headTransform.eulerAngles.y,
            visibleRadiusMeters = VgMapHalfSpanMeters,
            guideCount = guides.Length,
            vgGuides = guides,
        }, "vg_guide_snapshot");
    }

    private void WriteVgGuideState(
        string scope,
        string objectId,
        string stateName,
        bool visible,
        string reason)
    {
        if (loggingManager == null || !loggingManager.WriterOpen) return;
        targets.TryGetValue(objectId ?? string.Empty, out var target);
        vgTargetMarkers.TryGetValue(objectId ?? string.Empty, out var marker);
        var targetPosition = target?.transform != null ? target.transform.position : Vector3.zero;
        loggingManager.Write(new VgGuideStateLog
        {
            t = SessionTime,
            scope = scope ?? string.Empty,
            objectId = objectId ?? string.Empty,
            state = stateName ?? string.Empty,
            visible = visible,
            reason = reason ?? string.Empty,
            targetX = targetPosition.x,
            targetY = targetPosition.y,
            targetZ = targetPosition.z,
            markerX = marker != null ? marker.anchoredPosition.x : 0f,
            markerY = marker != null ? marker.anchoredPosition.y : 0f,
        }, "vg_guide_state");
    }

    private bool IsVgMarkerVisible(string objectId)
    {
        return !string.IsNullOrEmpty(objectId)
            && vgMarkerVisibility.TryGetValue(objectId, out var visible)
            && visible;
    }

    private void ClearVgMarkers()
    {
        foreach (var marker in vgTargetMarkers.Values)
            if (marker != null) Destroy(marker.gameObject);
        vgTargetMarkers.Clear();
        vgMarkerVisibility.Clear();
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

    private static AudioClip CreateDeliveryZoneEntryClip()
    {
        const int sampleRate = 24000;
        const float durationSeconds = 0.14f;
        var sampleCount = Mathf.CeilToInt(sampleRate * durationSeconds);
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var time = index / (float)sampleRate;
            var envelope = Mathf.Sin(Mathf.PI * Mathf.Clamp01(time / durationSeconds));
            samples[index] = Mathf.Sin(2f * Mathf.PI * 660f * time) * envelope * 0.32f;
        }

        var clip = AudioClip.Create("Delivery Zone Enter", sampleCount, 1, sampleRate, false);
        return clip.SetData(samples, 0) ? clip : null;
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

/// <summary>Shared fail-closed MRUK validation for every rigidly recovered pose.</summary>
public static class AagRecoveryPlacementValidator
{
    private const float InteriorMarginMeters = 0.08f;

    public static bool TryValidate(
        Vector3 worldPosition,
        out string containingRoomUuids,
        out string failure)
    {
        containingRoomUuids = string.Empty;
        failure = string.Empty;
        if (!AagRigidPoseRecovery.IsFinite(worldPosition))
        {
            failure = "non_finite_position";
            return false;
        }

        var rooms = FindContainingAllowedRooms(worldPosition);
        if (rooms.Count == 0)
        {
            failure = "outside_allowed_mruk_floor";
            return false;
        }

        if (TryFindDeepContainingVolume(worldPosition, rooms, out var volumeDetail))
        {
            failure = $"inside_mruk_volume_{volumeDetail}";
            return false;
        }

        containingRoomUuids = string.Join("|", rooms.Select(room => room.Anchor.Uuid.ToString()));
        return true;
    }

    private static List<MRUKRoom> FindContainingAllowedRooms(Vector3 worldPosition)
    {
        var result = new List<MRUKRoom>();
        if (MRUK.Instance == null) return result;

        foreach (var room in MRUK.Instance.Rooms)
        {
            if (room == null || room.Anchor == null || room.Anchor.Uuid == Guid.Empty
                || ExperimentSpaceRuntime.FloorPlan == null
                || !ExperimentSpaceRuntime.FloorPlan.ContainsRoom(room.Anchor.Uuid))
                continue;

            foreach (var floor in room.FloorAnchors)
            {
                if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3)
                    continue;
                var local = floor.transform.InverseTransformPoint(worldPosition);
                if (!floor.IsPositionInBoundary(new Vector2(local.x, local.y))) continue;
                result.Add(room);
                break;
            }
        }

        return result;
    }

    private static bool TryFindDeepContainingVolume(
        Vector3 worldPosition,
        IReadOnlyCollection<MRUKRoom> rooms,
        out string detail)
    {
        foreach (var room in rooms)
        {
            foreach (var anchor in room.Anchors)
            {
                if (anchor == null || !anchor.VolumeBounds.HasValue) continue;
                var bounds = anchor.VolumeBounds.Value;
                if (bounds.size.x <= InteriorMarginMeters * 2f
                    || bounds.size.y <= InteriorMarginMeters * 2f
                    || bounds.size.z <= InteriorMarginMeters * 2f)
                    continue;

                var local = anchor.transform.InverseTransformPoint(worldPosition);
                var minimum = bounds.min + Vector3.one * InteriorMarginMeters;
                var maximum = bounds.max - Vector3.one * InteriorMarginMeters;
                if (local.x <= minimum.x || local.x >= maximum.x
                    || local.y <= minimum.y || local.y >= maximum.y
                    || local.z <= minimum.z || local.z >= maximum.z)
                    continue;

                detail = $"{anchor.Label}_{anchor.Anchor.Uuid}";
                return true;
            }
        }

        detail = string.Empty;
        return false;
    }
}

/// <summary>
/// Fits a yaw-only rigid transform from a simultaneously captured anchor frame
/// into the current Quest frame. Pair candidates provide deterministic RANSAC;
/// a least-squares refit uses only the consensus anchors, so stale UUID bindings
/// cannot pull recovered targets away from their captured layout.
/// </summary>
public static class AagRigidPoseRecovery
{
    public const int MinimumInliers = 3;
    public const float MinimumPairSeparationMeters = 1f;
    public const float PairDistanceToleranceMeters = 0.15f;
    public const float InlierToleranceMeters = 0.25f;
    public const float MaximumRmsResidualMeters = 0.15f;

    public sealed class Reference
    {
        public string Id { get; }
        public Vector3 CapturedPosition { get; }
        public Vector3 CurrentPosition { get; }

        public Reference(string id, Vector3 capturedPosition, Vector3 currentPosition)
        {
            Id = id ?? string.Empty;
            CapturedPosition = capturedPosition;
            CurrentPosition = currentPosition;
        }
    }

    public readonly struct Solution
    {
        public Quaternion Rotation { get; }
        public Vector3 Translation { get; }
        public string[] InlierIds { get; }
        public string[] OutlierIds { get; }
        public float RmsResidualMeters { get; }
        public float MaxResidualMeters { get; }

        public Solution(
            Quaternion rotation,
            Vector3 translation,
            string[] inlierIds,
            string[] outlierIds,
            float rmsResidualMeters,
            float maxResidualMeters)
        {
            Rotation = rotation;
            Translation = translation;
            InlierIds = inlierIds;
            OutlierIds = outlierIds;
            RmsResidualMeters = rmsResidualMeters;
            MaxResidualMeters = maxResidualMeters;
        }

        public Vector3 TransformPoint(Vector3 capturedPosition) => Rotation * capturedPosition + Translation;
        public Quaternion TransformRotation(Quaternion capturedRotation) => Rotation * capturedRotation;
    }

    private sealed class Candidate
    {
        public Quaternion rotation;
        public Vector3 translation;
        public List<Reference> inliers;
        public float rms;
        public float maximum;
        public string signature;
    }

    public static bool TrySolve(
        IReadOnlyList<Reference> source,
        out Solution solution,
        out string failure)
    {
        return TrySolveInternal(source, true, out solution, out failure);
    }

    /// <summary>
    /// Returns the strongest rigid consensus even when its RMS exceeds the
    /// strict experiment-quality threshold. Callers may use this only for
    /// diagnostic orientation/fallback after independently validating every
    /// required room UUID and floor.
    /// </summary>
    public static bool TrySolveBestEffort(
        IReadOnlyList<Reference> source,
        out Solution solution,
        out string qualityWarning)
    {
        return TrySolveInternal(source, false, out solution, out qualityWarning);
    }

    private static bool TrySolveInternal(
        IReadOnlyList<Reference> source,
        bool enforceMaximumRms,
        out Solution solution,
        out string failure)
    {
        solution = default;
        failure = string.Empty;
        var references = (source ?? Array.Empty<Reference>())
            .Where(reference => reference != null
                && !string.IsNullOrWhiteSpace(reference.Id)
                && IsFinite(reference.CapturedPosition)
                && IsFinite(reference.CurrentPosition))
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(reference => reference.Id, StringComparer.Ordinal)
            .ToList();

        if (references.Count < MinimumInliers)
        {
            failure = $"references_{references.Count}_minimum_{MinimumInliers}";
            return false;
        }

        Candidate best = null;
        for (var firstIndex = 0; firstIndex < references.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < references.Count; secondIndex++)
            {
                var first = references[firstIndex];
                var second = references[secondIndex];
                var capturedDelta = second.CapturedPosition - first.CapturedPosition;
                var currentDelta = second.CurrentPosition - first.CurrentPosition;
                capturedDelta.y = 0f;
                currentDelta.y = 0f;
                var capturedDistance = capturedDelta.magnitude;
                var currentDistance = currentDelta.magnitude;
                if (capturedDistance < MinimumPairSeparationMeters
                    || currentDistance < MinimumPairSeparationMeters
                    || Mathf.Abs(capturedDistance - currentDistance) > PairDistanceToleranceMeters)
                    continue;

                var rotation = Quaternion.FromToRotation(capturedDelta, currentDelta);
                var translation = first.CurrentPosition - rotation * first.CapturedPosition;
                var candidate = Score(references, rotation, translation);
                if (candidate.inliers.Count < MinimumInliers) continue;

                if (!TryFitYaw(candidate.inliers, out rotation, out translation)) continue;
                candidate = Score(references, rotation, translation);
                if (candidate.inliers.Count < MinimumInliers) continue;

                // Refit once more after rescoring in case the first consensus
                // step dropped a stale reference or gained a valid one.
                if (!TryFitYaw(candidate.inliers, out rotation, out translation)) continue;
                candidate = Score(references, rotation, translation);
                if (IsBetter(candidate, best)) best = candidate;
            }
        }

        if (best == null)
        {
            failure = "no_three_reference_rigid_consensus";
            return false;
        }
        var inlierIds = best.inliers.Select(reference => reference.Id)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var inlierSet = new HashSet<string>(inlierIds, StringComparer.Ordinal);
        var outlierIds = references.Where(reference => !inlierSet.Contains(reference.Id))
            .Select(reference => reference.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        solution = new Solution(
            best.rotation,
            best.translation,
            inlierIds,
            outlierIds,
            best.rms,
            best.maximum);
        if (best.rms > MaximumRmsResidualMeters)
        {
            failure = $"rms_{best.rms:F4}_maximum_{MaximumRmsResidualMeters:F4}";
            if (enforceMaximumRms) return false;
        }
        return true;
    }

    /// <summary>
    /// Fits the same yaw-only rigid transform when exactly two trustworthy
    /// incidental anchors remain. This is deliberately separate from the
    /// three-point RANSAC path: with only two references there is no outlier
    /// consensus, so callers must use it only for a dedicated two-anchor
    /// fallback after validating pair distance and residuals.
    /// </summary>
    public static bool TrySolveTwoReference(
        IReadOnlyList<Reference> source,
        out Solution solution,
        out string failure)
    {
        solution = default;
        failure = string.Empty;
        var references = (source ?? Array.Empty<Reference>())
            .Where(reference => reference != null
                && !string.IsNullOrWhiteSpace(reference.Id)
                && IsFinite(reference.CapturedPosition)
                && IsFinite(reference.CurrentPosition))
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(reference => reference.Id, StringComparer.Ordinal)
            .ToArray();

        if (references.Length != 2)
        {
            failure = $"references_{references.Length}_requires_exactly_2";
            return false;
        }

        var first = references[0];
        var second = references[1];
        var capturedDelta = second.CapturedPosition - first.CapturedPosition;
        var currentDelta = second.CurrentPosition - first.CurrentPosition;
        capturedDelta.y = 0f;
        currentDelta.y = 0f;
        var capturedDistance = capturedDelta.magnitude;
        var currentDistance = currentDelta.magnitude;
        if (capturedDistance < MinimumPairSeparationMeters
            || currentDistance < MinimumPairSeparationMeters)
        {
            failure = $"pair_separation_{Mathf.Min(capturedDistance, currentDistance):F4}_minimum_{MinimumPairSeparationMeters:F4}";
            return false;
        }
        if (Mathf.Abs(capturedDistance - currentDistance) > PairDistanceToleranceMeters)
        {
            failure = $"pair_distance_delta_{Mathf.Abs(capturedDistance - currentDistance):F4}_maximum_{PairDistanceToleranceMeters:F4}";
            return false;
        }

        var rotation = Quaternion.FromToRotation(capturedDelta, currentDelta);
        var capturedCenter = (first.CapturedPosition + second.CapturedPosition) * 0.5f;
        var currentCenter = (first.CurrentPosition + second.CurrentPosition) * 0.5f;
        var translation = currentCenter - rotation * capturedCenter;
        if (!IsFinite(rotation) || !IsFinite(translation))
        {
            failure = "non_finite_two_reference_transform";
            return false;
        }

        var firstResidual = Vector3.Distance(
            rotation * first.CapturedPosition + translation, first.CurrentPosition);
        var secondResidual = Vector3.Distance(
            rotation * second.CapturedPosition + translation, second.CurrentPosition);
        var maximum = Mathf.Max(firstResidual, secondResidual);
        var rms = Mathf.Sqrt((firstResidual * firstResidual + secondResidual * secondResidual) * 0.5f);
        if (maximum > InlierToleranceMeters)
        {
            failure = $"pair_residual_{maximum:F4}_maximum_{InlierToleranceMeters:F4}";
            return false;
        }
        if (rms > MaximumRmsResidualMeters)
        {
            failure = $"rms_{rms:F4}_maximum_{MaximumRmsResidualMeters:F4}";
            return false;
        }

        solution = new Solution(
            rotation,
            translation,
            references.Select(reference => reference.Id).ToArray(),
            Array.Empty<string>(),
            rms,
            maximum);
        return true;
    }

    /// <summary>
    /// Recovers from a long, distance-preserving reference pair only when a
    /// third, non-collinear reference independently validates the same triangle.
    /// This does not relax the normal three-point fit thresholds: the pair must
    /// pass the existing pair solver and every triangle edge must remain within
    /// the existing inlier tolerance. Matching winding rejects mirrored layouts.
    /// </summary>
    public static bool TrySolveValidatedPair(
        IReadOnlyList<Reference> source,
        out Solution solution,
        out string referencePair,
        out string validatorId,
        out float maximumTriangleDistanceDelta,
        out string failure)
    {
        solution = default;
        referencePair = string.Empty;
        validatorId = string.Empty;
        maximumTriangleDistanceDelta = float.PositiveInfinity;
        failure = string.Empty;

        var references = (source ?? Array.Empty<Reference>())
            .Where(reference => reference != null
                && !string.IsNullOrWhiteSpace(reference.Id)
                && IsFinite(reference.CapturedPosition)
                && IsFinite(reference.CurrentPosition))
            .GroupBy(reference => reference.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(reference => reference.Id, StringComparer.Ordinal)
            .ToArray();
        if (references.Length < MinimumInliers)
        {
            failure = $"references_{references.Length}_minimum_{MinimumInliers}";
            return false;
        }

        Solution bestPairSolution = default;
        Reference bestFirst = null;
        Reference bestSecond = null;
        Reference bestValidator = null;
        var bestMaximumDelta = float.PositiveInfinity;
        var bestPairSeparation = 0f;
        var bestSignature = string.Empty;

        for (var firstIndex = 0; firstIndex < references.Length; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < references.Length; secondIndex++)
            {
                var first = references[firstIndex];
                var second = references[secondIndex];
                if (!TrySolveTwoReference(new[] { first, second }, out var pairSolution, out _))
                    continue;

                var capturedPair = Horizontal(second.CapturedPosition - first.CapturedPosition);
                var currentPair = Horizontal(second.CurrentPosition - first.CurrentPosition);
                var pairSeparation = capturedPair.magnitude;
                var pairDistanceDelta = Mathf.Abs(pairSeparation - currentPair.magnitude);

                for (var validatorIndex = 0; validatorIndex < references.Length; validatorIndex++)
                {
                    if (validatorIndex == firstIndex || validatorIndex == secondIndex) continue;
                    var validator = references[validatorIndex];
                    var capturedFirstDistance = Vector3.Distance(
                        first.CapturedPosition, validator.CapturedPosition);
                    var currentFirstDistance = Vector3.Distance(
                        first.CurrentPosition, validator.CurrentPosition);
                    var capturedSecondDistance = Vector3.Distance(
                        second.CapturedPosition, validator.CapturedPosition);
                    var currentSecondDistance = Vector3.Distance(
                        second.CurrentPosition, validator.CurrentPosition);
                    var firstDistanceDelta = Mathf.Abs(capturedFirstDistance - currentFirstDistance);
                    var secondDistanceDelta = Mathf.Abs(capturedSecondDistance - currentSecondDistance);
                    var maximumDelta = Mathf.Max(
                        pairDistanceDelta,
                        Mathf.Max(firstDistanceDelta, secondDistanceDelta));
                    if (maximumDelta > InlierToleranceMeters) continue;

                    var capturedToValidator = Horizontal(
                        validator.CapturedPosition - first.CapturedPosition);
                    var currentToValidator = Horizontal(
                        validator.CurrentPosition - first.CurrentPosition);
                    var capturedCross = Cross2D(capturedPair, capturedToValidator);
                    var currentCross = Cross2D(currentPair, currentToValidator);
                    var capturedPerpendicular = Mathf.Abs(capturedCross) / capturedPair.magnitude;
                    var currentPerpendicular = Mathf.Abs(currentCross) / currentPair.magnitude;
                    if (capturedPerpendicular < MinimumPairSeparationMeters
                        || currentPerpendicular < MinimumPairSeparationMeters
                        || Mathf.Sign(capturedCross) != Mathf.Sign(currentCross))
                        continue;

                    var signature = $"{first.Id}|{second.Id}|{validator.Id}";
                    var isBetter = bestValidator == null
                        || maximumDelta < bestMaximumDelta - 0.0001f
                        || (Mathf.Abs(maximumDelta - bestMaximumDelta) <= 0.0001f
                            && (pairSeparation > bestPairSeparation + 0.0001f
                                || (Mathf.Abs(pairSeparation - bestPairSeparation) <= 0.0001f
                                    && string.CompareOrdinal(signature, bestSignature) < 0)));
                    if (!isBetter) continue;

                    bestPairSolution = pairSolution;
                    bestFirst = first;
                    bestSecond = second;
                    bestValidator = validator;
                    bestMaximumDelta = maximumDelta;
                    bestPairSeparation = pairSeparation;
                    bestSignature = signature;
                }
            }
        }

        if (bestValidator == null)
        {
            failure = "no_validated_reference_pair";
            return false;
        }

        var inlierIds = new[] { bestFirst.Id, bestSecond.Id, bestValidator.Id }
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var inlierSet = new HashSet<string>(inlierIds, StringComparer.Ordinal);
        var outlierIds = references.Where(reference => !inlierSet.Contains(reference.Id))
            .Select(reference => reference.Id).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        solution = new Solution(
            bestPairSolution.Rotation,
            bestPairSolution.Translation,
            inlierIds,
            outlierIds,
            bestPairSolution.RmsResidualMeters,
            bestPairSolution.MaxResidualMeters);
        referencePair = $"{bestFirst.Id}|{bestSecond.Id}";
        validatorId = bestValidator.Id;
        maximumTriangleDistanceDelta = bestMaximumDelta;
        return true;
    }

    private static Vector3 Horizontal(Vector3 value)
    {
        value.y = 0f;
        return value;
    }

    private static float Cross2D(Vector3 first, Vector3 second) =>
        first.x * second.z - first.z * second.x;

    private static bool TryFitYaw(
        IReadOnlyCollection<Reference> references,
        out Quaternion rotation,
        out Vector3 translation)
    {
        rotation = Quaternion.identity;
        translation = Vector3.zero;
        if (references == null || references.Count < MinimumInliers) return false;

        var capturedCenter = Vector3.zero;
        var currentCenter = Vector3.zero;
        foreach (var reference in references)
        {
            capturedCenter += reference.CapturedPosition;
            currentCenter += reference.CurrentPosition;
        }
        capturedCenter /= references.Count;
        currentCenter /= references.Count;

        double dot = 0d;
        double cross = 0d;
        foreach (var reference in references)
        {
            var captured = reference.CapturedPosition - capturedCenter;
            var current = reference.CurrentPosition - currentCenter;
            dot += captured.x * current.x + captured.z * current.z;
            cross += captured.x * current.z - captured.z * current.x;
        }
        if (Math.Abs(dot) + Math.Abs(cross) < 1e-8d) return false;

        var yawDegrees = (float)(-Math.Atan2(cross, dot) * Mathf.Rad2Deg);
        rotation = Quaternion.Euler(0f, yawDegrees, 0f);
        translation = currentCenter - rotation * capturedCenter;
        return IsFinite(rotation) && IsFinite(translation);
    }

    private static Candidate Score(
        IReadOnlyCollection<Reference> references,
        Quaternion rotation,
        Vector3 translation)
    {
        var inliers = new List<Reference>();
        var sumSquared = 0f;
        var maximum = 0f;
        foreach (var reference in references)
        {
            var residual = Vector3.Distance(
                rotation * reference.CapturedPosition + translation,
                reference.CurrentPosition);
            if (residual > InlierToleranceMeters) continue;
            inliers.Add(reference);
            sumSquared += residual * residual;
            maximum = Mathf.Max(maximum, residual);
        }

        var rms = inliers.Count > 0 ? Mathf.Sqrt(sumSquared / inliers.Count) : float.PositiveInfinity;
        return new Candidate
        {
            rotation = rotation,
            translation = translation,
            inliers = inliers,
            rms = rms,
            maximum = maximum,
            signature = string.Join("|", inliers.Select(reference => reference.Id)
                .OrderBy(value => value, StringComparer.Ordinal)),
        };
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (current == null) return true;
        if (candidate.inliers.Count != current.inliers.Count)
            return candidate.inliers.Count > current.inliers.Count;
        if (!Mathf.Approximately(candidate.rms, current.rms))
            return candidate.rms < current.rms;
        if (!Mathf.Approximately(candidate.maximum, current.maximum))
            return candidate.maximum < current.maximum;
        return string.CompareOrdinal(candidate.signature, current.signature) < 0;
    }

    public static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    public static bool IsFinite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
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
