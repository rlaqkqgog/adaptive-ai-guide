using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Uses one native MRUK-tracked QR marker per FP1 zone to correct only that
/// zone's content root. It never moves OVRCameraRig/TrackingSpace and never
/// edits Meta Space Setup data.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(9000)]
public sealed class AagFiducialZoneAlignment : MonoBehaviour
{
    private static readonly string[] RequiredRoomIds =
    {
        "room1", "room2", "room3",
        "hall1-1", "hall1-2", "hall1-3",
        "hall2-1", "hall2-2",
    };

    public const float CalibrationHoldSeconds = 1.2f;
    public const float StableObservationSeconds = 0.6f;
    public const float MarkerFreshnessSeconds = 12f;
    public const float MaximumMarkerPoseStepMeters = 0.035f;
    public const float MaximumMarkerPoseStepDegrees = 2.5f;
    public const float MaximumAcceptedCorrectionMeters = 25f;
    public const float MaximumAcceptedCorrectionDegrees = 90f;
    public const float MaximumCalibrationBoundaryDistanceMeters = 0.75f;
    public const float MinimumCorrectionUpdateMeters = 0.03f;
    public const float MinimumCorrectionUpdateDegrees = 1.5f;

    private sealed class ZoneState
    {
        public ExperimentRoomMapping Mapping;
        public MRUKRoom Room;
        public MRUKAnchor Floor;
        public Transform Root;
        public Pose CorrectedFloorPose;
        public bool HasCorrection;
        public bool AlignedThisSession;
        public float LastAlignedAt = -1f;
        public readonly HashSet<Transform> Registered = new HashSet<Transform>();
    }

    private sealed class MarkerObservation
    {
        public string RoomId;
        public MRUKTrackable Trackable;
        public Pose LatestPose;
        public Pose PreviousPose;
        public bool HasPreviousPose;
        public float StableSince = -1f;
        public float LastSeenAt = -1f;
        public float LastAppliedAt = -1f;
    }

    private Fp1ExperimentConfig config;
    private AagFiducialMarkerStore.Catalog catalog;
    private Action<string, string> writeEvent;
    private readonly Dictionary<string, ZoneState> zones =
        new Dictionary<string, ZoneState>(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkerObservation> observations =
        new Dictionary<string, MarkerObservation>(StringComparer.Ordinal);
    private readonly List<MRUKTrackable> trackableBuffer = new List<MRUKTrackable>();

    private MRUK subscribedMruk;
    private bool trackerRequested;
    private bool trackerConfirmed;
    private bool sessionActive;
    private float calibrationHoldStartedAt = -1f;
    private bool calibrationHoldTriggered;
    private float lastZoneReferenceRefreshAt = -1f;
    private string lastStatus = "QR INITIALIZING";
    private string currentPhysicalRoomUuid = string.Empty;
    private string lastAmbiguousMarkerSet = string.Empty;

    public bool Enabled => config != null && config.fiducialMarkerAlignmentEnabled;
    public string StatusLine => Enabled
        ? $"QR MARKERS {ValidCalibrationCount}/{RequiredZoneCount} CALIBRATED | {lastStatus}"
        : "QR MARKERS DISABLED";
    public int RequiredZoneCount => config?.rooms?.Count(value =>
        value != null && !string.IsNullOrWhiteSpace(value.roomId)) ?? 0;
    public int ValidCalibrationCount => zones.Values.Count(IsZoneCalibrationCurrent);

    public string GetCurrentPhysicalRoomUuid() =>
        Enabled && sessionActive ? currentPhysicalRoomUuid : string.Empty;

    public void Initialize(Fp1ExperimentConfig experimentConfig, Action<string, string> eventWriter)
    {
        config = experimentConfig;
        writeEvent = eventWriter;
        catalog = AagFiducialMarkerStore.LoadOrCreate();
        RebuildZones();
        EnsureTrackerConfigured();
    }

    private void OnEnable()
    {
        EnsureTrackerConfigured();
    }

    private void OnDisable()
    {
        Unsubscribe();
        trackerRequested = false;
        trackerConfirmed = false;
    }

    private void Update()
    {
        if (!Enabled) return;
        EnsureTrackerConfigured();
        if (MRUK.Instance == null) return;

        MRUK.Instance.GetTrackables(trackableBuffer);
        var seenRoomIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trackable in trackableBuffer)
        {
            if (trackable == null
                || !trackable.IsTracked
                || trackable.TrackableType != OVRAnchor.TrackableType.QRCode
                || !AagFiducialMarkerStore.TryParsePayload(
                    trackable.MarkerPayloadString, out var roomId)
                || !zones.ContainsKey(roomId))
                continue;

            seenRoomIds.Add(roomId);
            Observe(roomId, trackable);
        }

        foreach (var observation in observations.Values)
        {
            if (!seenRoomIds.Contains(observation.RoomId)
                && Time.unscaledTime - observation.LastSeenAt > 0.5f)
            {
                observation.StableSince = -1f;
                observation.HasPreviousPose = false;
            }
        }

        UpdatePhysicalRoomFromVisibleMarkers(seenRoomIds);
    }

