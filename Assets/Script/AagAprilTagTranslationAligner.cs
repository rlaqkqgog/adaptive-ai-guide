using System;
using System.Collections.Generic;
using System.Linq;
using AprilTag;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Detects the configured AprilTag in the left passthrough camera, converts its pose
/// to Unity world space using the image-timestamp camera pose, and previews a
/// yaw-only rigid correction from the authored reference to the observed physical tag.
/// MRUK and the tracking rig remain unchanged. The measured translation is
/// applied only to experiment-owned content. FP2 can additionally use a filtered
/// tag yaw after position and yaw stability both pass strict acceptance gates.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(8400)]
public sealed class AagAprilTagTranslationAligner : MonoBehaviour
{
    public const int DefaultExpectedTagId = 0;
    public const float DefaultTagSizeMeters = 0.095f;
    public const float SampleWindowSeconds = 3f;
    public const float MarkerFreshnessSeconds = 12f;
    public const float MaximumAcceptedCorrectionMeters = 40f;
    public const float MaximumAcceptedVerticalCorrectionMeters = 0.5f;
    // A bundled baked scene and the current headset tracking frame may have
    // arbitrary horizontal headings. The correct tag-pose branch can therefore
    // exceed 90 degrees. FP2 accepts it only after the candidate rigid transform
    // maps the tracked head into the configured baked start room.
    public const float MaximumAcceptedYawCorrectionDegrees = 180f;

    private const int Decimation = 2;
    private const float ProcessingIntervalSeconds = 0.10f;
    private const int MinimumStableSamples = 20;
    private const float MaximumStableJitterMeters = 0.035f;
    private const float ControllerApplyHoldSeconds = 1.5f;

    [Header("Scene references")]
    [SerializeField] private AagRoom3TagReference room3TagReference;
    [SerializeField] private AagFixedSpaceOffset fixedSpaceOffset;

    [Header("Marker identity")]
    [Min(0)]
    [SerializeField] private int expectedTagId = DefaultExpectedTagId;
    [Min(0.001f)]
    [SerializeField] private float tagSizeMeters = DefaultTagSizeMeters;
    [SerializeField] private string alignmentLabel = "FP1 ROOM3";

    [Header("Safety")]
    [Tooltip("Keep enabled while positioning the reference. ApplyPreview is rejected in this mode.")]
    [SerializeField] private bool previewOnly = true;
    [Tooltip("When enabled, holding both thumbsticks applies a valid preview. Keep disabled until on-site review.")]
    [SerializeField] private bool allowQuestControllerApply;
    [SerializeField] private bool horizontalOnly = true;
    [SerializeField] private bool requireAppliedAlignmentBeforeSession = true;
    [Tooltip("Measure filtered tag yaw for HUD and logs without using it for alignment.")]
    [SerializeField] private bool recordDetectedYaw;
    [Tooltip("Apply the measured yaw to content. Keep disabled while yaw is diagnostic-only.")]
    [SerializeField] private bool applyDetectedYawRotation;
    [Range(0.1f, 5f)]
    [SerializeField] private float maximumStableYawJitterDegrees = 0.75f;

    [Header("Content fine tuning")]
    [Tooltip("Fixed on-site baseline along the tagged wall. FP2 starts at zero until measured.")]
    [SerializeField] private float contentAlongWallBaselineMeters = -0.65f;
    [Tooltip("Adjustment measured from the fixed -0.65 m baseline. The current +0.15 m produces -0.50 m.")]
    [SerializeField] private float contentAlongWallAdjustmentMeters = 0.15f;
    [Tooltip("Moves experiment content horizontally away from the tagged wall and into the room.")]
    [SerializeField] private float contentWallClearanceMeters = 0.25f;

    [Header("Runtime display")]
    [SerializeField] private bool showRuntimeHud = true;
    [Tooltip("Hide the red reference cube and any other reference renderers after alignment is applied.")]
    [SerializeField] private bool hideReferenceVisualsAfterApply = true;

    private readonly List<ObservationSample> samples = new List<ObservationSample>(32);
    private PassthroughCameraAccess cameraAccess;
    private TagDetector detector;
    private Color32[] pixels;
    private GameObject hudRoot;
    private Text hud;
    private Vector2Int detectorResolution;
    private float verticalFovRadians;
    private float nextProcessTime;
    private float lastDetectionTime = float.NegativeInfinity;
    private float controllerHoldStartedAt = -1f;
    private float lastPreviewLogTime = float.NegativeInfinity;
    private Vector3 lastLoggedPreview;
    private Vector3 lastCameraPosition;
    private Vector3 lastCameraLocalTagPosition;
    private Vector3 medianDetectedWorldPosition;
    private Quaternion previewYawRotation = Quaternion.identity;
    private float previewYawDegrees;
    private float previewYawJitterDegrees;
    private bool hasYawDiagnostic;
    private string yawBranchStatus = "not_evaluated";
    private Vector3 expectedReferenceWorldPosition;
    private Vector3 previewOffsetMeters;
    private float previewJitterMeters;
    private string runtimeStatus = "WAITING FOR HEADSET CAMERA";
    private string lastSeenIds = "none";
    private bool hasStablePreview;
    private bool isApplied;

