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
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public enum AagRoomDistributionMode
{
    BalancedAcrossAllRooms,
    AreaWeighted,
}

public enum AagDifficultyDistanceMetric
{
    MeanNearestNeighborDistance,
    MeanPairwiseDistance,
    MaximumPairwiseDistance,
}

/// <summary>
/// Deterministic FP1 placement authoring and preview. Confirmed coordinates are
/// immutable in-app and are stored separately from raw MRUK room exports.
/// </summary>
public sealed class AagFp1PlacementAuthoring : MonoBehaviour
{
    private const string ClearanceDistanceDefinition = "marker bounds edge to MRUK boundary/footprint (horizontal XZ meters)";

    private struct ClearanceMeasurements
    {
        public float wall;
        public float doorway;
        public float obstacle;
    }

    private enum CandidateDiagnosticKind
    {
        Valid,
        FloorOrWallFailure,
        DoorwayFailure,
        ObstacleFailure,
        WalkingPathPenalty,
    }

    private sealed class CandidateDiagnostic
    {
        public Vector3 position;
        public CandidateDiagnosticKind kind;
    }

    private sealed class PlacementCandidate
    {
        public MRUKRoom room;
        public MRUKAnchor floor;
        public Vector2 localPoint;
        public Vector3 floorWorld;
        public Vector3 markerWorld;
        public string zoneKey;
        public int nearestWallSegment;
        public float cornerDistance;
        public float wallClearance;
        public float doorwayClearance;
        public float obstacleClearance;
        public float walkingPathClearance;
        public int discoverableObservationCount;
        public bool visibleFromEntrance;
        public float deterministicVariation;
    }

    private sealed class RoomCandidatePool
    {
        public MRUKRoom room;
        public readonly List<PlacementCandidate> candidates = new List<PlacementCandidate>();
        public int initialCount;
        public int floorPassCount;
        public int wallPassCount;
        public int doorwayPassCount;
        public int obstaclePassCount;
        public int discoverablePassCount;
        public float minimumWidthMeters = float.PositiveInfinity;
        public float aspectRatio;
        public float wallBandAreaSquareMeters;
        public bool isNarrowHall;
    }

    private const string StorageFolderName = "AagFp1Placements";
    private const string JsonFileName = "fp1_confirmed_placements.json";
    private const string CsvFileName = "fp1_confirmed_placements.csv";
    private const string SchemaVersion = "aag-fp1-placement/v2";
    private const int MarkerCountPerSet = 12;
    private const int MarkersPerColor = 3;

    private static readonly string[] SetIds =
    {
        AagExperimentSpaceCatalog.Fp1S1,
        AagExperimentSpaceCatalog.Fp1S2,
        AagExperimentSpaceCatalog.Fp1S3,
    };

    private static readonly string[] ColorNames =
    {
        "red", "blue", "green", "yellow",
    };

    [Header("Scene references")]
    [SerializeField] private AagExperimentSpaceValidator spaceValidator;
    [SerializeField] private Transform hmdTransform;
    [SerializeField] private Camera hmdCamera;

    [Header("Selected preview")]
    [SerializeField, Range(0, 2)] private int initialSetIndex;
    [SerializeField] private bool enableQuestControls = true;
    [SerializeField] private bool enableKeyboardControls = true;

    [Header("PROVISIONAL clearance settings")]
    [Tooltip("PROVISIONAL: minimum horizontal distance from the MRUK floor polygon boundary, in meters.")]
    [SerializeField, Min(0f)] private float minimumWallDistanceMeters = 0.35f;
    [Tooltip("PROVISIONAL: minimum horizontal distance between preview markers, in meters.")]
    [SerializeField, Min(0f)] private float minimumObjectDistanceMeters = 1.0f;
    [Tooltip("PROVISIONAL: vertical gap between the marker renderer bottom and the MRUK floor, in meters.")]
    [SerializeField, Min(0f)] private float floorGapMeters = 0.02f;
    [Tooltip("PROVISIONAL: allowed vertical error when validating renderer-bottom-to-floor alignment.")]
    [SerializeField, Min(0.001f)] private float floorAlignmentToleranceMeters = 0.015f;
    [Tooltip("PROVISIONAL: minimum horizontal clearance from MRUK anchors with volume bounds, in meters.")]
    [SerializeField, Min(0f)] private float minimumObstacleDistanceMeters = 0.5f;
    [Tooltip("PROVISIONAL: extra wall clearance used only while generating candidates; it does not change the required minimum.")]
    [SerializeField, Min(0f)] private float generationSafetyMarginMeters = 0.01f;
    [Tooltip("PROVISIONAL: tiny validator tolerance for floating-point calculation error only; never use this to reduce physical safety clearance.")]
    [SerializeField, Min(0f)] private float validationEpsilonMeters = 0.0001f;

    [Header("PROVISIONAL peripheral / corner placement")]
    [Tooltip("Target minimum horizontal distance from a floor-polygon corner to the marker center.")]
    [SerializeField, Min(0f)] private float minimumCornerDistanceMeters = 0.45f;
    [Tooltip("Target maximum horizontal distance from a floor-polygon corner to the marker center.")]
    [SerializeField, Min(0f)] private float maximumCornerDistanceMeters = 1.25f;
    [Tooltip("Boundary vertices with a wider adjacent-edge angle are not treated as placement corners.")]
    [SerializeField, Range(30f, 175f)] private float maximumCornerAngleDegrees = 145f;
    [Tooltip("Ignores tiny floor-boundary notches whose adjacent edges are shorter than this value.")]
    [SerializeField, Min(0f)] private float minimumCornerAdjacentEdgeLengthMeters = 0.5f;
    [Tooltip("Minimum marker-edge clearance from a DOOR_FRAME footprint.")]
    [SerializeField, Min(0f)] private float minimumDoorwayDistanceMeters = 0.8f;
    [Tooltip("SOFT target distance from the room's main walking axis; candidates inside it are penalized, not rejected.")]
    [SerializeField, Min(0f)] private float minimumWalkingPathDistanceMeters = 0.55f;
    [Tooltip("SOFT target separation between marker bearings in the same room.")]
    [SerializeField, Range(0f, 180f)] private float minimumAngularSeparationDegrees = 65f;

    [Header("PROVISIONAL geometry-aware candidate pool")]
    [Tooltip("Spacing of deterministic floor-grid and long-wall candidates.")]
    [SerializeField, Min(0.05f)] private float candidateGridSpacingMeters = 0.25f;
    [Tooltip("Preferred peripheral band beyond the hard minimum wall clearance.")]
    [SerializeField, Min(0.05f)] private float preferredWallBandWidthMeters = 0.35f;
    [Tooltip("Rooms at or above this length/width ratio use the narrow-Hall scoring profile.")]
    [SerializeField, Min(1f)] private float narrowHallAspectRatioThreshold = 2.5f;
    [Tooltip("Seed-dependent score variation used to produce distinct but reproducible sets.")]
    [SerializeField, Min(0f)] private float deterministicVariationWeight = 0.2f;

    [Header("PROVISIONAL candidate scoring weights")]
    [SerializeField, Min(0f)] private float cornerProximityWeight = 2.0f;
    [SerializeField, Min(0f)] private float wallBandWeight = 2.0f;
    [SerializeField, Min(0f)] private float doorwayDistanceWeight = 0.5f;
    [SerializeField, Min(0f)] private float walkingPathDistanceWeight = 1.25f;
    [SerializeField, Min(0f)] private float entranceVisibilityPenaltyWeight = 1.0f;
    [SerializeField, Min(0f)] private float sameRoomCoVisibilityPenaltyWeight = 2.0f;
    [SerializeField, Min(0f)] private float objectDistanceWeight = 0.75f;
    [SerializeField, Min(0f)] private float sameWallSegmentPenaltyWeight = 0.75f;
    [SerializeField, Min(0f)] private float zoneReusePenaltyWeight = 0.5f;
    [SerializeField, Min(0f)] private float roomDistributionWeight = 1.0f;

    [Header("PROVISIONAL room / zone distribution")]
    [SerializeField] private AagRoomDistributionMode roomDistributionMode = AagRoomDistributionMode.BalancedAcrossAllRooms;
    [Tooltip("SOFT room-count target. Balanced mode prefers all eight rooms but may use a hard-valid fallback.")]
    [SerializeField, Range(1, 8)] private int minimumRoomsUsed = 6;
    [SerializeField, Range(1, 8)] private int zoneGridColumns = 2;
    [SerializeField, Range(1, 8)] private int zoneGridRows = 2;
    [SerializeField, Min(1)] private int maximumMarkersPerZone = 2;

    [Header("PROVISIONAL difficulty comparison")]
    [SerializeField] private AagDifficultyDistanceMetric difficultyDistanceMetric = AagDifficultyDistanceMetric.MeanNearestNeighborDistance;

    [Header("PROVISIONAL visibility / discovery validation")]
    [Tooltip("SOFT target for same-room markers in a sampled HMD-camera view.")]
    [SerializeField, Min(1)] private int maxVisibleObjectsPerView = 1;
    [Tooltip("Each marker must have line of sight from at least this many sampled observation positions.")]
    [SerializeField, Min(1)] private int minimumDiscoverableObservationCount = 1;
    [Tooltip("Eye height above the MRUK floor used for entrance, center, and walking observation samples.")]
    [SerializeField, Min(0.2f)] private float observationEyeHeightMeters = 1.6f;
    [Tooltip("Number of rows and columns in the deterministic walking-position observation grid.")]
    [SerializeField, Range(2, 6)] private int observationGridSize = 3;
    [Tooltip("Minimum distance of an observation point from the floor boundary.")]
    [SerializeField, Min(0f)] private float observationPointWallClearanceMeters = 0.35f;
    [Tooltip("Distance stepped inward from a DOOR_FRAME when creating an entrance observation point.")]
    [SerializeField, Min(0.1f)] private float doorwayObservationInsetMeters = 0.8f;

    [Header("Fixed set seeds")]
    [SerializeField] private int fp1S1Seed = 11031;
    [SerializeField] private int fp1S2Seed = 22063;
    [SerializeField] private int fp1S3Seed = 33091;

    [Header("PROVISIONAL preview appearance")]
    [SerializeField, Min(0.03f)] private float previewMarkerDiameterMeters = 0.18f;
    [Tooltip("Draw candidate-pool diagnostics while this component is selected in the Unity Scene view.")]
    [SerializeField] private bool drawCandidateDiagnostics = true;
    [SerializeField, Min(0.005f)] private float diagnosticPointRadiusMeters = 0.025f;

    private readonly Dictionary<string, List<AagPlacementRecord>> confirmedBySet = new Dictionary<string, List<AagPlacementRecord>>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AagPlacementRecord>> unconfirmedBySet = new Dictionary<string, List<AagPlacementRecord>>(StringComparer.Ordinal);
    private readonly List<GameObject> previewObjects = new List<GameObject>();
    private readonly List<Transform> previewLabels = new List<Transform>();
    private readonly List<CandidateDiagnostic> candidateDiagnostics = new List<CandidateDiagnostic>();

    private int selectedSetIndex;
    private bool isReady;
    private bool isGenerating;
    private TextMeshPro authoringHud;
    private string lastValidationSummary = "No preview";

    private string StorageFolderPath => Path.Combine(Application.persistentDataPath, StorageFolderName);
    private string JsonPath => Path.Combine(StorageFolderPath, JsonFileName);
    private string CsvPath => Path.Combine(StorageFolderPath, CsvFileName);
    private string SelectedSetId => SetIds[selectedSetIndex];
    private float PreviewMarkerRadiusMeters => previewMarkerDiameterMeters * 0.5f;
    private float PreviewObjectBottomOffsetMeters => PreviewMarkerRadiusMeters;

    private IEnumerator Start()
    {
        selectedSetIndex = Mathf.Clamp(initialSetIndex, 0, SetIds.Length - 1);
        CreateAuthoringHud();
        SetHud("Waiting for FP1 validation...");

        if (spaceValidator == null)
        {
            Debug.LogError("[AAG Authoring] AagExperimentSpaceValidator reference is missing.");
            SetHud("ERROR: validator reference missing");
            yield break;
        }

        if (hmdCamera == null)
        {
            hmdCamera = hmdTransform != null ? hmdTransform.GetComponent<Camera>() : Camera.main;
        }

        if (hmdCamera == null)
        {
            Debug.LogError("[AAG Authoring] HMD Camera reference is missing; FOV/frustum validation cannot run.");
            SetHud("ERROR: HMD Camera reference missing");
            yield break;
        }

        while (!spaceValidator.IsValidationComplete)
        {
            yield return null;
        }

        if (!spaceValidator.IsValidationPassed
            || spaceValidator.ValidatedFloorPlan == null
            || !string.Equals(spaceValidator.ValidatedFloorPlan.FloorPlanId, AagExperimentSpaceCatalog.Fp1Id, StringComparison.Ordinal))
        {
            Debug.LogError("[AAG Authoring] Placement authoring disabled because FP1 validation did not pass.");
            SetHud("FP1 VALIDATION FAILED\nAuthoring disabled");
            yield break;
        }

        if (!ValidateSeeds() || !ValidateProvisionalSettings())
        {
            SetHud("ERROR: invalid FP1 authoring settings");
            yield break;
        }

        LoadConfirmedPlacements();
        isReady = true;
        LogProvisionalSettings();
        ShowSelectedSet();
    }