    public bool HandleIdleCalibrationInput(out string status)
    {
        status = string.Empty;
        if (!Enabled) return false;

        var held = OVRInput.Get(
            OVRInput.RawButton.RThumbstick,
            OVRInput.Controller.RTouch);
#if ENABLE_LEGACY_INPUT_MANAGER
        var keyboardTriggered = Input.GetKeyDown(KeyCode.F9);
#else
        var keyboardTriggered = false;
#endif
        if (!held)
        {
            calibrationHoldStartedAt = -1f;
            calibrationHoldTriggered = false;
        }
        else if (calibrationHoldStartedAt < 0f)
        {
            calibrationHoldStartedAt = Time.unscaledTime;
        }
        else if (!calibrationHoldTriggered
            && Time.unscaledTime - calibrationHoldStartedAt >= CalibrationHoldSeconds)
        {
            calibrationHoldTriggered = true;
            keyboardTriggered = true;
        }

        if (!keyboardTriggered) return false;
        if (TryCalibrateVisibleMarker(out var detail, out var failure))
        {
            lastStatus = $"CALIBRATED {detail}";
            status = $"QR CALIBRATED: {detail}";
            Emit("fiducial_calibrated", detail);
        }
        else
        {
            lastStatus = $"CALIBRATION FAILED {failure}";
            status = $"QR CALIBRATION FAILED: {failure}";
            Emit("fiducial_calibration_failed", failure);
        }
        return true;
    }

    public bool TryValidateSessionStart(string requiredStartRoomId, out string failure)
    {
        failure = string.Empty;
        if (!Enabled) return true;
        EnsureTrackerConfigured();
        RebuildZoneReferences();

        if (MRUK.Instance == null)
        {
            failure = "fiducial_mruk_unavailable";
            return false;
        }
        if (!MRUK.Instance.QRCodeTrackingSupported)
        {
            failure = "fiducial_qr_tracking_unsupported";
            return false;
        }
        if (!MRUK.Instance.TrackerConfiguration.QRCodeTrackingEnabled)
        {
            failure = "fiducial_qr_tracker_not_ready";
            return false;
        }
        if (RequiredZoneCount != RequiredRoomIds.Length
            || zones.Count != RequiredRoomIds.Length
            || RequiredRoomIds.Any(value => !zones.ContainsKey(value)))
        {
            failure = $"fiducial_zone_mapping_invalid_config_{RequiredZoneCount}_unique_{zones.Count}";
            return false;
        }

        var invalid = zones.Values
            .Where(value => !IsZoneCalibrationCurrent(value))
            .Select(value => value.Mapping?.roomId ?? "null")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (invalid.Length > 0)
        {
            failure = $"fiducial_calibration_missing_or_stale_{string.Join("|", invalid)}";
            return false;
        }

        if (!zones.TryGetValue(requiredStartRoomId, out var startZone)
            || !startZone.HasCorrection
            || Time.unscaledTime - startZone.LastAlignedAt > MarkerFreshnessSeconds)
        {
            failure = $"fiducial_start_marker_not_recent_{requiredStartRoomId}";
            return false;
        }
        return true;
    }

    public void BeginSession(string requiredStartRoomId)
    {
        if (!Enabled) return;
        sessionActive = true;
        var now = Time.unscaledTime;
        foreach (var zone in zones.Values)
        {
            zone.AlignedThisSession = zone.HasCorrection
                && now - zone.LastAlignedAt <= MarkerFreshnessSeconds;
            UpdateZoneVisibility(zone);
        }
        currentPhysicalRoomUuid = zones.TryGetValue(requiredStartRoomId, out var startZone)
            && startZone.AlignedThisSession
                ? startZone.Mapping.roomUuid
                : string.Empty;
        Emit(
            "fiducial_session_begin",
            $"calibrated={ValidCalibrationCount}/{RequiredZoneCount}; "
            + $"recent={string.Join("|", zones.Values.Where(value => value.AlignedThisSession).Select(value => value.Mapping.roomId).OrderBy(value => value))}");
    }