    private struct ObservationSample
    {
        public float Time;
        public Vector3 WorldPosition;
        public Quaternion WorldRotation;
    }

    public bool PreviewOnly => previewOnly;
    public bool AllowQuestControllerApply => allowQuestControllerApply;
    public bool HorizontalOnly => horizontalOnly;
    public int ExpectedTagId => expectedTagId;
    public float TagSizeMeters => tagSizeMeters;
    public string AlignmentLabel => string.IsNullOrWhiteSpace(alignmentLabel)
        ? "APRILTAG"
        : alignmentLabel.Trim();
    public AagRoom3TagReference Room3TagReference => room3TagReference;
    public AagFixedSpaceOffset FixedSpaceOffset => fixedSpaceOffset;
    public bool RequireAppliedAlignmentBeforeSession => requireAppliedAlignmentBeforeSession;
    public bool RecordDetectedYaw => recordDetectedYaw;
    public bool ApplyDetectedYawRotation => applyDetectedYawRotation;
    public float MaximumStableYawJitterDegrees => maximumStableYawJitterDegrees;
    public bool ShowRuntimeHud => showRuntimeHud;
    public bool HideReferenceVisualsAfterApply => hideReferenceVisualsAfterApply;
    public bool HasStablePreview => hasStablePreview;
    public bool IsApplied => isApplied;
    public Vector3 PreviewOffsetMeters => previewOffsetMeters;
    public float ContentAlongWallBaselineMeters => contentAlongWallBaselineMeters;
    public float ContentAlongWallAdjustmentMeters => contentAlongWallAdjustmentMeters;
    public float ContentAlongWallBackMeters =>
        contentAlongWallBaselineMeters + contentAlongWallAdjustmentMeters;
    public float ContentWallClearanceMeters => contentWallClearanceMeters;
    public Quaternion PreviewYawRotation => previewYawRotation;
    public float PreviewYawDegrees => previewYawDegrees;
    public float PreviewYawJitterDegrees => previewYawJitterDegrees;
    public bool HasYawDiagnostic => hasYawDiagnostic;
    public string YawBranchStatus => yawBranchStatus;
    public Vector3 ContentFineTuneMeters => ResolveContentFineTuneMeters(AppliedYawRotation);
    public Vector3 ContentOffsetMeters => previewOffsetMeters + ContentFineTuneMeters;
    public Vector3 MedianDetectedWorldPosition => medianDetectedWorldPosition;
    public Vector3 ExpectedReferenceWorldPosition => expectedReferenceWorldPosition;
    public float PreviewJitterMeters => previewJitterMeters;
    public string StatusLine => runtimeStatus;

    private Quaternion AppliedYawRotation =>
        applyDetectedYawRotation ? previewYawRotation : Quaternion.identity;

    public void HideRuntimeHud()
    {
        SetHudVisible(false);
    }

    private void Awake()
    {
        // Domain reload can be disabled in the Unity Editor. Never inherit an
        // applied query correction from a previous play session or scene.
        AagMrukSpaceCorrection.Reset();
        Application.runInBackground = true;
        if (room3TagReference == null)
            room3TagReference = FindFirstObjectByType<AagRoom3TagReference>();
        if (fixedSpaceOffset == null)
            fixedSpaceOffset = GetComponent<AagFixedSpaceOffset>()
                ?? FindFirstObjectByType<AagFixedSpaceOffset>();
        if (showRuntimeHud) CreateHud();
        CreateCameraAccess();
        Debug.Log(
            $"[AAG AprilTag Align] Started. label={AlignmentLabel} family=tagStandard41h12 id={expectedTagId} "
            + $"tagSize={TagSizeMeters:F3}m previewOnly={previewOnly} horizontalOnly={horizontalOnly}",
            this);
    }

    private void Update()
    {
        // FP2 uses AprilTag as a one-shot session alignment. Once accepted,
        // release the passthrough camera and keep all setup UI hidden.
        if (isApplied)
        {
            SetHudVisible(false);
            return;
        }

        PruneSamples();
        UpdateExpectedReference();

        if (cameraAccess == null)
        {
            SetUnavailable("camera_access_missing");
            UpdateHud();
            return;
        }
        if (!cameraAccess.IsPlaying)
        {
            SetUnavailable(PassthroughCameraAccess.IsSupported
                ? "WAITING FOR HEADSET CAMERA PERMISSION"
                : "PASSTHROUGH CAMERA UNSUPPORTED");
            UpdateHud();
            return;
        }

        EnsureDetector();
        if (detector != null
            && cameraAccess.IsUpdatedThisFrame
            && Time.unscaledTime >= nextProcessTime)
        {
            nextProcessTime = Time.unscaledTime + ProcessingIntervalSeconds;
            ProcessLatestFrame();
        }

        EvaluatePreview();
        HandleControllerApply();
        UpdateHud();
    }

    private void OnDestroy()
    {
        AagMrukSpaceCorrection.Reset();
        detector?.Dispose();
        detector = null;
        if (hudRoot != null) Destroy(hudRoot);
    }