    private void Update()
    {
        if (!isReady || isGenerating)
        {
            return;
        }

        var cycleRequested = false;
        var regenerateRequested = false;
        var confirmRequested = false;

        if (enableKeyboardControls)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                cycleRequested = keyboard.f1Key.wasPressedThisFrame;
                regenerateRequested = keyboard.f2Key.wasPressedThisFrame;
                confirmRequested = keyboard.f3Key.wasPressedThisFrame;
            }
#else
            cycleRequested = Input.GetKeyDown(KeyCode.F1);
            regenerateRequested = Input.GetKeyDown(KeyCode.F2);
            confirmRequested = Input.GetKeyDown(KeyCode.F3);
#endif
        }

        if (enableQuestControls)
        {
            cycleRequested |= OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            regenerateRequested |= OVRInput.GetDown(OVRInput.RawButton.LHandTrigger, OVRInput.Controller.LTouch);
            confirmRequested |= OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);
        }

        if (cycleRequested)
        {
            SelectNextSet();
        }

        if (regenerateRequested)
        {
            RegenerateSelectedSet();
        }

        if (confirmRequested)
        {
            ConfirmSelectedSet();
        }
    }

    private void LateUpdate()
    {
        if (hmdTransform == null)
        {
            return;
        }

        foreach (var label in previewLabels)
        {
            if (label != null)
            {
                var awayFromHead = label.position - hmdTransform.position;
                if (awayFromHead.sqrMagnitude > 0.0001f)
                {
                    label.rotation = Quaternion.LookRotation(awayFromHead.normalized, Vector3.up);
                }
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawCandidateDiagnostics)
        {
            return;
        }

        foreach (var diagnostic in candidateDiagnostics)
        {
            Gizmos.color = diagnostic.kind == CandidateDiagnosticKind.Valid
                ? Color.green
                : diagnostic.kind == CandidateDiagnosticKind.DoorwayFailure
                    ? new Color(1f, 0.45f, 0f, 1f)
                    : diagnostic.kind == CandidateDiagnosticKind.ObstacleFailure
                        ? new Color(0.7f, 0.15f, 1f, 1f)
                        : diagnostic.kind == CandidateDiagnosticKind.WalkingPathPenalty
                            ? Color.yellow
                            : Color.red;
            Gizmos.DrawSphere(diagnostic.position + Vector3.up * 0.015f, diagnosticPointRadiusMeters);
        }

        if (!Application.isPlaying || MRUK.Instance == null)
        {
            return;
        }

        foreach (var room in MRUK.Instance.Rooms.Where(candidate =>
                     AagExperimentSpaceCatalog.Fp1.ContainsRoom(candidate.Anchor.Uuid)
                     || AagExperimentSpaceCatalog.Fp1.IsExcludedRoom(candidate.Anchor.Uuid)))
        {
            var isExcludedRoom = AagExperimentSpaceCatalog.Fp1.IsExcludedRoom(room.Anchor.Uuid);
            foreach (var floor in room.FloorAnchors.Where(candidate => candidate != null && candidate.PlaneBoundary2D != null))
            {
                Gizmos.color = Color.red;
                for (var index = 0; index < floor.PlaneBoundary2D.Count; index++)
                {
                    var start = floor.PlaneBoundary2D[index];
                    var end = floor.PlaneBoundary2D[(index + 1) % floor.PlaneBoundary2D.Count];
                    Gizmos.DrawLine(
                        floor.transform.TransformPoint(new Vector3(start.x, start.y, 0f)),
                        floor.transform.TransformPoint(new Vector3(end.x, end.y, 0f)));
                }

                GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
                if (!isExcludedRoom && TryGetInteriorReferenceLocal(floor, min, max, out var referenceLocal))
                {
                    Vector2 pathStartLocal;
                    Vector2 pathEndLocal;
                    if (max.x - min.x >= max.y - min.y)
                    {
                        pathStartLocal = new Vector2(min.x, referenceLocal.y);
                        pathEndLocal = new Vector2(max.x, referenceLocal.y);
                    }
                    else
                    {
                        pathStartLocal = new Vector2(referenceLocal.x, min.y);
                        pathEndLocal = new Vector2(referenceLocal.x, max.y);
                    }

                    Gizmos.color = Color.yellow;
                    Gizmos.DrawLine(
                        floor.transform.TransformPoint(new Vector3(pathStartLocal.x, pathStartLocal.y, 0f)),
                        floor.transform.TransformPoint(new Vector3(pathEndLocal.x, pathEndLocal.y, 0f)));
                }
            }

            if (isExcludedRoom)
            {
                continue;
            }

            foreach (var doorway in GetDoorwayAnchors(room))
            {
                Gizmos.color = new Color(1f, 0.45f, 0f, 1f);
                Gizmos.DrawWireSphere(
                    doorway.transform.position,
                    minimumDoorwayDistanceMeters + PreviewMarkerRadiusMeters);
            }

            foreach (var obstacle in room.Anchors.Where(anchor => anchor != null && anchor.VolumeBounds.HasValue))
            {
                var previousMatrix = Gizmos.matrix;
                Gizmos.matrix = obstacle.transform.localToWorldMatrix;
                Gizmos.color = new Color(0.7f, 0.15f, 1f, 1f);
                var bounds = obstacle.VolumeBounds.Value;
                bounds.Expand((minimumObstacleDistanceMeters + PreviewMarkerRadiusMeters) * 2f);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
                Gizmos.matrix = previousMatrix;
            }
        }
    }

    public void SelectNextSet()
    {
        if (!isReady)
        {
            return;
        }

        selectedSetIndex = (selectedSetIndex + 1) % SetIds.Length;
        Debug.Log($"[AAG Authoring] Explicit set switch -> {SelectedSetId}");
        ShowSelectedSet();
    }

    public void RegenerateSelectedSet()
    {
        if (!isReady || !spaceValidator.IsValidationPassed || isGenerating)
        {
            return;
        }

        if (confirmedBySet.ContainsKey(SelectedSetId))
        {
            Debug.LogWarning($"[AAG Authoring] {SelectedSetId} is already confirmed; regeneration is blocked and saved coordinates remain unchanged.");
            ShowSelectedSet();
            return;
        }

        StartCoroutine(RegenerateSelectedSetRoutine());
    }

    private IEnumerator RegenerateSelectedSetRoutine()
    {
        isGenerating = true;
        var requestedSetId = SelectedSetId;
        Debug.Log($"[AAG Authoring] LEFT GRIP detected -> generating {requestedSetId}.");
        SetHud($"LEFT GRIP DETECTED\nBUILDING {requestedSetId} CANDIDATE POOLS...\nHard constraints + soft scoring");
        yield return null;

        LogProvisionalSettings();
        if (!TryGenerateSet(requestedSetId, GetSeed(requestedSetId), out var placements, out var resultSummary))
        {
            unconfirmedBySet.Remove(requestedSetId);
            DestroyPreviewObjects();
            lastValidationSummary = resultSummary;
            Debug.LogError($"[AAG Authoring] {requestedSetId} generation failed: {resultSummary}");
            isGenerating = false;
            UpdateHud(false);
            yield break;
        }

        unconfirmedBySet[requestedSetId] = placements;
        lastValidationSummary = resultSummary;
        Debug.Log($"[AAG Authoring] {requestedSetId} deterministic preview generated: {resultSummary}");
        LogAllSetClearanceReport();
        isGenerating = false;
        ShowSelectedSet();
    }

    public void ConfirmSelectedSet()
    {
        if (!isReady || !spaceValidator.IsValidationPassed)
        {
            return;
        }

        if (confirmedBySet.ContainsKey(SelectedSetId))
        {
            Debug.LogWarning($"[AAG Authoring] {SelectedSetId} is already confirmed; saved coordinates were not modified.");
            ShowSelectedSet();
            return;
        }

        if (!unconfirmedBySet.TryGetValue(SelectedSetId, out var preview) || preview.Count != MarkerCountPerSet)
        {
            Debug.LogWarning($"[AAG Authoring] {SelectedSetId} has no complete preview. Regenerate explicitly before confirming.");
            UpdateHud(false);
            return;
        }

        if (!ValidatePlacementSet(preview, out var validationSummary))
        {
            Debug.LogError($"[AAG Authoring] {SelectedSetId} confirmation rejected: {validationSummary}");
            lastValidationSummary = validationSummary;
            UpdateHud(false);
            return;
        }

        confirmedBySet[SelectedSetId] = CloneRecords(preview);
        unconfirmedBySet.Remove(SelectedSetId);

        if (!SaveConfirmedPlacementsAndVerifyRoundTrip())
        {
            confirmedBySet.Remove(SelectedSetId);
            unconfirmedBySet[SelectedSetId] = preview;
            Debug.LogError($"[AAG Authoring] {SelectedSetId} confirmation rolled back because save/reload verification failed.");
            UpdateHud(false);
            return;
        }

        Debug.Log($"[AAG Authoring] {SelectedSetId} confirmed and saved. confirmedRows={GetConfirmedRowCount()}/36");
        LogAllSetClearanceReport();
        ShowSelectedSet();
    }

    private bool TryGenerateSet(string setId, int seed, out List<AagPlacementRecord> placements, out string resultSummary)
    {
        placements = new List<AagPlacementRecord>(MarkerCountPerSet);
        resultSummary = string.Empty;
        candidateDiagnostics.Clear();

        var allowedRooms = GetLoadedAllowedRooms();
        if (allowedRooms.Count != AagExperimentSpaceCatalog.Fp1.RoomIds.Count)
        {
            resultSummary = $"loaded allowed rooms={allowedRooms.Count}, expected={AagExperimentSpaceCatalog.Fp1.RoomIds.Count}";
            return false;
        }

        var random = new System.Random(seed);
        var roomSequence = BuildRoomSequence(allowedRooms, random);
        if (roomSequence.Count != MarkerCountPerSet)
        {
            resultSummary = "room distribution could not produce 12 assignments";
            return false;
        }

        var colors = BuildColorSequence(random);
        var observationCache = new Dictionary<Guid, List<Vector3>>();
        var pools = new Dictionary<Guid, RoomCandidatePool>();
        foreach (var room in allowedRooms)
        {
            var pool = BuildRoomCandidatePool(room, observationCache, seed);
            pools[room.Anchor.Uuid] = pool;
            Debug.Log($"[AAG Candidate Pool] room={room.Anchor.Uuid}; initial={pool.initialCount}; "
                + $"floor={pool.floorPassCount}; wall={pool.wallPassCount}; doorway={pool.doorwayPassCount}; "
                + $"obstacle={pool.obstaclePassCount}; discoverable={pool.discoverablePassCount}; "
                + $"validBeforeObjectDistance={pool.candidates.Count}; width={pool.minimumWidthMeters:F3}m; "
                + $"aspect={pool.aspectRatio:F2}; wallBandArea={pool.wallBandAreaSquareMeters:F3}m2; "
                + $"strategy={(pool.isNarrowHall ? "NARROW_HALL_LONG_WALL" : "ROOM_CORNER_THEN_WALL")}");
        }

        if (!ValidateCandidatePoolFeasibility(pools, out var feasibilityFailure))
        {
            resultSummary = feasibilityFailure;
            return false;
        }

        var colorNumbers = ColorNames.ToDictionary(color => color, _ => 0, StringComparer.Ordinal);
        var zoneCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var selectedCandidates = new List<PlacementCandidate>(MarkerCountPerSet);
        var draftPlacements = placements;
        var softWarnings = new List<string>();
        var selectedRoomCounts = allowedRooms.ToDictionary(room => room.Anchor.Uuid, _ => 0);

        for (var markerIndex = 0; markerIndex < MarkerCountPerSet; markerIndex++)
        {
            var preferredRoom = roomSequence[markerIndex];
            var eligible = pools.Values
                .SelectMany(pool => pool.candidates.Select(candidate => new { Pool = pool, Candidate = candidate }))
                .Where(item => DistanceToNearestPlacementSurface(draftPlacements, item.Candidate.markerWorld) >= minimumObjectDistanceMeters)
                .Select(item => new
                {
                    item.Candidate,
                    item.Pool,
                    Score = ScoreCandidate(item.Candidate, item.Pool, selectedCandidates, draftPlacements, zoneCounts, observationCache)
                        + ScoreRoomDistribution(item.Candidate, preferredRoom, selectedRoomCounts, pools),
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Candidate.deterministicVariation)
                .ToList();

            if (eligible.Count == 0)
            {
                resultSummary = $"candidate selection stopped at marker={markerIndex + 1}/12; "
                    + $"globalValidBeforeObjectDistance={pools.Values.Sum(pool => pool.candidates.Count)}; validAfterObjectDistance=0. "
                    + $"Increase candidate density (lower candidateGridSpacingMeters) or, after a safety review, "
                    + $"decrease minimumObjectDistanceMeters.";
                placements.Clear();
                return false;
            }

            var chosen = eligible.FirstOrDefault(item => CanCompleteRemainingAssignments(
                MarkerCountPerSet - markerIndex - 1,
                pools,
                selectedCandidates,
                item.Candidate));
            if (chosen == null)
            {
                resultSummary = $"candidate look-ahead found no hard-valid completion at marker={markerIndex + 1}/12; "
                    + "Increase pool density by lowering candidateGridSpacingMeters, "
                    + "or inspect minimumObjectDistanceMeters after a safety review.";
                placements.Clear();
                return false;
            }

            var selected = chosen.Candidate;
            selectedCandidates.Add(selected);
            selectedRoomCounts[selected.room.Anchor.Uuid]++;
            zoneCounts.TryGetValue(selected.zoneKey, out var zoneCount);
            zoneCounts[selected.zoneKey] = zoneCount + 1;
            Debug.Log($"[AAG Candidate Select] set={setId}; marker={markerIndex + 1}/12; "
                + $"room={selected.room.Anchor.Uuid}; score={chosen.Score:F6}; corner={selected.cornerDistance:F6}m; "
                + $"wallMeasured={FormatClearance(selected.wallClearance)}m; wallRequired={minimumWallDistanceMeters:F6}m; "
                + $"generationSafetyMargin={generationSafetyMarginMeters:F6}m; validationEpsilon={validationEpsilonMeters:F6}m; "
                + $"doorway={FormatClearance(selected.doorwayClearance)}m; obstacle={FormatClearance(selected.obstacleClearance)}m; "
                + $"obstacleStatus={(float.IsPositiveInfinity(selected.obstacleClearance) ? "NO_NEARBY_OBSTACLE_PASS" : "MEASURED")}; "
                + $"walking={selected.walkingPathClearance:F6}m; markerRadius={PreviewMarkerRadiusMeters:F6}m; "
                + $"discoverable={selected.discoverableObservationCount}; entranceVisible={selected.visibleFromEntrance}; "
                + $"wallSegment={selected.nearestWallSegment}; zone={selected.zoneKey}; definition=\"{ClearanceDistanceDefinition}\"");

            var color = colors[markerIndex];
            var colorNumber = ++colorNumbers[color];
            placements.Add(new AagPlacementRecord
            {
                floor_plan_id = AagExperimentSpaceCatalog.Fp1Id,
                set_id = setId,
                answer_marker_id = $"{setId}-{color.ToUpperInvariant()}-{colorNumber:D2}",
                color = color,
                room_uuid = selected.room.Anchor.Uuid.ToString(),
                world_x = selected.markerWorld.x,
                world_y = selected.markerWorld.y,
                world_z = selected.markerWorld.z,
                seed = seed,
            });

            if (selected.walkingPathClearance < minimumWalkingPathDistanceMeters)
            {
                softWarnings.Add($"{markerIndex + 1}:walkingPath={selected.walkingPathClearance:F2}m");
            }

            if (selected.cornerDistance < minimumCornerDistanceMeters
                || selected.cornerDistance > maximumCornerDistanceMeters)
            {
                softWarnings.Add($"{markerIndex + 1}:corner={selected.cornerDistance:F2}m");
            }

            if (selected.wallClearance > minimumWallDistanceMeters + preferredWallBandWidthMeters)
            {
                softWarnings.Add($"{markerIndex + 1}:outsidePreferredWallBand");
            }

            if (selected.visibleFromEntrance)
            {
                softWarnings.Add($"{markerIndex + 1}:visibleFromEntrance");
            }

            if (selected.room.Anchor.Uuid != preferredRoom.Anchor.Uuid)
            {
                softWarnings.Add($"{markerIndex + 1}:roomDistributionFallback");
            }
        }

        var usedRoomCount = selectedRoomCounts.Count(pair => pair.Value > 0);
        var preferredMinimumRooms = roomDistributionMode == AagRoomDistributionMode.BalancedAcrossAllRooms
            ? allowedRooms.Count
            : minimumRoomsUsed;
        if (usedRoomCount < preferredMinimumRooms)
        {
            softWarnings.Add($"roomsUsed={usedRoomCount}/{preferredMinimumRooms}");
        }

        if (!TryRepairHardInvalidMarkers(
                setId,
                placements,
                selectedCandidates,
                pools,
                observationCache,
                out var repairSummary))
        {
            resultSummary = repairSummary;
            placements.Clear();
            return false;
        }

        if (!string.IsNullOrEmpty(repairSummary))
        {
            softWarnings.Add(repairSummary);
        }

        if (!PopulatePlacementMetrics(placements, observationCache, out var metricFailure))
        {
            resultSummary = metricFailure;
            placements.Clear();
            return false;
        }

        var selectedMaximumVisible = placements.Max(record => record.set_max_visible_objects_per_view);
        if (selectedMaximumVisible > maxVisibleObjectsPerView)
        {
            softWarnings.Add($"maxVisible={selectedMaximumVisible}/{maxVisibleObjectsPerView}");
        }

        foreach (var group in selectedCandidates.GroupBy(candidate => candidate.room.Anchor.Uuid))
        {
            var roomCandidates = group.ToList();
            if (roomCandidates.Count < 2)
            {
                continue;
            }

            var sameSegment = roomCandidates
                .GroupBy(candidate => $"{candidate.floor.Anchor.Uuid}:{candidate.nearestWallSegment}")
                .Any(segment => segment.Count() > 1);
            if (sameSegment)
            {
                softWarnings.Add($"room={group.Key.ToString().Substring(0, 8)}:sameWallSegment");
            }

            for (var index = 0; index < roomCandidates.Count; index++)
            {
                var others = roomCandidates.Where((_, otherIndex) => otherIndex != index).ToList();
                if (GetMinimumBearingSeparation(roomCandidates[index], others) < minimumAngularSeparationDegrees)
                {
                    softWarnings.Add($"room={group.Key.ToString().Substring(0, 8)}:angularSeparation");
                    break;
                }
            }
        }

        if (HasDuplicatePositionCombination(setId, placements))
        {
            resultSummary = "position combination duplicates another FP1 set; adjust the set seed or deterministicVariationWeight";
            placements.Clear();
            return false;
        }

        if (!ValidatePlacementSet(placements, out resultSummary))
        {
            placements.Clear();
            return false;
        }

        if (softWarnings.Count > 0)
        {
            resultSummary += $"; SOFT warnings=[{string.Join(",", softWarnings.Take(12))}]";
        }

        return true;
    }

    private List<MRUKRoom> GetLoadedAllowedRooms()
    {
        var rooms = new List<MRUKRoom>();
        if (MRUK.Instance == null)
        {
            return rooms;
        }

        foreach (var room in MRUK.Instance.Rooms)
        {
            if (AagExperimentSpaceCatalog.Fp1.ContainsRoom(room.Anchor.Uuid))
            {
                rooms.Add(room);
            }
        }

        rooms.Sort((left, right) => string.CompareOrdinal(left.Anchor.Uuid.ToString(), right.Anchor.Uuid.ToString()));
        return rooms;
    }

    private List<MRUKRoom> BuildRoomSequence(List<MRUKRoom> rooms, System.Random random)
    {
        var result = new List<MRUKRoom>(MarkerCountPerSet);
        var shuffledRooms = new List<MRUKRoom>(rooms);
        Shuffle(shuffledRooms, random);

        if (roomDistributionMode == AagRoomDistributionMode.BalancedAcrossAllRooms)
        {
            result.AddRange(shuffledRooms);
            for (var index = 0; result.Count < MarkerCountPerSet; index++)
            {
                result.Add(shuffledRooms[index % shuffledRooms.Count]);
            }
        }
        else
        {
            var requiredDistinctRooms = Mathf.Clamp(minimumRoomsUsed, 1, rooms.Count);
            for (var index = 0; index < requiredDistinctRooms; index++)
            {
                result.Add(shuffledRooms[index]);
            }

            while (result.Count < MarkerCountPerSet)
            {
                result.Add(ChooseRoomByArea(rooms, random));
            }
        }

        Shuffle(result, random);
        return result;
    }

    private static MRUKRoom ChooseRoomByArea(List<MRUKRoom> rooms, System.Random random)
    {
        var totalArea = rooms.Sum(GetRoomFloorArea);
        if (totalArea <= 0f)
        {
            return rooms[random.Next(rooms.Count)];
        }

        var sample = random.NextDouble() * totalArea;
        foreach (var room in rooms)
        {
            sample -= GetRoomFloorArea(room);
            if (sample <= 0d)
            {
                return room;
            }
        }

        return rooms[rooms.Count - 1];
    }

    private RoomCandidatePool BuildRoomCandidatePool(
        MRUKRoom room,
        Dictionary<Guid, List<Vector3>> observationCache,
        int seed)
    {
        var pool = new RoomCandidatePool { room = room };
        var observations = GetObservationPoints(room, observationCache);
        var entranceObservations = GetEntranceObservationPoints(room);
        foreach (var floor in room.FloorAnchors
                     .Where(candidate => candidate != null
                         && candidate.PlaneBoundary2D != null
                         && candidate.PlaneBoundary2D.Count >= 3)
                     .OrderBy(candidate => candidate.Anchor.Uuid.ToString(), StringComparer.Ordinal))
        {
            GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
            var width = Mathf.Min(max.x - min.x, max.y - min.y);
            var length = Mathf.Max(max.x - min.x, max.y - min.y);
            pool.minimumWidthMeters = Mathf.Min(pool.minimumWidthMeters, width);
            pool.aspectRatio = Mathf.Max(pool.aspectRatio, length / Mathf.Max(0.001f, width));
            pool.wallBandAreaSquareMeters += EstimateWallBandArea(room, floor, min, max);

            var rawPoints = BuildGeometryAwareLocalCandidates(floor);
            pool.initialCount += rawPoints.Count;
            foreach (var localPoint in rawPoints)
            {
                var rawWorld = floor.transform.TransformPoint(new Vector3(localPoint.x, localPoint.y, 0f));
                if (!floor.IsPositionInBoundary(localPoint))
                {
                    AddCandidateDiagnostic(rawWorld, CandidateDiagnosticKind.FloorOrWallFailure);
                    continue;
                }

                pool.floorPassCount++;
                var clearances = MeasureClearances(room, floor, rawWorld);
                var targetWallClearance = minimumWallDistanceMeters + generationSafetyMarginMeters;
                if (FailsGenerationClearance(clearances.wall, targetWallClearance))
                {
                    AddCandidateDiagnostic(rawWorld, CandidateDiagnosticKind.FloorOrWallFailure);
                    continue;
                }

                pool.wallPassCount++;
                if (FailsGenerationClearance(clearances.doorway, minimumDoorwayDistanceMeters))
                {
                    AddCandidateDiagnostic(rawWorld, CandidateDiagnosticKind.DoorwayFailure);
                    continue;
                }

                pool.doorwayPassCount++;
                if (FailsGenerationClearance(clearances.obstacle, minimumObstacleDistanceMeters))
                {
                    AddCandidateDiagnostic(rawWorld, CandidateDiagnosticKind.ObstacleFailure);
                    continue;
                }

                pool.obstaclePassCount++;
                var markerWorld = new Vector3(
                    rawWorld.x,
                    rawWorld.y + PreviewObjectBottomOffsetMeters + floorGapMeters,
                    rawWorld.z);
                if (!EvaluateRoomVisibility(
                        room,
                        new List<Vector3> { markerWorld },
                        observations,
                        out var discoveryCounts,
                        out _)
                    || discoveryCounts[0] < minimumDiscoverableObservationCount)
                {
                    continue;
                }

                pool.discoverablePassCount++;
                var validCorners = GetValidCornerIndices(floor);
                var nearestCornerIndex = GetNearestValidCornerIndex(floor, rawWorld, validCorners, out var cornerDistance);
                if (nearestCornerIndex < 0)
                {
                    cornerDistance = GetNearestFloorVertexDistance(floor, rawWorld);
                }
                var walkingPathClearance = DistanceToMainWalkingPaths(floor, rawWorld) - PreviewMarkerRadiusMeters;
                var candidate = new PlacementCandidate
                {
                    room = room,
                    floor = floor,
                    localPoint = localPoint,
                    floorWorld = rawWorld,
                    markerWorld = markerWorld,
                    zoneKey = BuildZoneKey(room, floor, localPoint, min, max),
                    nearestWallSegment = GetNearestBoundarySegmentIndex(floor.PlaneBoundary2D, localPoint),
                    cornerDistance = cornerDistance,
                    wallClearance = clearances.wall,
                    doorwayClearance = clearances.doorway,
                    obstacleClearance = clearances.obstacle,
                    walkingPathClearance = walkingPathClearance,
                    discoverableObservationCount = discoveryCounts[0],
                    visibleFromEntrance = entranceObservations.Any(observation => HasMrukLineOfSight(room, observation, markerWorld)),
                    deterministicVariation = GetDeterministicVariation(seed, room.Anchor.Uuid, localPoint),
                };
                pool.candidates.Add(candidate);

                AddCandidateDiagnostic(
                    rawWorld,
                    walkingPathClearance < minimumWalkingPathDistanceMeters
                        ? CandidateDiagnosticKind.WalkingPathPenalty
                        : CandidateDiagnosticKind.Valid);
            }
        }

        pool.isNarrowHall = pool.aspectRatio >= narrowHallAspectRatioThreshold;
        if (float.IsPositiveInfinity(pool.minimumWidthMeters))
        {
            pool.minimumWidthMeters = 0f;
        }

        return pool;
    }

    private float EstimateWallBandArea(MRUKRoom room, MRUKAnchor floor, Vector2 min, Vector2 max)
    {
        var spacing = Mathf.Max(0.05f, candidateGridSpacingMeters);
        var area = 0f;
        for (var y = min.y + spacing * 0.5f; y <= max.y; y += spacing)
        {
            for (var x = min.x + spacing * 0.5f; x <= max.x; x += spacing)
            {
                var localPoint = new Vector2(x, y);
                if (!floor.IsPositionInBoundary(localPoint))
                {
                    continue;
                }

                var world = floor.transform.TransformPoint(new Vector3(x, y, 0f));
                var clearance = MeasureClearances(room, floor, world).wall;
                if (clearance >= minimumWallDistanceMeters + generationSafetyMarginMeters
                    && clearance <= minimumWallDistanceMeters + preferredWallBandWidthMeters)
                {
                    area += spacing * spacing;
                }
            }
        }

        return area;
    }

    private List<Vector2> BuildGeometryAwareLocalCandidates(MRUKAnchor floor)
    {
        GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
        var result = new List<Vector2>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        void AddUnique(Vector2 point)
        {
            var key = $"{Mathf.RoundToInt(point.x * 1000f)}:{Mathf.RoundToInt(point.y * 1000f)}";
            if (unique.Add(key))
            {
                result.Add(point);
            }
        }

        var spacing = Mathf.Max(0.05f, candidateGridSpacingMeters);
        for (var y = min.y + spacing * 0.5f; y <= max.y; y += spacing)
        {
            for (var x = min.x + spacing * 0.5f; x <= max.x; x += spacing)
            {
                AddUnique(new Vector2(x, y));
            }
        }

        foreach (var cornerIndex in GetValidCornerIndices(floor))
        {
            if (!TryGetInwardCornerDirection(floor, cornerIndex, out var inward, out _))
            {
                continue;
            }

            var minimumRadius = Mathf.Max(
                minimumCornerDistanceMeters,
                GetMinimumCornerRadiusForWallClearance(floor, cornerIndex, inward));
            if (minimumRadius <= maximumCornerDistanceMeters)
            {
                var corner = floor.PlaneBoundary2D[cornerIndex];
                AddUnique(corner + inward * minimumRadius);
                AddUnique(corner + inward * Mathf.Min(maximumCornerDistanceMeters, minimumRadius + spacing));
            }
        }

        var requiredCenterWallDistance = minimumWallDistanceMeters
            + generationSafetyMarginMeters
            + PreviewMarkerRadiusMeters;
        var boundary = floor.PlaneBoundary2D;
        for (var segmentIndex = 0; segmentIndex < boundary.Count; segmentIndex++)
        {
            var start = boundary[segmentIndex];
            var end = boundary[(segmentIndex + 1) % boundary.Count];
            var edge = end - start;
            var length = edge.magnitude;
            if (length < spacing)
            {
                continue;
            }

            var direction = edge / length;
            var alongMargin = Mathf.Min(spacing * 0.5f, length * 0.25f);
            for (var along = alongMargin; along <= length - alongMargin; along += spacing)
            {
                var boundaryPoint = start + direction * along;
                if (!TryGetInwardWallNormal(floor, boundaryPoint, direction, requiredCenterWallDistance, out var inward))
                {
                    continue;
                }

                AddUnique(boundaryPoint + inward * requiredCenterWallDistance);
                AddUnique(boundaryPoint + inward * (requiredCenterWallDistance + preferredWallBandWidthMeters * 0.5f));
            }
        }

        return result;
    }

    private static bool TryGetInwardWallNormal(
        MRUKAnchor floor,
        Vector2 boundaryPoint,
        Vector2 edgeDirection,
        float offset,
        out Vector2 inward)
    {
        inward = new Vector2(-edgeDirection.y, edgeDirection.x);
        var probeDistance = Mathf.Max(0.01f, offset);
        if (floor.IsPositionInBoundary(boundaryPoint + inward * probeDistance))
        {
            return true;
        }

        inward = -inward;
        if (floor.IsPositionInBoundary(boundaryPoint + inward * probeDistance))
        {
            return true;
        }

        inward = Vector2.zero;
        return false;
    }

    private static int GetNearestBoundarySegmentIndex(List<Vector2> boundary, Vector2 point)
    {
        var result = -1;
        var minimum = float.PositiveInfinity;
        for (var index = 0; index < boundary.Count; index++)
        {
            var distance = DistancePointToSegment2D(point, boundary[index], boundary[(index + 1) % boundary.Count]);
            if (distance < minimum)
            {
                minimum = distance;
                result = index;
            }
        }

        return result;
    }

    private static float GetDeterministicVariation(int seed, Guid roomId, Vector2 point)
    {
        unchecked
        {
            var hash = 2166136261u;
            hash = (hash ^ (uint)seed) * 16777619u;
            foreach (var value in roomId.ToByteArray())
            {
                hash = (hash ^ value) * 16777619u;
            }

            hash = (hash ^ (uint)Mathf.RoundToInt(point.x * 1000f)) * 16777619u;
            hash = (hash ^ (uint)Mathf.RoundToInt(point.y * 1000f)) * 16777619u;
            return hash / (float)uint.MaxValue;
        }
    }

    private void AddCandidateDiagnostic(Vector3 position, CandidateDiagnosticKind kind)
    {
        if (!drawCandidateDiagnostics)
        {
            return;
        }

        candidateDiagnostics.Add(new CandidateDiagnostic { position = position, kind = kind });
    }

    private bool ValidateCandidatePoolFeasibility(
        Dictionary<Guid, RoomCandidatePool> pools,
        out string failure)
    {
        failure = string.Empty;
        foreach (var pool in pools.Values.Where(candidate => candidate.candidates.Count == 0))
        {
            Debug.LogWarning($"[AAG Feasibility] room={pool.room.Anchor.Uuid} contributes zero candidates; "
                + $"initial={pool.initialCount}; floor={pool.floorPassCount}; wall={pool.wallPassCount}; "
                + $"doorway={pool.doorwayPassCount}; obstacle={pool.obstaclePassCount}; "
                + $"discoverable={pool.discoverablePassCount}. Room distribution will use a hard-valid fallback. "
                + GetFeasibilityAdjustmentAdvice(pool));
        }

        var totalCandidates = pools.Values.Sum(pool => pool.candidates.Count);
        if (totalCandidates < MarkerCountPerSet)
        {
            failure = $"FEASIBILITY FAILED total valid candidates={totalCandidates}/12 before object-distance selection. "
                + "Decrease candidateGridSpacingMeters or inspect the per-room hard-constraint counts.";
            return false;
        }

        var allCandidates = pools.Values.SelectMany(pool => pool.candidates).ToList();
        if (!HasObjectDistanceCapacity(allCandidates, MarkerCountPerSet))
        {
            failure = $"FEASIBILITY FAILED: the deterministic candidate-packing scan could not find 12 positions "
                + $"at minimumObjectDistanceMeters={minimumObjectDistanceMeters:F3}m from {totalCandidates} hard-valid candidates. "
                + "Decrease candidateGridSpacingMeters first; if the denser pool still fails, decrease minimumObjectDistanceMeters after review.";
            return false;
        }

        Debug.Log($"[AAG Feasibility] PASS: contributingRooms={pools.Values.Count(pool => pool.candidates.Count > 0)}/{pools.Count}, "
            + $"validCandidates={totalCandidates}, requiredMarkers=12, "
            + $"minimumObjectDistance={minimumObjectDistanceMeters:F3}m.");
        return true;
    }

    private bool HasObjectDistanceCapacity(List<PlacementCandidate> candidates, int requiredCount)
    {
        if (requiredCount <= 1)
        {
            return candidates.Count >= requiredCount;
        }

        if (requiredCount == 2)
        {
            for (var first = 0; first < candidates.Count; first++)
            {
                for (var second = first + 1; second < candidates.Count; second++)
                {
                    if (HorizontalDistance(candidates[first].markerWorld, candidates[second].markerWorld)
                            - previewMarkerDiameterMeters
                        >= minimumObjectDistanceMeters)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var orderings = new List<IEnumerable<PlacementCandidate>>
        {
            candidates.OrderByDescending(item => item.deterministicVariation),
            candidates.OrderBy(item => item.markerWorld.x),
            candidates.OrderByDescending(item => item.markerWorld.x),
            candidates.OrderBy(item => item.markerWorld.z),
            candidates.OrderByDescending(item => item.markerWorld.z),
            candidates.OrderBy(item => item.room.Anchor.Uuid.ToString(), StringComparer.Ordinal)
                .ThenByDescending(item => item.deterministicVariation),
        };
        foreach (var ordering in orderings)
        {
            var selected = new List<PlacementCandidate>();
            foreach (var candidate in ordering)
            {
                if (selected.All(existing => HorizontalDistance(existing.markerWorld, candidate.markerWorld)
                        - previewMarkerDiameterMeters
                    >= minimumObjectDistanceMeters))
                {
                    selected.Add(candidate);
                    if (selected.Count >= requiredCount)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool CanCompleteRemainingAssignments(
        int remainingMarkerCount,
        Dictionary<Guid, RoomCandidatePool> pools,
        List<PlacementCandidate> alreadySelected,
        PlacementCandidate proposed)
    {
        if (remainingMarkerCount <= 0)
        {
            return true;
        }

        var fixedCandidates = new List<PlacementCandidate>(alreadySelected) { proposed };
        var available = pools.Values
            .SelectMany(pool => pool.candidates)
            .Where(candidate => fixedCandidates.All(existing =>
                HorizontalDistance(existing.markerWorld, candidate.markerWorld) - previewMarkerDiameterMeters
                >= minimumObjectDistanceMeters))
            .ToList();
        return HasObjectDistanceCapacity(available, remainingMarkerCount);
    }

    private bool TryRepairHardInvalidMarkers(
        string setId,
        List<AagPlacementRecord> placements,
        List<PlacementCandidate> selectedCandidates,
        Dictionary<Guid, RoomCandidatePool> pools,
        Dictionary<Guid, List<Vector3>> observationCache,
        out string summary)
    {
        summary = string.Empty;
        var repairedMarkerIds = new List<string>();
        var rejectedPositions = new HashSet<string>(StringComparer.Ordinal);
        for (var repair = 0; repair < MarkerCountPerSet; repair++)
        {
            if (!TryFindFirstHardInvalidMarker(placements, observationCache, out var invalidIndex, out var failure))
            {
                if (repairedMarkerIds.Count > 0)
                {
                    summary = $"repairedMarkers=[{string.Join(",", repairedMarkerIds)}]";
                }

                return true;
            }

            var invalidRecord = placements[invalidIndex];
            var previousPosition = ToVector3(invalidRecord);
            rejectedPositions.Add(PositionKey(previousPosition));
            var otherPlacements = placements.Where((_, index) => index != invalidIndex).ToList();
            var otherCandidates = selectedCandidates.Where((_, index) => index != invalidIndex).ToList();
            var zoneCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in otherCandidates)
            {
                zoneCounts.TryGetValue(candidate.zoneKey, out var count);
                zoneCounts[candidate.zoneKey] = count + 1;
            }

            var roomCounts = pools.Keys.ToDictionary(roomId => roomId, _ => 0);
            foreach (var candidate in otherCandidates)
            {
                roomCounts[candidate.room.Anchor.Uuid]++;
            }

            var preferredRoom = Guid.TryParse(invalidRecord.room_uuid, out var preferredRoomId)
                && pools.TryGetValue(preferredRoomId, out var preferredPool)
                    ? preferredPool.room
                    : pools.Values.First().room;
            var replacement = pools.Values
                .SelectMany(pool => pool.candidates.Select(candidate => new { Pool = pool, Candidate = candidate }))
                .Where(item => !rejectedPositions.Contains(PositionKey(item.Candidate.markerWorld)))
                .Where(item => DistanceToNearestPlacementSurface(otherPlacements, item.Candidate.markerWorld)
                    >= minimumObjectDistanceMeters)
                .Select(item => new
                {
                    item.Candidate,
                    Score = ScoreCandidate(
                            item.Candidate,
                            item.Pool,
                            otherCandidates,
                            otherPlacements,
                            zoneCounts,
                            observationCache)
                        + ScoreRoomDistribution(item.Candidate, preferredRoom, roomCounts, pools),
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Candidate.deterministicVariation)
                .FirstOrDefault();
            if (replacement == null)
            {
                summary = $"marker repair failed for {invalidRecord.answer_marker_id}: {failure}; "
                    + "no alternate hard-valid candidate remains after object-distance filtering";
                return false;
            }

            var selected = replacement.Candidate;
            invalidRecord.room_uuid = selected.room.Anchor.Uuid.ToString();
            invalidRecord.world_x = selected.markerWorld.x;
            invalidRecord.world_y = selected.markerWorld.y;
            invalidRecord.world_z = selected.markerWorld.z;
            selectedCandidates[invalidIndex] = selected;
            repairedMarkerIds.Add(invalidRecord.answer_marker_id);
            Debug.LogWarning($"[AAG Marker Repair] set={setId}; marker={invalidRecord.answer_marker_id}; "
                + $"reason=\"{failure}\"; old=({previousPosition.x:F6},{previousPosition.y:F6},{previousPosition.z:F6}); "
                + $"new=({selected.markerWorld.x:F6},{selected.markerWorld.y:F6},{selected.markerWorld.z:F6}); "
                + $"newWall={FormatClearance(selected.wallClearance)}m; wallRequired={minimumWallDistanceMeters:F6}m; "
                + $"generationSafetyMargin={generationSafetyMarginMeters:F6}m; validationEpsilon={validationEpsilonMeters:F6}m; "
                + $"markerRadius={PreviewMarkerRadiusMeters:F6}m; definition=\"{ClearanceDistanceDefinition}\"");
        }

        summary = "marker repair limit reached before all hard constraints passed";
        return false;
    }

    private bool TryFindFirstHardInvalidMarker(
        List<AagPlacementRecord> placements,
        Dictionary<Guid, List<Vector3>> observationCache,
        out int invalidIndex,
        out string failure)
    {
        for (var index = 0; index < placements.Count; index++)
        {
            var record = placements[index];
            if (!Guid.TryParse(record.room_uuid, out var roomId)
                || !AagExperimentSpaceCatalog.Fp1.ContainsRoom(roomId)
                || AagExperimentSpaceCatalog.Fp1.IsExcludedRoom(roomId)
                || FindLoadedRoom(roomId) is not MRUKRoom room)
            {
                invalidIndex = index;
                failure = "room UUID is not an allowed loaded FP1 room";
                return true;
            }

            var position = ToVector3(record);
            if (!TryFindContainingFloor(room, position, out var floor, out var floorPoint))
            {
                invalidIndex = index;
                failure = "marker center is outside the MRUK floor polygon";
                return true;
            }

            var expectedCenterY = floorPoint.y + PreviewObjectBottomOffsetMeters + floorGapMeters;
            if (Mathf.Abs(position.y - expectedCenterY) > floorAlignmentToleranceMeters)
            {
                invalidIndex = index;
                failure = $"floor alignment measuredY={position.y:F6}, expectedY={expectedCenterY:F6}";
                return true;
            }

            var measured = MeasureClearances(room, floor, floorPoint);
            if (FailsValidatedClearance(measured.wall, minimumWallDistanceMeters)
                || FailsValidatedClearance(measured.doorway, minimumDoorwayDistanceMeters)
                || FailsValidatedClearance(measured.obstacle, minimumObstacleDistanceMeters))
            {
                invalidIndex = index;
                failure = $"clearance wall={FormatClearance(measured.wall)}/{minimumWallDistanceMeters:F6}, "
                    + $"doorway={FormatClearance(measured.doorway)}/{minimumDoorwayDistanceMeters:F6}, "
                    + $"obstacle={FormatClearance(measured.obstacle)}/{minimumObstacleDistanceMeters:F6}";
                return true;
            }

            for (var other = 0; other < placements.Count; other++)
            {
                if (other == index)
                {
                    continue;
                }

                var objectClearance = HorizontalDistance(position, ToVector3(placements[other]))
                    - previewMarkerDiameterMeters;
                if (FailsValidatedClearance(objectClearance, minimumObjectDistanceMeters))
                {
                    invalidIndex = index;
                    failure = $"object clearance={objectClearance:F6}/{minimumObjectDistanceMeters:F6} against {placements[other].answer_marker_id}";
                    return true;
                }
            }

            var observations = GetObservationPoints(room, observationCache);
            if (!EvaluateRoomVisibility(
                    room,
                    new List<Vector3> { position },
                    observations,
                    out var discoveryCounts,
                    out _)
                || discoveryCounts[0] < minimumDiscoverableObservationCount)
            {
                invalidIndex = index;
                failure = $"discoverable observations={discoveryCounts[0]}/{minimumDiscoverableObservationCount}";
                return true;
            }
        }

        invalidIndex = -1;
        failure = string.Empty;
        return false;
    }

    private static string PositionKey(Vector3 position)
    {
        return $"{position.x:R},{position.y:R},{position.z:R}";
    }

    private string GetFeasibilityAdjustmentAdvice(RoomCandidatePool pool)
    {
        if (pool.initialCount == 0)
        {
            return "No source candidates: decrease candidateGridSpacingMeters or inspect the floor polygon.";
        }

        if (pool.floorPassCount == 0)
        {
            return "Floor removed the full pool: decrease candidateGridSpacingMeters or rescan an invalid floor polygon.";
        }

        if (pool.wallPassCount == 0)
        {
            return $"Wall clearance removed the full pool: roomWidth={pool.minimumWidthMeters:F3}m; "
                + "decrease minimumWallDistanceMeters/previewMarkerDiameterMeters only after confirming physical bounds.";
        }

        if (pool.doorwayPassCount == 0)
        {
            return "Doorway exclusion removed the full pool: verify DOOR_FRAME scan extents; reduce minimumDoorwayDistanceMeters only after a safety review.";
        }

        if (pool.obstaclePassCount == 0)
        {
            return "Obstacle bounds removed the full pool: inspect MRUK furniture bounds/rescan; reduce minimumObstacleDistanceMeters only after a safety review.";
        }

        return "Discoverability removed the full pool: inspect observation points/occlusion scan or lower minimumDiscoverableObservationCount.";
    }

    private float ScoreCandidate(
        PlacementCandidate candidate,
        RoomCandidatePool pool,
        List<PlacementCandidate> selectedCandidates,
        List<AagPlacementRecord> placements,
        Dictionary<string, int> zoneCounts,
        Dictionary<Guid, List<Vector3>> observationCache)
    {
        var cornerRange = Mathf.Max(0.001f, maximumCornerDistanceMeters - minimumCornerDistanceMeters);
        var cornerScore = 1f - Mathf.Clamp01((candidate.cornerDistance - minimumCornerDistanceMeters) / cornerRange);
        var wallBandScore = 1f - Mathf.Clamp01(
            (candidate.wallClearance - minimumWallDistanceMeters) / Mathf.Max(0.001f, preferredWallBandWidthMeters));
        var doorwayScore = Mathf.Clamp01(
            (candidate.doorwayClearance - minimumDoorwayDistanceMeters) / Mathf.Max(0.5f, minimumDoorwayDistanceMeters + 1f));
        var walkingScore = minimumWalkingPathDistanceMeters <= 0f
            ? 1f
            : Mathf.Clamp01(candidate.walkingPathClearance / minimumWalkingPathDistanceMeters);
        var nearestObjectSurface = DistanceToNearestPlacementSurface(placements, candidate.markerWorld);
        var objectScore = float.IsPositiveInfinity(nearestObjectSurface)
            ? 1f
            : Mathf.Clamp01((nearestObjectSurface - minimumObjectDistanceMeters) / Mathf.Max(0.5f, minimumObjectDistanceMeters));

        var sameRoom = selectedCandidates
            .Where(existing => existing.room.Anchor.Uuid == candidate.room.Anchor.Uuid)
            .ToList();
        var sameWallSegmentCount = sameRoom.Count(existing => existing.floor.Anchor.Uuid == candidate.floor.Anchor.Uuid
            && existing.nearestWallSegment == candidate.nearestWallSegment);
        var coVisibilityPenalty = 0f;
        if (sameRoom.Count > 0)
        {
            var positions = sameRoom.Select(existing => existing.markerWorld).ToList();
            positions.Add(candidate.markerWorld);
            EvaluateRoomVisibility(
                candidate.room,
                positions,
                GetObservationPoints(candidate.room, observationCache),
                out _,
                out var maximumVisible);
            coVisibilityPenalty = Mathf.Max(0, maximumVisible - maxVisibleObjectsPerView);

            var minimumBearing = GetMinimumBearingSeparation(candidate, sameRoom);
            if (minimumBearing < minimumAngularSeparationDegrees)
            {
                coVisibilityPenalty += 1f - minimumBearing / Mathf.Max(1f, minimumAngularSeparationDegrees);
            }
        }

        zoneCounts.TryGetValue(candidate.zoneKey, out var zoneCount);
        var zoneOverTarget = Mathf.Max(0, zoneCount + 1 - maximumMarkersPerZone);
        var appliedCornerWeight = pool.isNarrowHall ? cornerProximityWeight * 0.35f : cornerProximityWeight;
        var appliedWallWeight = pool.isNarrowHall ? wallBandWeight * 1.5f : wallBandWeight;
        return appliedCornerWeight * cornerScore
            + appliedWallWeight * wallBandScore
            + doorwayDistanceWeight * doorwayScore
            + walkingPathDistanceWeight * walkingScore
            + objectDistanceWeight * objectScore
            + deterministicVariationWeight * candidate.deterministicVariation
            - entranceVisibilityPenaltyWeight * (candidate.visibleFromEntrance ? 1f : 0f)
            - sameRoomCoVisibilityPenaltyWeight * coVisibilityPenalty
            - sameWallSegmentPenaltyWeight * sameWallSegmentCount
            - zoneReusePenaltyWeight * zoneOverTarget;
    }

    private float ScoreRoomDistribution(
        PlacementCandidate candidate,
        MRUKRoom preferredRoom,
        Dictionary<Guid, int> selectedRoomCounts,
        Dictionary<Guid, RoomCandidatePool> pools)
    {
        selectedRoomCounts.TryGetValue(candidate.room.Anchor.Uuid, out var currentCount);
        var preferredBonus = candidate.room.Anchor.Uuid == preferredRoom.Anchor.Uuid ? 0.5f : 0f;
        if (roomDistributionMode == AagRoomDistributionMode.BalancedAcrossAllRooms)
        {
            var minimumUsedCount = selectedRoomCounts
                .Where(pair => pools[pair.Key].candidates.Count > 0)
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0)
                .Min();
            var balanceScore = currentCount == minimumUsedCount
                ? 1f
                : 1f / (1f + currentCount - minimumUsedCount);
            return roomDistributionWeight * (balanceScore + preferredBonus);
        }

        var totalArea = pools.Values.Sum(pool => Mathf.Max(0f, GetRoomFloorArea(pool.room)));
        var desiredShare = totalArea <= 0f
            ? 1f / Mathf.Max(1, pools.Count)
            : GetRoomFloorArea(candidate.room) / totalArea;
        var selectedTotal = Mathf.Max(1, selectedRoomCounts.Values.Sum() + 1);
        var projectedShare = (currentCount + 1f) / selectedTotal;
        var deficitScore = Mathf.Clamp01(0.5f + desiredShare - projectedShare);
        var unusedRoomBonus = currentCount == 0 && selectedRoomCounts.Count(pair => pair.Value > 0) < minimumRoomsUsed
            ? 0.5f
            : 0f;
        return roomDistributionWeight * (deficitScore + unusedRoomBonus + preferredBonus);
    }

    private static float GetMinimumBearingSeparation(
        PlacementCandidate candidate,
        List<PlacementCandidate> sameRoomCandidates)
    {
        GetLocalBounds(candidate.floor.PlaneBoundary2D, out var min, out var max);
        if (!TryGetInteriorReferenceLocal(candidate.floor, min, max, out var centerLocal))
        {
            return 0f;
        }

        var centerWorld = candidate.floor.transform.TransformPoint(new Vector3(centerLocal.x, centerLocal.y, 0f));
        var bearing = Mathf.Atan2(
            candidate.markerWorld.z - centerWorld.z,
            candidate.markerWorld.x - centerWorld.x) * Mathf.Rad2Deg;
        var minimum = 180f;
        foreach (var existing in sameRoomCandidates)
        {
            var existingBearing = Mathf.Atan2(
                existing.markerWorld.z - centerWorld.z,
                existing.markerWorld.x - centerWorld.x) * Mathf.Rad2Deg;
            minimum = Mathf.Min(minimum, Mathf.Abs(Mathf.DeltaAngle(bearing, existingBearing)));
        }

        return minimum;
    }


    private bool ValidatePlacementSet(List<AagPlacementRecord> placements, out string summary)
    {
        summary = string.Empty;
        if (placements == null || placements.Count != MarkerCountPerSet)
        {
            summary = $"row count={(placements == null ? 0 : placements.Count)}/12";
            return false;
        }

        var expectedSetId = placements[0].set_id;
        if (!AagExperimentSpaceCatalog.Fp1.SupportsSet(expectedSetId)
            || placements.Any(record => !string.Equals(record.floor_plan_id, AagExperimentSpaceCatalog.Fp1Id, StringComparison.Ordinal)
                || !string.Equals(record.set_id, expectedSetId, StringComparison.Ordinal))
            || placements.Select(record => record.answer_marker_id).Distinct(StringComparer.Ordinal).Count() != MarkerCountPerSet)
        {
            summary = "floor plan, set ID, or marker IDs are inconsistent";
            return false;
        }

        foreach (var color in ColorNames)
        {
            if (placements.Count(record => string.Equals(record.color, color, StringComparison.Ordinal)) != MarkersPerColor)
            {
                summary = $"color count invalid for {color}";
                return false;
            }
        }

        var roomCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var minimumWall = float.PositiveInfinity;
        var minimumObstacle = float.PositiveInfinity;
        var minimumCorner = float.PositiveInfinity;
        var minimumWalkingPath = float.PositiveInfinity;
        var minimumDiscoveryCount = int.MaxValue;
        var cornerPreferenceMisses = 0;
        var walkingPreferenceMisses = 0;

        foreach (var record in placements)
        {
            if (!Guid.TryParse(record.room_uuid, out var roomId)
                || !AagExperimentSpaceCatalog.Fp1.ContainsRoom(roomId)
                || AagExperimentSpaceCatalog.Fp1.IsExcludedRoom(roomId))
            {
                summary = $"disallowed room UUID in {record.answer_marker_id}";
                return false;
            }

            var room = FindLoadedRoom(roomId);
            if (room == null)
            {
                summary = $"room {roomId} is not loaded";
                return false;
            }

            var position = ToVector3(record);
            if (!TryFindContainingFloor(room, position, out var floor, out var floorPoint))
            {
                summary = $"{record.answer_marker_id} is outside its MRUK floor polygon";
                return false;
            }

            var expectedCenterY = floorPoint.y + PreviewObjectBottomOffsetMeters + floorGapMeters;
            if (Mathf.Abs(position.y - expectedCenterY) > floorAlignmentToleranceMeters)
            {
                summary = $"floor alignment violation in {record.answer_marker_id}: actualY={position.y:F3}, expectedY={expectedCenterY:F3}";
                return false;
            }

            var validCorners = GetValidCornerIndices(floor);
            var cornerIndex = GetNearestValidCornerIndex(floor, floorPoint, validCorners, out var cornerDistance);
            if (cornerIndex < 0)
            {
                cornerDistance = GetNearestFloorVertexDistance(floor, floorPoint);
            }
            var clearances = MeasureClearances(room, floor, floorPoint);
            LogClearanceMeasurement(record.answer_marker_id, clearances);
            var walkingPathDistance = DistanceToMainWalkingPaths(floor, floorPoint) - PreviewMarkerRadiusMeters;
            minimumWall = Mathf.Min(minimumWall, clearances.wall);
            minimumObstacle = Mathf.Min(minimumObstacle, clearances.obstacle);
            minimumCorner = Mathf.Min(minimumCorner, cornerDistance);
            minimumWalkingPath = Mathf.Min(minimumWalkingPath, walkingPathDistance);

            if (cornerDistance < minimumCornerDistanceMeters
                || cornerDistance > maximumCornerDistanceMeters)
            {
                cornerPreferenceMisses++;
            }

            if (FailsValidatedClearance(clearances.wall, minimumWallDistanceMeters)
                || FailsValidatedClearance(clearances.obstacle, minimumObstacleDistanceMeters)
                || FailsValidatedClearance(clearances.doorway, minimumDoorwayDistanceMeters))
            {
                summary = $"clearance violation in {record.answer_marker_id}: "
                    + $"wallMeasured={FormatClearance(clearances.wall)}, wallRequired={minimumWallDistanceMeters:F6}, "
                    + $"obstacleMeasured={FormatClearance(clearances.obstacle)}, obstacleRequired={minimumObstacleDistanceMeters:F6}, "
                    + $"doorwayMeasured={FormatClearance(clearances.doorway)}, doorwayRequired={minimumDoorwayDistanceMeters:F6}, "
                    + $"generationSafetyMargin={generationSafetyMarginMeters:F6}, validationEpsilon={validationEpsilonMeters:F6}, "
                    + $"markerRadius={PreviewMarkerRadiusMeters:F6}, definition={ClearanceDistanceDefinition}";
                return false;
            }

            if (walkingPathDistance < minimumWalkingPathDistanceMeters)
            {
                walkingPreferenceMisses++;
            }

            var key = roomId.ToString().Substring(0, 8);
            roomCounts.TryGetValue(key, out var count);
            roomCounts[key] = count + 1;
        }

        var measured = CloneRecords(placements);
        if (!PopulatePlacementMetrics(measured, new Dictionary<Guid, List<Vector3>>(), out var metricFailure))
        {
            summary = metricFailure;
            return false;
        }

        var minimumObject = CalculateMinimumObjectDistance(measured) - previewMarkerDiameterMeters;
        if (FailsValidatedClearance(minimumObject, minimumObjectDistanceMeters))
        {
            summary = $"object clearance violation: measured={minimumObject:F6}, required={minimumObjectDistanceMeters:F6}, "
                + $"validationEpsilon={validationEpsilonMeters:F6}, definition=marker bounds edge to marker bounds edge (horizontal XZ meters)";
            return false;
        }

        minimumDiscoveryCount = measured.Min(record => record.discoverable_observation_count);
        var setMaximumVisible = measured.Max(record => record.set_max_visible_objects_per_view);
        if (minimumDiscoveryCount < minimumDiscoverableObservationCount)
        {
            summary = $"hard discoverability violation: minDiscoverableObservations={minimumDiscoveryCount}";
            return false;
        }

        var meanNearest = CalculateMeanNearestNeighborDistance(placements);
        var meanPairwise = CalculateMeanPairwiseDistance(placements);
        var maximumPairwise = CalculateMaximumPairwiseDistance(placements);
        var selectedMetricValue = difficultyDistanceMetric == AagDifficultyDistanceMetric.MeanNearestNeighborDistance
            ? meanNearest
            : difficultyDistanceMetric == AagDifficultyDistanceMetric.MeanPairwiseDistance
                ? meanPairwise
                : maximumPairwise;

        summary = $"rooms=[{string.Join(",", roomCounts.Select(pair => pair.Key + "=" + pair.Value))}], "
            + $"minObjectSurface={minimumObject:F3}m, minWallEdge={minimumWall:F3}m, minObstacleEdge={minimumObstacle:F3}m, "
            + $"minCorner={minimumCorner:F3}m, minDiscoverableObservations={minimumDiscoveryCount}, "
            + $"maxVisibleInOneView={setMaximumVisible}/{maxVisibleObjectsPerView}, "
            + $"softCornerMisses={cornerPreferenceMisses}, softWalkingMisses={walkingPreferenceMisses}, "
            + $"minWalkingPath={minimumWalkingPath:F3}m, "
            + $"difficultyMetric={difficultyDistanceMetric}:{selectedMetricValue:F3}m, "
            + $"meanNearest={meanNearest:F3}m, meanPairwise={meanPairwise:F3}m, maxPairwise={maximumPairwise:F3}m";
        return true;
    }

    private void LogAllSetClearanceReport()
    {
        var totalMarkers = 0;
        var totalFailures = 0;
        var totalMinimumWall = float.PositiveInfinity;
        foreach (var setId in SetIds)
        {
            if (!confirmedBySet.TryGetValue(setId, out var placements)
                && !unconfirmedBySet.TryGetValue(setId, out placements))
            {
                Debug.Log($"[AAG Clearance Report] set={setId}; markers=0/12; status=NOT_AVAILABLE; "
                    + $"wallRequired={minimumWallDistanceMeters:F6}m; generationSafetyMargin={generationSafetyMarginMeters:F6}m; "
                    + $"validationEpsilon={validationEpsilonMeters:F6}m; markerRadius={PreviewMarkerRadiusMeters:F6}m; "
                    + $"definition=\"{ClearanceDistanceDefinition}\"");
                continue;
            }

            var minimumWall = float.PositiveInfinity;
            var failureCount = 0;
            foreach (var record in placements)
            {
                if (!Guid.TryParse(record.room_uuid, out var roomId)
                    || FindLoadedRoom(roomId) is not MRUKRoom room
                    || !TryFindContainingFloor(room, ToVector3(record), out var floor, out var floorPoint))
                {
                    failureCount++;
                    continue;
                }

                var measured = MeasureClearances(room, floor, floorPoint);
                minimumWall = Mathf.Min(minimumWall, measured.wall);
                if (FailsValidatedClearance(measured.wall, minimumWallDistanceMeters))
                {
                    failureCount++;
                }
            }

            totalMarkers += placements.Count;
            totalFailures += failureCount;
            totalMinimumWall = Mathf.Min(totalMinimumWall, minimumWall);
            Debug.Log($"[AAG Clearance Report] set={setId}; markers={placements.Count}/12; "
                + $"minimumActualWall={FormatClearance(minimumWall)}m; wallFailureCount={failureCount}; "
                + $"wallRequired={minimumWallDistanceMeters:F6}m; generationSafetyMargin={generationSafetyMarginMeters:F6}m; "
                + $"validationEpsilon={validationEpsilonMeters:F6}m; markerRadius={PreviewMarkerRadiusMeters:F6}m; "
                + $"definition=\"{ClearanceDistanceDefinition}\"");
        }

        Debug.Log($"[AAG Clearance Report] ALL FP1 sets: markers={totalMarkers}/36; "
            + $"minimumActualWall={FormatClearance(totalMinimumWall)}m; wallFailureCount={totalFailures}; "
            + $"wallRequired={minimumWallDistanceMeters:F6}m; generationSafetyMargin={generationSafetyMarginMeters:F6}m; "
            + $"validationEpsilon={validationEpsilonMeters:F6}m; markerRadius={PreviewMarkerRadiusMeters:F6}m; "
            + $"definition=\"{ClearanceDistanceDefinition}\"");
    }

    private bool PopulatePlacementMetrics(
        List<AagPlacementRecord> placements,
        Dictionary<Guid, List<Vector3>> observationCache,
        out string failure)
    {
        failure = string.Empty;
        var setMaximumVisible = 0;
        foreach (var roomGroup in placements.GroupBy(record => record.room_uuid, StringComparer.Ordinal))
        {
            if (!Guid.TryParse(roomGroup.Key, out var roomId) || FindLoadedRoom(roomId) is not MRUKRoom room)
            {
                failure = $"metrics failed: room {roomGroup.Key} is not loaded";
                return false;
            }

            var roomRecords = roomGroup.ToList();
            var observations = GetObservationPoints(room, observationCache);
            var positions = roomRecords.Select(ToVector3).ToList();
            if (!EvaluateRoomVisibility(room, positions, observations, out var discoveryCounts, out var roomMaximumVisible))
            {
                failure = $"metrics failed: room {roomGroup.Key} has no valid observation positions";
                return false;
            }

            setMaximumVisible = Mathf.Max(setMaximumVisible, roomMaximumVisible);
            for (var index = 0; index < roomRecords.Count; index++)
            {
                var record = roomRecords[index];
                if (!TryFindContainingFloor(room, positions[index], out var floor, out var floorPoint))
                {
                    failure = $"metrics failed: {record.answer_marker_id} is outside its floor polygon";
                    return false;
                }

                var validCorners = GetValidCornerIndices(floor);
                var nearestCornerIndex = GetNearestValidCornerIndex(floor, floorPoint, validCorners, out record.corner_distance);
                if (nearestCornerIndex < 0)
                {
                    record.corner_distance = GetNearestFloorVertexDistance(floor, floorPoint);
                }
                record.wall_distance = MeasureClearances(room, floor, floorPoint).wall;
                record.discoverable_observation_count = discoveryCounts[index];
            }
        }

        for (var index = 0; index < placements.Count; index++)
        {
            var nearest = float.PositiveInfinity;
            for (var other = 0; other < placements.Count; other++)
            {
                if (index != other)
                {
                    nearest = Mathf.Min(
                        nearest,
                        HorizontalDistance(ToVector3(placements[index]), ToVector3(placements[other])) - previewMarkerDiameterMeters);
                }
            }

            placements[index].nearest_object_distance = nearest;
            placements[index].set_max_visible_objects_per_view = setMaximumVisible;
        }

        return true;
    }

    private bool TryFindContainingFloor(MRUKRoom room, Vector3 markerPosition, out MRUKAnchor containingFloor)
    {
        return TryFindContainingFloor(room, markerPosition, out containingFloor, out _);
    }

    private static bool TryFindContainingFloor(
        MRUKRoom room,
        Vector3 markerPosition,
        out MRUKAnchor containingFloor,
        out Vector3 floorWorldPoint)
    {
        foreach (var floor in room.FloorAnchors)
        {
            if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3)
            {
                continue;
            }

            var local = floor.transform.InverseTransformPoint(markerPosition);
            if (floor.IsPositionInBoundary(new Vector2(local.x, local.y)))
            {
                containingFloor = floor;
                floorWorldPoint = floor.transform.TransformPoint(new Vector3(local.x, local.y, 0f));
                return true;
            }
        }

        containingFloor = null;
        floorWorldPoint = Vector3.zero;
        return false;
    }

    private static MRUKRoom FindLoadedRoom(Guid roomId)
    {
        if (MRUK.Instance == null)
        {
            return null;
        }

        foreach (var room in MRUK.Instance.Rooms)
        {
            if (room.Anchor.Uuid == roomId)
            {
                return room;
            }
        }

        return null;
    }

    private ClearanceMeasurements MeasureClearances(MRUKRoom room, MRUKAnchor floor, Vector3 floorWorldPoint)
    {
        return new ClearanceMeasurements
        {
            wall = DistanceToFloorBoundaryWorld(floor, floorWorldPoint) - PreviewMarkerRadiusMeters,
            doorway = DistanceToNearestDoorway(room, floorWorldPoint) - PreviewMarkerRadiusMeters,
            obstacle = DistanceToNearestObstacle(room, floorWorldPoint) - PreviewMarkerRadiusMeters,
        };
    }

    private bool FailsValidatedClearance(float measuredClearance, float requiredClearance)
    {
        return !float.IsPositiveInfinity(measuredClearance)
            && measuredClearance + validationEpsilonMeters < requiredClearance;
    }

    private static bool FailsGenerationClearance(float measuredClearance, float targetClearance)
    {
        return !float.IsPositiveInfinity(measuredClearance)
            && measuredClearance < targetClearance;
    }

    private void LogClearanceMeasurement(string markerId, ClearanceMeasurements measured)
    {
        Debug.Log($"[AAG Clearance] marker={markerId}; wallMeasured={FormatClearance(measured.wall)}m; "
            + $"wallRequired={minimumWallDistanceMeters:F6}m; generationSafetyMargin={generationSafetyMarginMeters:F6}m; "
            + $"validationEpsilon={validationEpsilonMeters:F6}m; doorwayMeasured={FormatClearance(measured.doorway)}m; "
            + $"doorwayRequired={minimumDoorwayDistanceMeters:F6}m; obstacleMeasured={FormatClearance(measured.obstacle)}m; "
            + $"obstacleRequired={minimumObstacleDistanceMeters:F6}m; markerRadius={PreviewMarkerRadiusMeters:F6}m; "
            + $"markerDiameter={previewMarkerDiameterMeters:F6}m; "
            + $"obstacleStatus={(float.IsPositiveInfinity(measured.obstacle) ? "NO_NEARBY_OBSTACLE_PASS" : "MEASURED")}; "
            + $"definition=\"{ClearanceDistanceDefinition}\"");
    }

    private static string FormatClearance(float value)
    {
        return float.IsPositiveInfinity(value)
            ? "Infinity"
            : value.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static float DistanceToFloorBoundaryWorld(MRUKAnchor floor, Vector3 worldPoint)
    {
        var minimum = float.PositiveInfinity;
        var boundary = floor.PlaneBoundary2D;
        for (var index = 0; index < boundary.Count; index++)
        {
            var nextIndex = (index + 1) % boundary.Count;
            var start = floor.transform.TransformPoint(new Vector3(boundary[index].x, boundary[index].y, 0f));
            var end = floor.transform.TransformPoint(new Vector3(boundary[nextIndex].x, boundary[nextIndex].y, 0f));
            minimum = Mathf.Min(minimum, DistancePointToSegmentXZ(worldPoint, start, end));
        }

        return minimum;
    }

    private static float DistanceToNearestObstacle(MRUKRoom room, Vector3 floorWorldPoint)
    {
        var minimum = float.PositiveInfinity;
        foreach (var anchor in room.Anchors)
        {
            if (anchor == null || !anchor.VolumeBounds.HasValue)
            {
                continue;
            }

            minimum = Mathf.Min(minimum, HorizontalDistanceToWorldAabb(anchor, anchor.VolumeBounds.Value, floorWorldPoint));
        }

        return minimum;
    }

    private static float HorizontalDistanceToWorldAabb(MRUKAnchor anchor, Bounds localBounds, Vector3 worldPoint)
    {
        var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                for (var z = -1; z <= 1; z += 2)
                {
                    var localCorner = localBounds.center + Vector3.Scale(localBounds.extents, new Vector3(x, y, z));
                    var worldCorner = anchor.transform.TransformPoint(localCorner);
                    minimum = Vector3.Min(minimum, worldCorner);
                    maximum = Vector3.Max(maximum, worldCorner);
                }
            }
        }

        var dx = Mathf.Max(minimum.x - worldPoint.x, 0f, worldPoint.x - maximum.x);
        var dz = Mathf.Max(minimum.z - worldPoint.z, 0f, worldPoint.z - maximum.z);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private float DistanceToNearestPlacementSurface(List<AagPlacementRecord> placements, Vector3 position)
    {
        if (placements.Count == 0)
        {
            return float.PositiveInfinity;
        }

        var minimum = float.PositiveInfinity;
        foreach (var placement in placements)
        {
            minimum = Mathf.Min(minimum, HorizontalDistance(position, ToVector3(placement)) - previewMarkerDiameterMeters);
        }

        return minimum;
    }

    private List<int> GetValidCornerIndices(MRUKAnchor floor)
    {
        var result = new List<int>();
        var boundary = floor.PlaneBoundary2D;
        var signedDoubleArea = 0f;
        for (var index = 0; index < boundary.Count; index++)
        {
            var current = boundary[index];
            var next = boundary[(index + 1) % boundary.Count];
            signedDoubleArea += current.x * next.y - next.x * current.y;
        }

        if (Mathf.Abs(signedDoubleArea) <= 0.000001f)
        {
            return result;
        }

        for (var index = 0; index < boundary.Count; index++)
        {
            var previous = boundary[(index - 1 + boundary.Count) % boundary.Count] - boundary[index];
            var next = boundary[(index + 1) % boundary.Count] - boundary[index];
            if (previous.sqrMagnitude <= 0.000001f || next.sqrMagnitude <= 0.000001f)
            {
                continue;
            }

            var cornerCross = previous.x * next.y - previous.y * next.x;
            var isConvexCorner = signedDoubleArea * cornerCross < 0f;
            if (previous.magnitude >= minimumCornerAdjacentEdgeLengthMeters
                && next.magnitude >= minimumCornerAdjacentEdgeLengthMeters
                && isConvexCorner
                && Vector2.Angle(previous, next) <= maximumCornerAngleDegrees)
            {
                result.Add(index);
            }
        }

        return result;
    }

    private static bool TryGetInwardCornerDirection(
        MRUKAnchor floor,
        int cornerIndex,
        out Vector2 inwardDirection,
        out float cornerAngleDegrees)
    {
        var boundary = floor.PlaneBoundary2D;
        var corner = boundary[cornerIndex];
        var previous = boundary[(cornerIndex - 1 + boundary.Count) % boundary.Count] - corner;
        var next = boundary[(cornerIndex + 1) % boundary.Count] - corner;
        cornerAngleDegrees = Vector2.Angle(previous, next);
        inwardDirection = previous.normalized + next.normalized;
        if (inwardDirection.sqrMagnitude <= 0.000001f)
        {
            inwardDirection = Vector2.zero;
            return false;
        }

        inwardDirection.Normalize();
        var probeDistance = Mathf.Min(0.05f, Mathf.Min(previous.magnitude, next.magnitude) * 0.1f);
        if (!floor.IsPositionInBoundary(corner + inwardDirection * probeDistance))
        {
            inwardDirection = -inwardDirection;
            if (!floor.IsPositionInBoundary(corner + inwardDirection * probeDistance))
            {
                inwardDirection = Vector2.zero;
                return false;
            }
        }

        return true;
    }

    private float GetMinimumCornerRadiusForWallClearance(
        MRUKAnchor floor,
        int cornerIndex,
        Vector2 candidateDirection)
    {
        var boundary = floor.PlaneBoundary2D;
        var corner = boundary[cornerIndex];
        var previous = (boundary[(cornerIndex - 1 + boundary.Count) % boundary.Count] - corner).normalized;
        var next = (boundary[(cornerIndex + 1) % boundary.Count] - corner).normalized;
        var previousCoefficient = Mathf.Abs(candidateDirection.x * previous.y - candidateDirection.y * previous.x);
        var nextCoefficient = Mathf.Abs(candidateDirection.x * next.y - candidateDirection.y * next.x);
        var minimumCoefficient = Mathf.Min(previousCoefficient, nextCoefficient);
        if (minimumCoefficient <= 0.0001f)
        {
            return float.PositiveInfinity;
        }

        var requiredCenterClearance = minimumWallDistanceMeters
            + generationSafetyMarginMeters
            + PreviewMarkerRadiusMeters;
        return requiredCenterClearance / minimumCoefficient;
    }

    private static int GetNearestValidCornerIndex(
        MRUKAnchor floor,
        Vector3 floorWorldPoint,
        List<int> validCornerIndices,
        out float distance)
    {
        var result = -1;
        distance = float.PositiveInfinity;
        foreach (var cornerIndex in validCornerIndices)
        {
            var corner = floor.PlaneBoundary2D[cornerIndex];
            var cornerWorld = floor.transform.TransformPoint(new Vector3(corner.x, corner.y, 0f));
            var candidateDistance = HorizontalDistance(floorWorldPoint, cornerWorld);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                result = cornerIndex;
            }
        }

        return result;
    }

    private static float GetNearestFloorVertexDistance(MRUKAnchor floor, Vector3 floorWorldPoint)
    {
        var minimum = float.PositiveInfinity;
        foreach (var vertex in floor.PlaneBoundary2D)
        {
            var vertexWorld = floor.transform.TransformPoint(new Vector3(vertex.x, vertex.y, 0f));
            minimum = Mathf.Min(minimum, HorizontalDistance(floorWorldPoint, vertexWorld));
        }

        return minimum;
    }

    private static bool TryGetInteriorReferenceLocal(MRUKAnchor floor, Vector2 min, Vector2 max, out Vector2 result)
    {
        var boundsCenter = (min + max) * 0.5f;
        if (floor.IsPositionInBoundary(boundsCenter))
        {
            result = boundsCenter;
            return true;
        }

        var vertexAverage = Vector2.zero;
        foreach (var vertex in floor.PlaneBoundary2D)
        {
            vertexAverage += vertex;
        }

        vertexAverage /= floor.PlaneBoundary2D.Count;
        if (floor.IsPositionInBoundary(vertexAverage))
        {
            result = vertexAverage;
            return true;
        }

        var bestClearance = float.NegativeInfinity;
        result = Vector2.zero;
        for (var row = 0; row < 7; row++)
        {
            for (var column = 0; column < 7; column++)
            {
                var sample = new Vector2(
                    Mathf.Lerp(min.x, max.x, (column + 0.5f) / 7f),
                    Mathf.Lerp(min.y, max.y, (row + 0.5f) / 7f));
                if (!floor.IsPositionInBoundary(sample))
                {
                    continue;
                }

                var clearance = DistanceToBoundaryLocal(floor.PlaneBoundary2D, sample);
                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    result = sample;
                }
            }
        }

        return bestClearance > float.NegativeInfinity;
    }

    private static float DistanceToBoundaryLocal(List<Vector2> boundary, Vector2 point)
    {
        var minimum = float.PositiveInfinity;
        for (var index = 0; index < boundary.Count; index++)
        {
            minimum = Mathf.Min(
                minimum,
                DistancePointToSegment2D(point, boundary[index], boundary[(index + 1) % boundary.Count]));
        }

        return minimum;
    }

    private static float DistancePointToSegment2D(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.000001f)
        {
            return Vector2.Distance(point, start);
        }

        var t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
        return Vector2.Distance(point, start + segment * t);
    }

    private static List<MRUKAnchor> GetDoorwayAnchors(MRUKRoom room)
    {
        return room.Anchors
            .Where(anchor => anchor != null && (anchor.Label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0)
            .OrderBy(anchor => anchor.Anchor.Uuid.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private static float DistanceToNearestDoorway(MRUKRoom room, Vector3 point)
    {
        var minimum = float.PositiveInfinity;
        foreach (var doorway in GetDoorwayAnchors(room))
        {
            minimum = Mathf.Min(minimum, DistanceToAnchorFootprintXZ(doorway, point));
        }

        return minimum;
    }

    private static float DistanceToAnchorFootprintXZ(MRUKAnchor anchor, Vector3 point)
    {
        if (anchor.PlaneBoundary2D != null && anchor.PlaneBoundary2D.Count >= 2)
        {
            var minimum = float.PositiveInfinity;
            for (var index = 0; index < anchor.PlaneBoundary2D.Count; index++)
            {
                var startLocal = anchor.PlaneBoundary2D[index];
                var endLocal = anchor.PlaneBoundary2D[(index + 1) % anchor.PlaneBoundary2D.Count];
                var start = anchor.transform.TransformPoint(new Vector3(startLocal.x, startLocal.y, 0f));
                var end = anchor.transform.TransformPoint(new Vector3(endLocal.x, endLocal.y, 0f));
                minimum = Mathf.Min(minimum, DistancePointToSegmentXZ(point, start, end));
            }

            return minimum;
        }

        if (anchor.VolumeBounds.HasValue)
        {
            return HorizontalDistanceToWorldAabb(anchor, anchor.VolumeBounds.Value, point);
        }

        return HorizontalDistance(point, anchor.transform.position);
    }

    private float DistanceToMainWalkingPaths(MRUKAnchor floor, Vector3 point)
    {
        GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
        if (!TryGetInteriorReferenceLocal(floor, min, max, out var referenceLocal))
        {
            return 0f;
        }

        Vector2 pathStartLocal;
        Vector2 pathEndLocal;
        if (max.x - min.x >= max.y - min.y)
        {
            pathStartLocal = new Vector2(min.x, referenceLocal.y);
            pathEndLocal = new Vector2(max.x, referenceLocal.y);
        }
        else
        {
            pathStartLocal = new Vector2(referenceLocal.x, min.y);
            pathEndLocal = new Vector2(referenceLocal.x, max.y);
        }

        var pathStart = floor.transform.TransformPoint(new Vector3(pathStartLocal.x, pathStartLocal.y, 0f));
        var pathEnd = floor.transform.TransformPoint(new Vector3(pathEndLocal.x, pathEndLocal.y, 0f));
        return DistancePointToSegmentXZ(point, pathStart, pathEnd);
    }


    private List<Vector3> GetObservationPoints(MRUKRoom room, Dictionary<Guid, List<Vector3>> cache)
    {
        if (cache.TryGetValue(room.Anchor.Uuid, out var cached))
        {
            return cached;
        }

        var result = new List<Vector3>();
        var floors = room.FloorAnchors
            .Where(floor => floor != null && floor.PlaneBoundary2D != null && floor.PlaneBoundary2D.Count >= 3)
            .OrderBy(floor => floor.Anchor.Uuid.ToString(), StringComparer.Ordinal)
            .ToList();
        var doorways = GetDoorwayAnchors(room);
        var entranceObservationCount = 0;
        foreach (var floor in floors)
        {
            GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
            if (!TryGetInteriorReferenceLocal(floor, min, max, out var centerLocal))
            {
                continue;
            }

            TryAddObservationPoint(result, floor, centerLocal);

            for (var row = 0; row < observationGridSize; row++)
            {
                for (var column = 0; column < observationGridSize; column++)
                {
                    var localPoint = new Vector2(
                        Mathf.Lerp(min.x, max.x, (column + 0.5f) / observationGridSize),
                        Mathf.Lerp(min.y, max.y, (row + 0.5f) / observationGridSize));
                    TryAddObservationPoint(result, floor, localPoint);
                }
            }

            foreach (var doorway in doorways)
            {
                var doorwayOnFloor = floor.transform.InverseTransformPoint(doorway.transform.position);
                var doorwayLocal = new Vector2(doorwayOnFloor.x, doorwayOnFloor.y);
                var inward = centerLocal - doorwayLocal;
                if (inward.sqrMagnitude <= 0.000001f)
                {
                    continue;
                }

                inward.Normalize();
                for (var step = 1; step <= 4; step++)
                {
                    if (TryAddObservationPoint(
                            result,
                            floor,
                            doorwayLocal + inward * doorwayObservationInsetMeters * step))
                    {
                        entranceObservationCount++;
                        break;
                    }
                }
            }
        }

        if (doorways.Count == 0)
        {
            Debug.LogWarning($"[AAG Authoring] Room {room.Anchor.Uuid} has no MRUK DOOR_FRAME; entrance observation sampling is unavailable for this room.");
        }
        else if (entranceObservationCount == 0)
        {
            Debug.LogWarning($"[AAG Authoring] Room {room.Anchor.Uuid} has {doorways.Count} DOOR_FRAME anchors but no entrance observation passed the floor/clearance checks.");
        }

        Debug.Log($"[AAG Authoring] Room {room.Anchor.Uuid} observation samples: total={result.Count}, entrance={entranceObservationCount}, doorFrames={doorways.Count}.");
        cache[room.Anchor.Uuid] = result;
        return result;
    }

    private List<Vector3> GetEntranceObservationPoints(MRUKRoom room)
    {
        var result = new List<Vector3>();
        var doorways = GetDoorwayAnchors(room);
        foreach (var floor in room.FloorAnchors
                     .Where(candidate => candidate != null
                         && candidate.PlaneBoundary2D != null
                         && candidate.PlaneBoundary2D.Count >= 3))
        {
            GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
            if (!TryGetInteriorReferenceLocal(floor, min, max, out var centerLocal))
            {
                continue;
            }

            foreach (var doorway in doorways)
            {
                var doorwayOnFloor = floor.transform.InverseTransformPoint(doorway.transform.position);
                var doorwayLocal = new Vector2(doorwayOnFloor.x, doorwayOnFloor.y);
                var inward = centerLocal - doorwayLocal;
                if (inward.sqrMagnitude <= 0.000001f)
                {
                    continue;
                }

                inward.Normalize();
                for (var step = 1; step <= 4; step++)
                {
                    if (TryAddObservationPoint(
                            result,
                            floor,
                            doorwayLocal + inward * doorwayObservationInsetMeters * step))
                    {
                        break;
                    }
                }
            }
        }

        return result;
    }

    private bool TryAddObservationPoint(List<Vector3> result, MRUKAnchor floor, Vector2 localPoint)
    {
        if (!floor.IsPositionInBoundary(localPoint)
            || DistanceToBoundaryLocal(floor.PlaneBoundary2D, localPoint) < observationPointWallClearanceMeters)
        {
            return false;
        }

        var floorWorld = floor.transform.TransformPoint(new Vector3(localPoint.x, localPoint.y, 0f));
        var eyePosition = new Vector3(floorWorld.x, floorWorld.y + observationEyeHeightMeters, floorWorld.z);
        if (result.Any(existing => HorizontalDistance(existing, eyePosition) < 0.2f))
        {
            return true;
        }

        result.Add(eyePosition);
        return true;
    }

    private bool EvaluateRoomVisibility(
        MRUKRoom room,
        List<Vector3> markerPositions,
        List<Vector3> observations,
        out List<int> discoverableCounts,
        out int maximumVisible)
    {
        discoverableCounts = Enumerable.Repeat(0, markerPositions.Count).ToList();
        maximumVisible = 0;
        GetHmdFrustumFovDegrees(out var horizontalFov, out var verticalFov);

        foreach (var observation in observations)
        {
            var visibleDirections = new List<Vector3>();
            for (var index = 0; index < markerPositions.Count; index++)
            {
                if (!HasMrukLineOfSight(room, observation, markerPositions[index]))
                {
                    continue;
                }

                discoverableCounts[index]++;
                visibleDirections.Add((markerPositions[index] - observation).normalized);
            }

            maximumVisible = Mathf.Max(
                maximumVisible,
                CalculateMaximumObjectsInFrustum(visibleDirections, horizontalFov, verticalFov));
        }

        return observations.Count > 0;
    }

    private bool HasMrukLineOfSight(MRUKRoom room, Vector3 observation, Vector3 markerPosition)
    {
        var direction = markerPosition - observation;
        var targetDistance = direction.magnitude;
        if (targetDistance <= 0.001f)
        {
            return true;
        }

        var occlusionLabels = MRUKAnchor.SceneLabels.WALL_FACE
            | MRUKAnchor.SceneLabels.INNER_WALL_FACE
            | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE
            | MRUKAnchor.SceneLabels.TABLE
            | MRUKAnchor.SceneLabels.COUCH
            | MRUKAnchor.SceneLabels.OTHER
            | MRUKAnchor.SceneLabels.STORAGE
            | MRUKAnchor.SceneLabels.BED
            | MRUKAnchor.SceneLabels.SCREEN
            | MRUKAnchor.SceneLabels.LAMP
            | MRUKAnchor.SceneLabels.PLANT
            | MRUKAnchor.SceneLabels.WALL_ART
            | MRUKAnchor.SceneLabels.GLOBAL_MESH;
        var ray = new Ray(observation, direction / targetDistance);
        var maximumOccluderDistance = Mathf.Max(0f, targetDistance - PreviewMarkerRadiusMeters * 0.5f);
        return !room.Raycast(
            ray,
            maximumOccluderDistance,
            new LabelFilter(occlusionLabels),
            out _,
            out _);
    }

    private void GetHmdFrustumFovDegrees(out float horizontalFov, out float verticalFov)
    {
        var projection = hmdCamera.projectionMatrix;
        if (Mathf.Abs(projection[0, 0]) > 0.0001f && Mathf.Abs(projection[1, 1]) > 0.0001f)
        {
            var left = Mathf.Atan(Mathf.Abs((-1f - projection[0, 2]) / projection[0, 0]));
            var right = Mathf.Atan(Mathf.Abs((1f - projection[0, 2]) / projection[0, 0]));
            var bottom = Mathf.Atan(Mathf.Abs((-1f - projection[1, 2]) / projection[1, 1]));
            var top = Mathf.Atan(Mathf.Abs((1f - projection[1, 2]) / projection[1, 1]));
            horizontalFov = Mathf.Clamp((left + right) * Mathf.Rad2Deg, 1f, 179f);
            verticalFov = Mathf.Clamp((bottom + top) * Mathf.Rad2Deg, 1f, 179f);
            return;
        }

        verticalFov = Mathf.Clamp(hmdCamera.fieldOfView, 1f, 179f);
        horizontalFov = Mathf.Clamp(
            Camera.VerticalToHorizontalFieldOfView(verticalFov, Mathf.Max(0.01f, hmdCamera.aspect)),
            1f,
            179f);
    }

    private static int CalculateMaximumObjectsInFrustum(
        List<Vector3> directions,
        float horizontalFov,
        float verticalFov)
    {
        if (directions.Count == 0)
        {
            return 0;
        }

        var azimuths = new List<float>(directions.Count);
        var elevations = new List<float>(directions.Count);
        foreach (var direction in directions)
        {
            azimuths.Add(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg);
            elevations.Add(Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg);
        }

        var horizontalHalf = horizontalFov * 0.5f;
        var verticalHalf = verticalFov * 0.5f;
        var yawCandidates = azimuths.SelectMany(value => new[] { value, value - horizontalHalf, value + horizontalHalf });
        var pitchCandidates = elevations.SelectMany(value => new[] { value, value - verticalHalf, value + verticalHalf });
        var maximum = 0;
        foreach (var yaw in yawCandidates)
        {
            foreach (var pitch in pitchCandidates)
            {
                var count = 0;
                for (var index = 0; index < directions.Count; index++)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(yaw, azimuths[index])) <= horizontalHalf + 0.001f
                        && Mathf.Abs(pitch - elevations[index]) <= verticalHalf + 0.001f)
                    {
                        count++;
                    }
                }

                maximum = Mathf.Max(maximum, count);
            }
        }

        return maximum;
    }


    private string BuildZoneKey(MRUKRoom room, MRUKAnchor floor, Vector2 point, Vector2 min, Vector2 max)
    {
        var normalizedX = Mathf.InverseLerp(min.x, max.x, point.x);
        var normalizedY = Mathf.InverseLerp(min.y, max.y, point.y);
        var column = Mathf.Clamp(Mathf.FloorToInt(normalizedX * zoneGridColumns), 0, zoneGridColumns - 1);
        var row = Mathf.Clamp(Mathf.FloorToInt(normalizedY * zoneGridRows), 0, zoneGridRows - 1);
        return $"{room.Anchor.Uuid}:{floor.Anchor.Uuid}:{column}:{row}";
    }

    private static void GetLocalBounds(List<Vector2> boundary, out Vector2 min, out Vector2 max)
    {
        min = boundary[0];
        max = boundary[0];
        for (var index = 1; index < boundary.Count; index++)
        {
            min = Vector2.Min(min, boundary[index]);
            max = Vector2.Max(max, boundary[index]);
        }
    }

    private static float GetRoomFloorArea(MRUKRoom room)
    {
        var area = 0f;
        foreach (var floor in room.FloorAnchors)
        {
            area += GetFloorArea(floor);
        }

        return area;
    }

    private static float GetFloorArea(MRUKAnchor floor)
    {
        if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3)
        {
            return 0f;
        }

        var doubledArea = 0f;
        for (var index = 0; index < floor.PlaneBoundary2D.Count; index++)
        {
            var current = floor.PlaneBoundary2D[index];
            var next = floor.PlaneBoundary2D[(index + 1) % floor.PlaneBoundary2D.Count];
            doubledArea += current.x * next.y - next.x * current.y;
        }

        return Mathf.Abs(doubledArea) * 0.5f;
    }

    private static float DistancePointToSegmentXZ(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
    {
        var point2 = new Vector2(point.x, point.z);
        var start2 = new Vector2(segmentStart.x, segmentStart.z);
        var end2 = new Vector2(segmentEnd.x, segmentEnd.z);
        var segment = end2 - start2;
        var lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.000001f)
        {
            return Vector2.Distance(point2, start2);
        }

        var t = Mathf.Clamp01(Vector2.Dot(point2 - start2, segment) / lengthSquared);
        return Vector2.Distance(point2, start2 + segment * t);
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.x - right.x;
        var dz = left.z - right.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static List<string> BuildColorSequence(System.Random random)
    {
        var colors = new List<string>(MarkerCountPerSet);
        foreach (var color in ColorNames)
        {
            for (var index = 0; index < MarkersPerColor; index++)
            {
                colors.Add(color);
            }
        }

        Shuffle(colors, random);
        return colors;
    }

    private static void Shuffle<T>(IList<T> values, System.Random random)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            var temporary = values[index];
            values[index] = values[swapIndex];
            values[swapIndex] = temporary;
        }
    }

    private int GetSeed(string setId)
    {
        if (string.Equals(setId, AagExperimentSpaceCatalog.Fp1S1, StringComparison.Ordinal))
        {
            return fp1S1Seed;
        }

        if (string.Equals(setId, AagExperimentSpaceCatalog.Fp1S2, StringComparison.Ordinal))
        {
            return fp1S2Seed;
        }

        return fp1S3Seed;
    }

    private bool ValidateSeeds()
    {
        var valid = fp1S1Seed != fp1S2Seed && fp1S1Seed != fp1S3Seed && fp1S2Seed != fp1S3Seed;
        if (!valid)
        {
            Debug.LogError("[AAG Authoring] FP1-S1, FP1-S2, and FP1-S3 seeds must be distinct.");
        }

        return valid;
    }

    private bool HasDuplicatePositionCombination(string currentSetId, List<AagPlacementRecord> candidate)
    {
        var candidateSignature = BuildPositionSignature(candidate);
        foreach (var pair in confirmedBySet.Concat(unconfirmedBySet))
        {
            if (!string.Equals(pair.Key, currentSetId, StringComparison.Ordinal)
                && string.Equals(candidateSignature, BuildPositionSignature(pair.Value), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildPositionSignature(List<AagPlacementRecord> placements)
    {
        return string.Join("|", placements
            .Select(record => string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", record.world_x, record.world_y, record.world_z))
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    private void ShowSelectedSet()
    {
        DestroyPreviewObjects();

        List<AagPlacementRecord> placements = null;
        var confirmed = confirmedBySet.TryGetValue(SelectedSetId, out placements);
        if (!confirmed)
        {
            unconfirmedBySet.TryGetValue(SelectedSetId, out placements);
        }

        if (placements == null)
        {
            lastValidationSummary = "No preview. Explicitly regenerate this set.";
            UpdateHud(false);
            return;
        }

        if (!ValidatePlacementSet(placements, out lastValidationSummary))
        {
            Debug.LogError($"[AAG Authoring] {SelectedSetId} preview not spawned because validation failed: {lastValidationSummary}");
            UpdateHud(confirmed);
            return;
        }

        foreach (var placement in placements)
        {
            CreatePreviewMarker(placement);
        }

        Debug.Log($"[AAG Authoring] Showing {(confirmed ? "confirmed" : "unconfirmed")} {SelectedSetId}: {lastValidationSummary}");
        UpdateHud(confirmed);
    }

    private void CreatePreviewMarker(AagPlacementRecord placement)
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = $"PROVISIONAL {placement.answer_marker_id}";
        marker.transform.position = ToVector3(placement);
        marker.transform.localScale = Vector3.one * previewMarkerDiameterMeters;

        var collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }

        var renderer = marker.GetComponent<Renderer>();
        if (renderer != null)
        {
            var color = ToUnityColor(placement.color);
            renderer.material.color = color;
            if (renderer.material.HasProperty("_BaseColor"))
            {
                renderer.material.SetColor("_BaseColor", color);
            }
        }

        var labelObject = new GameObject("Label");
        labelObject.transform.SetParent(marker.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 1.2f, 0f);
        labelObject.transform.localScale = Vector3.one * 0.8f;
        var label = labelObject.AddComponent<TextMeshPro>();
        label.text = $"{placement.set_id}\n{placement.color.ToUpperInvariant()} #{ExtractColorNumber(placement.answer_marker_id)}";
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 1.5f;
        label.rectTransform.sizeDelta = new Vector2(6f, 2f);
        label.color = Color.white;

        previewObjects.Add(marker);
        previewLabels.Add(labelObject.transform);
    }

    private static string ExtractColorNumber(string markerId)
    {
        var separator = markerId.LastIndexOf('-');
        return separator >= 0 ? markerId.Substring(separator + 1) : markerId;
    }

    private static Color ToUnityColor(string color)
    {
        switch (color)
        {
            case "red": return Color.red;
            case "blue": return Color.blue;
            case "green": return Color.green;
            case "yellow": return Color.yellow;
            default: return Color.white;
        }
    }

    private void DestroyPreviewObjects()
    {
        foreach (var previewObject in previewObjects)
        {
            if (previewObject != null)
            {
                Destroy(previewObject);
            }
        }

        previewObjects.Clear();
        previewLabels.Clear();
    }

    private void CreateAuthoringHud()
    {
        if (hmdTransform == null)
        {
            return;
        }

        var hudObject = new GameObject("AAG FP1 Authoring HUD");
        hudObject.transform.SetParent(hmdTransform, false);
        hudObject.transform.localPosition = new Vector3(0f, -0.29f, 0.75f);
        hudObject.transform.localRotation = Quaternion.identity;
        hudObject.transform.localScale = Vector3.one * 0.06f;
        authoringHud = hudObject.AddComponent<TextMeshPro>();
        authoringHud.alignment = TextAlignmentOptions.Center;
        authoringHud.fontSize = 1.6f;
        authoringHud.rectTransform.sizeDelta = new Vector2(13f, 12f);
        authoringHud.textWrappingMode = TextWrappingModes.Normal;
        authoringHud.overflowMode = TextOverflowModes.Truncate;
        authoringHud.color = Color.white;
        authoringHud.outlineColor = Color.black;
        authoringHud.outlineWidth = 0.22f;
    }

    private void UpdateHud(bool confirmed)
    {
        var state = confirmed ? "CONFIRMED" : unconfirmedBySet.ContainsKey(SelectedSetId) ? "UNCONFIRMED" : "EMPTY";
        SetHud($"PROVISIONAL {SelectedSetId} [{state}]\n{FormatHudSummary(lastValidationSummary)}\nL INDEX next | L GRIP generate | L STICK save");
    }

    private static string FormatHudSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "No status";
        }

        var formatted = summary;
        var roomIndex = formatted.IndexOf("room=", StringComparison.Ordinal);
        if (roomIndex >= 0 && formatted.Length >= roomIndex + 41)
        {
            var uuidStart = roomIndex + 5;
            var possibleUuid = formatted.Substring(uuidStart, 36);
            if (Guid.TryParse(possibleUuid, out _))
            {
                formatted = formatted.Remove(uuidStart + 8, 28);
            }
        }

        formatted = formatted
            .Replace("; room=", "\nroom=")
            .Replace("; marker=", "\nmarker=")
            .Replace("; validBeforeObjectDistance=", "\nvalidBeforeObjectDistance=")
            .Replace("], min", "]\nmin")
            .Replace(", difficultyMetric=", "\ndifficulty=");

        const int maximumHudCharacters = 300;
        return formatted.Length <= maximumHudCharacters
            ? formatted
            : formatted.Substring(0, maximumHudCharacters) + "...\nSee [AAG Authoring] log";
    }

    private void SetHud(string text)
    {
        if (authoringHud != null)
        {
            authoringHud.text = text;
        }
    }

    private void LogProvisionalSettings()
    {
        GetHmdFrustumFovDegrees(out var horizontalFov, out var verticalFov);
        Debug.Log($"[AAG Authoring] PROVISIONAL settings: wall={minimumWallDistanceMeters:F3}m, "
            + $"objectSurface={minimumObjectDistanceMeters:F3}m, floorGap={floorGapMeters:F3}m, "
            + $"markerBottomOffset={PreviewObjectBottomOffsetMeters:F3}m, floorTolerance={floorAlignmentToleranceMeters:F3}m, "
            + $"obstacle={minimumObstacleDistanceMeters:F3}m, generationSafetyMargin={generationSafetyMarginMeters:F6}m, "
            + $"validationEpsilon={validationEpsilonMeters:F6}m, clearanceDefinition=\"{ClearanceDistanceDefinition}\", "
            + $"roomDistribution={roomDistributionMode}, "
            + $"cornerRange={minimumCornerDistanceMeters:F3}-{maximumCornerDistanceMeters:F3}m, "
            + $"wallCornerAngleMax={maximumCornerAngleDegrees:F1}deg, cornerEdgeMin={minimumCornerAdjacentEdgeLengthMeters:F3}m, "
            + $"doorway={minimumDoorwayDistanceMeters:F3}m, "
            + $"candidateGrid={candidateGridSpacingMeters:F3}m, preferredWallBand={preferredWallBandWidthMeters:F3}m, "
            + $"hallAspect={narrowHallAspectRatioThreshold:F2}, walkingPathSoft={minimumWalkingPathDistanceMeters:F3}m, "
            + $"angularSeparationSoft={minimumAngularSeparationDegrees:F1}deg, maxVisibleSoft={maxVisibleObjectsPerView}, "
            + $"minDiscoverableHard={minimumDiscoverableObservationCount}, "
            + $"observationGrid={observationGridSize}x{observationGridSize}, eyeHeight={observationEyeHeightMeters:F3}m, "
            + $"HMDFrustum={horizontalFov:F1}x{verticalFov:F1}deg, "
            + $"minimumRooms={minimumRoomsUsed}, zones={zoneGridColumns}x{zoneGridRows}, "
            + $"maxPerZone={maximumMarkersPerZone}, difficultyMetric={difficultyDistanceMetric}, "
            + $"seeds={fp1S1Seed}/{fp1S2Seed}/{fp1S3Seed}");
        Debug.Log($"[AAG Authoring] PROVISIONAL scoring weights: corner={cornerProximityWeight:F2}, "
            + $"wallBand={wallBandWeight:F2}, doorway={doorwayDistanceWeight:F2}, walking={walkingPathDistanceWeight:F2}, "
            + $"entrancePenalty={entranceVisibilityPenaltyWeight:F2}, coVisibilityPenalty={sameRoomCoVisibilityPenaltyWeight:F2}, "
            + $"objectDistance={objectDistanceWeight:F2}, sameWallPenalty={sameWallSegmentPenaltyWeight:F2}, "
            + $"zonePenalty={zoneReusePenaltyWeight:F2}, roomDistribution={roomDistributionWeight:F2}, "
            + $"seedVariation={deterministicVariationWeight:F2}");
    }

    private bool ValidateProvisionalSettings()
    {
        var valid = minimumCornerDistanceMeters <= maximumCornerDistanceMeters
            && minimumCornerAdjacentEdgeLengthMeters >= 0f
            && candidateGridSpacingMeters > 0f
            && preferredWallBandWidthMeters > 0f
            && narrowHallAspectRatioThreshold >= 1f
            && generationSafetyMarginMeters >= 0f
            && validationEpsilonMeters >= 0f
            && previewMarkerDiameterMeters > 0f
            && maxVisibleObjectsPerView >= 1
            && minimumDiscoverableObservationCount >= 1
            && observationGridSize >= 2;
        if (!valid)
        {
            Debug.LogError("[AAG Authoring] Invalid PROVISIONAL settings: corner min must not exceed corner max; corner edge/jitter must be non-negative; marker/visibility/observation values must be positive.");
        }

        return valid;
    }

    private void LoadConfirmedPlacements()
    {
        confirmedBySet.Clear();
        if (!File.Exists(JsonPath))
        {
            Debug.Log($"[AAG Authoring] No confirmed placement file yet: {JsonPath}");
            return;
        }

        try
        {
            var file = JsonUtility.FromJson<AagPlacementFile>(File.ReadAllText(JsonPath));
            if (file == null || file.placements == null)
            {
                Debug.LogError($"[AAG Authoring] Confirmed placement JSON is invalid: {JsonPath}");
                return;
            }

            foreach (var setId in SetIds)
            {
                var records = file.placements.Where(record => string.Equals(record.set_id, setId, StringComparison.Ordinal)).ToList();
                if (records.Count == 0)
                {
                    continue;
                }

                if (records.Count != MarkerCountPerSet)
                {
                    Debug.LogError($"[AAG Authoring] Stored {setId} has {records.Count} rows, expected 12; it will not be spawned or replaced.");
                    continue;
                }

                confirmedBySet[setId] = records;
            }

            Debug.Log($"[AAG Authoring] Loaded confirmed placements without regeneration: rows={GetConfirmedRowCount()}/36 from {JsonPath}");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Authoring] Failed to load confirmed placements: {exception}");
        }
    }

    private bool SaveConfirmedPlacementsAndVerifyRoundTrip()
    {
        var temporaryJsonPath = JsonPath + ".tmp";
        var temporaryCsvPath = CsvPath + ".tmp";
        byte[] previousJson = null;
        byte[] previousCsv = null;
        var jsonPreviouslyExisted = false;
        var csvPreviouslyExisted = false;
        try
        {
            Directory.CreateDirectory(StorageFolderPath);
            jsonPreviouslyExisted = File.Exists(JsonPath);
            csvPreviouslyExisted = File.Exists(CsvPath);
            if (jsonPreviouslyExisted)
            {
                previousJson = File.ReadAllBytes(JsonPath);
            }

            if (csvPreviouslyExisted)
            {
                previousCsv = File.ReadAllBytes(CsvPath);
            }

            var expected = BuildPlacementFile();
            var expectedCsv = BuildCsv(expected.placements);
            File.WriteAllText(temporaryJsonPath, JsonUtility.ToJson(expected, true), Encoding.UTF8);
            File.WriteAllText(temporaryCsvPath, expectedCsv, Encoding.UTF8);

            var reloaded = JsonUtility.FromJson<AagPlacementFile>(File.ReadAllText(temporaryJsonPath));
            if (!RecordsAreExactlyEqual(expected.placements, reloaded?.placements))
            {
                Debug.LogError("[AAG Authoring] JSON round-trip verification failed; coordinates are not identical.");
                RestorePreviousConfirmedFiles(jsonPreviouslyExisted, previousJson, csvPreviouslyExisted, previousCsv);
                return false;
            }

            var reloadedCsv = File.ReadAllText(temporaryCsvPath);
            var csvDataRowCount = Math.Max(0, File.ReadAllLines(temporaryCsvPath).Length - 1);
            if (csvDataRowCount != expected.placements.Count)
            {
                Debug.LogError($"[AAG Authoring] CSV row verification failed: rows={csvDataRowCount}, expected={expected.placements.Count}.");
                RestorePreviousConfirmedFiles(jsonPreviouslyExisted, previousJson, csvPreviouslyExisted, previousCsv);
                return false;
            }

            if (!string.Equals(expectedCsv, reloadedCsv, StringComparison.Ordinal))
            {
                Debug.LogError("[AAG Authoring] CSV round-trip verification failed; saved content is not identical.");
                RestorePreviousConfirmedFiles(jsonPreviouslyExisted, previousJson, csvPreviouslyExisted, previousCsv);
                return false;
            }

            File.Copy(temporaryJsonPath, JsonPath, true);
            File.Copy(temporaryCsvPath, CsvPath, true);

            var finalReloaded = JsonUtility.FromJson<AagPlacementFile>(File.ReadAllText(JsonPath));
            var finalCsv = File.ReadAllText(CsvPath);
            if (!RecordsAreExactlyEqual(expected.placements, finalReloaded?.placements)
                || !string.Equals(expectedCsv, finalCsv, StringComparison.Ordinal))
            {
                Debug.LogError("[AAG Authoring] Final JSON/CSV reload verification failed; restoring the previous confirmed files.");
                RestorePreviousConfirmedFiles(jsonPreviouslyExisted, previousJson, csvPreviouslyExisted, previousCsv);
                return false;
            }

            Debug.Log($"[AAG Authoring] JSON/CSV save-and-reload verification passed with identical coordinates/content: rows={expected.placements.Count}.");

            var expectedRowCount = GetConfirmedRowCount();
            if (expectedRowCount == 36)
            {
                Debug.Log($"[AAG Authoring] FINAL FP1 export verified: JSON/CSV rows=36; JSON={JsonPath}; CSV={CsvPath}");
            }
            else
            {
                Debug.Log($"[AAG Authoring] Partial FP1 export verified: JSON/CSV rows={expectedRowCount}/36; remaining sets are not fabricated.");
            }

            return true;
        }
        catch (Exception exception)
        {
            RestorePreviousConfirmedFiles(jsonPreviouslyExisted, previousJson, csvPreviouslyExisted, previousCsv);
            Debug.LogError($"[AAG Authoring] Save/export failed: {exception}");
            return false;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryJsonPath);
            TryDeleteTemporaryFile(temporaryCsvPath);
        }
    }

    private void RestorePreviousConfirmedFiles(
        bool jsonPreviouslyExisted,
        byte[] previousJson,
        bool csvPreviouslyExisted,
        byte[] previousCsv)
    {
        try
        {
            if (jsonPreviouslyExisted)
            {
                if (previousJson != null)
                {
                    File.WriteAllBytes(JsonPath, previousJson);
                }
            }
            else if (File.Exists(JsonPath))
            {
                File.Delete(JsonPath);
            }

            if (csvPreviouslyExisted)
            {
                if (previousCsv != null)
                {
                    File.WriteAllBytes(CsvPath, previousCsv);
                }
            }
            else if (File.Exists(CsvPath))
            {
                File.Delete(CsvPath);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Authoring] Failed to restore the previous confirmed files: {exception}");
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[AAG Authoring] Could not remove temporary file '{path}': {exception.Message}");
        }
    }

    private AagPlacementFile BuildPlacementFile()
    {
        var file = new AagPlacementFile
        {
            schema_version = SchemaVersion,
            placements = new List<AagPlacementRecord>(),
        };

        foreach (var setId in SetIds)
        {
            if (confirmedBySet.TryGetValue(setId, out var records))
            {
                file.placements.AddRange(records.OrderBy(record => record.answer_marker_id, StringComparer.Ordinal));
            }
        }

        return file;
    }

    private static string BuildCsv(List<AagPlacementRecord> placements)
    {
        var csv = new StringBuilder();
        csv.AppendLine("floor_plan_id,set_id,answer_marker_id,color,room_uuid,world_x,world_y,world_z,seed,corner_distance,wall_distance,nearest_object_distance,discoverable_observation_count,set_max_visible_objects_per_view");
        foreach (var record in placements)
        {
            var fields = new[]
            {
                record.floor_plan_id,
                record.set_id,
                record.answer_marker_id,
                record.color,
                record.room_uuid,
                record.world_x.ToString("R", CultureInfo.InvariantCulture),
                record.world_y.ToString("R", CultureInfo.InvariantCulture),
                record.world_z.ToString("R", CultureInfo.InvariantCulture),
                record.seed.ToString(CultureInfo.InvariantCulture),
                record.corner_distance.ToString("R", CultureInfo.InvariantCulture),
                record.wall_distance.ToString("R", CultureInfo.InvariantCulture),
                record.nearest_object_distance.ToString("R", CultureInfo.InvariantCulture),
                record.discoverable_observation_count.ToString(CultureInfo.InvariantCulture),
                record.set_max_visible_objects_per_view.ToString(CultureInfo.InvariantCulture),
            };
            csv.AppendLine(string.Join(",", fields.Select(CsvEscape)));
        }

        return csv.ToString();
    }

    private static string CsvEscape(string value)
    {
        return $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
    }

    private static bool RecordsAreExactlyEqual(List<AagPlacementRecord> expected, List<AagPlacementRecord> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            if (!string.Equals(left.floor_plan_id, right.floor_plan_id, StringComparison.Ordinal)
                || !string.Equals(left.set_id, right.set_id, StringComparison.Ordinal)
                || !string.Equals(left.answer_marker_id, right.answer_marker_id, StringComparison.Ordinal)
                || !string.Equals(left.color, right.color, StringComparison.Ordinal)
                || !string.Equals(left.room_uuid, right.room_uuid, StringComparison.Ordinal)
                || !left.world_x.Equals(right.world_x)
                || !left.world_y.Equals(right.world_y)
                || !left.world_z.Equals(right.world_z)
                || left.seed != right.seed
                || !left.corner_distance.Equals(right.corner_distance)
                || !left.wall_distance.Equals(right.wall_distance)
                || !left.nearest_object_distance.Equals(right.nearest_object_distance)
                || left.discoverable_observation_count != right.discoverable_observation_count
                || left.set_max_visible_objects_per_view != right.set_max_visible_objects_per_view)
            {
                return false;
            }
        }

        return true;
    }

    private static List<AagPlacementRecord> CloneRecords(List<AagPlacementRecord> records)
    {
        return records.Select(record => new AagPlacementRecord
        {
            floor_plan_id = record.floor_plan_id,
            set_id = record.set_id,
            answer_marker_id = record.answer_marker_id,
            color = record.color,
            room_uuid = record.room_uuid,
            world_x = record.world_x,
            world_y = record.world_y,
            world_z = record.world_z,
            seed = record.seed,
            corner_distance = record.corner_distance,
            wall_distance = record.wall_distance,
            nearest_object_distance = record.nearest_object_distance,
            discoverable_observation_count = record.discoverable_observation_count,
            set_max_visible_objects_per_view = record.set_max_visible_objects_per_view,
        }).ToList();
    }

    private int GetConfirmedRowCount()
    {
        return confirmedBySet.Values.Sum(records => records.Count);
    }

    private static float CalculateMinimumObjectDistance(List<AagPlacementRecord> placements)
    {
        var minimum = float.PositiveInfinity;
        for (var left = 0; left < placements.Count; left++)
        {
            for (var right = left + 1; right < placements.Count; right++)
            {
                minimum = Mathf.Min(minimum, HorizontalDistance(ToVector3(placements[left]), ToVector3(placements[right])));
            }
        }

        return minimum;
    }

    private static float CalculateMeanNearestNeighborDistance(List<AagPlacementRecord> placements)
    {
        var total = 0f;
        for (var left = 0; left < placements.Count; left++)
        {
            var nearest = float.PositiveInfinity;
            for (var right = 0; right < placements.Count; right++)
            {
                if (left != right)
                {
                    nearest = Mathf.Min(nearest, HorizontalDistance(ToVector3(placements[left]), ToVector3(placements[right])));
                }
            }

            total += nearest;
        }

        return total / placements.Count;
    }

    private static float CalculateMeanPairwiseDistance(List<AagPlacementRecord> placements)
    {
        var total = 0f;
        var count = 0;
        for (var left = 0; left < placements.Count; left++)
        {
            for (var right = left + 1; right < placements.Count; right++)
            {
                total += HorizontalDistance(ToVector3(placements[left]), ToVector3(placements[right]));
                count++;
            }
        }

        return count == 0 ? 0f : total / count;
    }

    private static float CalculateMaximumPairwiseDistance(List<AagPlacementRecord> placements)
    {
        var maximum = 0f;
        for (var left = 0; left < placements.Count; left++)
        {
            for (var right = left + 1; right < placements.Count; right++)
            {
                maximum = Mathf.Max(maximum, HorizontalDistance(ToVector3(placements[left]), ToVector3(placements[right])));
            }
        }

        return maximum;
    }

    private static Vector3 ToVector3(AagPlacementRecord record)
    {
        return new Vector3(record.world_x, record.world_y, record.world_z);
    }

    [Serializable]
    private sealed class AagPlacementFile
    {
        public string schema_version;
        public List<AagPlacementRecord> placements = new List<AagPlacementRecord>();
    }

    [Serializable]
    private sealed class AagPlacementRecord
    {
        public string floor_plan_id;
        public string set_id;
        public string answer_marker_id;
        public string color;
        public string room_uuid;
        public float world_x;
        public float world_y;
        public float world_z;
        public int seed;
        public float corner_distance;
        public float wall_distance;
        public float nearest_object_distance;
        public int discoverable_observation_count;
        public int set_max_visible_objects_per_view;
    }
}