    public void EndSession()
    {
        sessionActive = false;
        currentPhysicalRoomUuid = string.Empty;
        foreach (var zone in zones.Values)
        {
            zone.AlignedThisSession = false;
            if (zone.Root != null) zone.Root.gameObject.SetActive(true);
        }
    }

    public bool RegisterContent(string roomUuid, Transform contentRoot, string contentId, out string failure)
    {
        failure = string.Empty;
        if (!Enabled || contentRoot == null) return true;
        var mapping = FindMappingByUuid(roomUuid);
        if (mapping == null || !zones.TryGetValue(mapping.roomId, out var zone))
        {
            failure = $"fiducial_unknown_room_{roomUuid}";
            return false;
        }
        if (!RefreshZoneReference(zone, out failure)) return false;

        var currentFloorPose = new Pose(
            zone.Floor.transform.position,
            zone.Floor.transform.rotation);
        var contentWorldPose = new Pose(contentRoot.position, contentRoot.rotation);
        var floorLocalPose = AagFiducialMarkerStore.RelativeTo(
            currentFloorPose,
            contentWorldPose);
        EnsureZoneRoot(zone);
        if (zone.Registered.Add(contentRoot))
        {
            contentRoot.SetParent(zone.Root, true);
            contentRoot.localPosition = floorLocalPose.position;
            contentRoot.localRotation = floorLocalPose.rotation;
        }
        UpdateZoneVisibility(zone);
        Emit(
            "fiducial_content_registered",
            $"roomId={mapping.roomId}; roomUuid={mapping.roomUuid}; content={contentId}; "
            + $"aligned={zone.AlignedThisSession}");
        return true;
    }

    public void DetachRegisteredAncestor(Transform descendant, string reason)
    {
        if (!Enabled || descendant == null) return;
        foreach (var zone in zones.Values)
        {
            var registered = zone.Registered.FirstOrDefault(value =>
                value != null && (value == descendant || descendant.IsChildOf(value)));
            if (registered == null) continue;
            registered.SetParent(null, true);
            zone.Registered.Remove(registered);
            Emit(
                "fiducial_content_detached",
                $"roomId={zone.Mapping.roomId}; content={registered.name}; reason={reason}");
            return;
        }
    }

    public void ReleaseAllContent()
    {
        foreach (var zone in zones.Values)
        {
            foreach (var content in zone.Registered.Where(value => value != null).ToArray())
                content.SetParent(null, true);
            zone.Registered.Clear();
            if (zone.Root != null) Destroy(zone.Root.gameObject);
            zone.Root = null;
            zone.AlignedThisSession = false;
        }
    }

    private void Observe(string roomId, MRUKTrackable trackable)
    {
        if (!observations.TryGetValue(roomId, out var observation))
        {
            observation = new MarkerObservation { RoomId = roomId };
            observations.Add(roomId, observation);
        }

        var now = Time.unscaledTime;
        var pose = new Pose(trackable.transform.position, trackable.transform.rotation);
        if (!AagFiducialMarkerStore.IsFinite(pose)) return;

        observation.Trackable = trackable;
        observation.LatestPose = pose;
        observation.LastSeenAt = now;
        if (!observation.HasPreviousPose
            || Vector3.Distance(observation.PreviousPose.position, pose.position)
                > MaximumMarkerPoseStepMeters
            || Quaternion.Angle(observation.PreviousPose.rotation, pose.rotation)
                > MaximumMarkerPoseStepDegrees)
        {
            observation.StableSince = now;
        }
        else if (observation.StableSince < 0f)
        {
            observation.StableSince = now;
        }
        observation.PreviousPose = pose;
        observation.HasPreviousPose = true;

        if (now - observation.StableSince < StableObservationSeconds
            || now - observation.LastAppliedAt < 0.4f)
            return;

        observation.LastAppliedAt = now;
        TryApplyObservation(observation);
    }