    public bool TryValidateSessionStart(out string failure)
    {
        failure = string.Empty;
        if (!requireAppliedAlignmentBeforeSession) return true;
        if (!isApplied)
        {
            failure = "apriltag_translation_not_applied";
            return false;
        }
        return true;
    }

    [ContextMenu("Apply Stable Preview")]
    public bool ApplyPreview()
    {
        if (isApplied) return true;
        if (previewOnly)
        {
            RejectApply("preview_only_is_enabled");
            return false;
        }
        if (!hasStablePreview)
        {
            RejectApply("stable_preview_unavailable");
            return false;
        }
        if (fixedSpaceOffset == null)
        {
            RejectApply("fixed_space_offset_missing");
            return false;
        }
        if (!IsFinite(previewOffsetMeters)
            || previewOffsetMeters.magnitude > MaximumAcceptedCorrectionMeters)
        {
            RejectApply($"correction_rejected_{previewOffsetMeters.magnitude:F3}m");
            return false;
        }

        if (!room3TagReference.TryResolveExpectedWorldPose(out var referencePose, out var referenceFailure))
        {
            RejectApply(referenceFailure);
            return false;
        }
        var appliedYawRotation = AppliedYawRotation;
        var alignedReferenceRotation = appliedYawRotation * referencePose.rotation;
        var contentFineTuneMeters = ResolveHorizontalReferenceFineTune(
            alignedReferenceRotation,
            ContentAlongWallBackMeters,
            contentWallClearanceMeters);
        var contentOffsetMeters = previewOffsetMeters + contentFineTuneMeters;
        if (!IsFinite(contentOffsetMeters)
            || contentOffsetMeters.magnitude > MaximumAcceptedCorrectionMeters)
        {
            RejectApply($"content_correction_rejected_{contentOffsetMeters.magnitude:F3}m");
            return false;
        }
        // Keep the reference visual centered on the detected physical tag. The
        // optional fine tune is deliberately applied only to experiment content.
        if (room3TagReference != null)
            room3TagReference.transform.SetPositionAndRotation(
                medianDetectedWorldPosition,
                alignedReferenceRotation);

        // Translation-only is the active FP2 field mode. The rigid branch stays
        // available for a later, explicit trial after diagnostic yaw evidence is reviewed.
        if (applyDetectedYawRotation)
        {
            if (!AagMrukSpaceCorrection.TryApplyRigid(
                    contentOffsetMeters,
                    appliedYawRotation,
                    out var rigidFailure))
            {
                RejectApply(rigidFailure);
                return false;
            }
            fixedSpaceOffset.Configure(false, Vector3.zero, horizontalOnly);
        }
        else
        {
            fixedSpaceOffset.Configure(true, contentOffsetMeters, horizontalOnly);
            fixedSpaceOffset.ApplyCorrection();
        }
        isApplied = true;
        runtimeStatus = applyDetectedYawRotation
            ? $"APPLIED CONTENT {contentOffsetMeters:F3} YAW {previewYawDegrees:F2}deg"
            : $"APPLIED TRANSLATION {contentOffsetMeters:F3} | YAW DIAGNOSTIC ONLY";
        if (hideReferenceVisualsAfterApply) SetReferenceVisualsVisible(false);
        StopDetectionAfterApply();
        var residual = Vector3.Distance(
            appliedYawRotation * expectedReferenceWorldPosition + previewOffsetMeters,
            medianDetectedWorldPosition);
        Debug.Log(
            $"[AAG AprilTag Align] APPLY detected={medianDetectedWorldPosition:F4} "
            + $"reference={expectedReferenceWorldPosition:F4} tagOffset={previewOffsetMeters:F4} "
            + $"fineTuneWorld={contentFineTuneMeters:F4} "
            + $"alongWallBase={contentAlongWallBaselineMeters:F3}m "
            + $"alongWallAdjustment={contentAlongWallAdjustmentMeters:F3}m "
            + $"alongWallFinal={ContentAlongWallBackMeters:F3}m "
            + $"wallClearance={contentWallClearanceMeters:F3}m contentOffset={contentOffsetMeters:F4} "
            + $"residual={residual:F4}m jitter={previewJitterMeters:F4}m "
            + $"yawDiagnosticAvailable={hasYawDiagnostic} yawDiagnostic={previewYawDegrees:F3}deg "
            + $"yawJitter={previewYawJitterDegrees:F3}deg yawApplied={applyDetectedYawRotation} "
            + $"yawBranch={yawBranchStatus} "
            + $"horizontalOnly={horizontalOnly}",
            this);
        SetHudVisible(false);
        return true;
    }

    [ContextMenu("Reset Applied Alignment")]
    public void ResetAppliedAlignment()
    {
        fixedSpaceOffset?.ResetCorrection();
        AagMrukSpaceCorrection.Reset();
        isApplied = false;
        samples.Clear();
        hasStablePreview = false;
        previewYawRotation = Quaternion.identity;
        previewYawDegrees = 0f;
        previewYawJitterDegrees = 0f;
        hasYawDiagnostic = false;
        yawBranchStatus = "not_evaluated";
        runtimeStatus = "ALIGNMENT RESET";
        SetReferenceVisualsVisible(true);
        if (cameraAccess != null && !cameraAccess.enabled) cameraAccess.enabled = true;
        SetHudVisible(true);
        Debug.Log("[AAG AprilTag Align] Applied alignment reset.", this);
    }

    private void RejectApply(string reason)
    {
        runtimeStatus = $"APPLY BLOCKED: {reason}";
        Debug.LogWarning($"[AAG AprilTag Align] Apply blocked: {reason}", this);
    }

    private void CreateCameraAccess()
    {
        var cameraObject = new GameObject("AAG Alignment Left Passthrough Camera");
        cameraObject.transform.SetParent(transform, false);
        cameraAccess = cameraObject.AddComponent<PassthroughCameraAccess>();
        cameraAccess.enabled = false;
        cameraAccess.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        cameraAccess.RequestedResolution = new Vector2Int(1280, 960);
        cameraAccess.MaxFramerate = 30;
        cameraAccess.enabled = true;
    }

    private void EnsureDetector()
    {
        var resolution = cameraAccess.CurrentResolution;
        if (resolution.x <= 0 || resolution.y <= 0)
        {
            SetUnavailable("CAMERA OPEN; WAITING FOR RESOLUTION");
            return;
        }
        if (detector != null && resolution == detectorResolution) return;

        detector?.Dispose();
        detectorResolution = resolution;
        pixels = new Color32[resolution.x * resolution.y];
        verticalFovRadians = CalculateVerticalFov(cameraAccess.Intrinsics, resolution);
        detector = new TagDetector(resolution.x, resolution.y, Decimation);
        samples.Clear();
        Debug.Log(
            $"[AAG AprilTag Align] Camera ready: {resolution.x}x{resolution.y}, "
            + $"verticalFov={verticalFovRadians * Mathf.Rad2Deg:F2}deg, decimation={Decimation}",
            this);
    }

    private void ProcessLatestFrame()
    {
        try
        {
            var colors = cameraAccess.GetColors();
            var pixelCount = detectorResolution.x * detectorResolution.y;
            if (!colors.IsCreated || colors.Length < pixelCount)
            {
                SetUnavailable($"CAMERA PIXELS INVALID {colors.Length}/{pixelCount}");
                return;
            }

            var frameCameraPose = cameraAccess.GetCameraPose();
            if (!AagFiducialMarkerStore.IsFinite(frameCameraPose))
            {
                SetUnavailable("CAMERA WORLD POSE INVALID");
                return;
            }

            NativeArray<Color32>.Copy(colors, pixels, pixelCount);
            detector.ProcessImage(pixels, verticalFovRadians, tagSizeMeters);
            var detections = detector.DetectedTags.ToArray();
            lastSeenIds = detections.Length == 0
                ? "none"
                : string.Join(",", detections.Select(value => value.ID.ToString()));
            var found = false;
            TagPose tag = default;
            foreach (var candidate in detections)
            {
                if (candidate.ID != expectedTagId) continue;
                tag = candidate;
                found = true;
                break;
            }
            if (!found) return;

            var detectedWorld = frameCameraPose.position
                + frameCameraPose.rotation * tag.Position;
            var detectedWorldRotation = frameCameraPose.rotation * tag.Rotation;
            if (!IsFinite(detectedWorld) || !IsFinite(detectedWorldRotation)) return;

            lastCameraPosition = frameCameraPose.position;
            lastCameraLocalTagPosition = tag.Position;
            lastDetectionTime = Time.unscaledTime;
            samples.Add(new ObservationSample
            {
                Time = Time.unscaledTime,
                WorldPosition = detectedWorld,
                WorldRotation = detectedWorldRotation,
            });
        }
        catch (Exception exception)
        {
            SetUnavailable($"{exception.GetType().Name}: {exception.Message}");
            Debug.LogException(exception, this);
        }
    }

    private void UpdateExpectedReference()
    {
        if (room3TagReference == null)
        {
            SetUnavailable("ROOM3 TAG REFERENCE MISSING");
            return;
        }
        if (!room3TagReference.TryResolveExpectedWorldPose(out var pose, out var failure))
        {
            SetUnavailable(failure.ToUpperInvariant());
            return;
        }
        expectedReferenceWorldPosition = pose.position;
        if (!isApplied)
        {
            // The authored transform is stored in the exported Room3 coordinate
            // frame. At runtime MRUK can recreate that room in a different world
            // frame, so the visible reference must use the same reconstructed pose
            // that is used to calculate the correction.
            room3TagReference.transform.SetPositionAndRotation(
                pose.position,
                pose.rotation);
        }
    }