    private bool TryApplyObservation(MarkerObservation observation)
    {
        if (!zones.TryGetValue(observation.RoomId, out var zone))
        {
            lastStatus = $"SEEN {observation.RoomId}; NOT CALIBRATED";
            return false;
        }
        if (!RefreshZoneReference(zone, out var referenceFailure))
        {
            lastStatus = $"SEEN {observation.RoomId}; FLOOR MISSING";
            Emit("fiducial_alignment_rejected", referenceFailure);
            return false;
        }
        if (!IsZoneCalibrationCurrent(zone))
        {
            lastStatus = $"SEEN {observation.RoomId}; NOT CALIBRATED";
            return false;
        }

        AagFiducialMarkerStore.TryGet(catalog, observation.RoomId, out var calibration);
        var rawCorrectedFloor = AagFiducialMarkerStore.Compose(
            observation.LatestPose,
            AagFiducialMarkerStore.Inverse(calibration.FloorLocalPose));
        var currentFloorPose = new Pose(zone.Floor.transform.position, zone.Floor.transform.rotation);
        var yawDelta = Mathf.DeltaAngle(
            currentFloorPose.rotation.eulerAngles.y,
            rawCorrectedFloor.rotation.eulerAngles.y);
        var correctedFloor = new Pose(
            rawCorrectedFloor.position,
            Quaternion.AngleAxis(yawDelta, Vector3.up) * currentFloorPose.rotation);

        var correctionDistance = Vector3.Distance(
            currentFloorPose.position, correctedFloor.position);
        var correctionAngle = Quaternion.Angle(
            currentFloorPose.rotation, correctedFloor.rotation);
        if (!AagFiducialMarkerStore.IsFinite(correctedFloor)
            || correctionDistance > MaximumAcceptedCorrectionMeters
            || correctionAngle > MaximumAcceptedCorrectionDegrees)
        {
            lastStatus = $"REJECTED {observation.RoomId}";
            Emit(
                "fiducial_alignment_rejected",
                $"roomId={observation.RoomId}; distance={correctionDistance:F3}; "
                + $"angle={correctionAngle:F2}");
            return false;
        }

        var firstAlignment = !zone.HasCorrection;
        var correctionUpdateDistance = firstAlignment
            ? float.PositiveInfinity
            : Vector3.Distance(zone.CorrectedFloorPose.position, correctedFloor.position);
        var correctionUpdateAngle = firstAlignment
            ? float.PositiveInfinity
            : Quaternion.Angle(zone.CorrectedFloorPose.rotation, correctedFloor.rotation);
        var shouldMoveContent = firstAlignment
            || correctionUpdateDistance >= MinimumCorrectionUpdateMeters
            || correctionUpdateAngle >= MinimumCorrectionUpdateDegrees;

        if (shouldMoveContent) zone.CorrectedFloorPose = correctedFloor;
        zone.HasCorrection = true;
        var firstSessionAlignment = !zone.AlignedThisSession;
        zone.AlignedThisSession = true;
        zone.LastAlignedAt = Time.unscaledTime;
        if (zone.Root != null && shouldMoveContent)
        {
            zone.Root.SetPositionAndRotation(
                zone.CorrectedFloorPose.position,
                zone.CorrectedFloorPose.rotation);
        }
        UpdateZoneVisibility(zone);

        lastStatus = $"ALIGNED {observation.RoomId}";
        if (firstSessionAlignment || shouldMoveContent)
        {
            Emit(
                "fiducial_zone_aligned",
                $"roomId={observation.RoomId}; payload={observation.Trackable.MarkerPayloadString}; "
                + $"correctionMeters={correctionDistance:F4}; correctionDegrees={correctionAngle:F3}; "
                + $"updateMeters={correctionUpdateDistance:F4}; updateDegrees={correctionUpdateAngle:F3}; "
                + $"registered={zone.Registered.Count}");
        }
        return true;
    }