    private void EvaluatePreview()
    {
        hasStablePreview = false;
        if (room3TagReference == null
            || !room3TagReference.ReferencePlacementConfirmed
            || samples.Count < MinimumStableSamples
            || Time.unscaledTime - lastDetectionTime > 0.5f)
            return;
        if (!room3TagReference.TryResolveExpectedWorldPose(out var referencePose, out _)) return;

        medianDetectedWorldPosition = MedianPosition(samples.Select(value => value.WorldPosition));
        previewJitterMeters = samples.Max(value =>
            Vector3.Distance(value.WorldPosition, medianDetectedWorldPosition));
        if (previewJitterMeters > MaximumStableJitterMeters)
        {
            runtimeStatus = $"UNSTABLE TAG JITTER {previewJitterMeters:F3}m";
            return;
        }

        previewYawRotation = Quaternion.identity;
        previewYawDegrees = 0f;
        previewYawJitterDegrees = 0f;
        hasYawDiagnostic = false;
        yawBranchStatus = "not_evaluated";
        if (recordDetectedYaw || applyDetectedYawRotation)
        {
            hasYawDiagnostic = TryResolveStableYawCorrection(
                    referencePose.rotation,
                    samples.Select(value => value.WorldRotation),
                    out previewYawRotation,
                    out previewYawDegrees,
                    out previewYawJitterDegrees);
            if (!hasYawDiagnostic && applyDetectedYawRotation)
            {
                runtimeStatus = "TAG YAW UNAVAILABLE";
                return;
            }
            if (applyDetectedYawRotation
                && previewYawJitterDegrees > maximumStableYawJitterDegrees)
            {
                runtimeStatus = $"UNSTABLE TAG YAW {previewYawJitterDegrees:F2}deg";
                return;
            }
            if (applyDetectedYawRotation
                && ExperimentSpaceRuntime.UsesTagCorrectedReferenceSpace)
            {
                var trackedHead = ResolveTrackedHeadTransform();
                if (trackedHead == null)
                {
                    runtimeStatus = "TAG YAW BLOCKED: HEAD TRACKING UNAVAILABLE";
                    return;
                }
                if (!TryResolveBakedRoomValidatedYaw(
                        referencePose.position,
                        medianDetectedWorldPosition,
                        trackedHead.position,
                        room3TagReference.ExpectedRoomUuid,
                        previewYawDegrees,
                        horizontalOnly,
                        out previewYawRotation,
                        out previewYawDegrees,
                        out _,
                        out yawBranchStatus,
                        out var branchFailure))
                {
                    runtimeStatus = $"TAG YAW BLOCKED: {branchFailure}";
                    return;
                }
            }
            if (applyDetectedYawRotation
                && Mathf.Abs(previewYawDegrees) > MaximumAcceptedYawCorrectionDegrees)
            {
                runtimeStatus = $"REJECTED TAG YAW {previewYawDegrees:F2}deg";
                return;
            }
        }

        expectedReferenceWorldPosition = referencePose.position;
        previewOffsetMeters = medianDetectedWorldPosition
            - AppliedYawRotation * expectedReferenceWorldPosition;
        if (horizontalOnly) previewOffsetMeters.y = 0f;
        if (!IsFinite(previewOffsetMeters)
            || previewOffsetMeters.magnitude > MaximumAcceptedCorrectionMeters)
        {
            runtimeStatus = $"REJECTED OFFSET {previewOffsetMeters.magnitude:F2}m";
            return;
        }
        if (!horizontalOnly
            && Mathf.Abs(previewOffsetMeters.y) > MaximumAcceptedVerticalCorrectionMeters)
        {
            runtimeStatus = $"REJECTED VERTICAL OFFSET {previewOffsetMeters.y:F2}m";
            return;
        }

        hasStablePreview = true;
        runtimeStatus = isApplied
            ? $"APPLIED | OFFSET {previewOffsetMeters:F3}"
            : previewOnly
                ? $"PREVIEW ONLY | OFFSET {previewOffsetMeters:F3}"
                : $"ARMED | OFFSET {previewOffsetMeters:F3}";

        if (Time.unscaledTime - lastPreviewLogTime >= 1f
            && (lastPreviewLogTime < 0f
                || Vector3.Distance(lastLoggedPreview, previewOffsetMeters) >= 0.02f))
        {
            lastPreviewLogTime = Time.unscaledTime;
            lastLoggedPreview = previewOffsetMeters;
            Debug.Log(
                $"[AAG AprilTag Align] PREVIEW detected={medianDetectedWorldPosition:F4} "
                + $"reference={expectedReferenceWorldPosition:F4} offset={previewOffsetMeters:F4} "
                + $"magnitude={previewOffsetMeters.magnitude:F3}m jitter={previewJitterMeters:F4}m "
                + $"yawDiagnosticAvailable={hasYawDiagnostic} yawDiagnostic={previewYawDegrees:F3}deg "
                + $"yawJitter={previewYawJitterDegrees:F3}deg yawApplied={applyDetectedYawRotation} "
                + $"yawBranch={yawBranchStatus} "
                + $"samples={samples.Count} camera={lastCameraPosition:F4} tagCamera={lastCameraLocalTagPosition:F4}",
                this);
        }
    }

    private void HandleControllerApply()
    {
        if (isApplied || !allowQuestControllerApply || previewOnly || !hasStablePreview)
        {
            controllerHoldStartedAt = -1f;
            return;
        }

        var held = OVRInput.Get(OVRInput.RawButton.LThumbstick, OVRInput.Controller.LTouch)
            && OVRInput.Get(OVRInput.RawButton.RThumbstick, OVRInput.Controller.RTouch);
        if (!held)
        {
            controllerHoldStartedAt = -1f;
            return;
        }
        if (controllerHoldStartedAt < 0f)
        {
            controllerHoldStartedAt = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - controllerHoldStartedAt < ControllerApplyHoldSeconds) return;

        controllerHoldStartedAt = float.PositiveInfinity;
        ApplyPreview();
    }

    private void PruneSamples()
    {
        var cutoff = Time.unscaledTime - SampleWindowSeconds;
        samples.RemoveAll(value => value.Time < cutoff);
    }

    private void SetUnavailable(string status)
    {
        if (!isApplied) runtimeStatus = status;
        hasStablePreview = false;
    }

    private void CreateHud()
    {
        var anchor = GameObject.Find("CenterEyeAnchor")?.transform;
        if (anchor == null && Camera.main != null) anchor = Camera.main.transform;

        var canvasObject = new GameObject(
            "AAG AprilTag Alignment HUD",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        hudRoot = canvasObject;
        var rect = canvasObject.GetComponent<RectTransform>();
        if (anchor != null)
        {
            rect.SetParent(anchor, false);
            rect.localPosition = new Vector3(0.42f, -0.16f, 1.15f);
            rect.localRotation = Quaternion.identity;
        }
        rect.sizeDelta = new Vector2(720f, 420f);
        rect.localScale = Vector3.one * 0.00072f;

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = anchor != null ? anchor.GetComponent<Camera>() : Camera.main;
        canvas.sortingOrder = 110;

        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.SetParent(rect, false);
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

        var textObject = new GameObject("Readout", typeof(RectTransform), typeof(Text));
        var textRect = textObject.GetComponent<RectTransform>();
        textRect.SetParent(panelRect, false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(24f, 20f);
        textRect.offsetMax = new Vector2(-24f, -20f);
        hud = textObject.GetComponent<Text>();
        hud.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        hud.fontSize = 25;
        hud.alignment = TextAnchor.UpperLeft;
        hud.horizontalOverflow = HorizontalWrapMode.Wrap;
        hud.verticalOverflow = VerticalWrapMode.Overflow;
        hud.supportRichText = true;
        hud.color = Color.white;
    }

    private void UpdateHud()
    {
        if (isApplied)
        {
            SetHudVisible(false);
            return;
        }
        if (hud == null || hudRoot == null || !hudRoot.activeSelf) return;
        var color = isApplied ? "#55FF88" : hasStablePreview ? "#FFD966" : "#FF8888";
        hud.text =
            $"<b>{AlignmentLabel} APRILTAG {(applyDetectedYawRotation ? "RIGID" : "TRANSLATION")} ALIGNMENT</b>\n"
            + $"<color={color}><b>{runtimeStatus}</b></color>\n\n"
            + $"Detected median: {medianDetectedWorldPosition:F3}\n"
            + $"MRUK reference:  {expectedReferenceWorldPosition:F3}\n"
            + $"Tag offset:      {previewOffsetMeters:F3} ({previewOffsetMeters.magnitude:F2}m)\n"
            + $"Yaw diagnostic:  {(hasYawDiagnostic ? $"{previewYawDegrees:F2}deg" : "unavailable")} "
            + $"(jitter {previewYawJitterDegrees:F2}deg, "
            + $"{(applyDetectedYawRotation ? "USED FOR ALIGNMENT" : "DIAGNOSTIC ONLY")})\n"
            + $"Yaw branch:      {yawBranchStatus}\n"
            + $"Content fine:    {ContentFineTuneMeters:F3}\n"
            + $"Reference axes:  base {contentAlongWallBaselineMeters:F2}m + adjust "
            + $"{contentAlongWallAdjustmentMeters:F2}m = {ContentAlongWallBackMeters:F2}m | "
            + $"wall {contentWallClearanceMeters:F2}m\n"
            + $"Content offset:  {ContentOffsetMeters:F3}\n"
            + $"Jitter: {previewJitterMeters:F3}m | samples: {samples.Count} | seen: {lastSeenIds}\n\n"
            + (previewOnly
                ? "PREVIEW ONLY: confirm the scene reference before arming."
                : allowQuestControllerApply
                    ? "Hold BOTH thumbsticks 1.5s to apply."
                    : "Call ApplyPreview() from an approved operator control.");
    }

    private void SetHudVisible(bool visible)
    {
        if (hudRoot != null) hudRoot.SetActive(visible);
    }

    private void SetReferenceVisualsVisible(bool visible)
    {
        if (room3TagReference == null) return;
        foreach (var renderer in room3TagReference.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = visible;
    }

    private void StopDetectionAfterApply()
    {
        detector?.Dispose();
        detector = null;
        pixels = null;
        if (cameraAccess != null) cameraAccess.enabled = false;
    }

    private Vector3 ResolveContentFineTuneMeters(Quaternion yawCorrection)
    {
        if (room3TagReference == null) return Vector3.zero;
        var uncorrectedReferenceRotation = room3TagReference.TryResolveExpectedWorldPose(
            out var referencePose,
            out _)
            ? referencePose.rotation
            : room3TagReference.transform.rotation;
        return ResolveHorizontalReferenceFineTune(
            yawCorrection * uncorrectedReferenceRotation,
            ContentAlongWallBackMeters,
            contentWallClearanceMeters);
    }

    public static bool TryResolveStableYawCorrection(
        Quaternion expectedReferenceRotation,
        IEnumerable<Quaternion> observedWorldRotations,
        out Quaternion yawCorrection,
        out float yawDegrees,
        out float jitterDegrees)
    {
        yawCorrection = Quaternion.identity;
        yawDegrees = 0f;
        jitterDegrees = 0f;
        if (!TryGetYawDegrees(expectedReferenceRotation, out var expectedYaw)) return false;

        // AprilTag's planar pose has a 180-degree branch ambiguity. Treat the
        // measurements as axial angles while establishing stability, then let
        // the baked-room validation below choose the physical yaw branch.
        // Folding each sample independently by smallest absolute yaw makes
        // measurements around 90 degrees alternate between +89 and -89 and
        // falsely reports almost 180 degrees of jitter.
        var corrections = new List<float>();
        foreach (var observed in observedWorldRotations ?? Array.Empty<Quaternion>())
        {
            if (!TryGetYawDegrees(observed, out var observedYaw)) continue;
            corrections.Add(Mathf.DeltaAngle(expectedYaw, observedYaw));
        }
        if (corrections.Count == 0) return false;

        var doubledSin = corrections.Sum(value =>
            Mathf.Sin(2f * value * Mathf.Deg2Rad));
        var doubledCos = corrections.Sum(value =>
            Mathf.Cos(2f * value * Mathf.Deg2Rad));
        if (Mathf.Abs(doubledSin) < 0.000001f
            && Mathf.Abs(doubledCos) < 0.000001f)
            return false;

        var axialSeed = 0.5f
            * Mathf.Atan2(doubledSin, doubledCos)
            * Mathf.Rad2Deg;
        var medianOffset = Median(corrections.Select(value =>
            0.5f * Mathf.DeltaAngle(2f * axialSeed, 2f * value)));
        var medianYaw = NormalizeAxialYaw(axialSeed + medianOffset);
        var maximumJitter = corrections.Max(value =>
            0.5f * Mathf.Abs(Mathf.DeltaAngle(2f * medianYaw, 2f * value)));
        yawDegrees = medianYaw;
        jitterDegrees = maximumJitter;
        yawCorrection = Quaternion.Euler(0f, medianYaw, 0f);
        return IsFinite(yawCorrection);
    }

    private static float NormalizeAxialYaw(float yawDegrees) =>
        Mathf.Repeat(yawDegrees + 90f, 180f) - 90f;

    public static bool TryResolveBakedRoomValidatedYaw(
        Vector3 expectedReferencePosition,
        Vector3 detectedReferencePosition,
        Vector3 observedHeadPosition,
        string expectedRoomUuidText,
        float foldedYawDegrees,
        bool horizontalOnly,
        out Quaternion yawCorrection,
        out float yawDegrees,
        out Vector3 translation,
        out string branchStatus,
        out string failure)
    {
        yawCorrection = Quaternion.identity;
        yawDegrees = 0f;
        translation = Vector3.zero;
        branchStatus = "not_resolved";
        failure = string.Empty;
        if (!Guid.TryParse(expectedRoomUuidText, out var expectedRoomUuid))
        {
            failure = "EXPECTED START ROOM UUID INVALID";
            return false;
        }
        if (!IsFinite(expectedReferencePosition)
            || !IsFinite(detectedReferencePosition)
            || !IsFinite(observedHeadPosition)
            || !IsFinite(foldedYawDegrees))
        {
            failure = "NON-FINITE YAW BRANCH INPUT";
            return false;
        }

        var folded = Mathf.DeltaAngle(0f, foldedYawDegrees);
        var opposite = Mathf.DeltaAngle(0f, folded + 180f);
        var foldedMatches = EvaluateBakedRoomYawCandidate(
            expectedReferencePosition,
            detectedReferencePosition,
            observedHeadPosition,
            expectedRoomUuid,
            folded,
            horizontalOnly,
            out var foldedRotation,
            out var foldedTranslation,
            out var foldedRoom);
        var oppositeMatches = EvaluateBakedRoomYawCandidate(
            expectedReferencePosition,
            detectedReferencePosition,
            observedHeadPosition,
            expectedRoomUuid,
            opposite,
            horizontalOnly,
            out var oppositeRotation,
            out var oppositeTranslation,
            out var oppositeRoom);

        if (foldedMatches == oppositeMatches)
        {
            var reason = foldedMatches ? "AMBIGUOUS BOTH MATCH" : "NO EXPECTED ROOM MATCH";
            failure = $"{reason}; stand inside the tag reference room "
                + $"(folded={FormatResolvedRoom(foldedRoom)}, opposite={FormatResolvedRoom(oppositeRoom)})";
            return false;
        }

        if (foldedMatches)
        {
            yawCorrection = foldedRotation;
            yawDegrees = folded;
            translation = foldedTranslation;
            branchStatus = "folded_expected_room";
        }
        else
        {
            yawCorrection = oppositeRotation;
            yawDegrees = opposite;
            translation = oppositeTranslation;
            branchStatus = "opposite_expected_room";
        }
        return true;
    }

    private static bool EvaluateBakedRoomYawCandidate(
        Vector3 expectedReferencePosition,
        Vector3 detectedReferencePosition,
        Vector3 observedHeadPosition,
        Guid expectedRoomUuid,
        float candidateYawDegrees,
        bool horizontalOnly,
        out Quaternion candidateRotation,
        out Vector3 candidateTranslation,
        out Guid resolvedRoomUuid)
    {
        candidateRotation = Quaternion.Euler(0f, candidateYawDegrees, 0f);
        candidateTranslation = detectedReferencePosition
            - candidateRotation * expectedReferencePosition;
        if (horizontalOnly) candidateTranslation.y = 0f;
        var canonicalHeadPosition = Quaternion.Inverse(candidateRotation)
            * (observedHeadPosition - candidateTranslation);
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace)
            return AagFp2BakedSpace.TryResolveRoom(
                    canonicalHeadPosition, out resolvedRoomUuid)
                && resolvedRoomUuid == expectedRoomUuid;

        resolvedRoomUuid = Guid.Empty;
        var rooms = MRUK.Instance?.Rooms;
        if (rooms == null) return false;
        foreach (var room in rooms)
        {
            if (room == null || room.Anchor == null) continue;
            foreach (var floor in room.FloorAnchors)
            {
                if (floor == null || floor.PlaneBoundary2D == null
                    || floor.PlaneBoundary2D.Count < 3) continue;
                var local = floor.transform.InverseTransformPoint(canonicalHeadPosition);
                if (!floor.IsPositionInBoundary(new Vector2(local.x, local.y))) continue;
                if (resolvedRoomUuid != Guid.Empty) return false;
                resolvedRoomUuid = room.Anchor.Uuid;
            }
        }
        return resolvedRoomUuid == expectedRoomUuid;
    }

    private static string FormatResolvedRoom(Guid roomUuid) =>
        roomUuid == Guid.Empty ? "outside" : roomUuid.ToString();

    private static Transform ResolveTrackedHeadTransform()
    {
        var trackedHead = GameObject.Find("CenterEyeAnchor")?.transform;
        return trackedHead != null || Camera.main == null
            ? trackedHead
            : Camera.main.transform;
    }

    private static bool TryGetYawDegrees(Quaternion rotation, out float yawDegrees)
    {
        yawDegrees = 0f;
        if (!IsFinite(rotation)) return false;
        var forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.000001f) return false;
        forward.Normalize();
        yawDegrees = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        return IsFinite(yawDegrees);
    }

    public static Vector3 ResolveHorizontalReferenceFineTune(
        Quaternion referenceRotation,
        float alongWallBackMeters,
        float wallClearanceMeters)
    {
        // The board's local +Z points away from its wall. Its local -X is the
        // authored wall "back" direction. Rebuild a level basis so small MRUK
        // floor pitch/roll never changes experiment content height.
        var awayFromWall = Vector3.ProjectOnPlane(
            referenceRotation * Vector3.forward,
            Vector3.up);
        if (awayFromWall.sqrMagnitude < 0.000001f) awayFromWall = Vector3.forward;
        awayFromWall.Normalize();
        var alongWallBack = -Vector3.Cross(Vector3.up, awayFromWall).normalized;
        return alongWallBack * alongWallBackMeters
            + awayFromWall * wallClearanceMeters;
    }

    private static float CalculateVerticalFov(
        PassthroughCameraAccess.CameraIntrinsics intrinsics,
        Vector2Int outputResolution)
    {
        var sensor = (Vector2)intrinsics.SensorResolution;
        var output = (Vector2)outputResolution;
        if (sensor.x <= 0f || sensor.y <= 0f || intrinsics.FocalLength.y <= 0f)
            return 60f * Mathf.Deg2Rad;
        var scale = new Vector2(output.x / sensor.x, output.y / sensor.y);
        scale /= Mathf.Max(scale.x, scale.y);
        var croppedSensorHeight = sensor.y * scale.y;
        return 2f * Mathf.Atan(croppedSensorHeight / (2f * intrinsics.FocalLength.y));
    }

    private static Vector3 MedianPosition(IEnumerable<Vector3> source)
    {
        var values = source.ToArray();
        return values.Length == 0
            ? Vector3.zero
            : new Vector3(
                Median(values.Select(value => value.x)),
                Median(values.Select(value => value.y)),
                Median(values.Select(value => value.z)));
    }

    private static float Median(IEnumerable<float> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0) return 0f;
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) * 0.5f
            : values[middle];
    }

    private static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}