    private bool TryCalibrateVisibleMarker(out string detail, out string failure)
    {
        detail = string.Empty;
        failure = string.Empty;
        var now = Time.unscaledTime;
        var candidates = observations.Values
            .Where(value => value.Trackable != null
                && value.Trackable.IsTracked
                && value.StableSince >= 0f
                && now - value.StableSince >= StableObservationSeconds
                && now - value.LastSeenAt <= 0.5f)
            .OrderBy(value => value.RoomId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length != 1)
        {
            failure = $"requires_exactly_one_stable_marker_actual_{candidates.Length}";
            return false;
        }

        var observation = candidates[0];
        if (!zones.TryGetValue(observation.RoomId, out var zone)
            || !RefreshZoneReference(zone, out failure))
            return false;

        var localPosition = zone.Floor.transform.InverseTransformPoint(
            observation.LatestPose.position);
        var boundaryPoint = new Vector2(localPosition.x, localPosition.y);
        var boundaryDistance = DistanceToBoundary(boundaryPoint, zone.Floor.PlaneBoundary2D);
        if (!zone.Floor.IsPositionInBoundary(boundaryPoint)
            && boundaryDistance > MaximumCalibrationBoundaryDistanceMeters)
        {
            failure = $"marker_not_near_intended_room_{observation.RoomId}_distance_{boundaryDistance:F2}m";
            return false;
        }

        var floorPose = new Pose(zone.Floor.transform.position, zone.Floor.transform.rotation);
        var localPose = AagFiducialMarkerStore.RelativeTo(
            floorPose,
            observation.LatestPose);
        if (!Guid.TryParse(zone.Mapping.roomUuid, out var roomUuid)
            || zone.Floor.Anchor == null
            || zone.Floor.Anchor.Uuid == Guid.Empty
            || !AagFiducialMarkerStore.Upsert(
                catalog,
                observation.RoomId,
                roomUuid,
                zone.Floor.Anchor.Uuid,
                observation.Trackable.MarkerPayloadString,
                localPose,
                out failure))
            return false;

        catalog = AagFiducialMarkerStore.LoadOrCreate();
        detail = $"roomId={observation.RoomId}; roomUuid={roomUuid}; "
            + $"floorUuid={zone.Floor.Anchor.Uuid}; boundaryDistance={boundaryDistance:F3}; "
            + $"count={ValidCalibrationCount}/{RequiredZoneCount}";
        return true;
    }

    private void EnsureTrackerConfigured()
    {
        if (!Enabled || MRUK.Instance == null || MRUK.Instance.SceneSettings == null) return;
        if (Time.unscaledTime - lastZoneReferenceRefreshAt >= 1f)
        {
            RebuildZoneReferences();
            lastZoneReferenceRefreshAt = Time.unscaledTime;
        }
        if (subscribedMruk != MRUK.Instance)
        {
            Unsubscribe();
            subscribedMruk = MRUK.Instance;
            subscribedMruk.SceneSettings.TrackableAdded.AddListener(OnTrackableAdded);
            subscribedMruk.SceneSettings.TrackableRemoved.AddListener(OnTrackableRemoved);
        }

        var desired = MRUK.Instance.SceneSettings.TrackerConfiguration;
        if (!desired.QRCodeTrackingEnabled)
        {
            desired.QRCodeTrackingEnabled = true;
            MRUK.Instance.SceneSettings.TrackerConfiguration = desired;
            trackerRequested = true;
            lastStatus = "QR TRACKER REQUESTED";
            Debug.Log("[Fiducial] Requested native MRUK QR tracking.", this);
        }
        else if (!trackerRequested)
        {
            trackerRequested = true;
            lastStatus = "QR TRACKER REQUESTED";
        }

        if (!trackerConfirmed && MRUK.Instance.TrackerConfiguration.QRCodeTrackingEnabled)
        {
            trackerConfirmed = true;
            lastStatus = "QR TRACKER READY";
            Debug.Log("[Fiducial] Native MRUK QR tracker is active.", this);
        }
    }

    private void Unsubscribe()
    {
        if (subscribedMruk == null || subscribedMruk.SceneSettings == null) return;
        subscribedMruk.SceneSettings.TrackableAdded.RemoveListener(OnTrackableAdded);
        subscribedMruk.SceneSettings.TrackableRemoved.RemoveListener(OnTrackableRemoved);
        subscribedMruk = null;
    }

    private void OnTrackableAdded(MRUKTrackable trackable)
    {
        if (trackable == null || trackable.TrackableType != OVRAnchor.TrackableType.QRCode) return;
        Emit(
            "fiducial_marker_added",
            $"payload={trackable.MarkerPayloadString ?? "binary_or_pending"}; uuid={trackable.Anchor.Uuid}");
    }

    private void OnTrackableRemoved(MRUKTrackable trackable)
    {
        if (trackable == null || trackable.TrackableType != OVRAnchor.TrackableType.QRCode) return;
        Emit(
            "fiducial_marker_removed",
            $"payload={trackable.MarkerPayloadString ?? "binary_or_unknown"}");
    }

    private void RebuildZones()
    {
        zones.Clear();
        foreach (var mapping in config?.rooms ?? Array.Empty<ExperimentRoomMapping>())
        {
            if (mapping == null
                || string.IsNullOrWhiteSpace(mapping.roomId)
                || string.IsNullOrWhiteSpace(mapping.roomUuid))
                continue;
            zones[mapping.roomId.Trim().ToLowerInvariant()] = new ZoneState { Mapping = mapping };
        }
        RebuildZoneReferences();
    }

    private void RebuildZoneReferences()
    {
        foreach (var zone in zones.Values) RefreshZoneReference(zone, out _);
    }

    private bool RefreshZoneReference(ZoneState zone, out string failure)
    {
        failure = string.Empty;
        if (zone?.Mapping == null
            || !Guid.TryParse(zone.Mapping.roomUuid, out var roomUuid))
        {
            failure = "fiducial_invalid_room_mapping";
            return false;
        }

        zone.Room = MRUK.Instance?.Rooms?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == roomUuid);
        zone.Floor = zone.Room?.FloorAnchors?.FirstOrDefault(value =>
            value != null && value.Anchor != null
            && value.PlaneBoundary2D != null && value.PlaneBoundary2D.Count >= 3);
        if (zone.Floor == null)
        {
            failure = $"fiducial_floor_missing_{zone.Mapping.roomId}_{roomUuid}";
            return false;
        }
        return true;
    }

    private bool IsZoneCalibrationCurrent(ZoneState zone)
    {
        if (zone?.Mapping == null
            || !AagFiducialMarkerStore.TryGet(catalog, zone.Mapping.roomId, out var calibration)
            || !string.Equals(calibration.roomUuid, zone.Mapping.roomUuid, StringComparison.OrdinalIgnoreCase)
            || zone.Floor == null
            || zone.Floor.Anchor == null)
            return false;
        return string.Equals(
            calibration.floorAnchorUuid,
            zone.Floor.Anchor.Uuid.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureZoneRoot(ZoneState zone)
    {
        if (zone.Root != null) return;
        var root = new GameObject($"FiducialZoneRoot {zone.Mapping.roomId}");
        zone.Root = root.transform;
        var pose = zone.HasCorrection
            ? zone.CorrectedFloorPose
            : new Pose(zone.Floor.transform.position, zone.Floor.transform.rotation);
        zone.Root.SetPositionAndRotation(pose.position, pose.rotation);
    }

    private void UpdateZoneVisibility(ZoneState zone)
    {
        if (zone?.Root == null) return;
        zone.Root.gameObject.SetActive(!sessionActive || zone.AlignedThisSession);
    }

    private void UpdatePhysicalRoomFromVisibleMarkers(HashSet<string> seenRoomIds)
    {
        if (!sessionActive) return;
        var now = Time.unscaledTime;
        var candidates = observations.Values
            .Where(value => seenRoomIds.Contains(value.RoomId)
                && value.StableSince >= 0f
                && now - value.StableSince >= StableObservationSeconds
                && now - value.LastSeenAt <= 0.5f
                && zones.TryGetValue(value.RoomId, out var zone)
                && zone.AlignedThisSession
                && IsZoneCalibrationCurrent(zone))
            .Select(value => value.RoomId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 1)
        {
            currentPhysicalRoomUuid = zones[candidates[0]].Mapping.roomUuid;
            lastAmbiguousMarkerSet = string.Empty;
            return;
        }
        if (candidates.Length <= 1) return;

        var markerSet = string.Join("|", candidates);
        if (string.Equals(markerSet, lastAmbiguousMarkerSet, StringComparison.Ordinal)) return;
        lastAmbiguousMarkerSet = markerSet;
        Emit(
            "fiducial_room_ambiguous",
            $"markers={markerSet}; retainedRoomUuid={currentPhysicalRoomUuid}");
    }

    private ExperimentRoomMapping FindMappingByUuid(string roomUuid)
    {
        return (config?.rooms ?? Array.Empty<ExperimentRoomMapping>()).FirstOrDefault(value =>
            value != null
            && string.Equals(value.roomUuid, roomUuid, StringComparison.OrdinalIgnoreCase));
    }

    private static float DistanceToBoundary(
        Vector2 point,
        IReadOnlyList<Vector2> boundary)
    {
        if (boundary == null || boundary.Count < 2) return float.PositiveInfinity;
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
            minimumSquared = Mathf.Min(
                minimumSquared,
                (point - (start + segment * t)).sqrMagnitude);
        }
        return Mathf.Sqrt(minimumSquared);
    }

    private void Emit(string eventType, string detail)
    {
        Debug.Log($"[Fiducial] {eventType}: {detail}", this);
        writeEvent?.Invoke(eventType, detail);
    }
}
