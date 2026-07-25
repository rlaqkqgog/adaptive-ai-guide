using System;
using System.Collections.Generic;
using System.Linq;
using AprilTag;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Detects the Room3 AprilTag in the left passthrough camera, converts its pose
/// to Unity world space using the image-timestamp camera pose, and previews a
/// translation from the authored Room3 reference to the observed physical tag.
/// Application is explicit and delegates to AagFixedSpaceOffset, which refuses
/// to move MRUK, TrackingSpace, or the camera rig.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(8400)]
public sealed class AagAprilTagTranslationAligner : MonoBehaviour
{
    public const int ExpectedTagId = 0;
    public const float TagSizeMeters = 0.095f;
    public const float SampleWindowSeconds = 2f;
    public const float MarkerFreshnessSeconds = 12f;
    public const float MaximumAcceptedCorrectionMeters = 40f;
    public const float MaximumAcceptedVerticalCorrectionMeters = 0.5f;

    private const int Decimation = 2;
    private const float ProcessingIntervalSeconds = 0.10f;
    private const int MinimumStableSamples = 12;
    private const float MaximumStableJitterMeters = 0.045f;
    private const float ControllerApplyHoldSeconds = 1.5f;

    [Header("Scene references")]
    [SerializeField] private AagRoom3TagReference room3TagReference;
    [SerializeField] private AagFixedSpaceOffset fixedSpaceOffset;

    [Header("Safety")]
    [Tooltip("Keep enabled while positioning the reference. ApplyPreview is rejected in this mode.")]
    [SerializeField] private bool previewOnly = true;
    [Tooltip("When enabled, holding both thumbsticks applies a valid preview. Keep disabled until on-site review.")]
    [SerializeField] private bool allowQuestControllerApply;
    [SerializeField] private bool horizontalOnly = true;
    [SerializeField] private bool requireAppliedAlignmentBeforeSession = true;

    [Header("Content fine tuning")]
    [Tooltip("Fixed on-site baseline along the Room3 wall. Keep this at the confirmed -0.65 m reference.")]
    [SerializeField] private float contentAlongWallBaselineMeters = -0.65f;
    [Tooltip("Adjustment measured from the fixed -0.65 m baseline. The current +0.15 m produces -0.50 m.")]
    [SerializeField] private float contentAlongWallAdjustmentMeters = 0.15f;
    [Tooltip("Moves experiment content horizontally away from the tagged Room3 wall and into the room.")]
    [SerializeField] private float contentWallClearanceMeters = 0.25f;

    [Header("Runtime display")]
    [SerializeField] private bool showRuntimeHud = true;

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
    }

    public bool PreviewOnly => previewOnly;
    public bool AllowQuestControllerApply => allowQuestControllerApply;
    public bool HorizontalOnly => horizontalOnly;
    public AagRoom3TagReference Room3TagReference => room3TagReference;
    public AagFixedSpaceOffset FixedSpaceOffset => fixedSpaceOffset;
    public bool RequireAppliedAlignmentBeforeSession => requireAppliedAlignmentBeforeSession;
    public bool HasStablePreview => hasStablePreview;
    public bool IsApplied => isApplied;
    public Vector3 PreviewOffsetMeters => previewOffsetMeters;
    public float ContentAlongWallBaselineMeters => contentAlongWallBaselineMeters;
    public float ContentAlongWallAdjustmentMeters => contentAlongWallAdjustmentMeters;
    public float ContentAlongWallBackMeters =>
        contentAlongWallBaselineMeters + contentAlongWallAdjustmentMeters;
    public float ContentWallClearanceMeters => contentWallClearanceMeters;
    public Vector3 ContentFineTuneMeters => ResolveContentFineTuneMeters();
    public Vector3 ContentOffsetMeters => previewOffsetMeters + ContentFineTuneMeters;
    public Vector3 MedianDetectedWorldPosition => medianDetectedWorldPosition;
    public Vector3 ExpectedReferenceWorldPosition => expectedReferenceWorldPosition;
    public float PreviewJitterMeters => previewJitterMeters;
    public string StatusLine => runtimeStatus;

    public void HideRuntimeHud()
    {
        SetHudVisible(false);
    }

    private void Awake()
    {
        Application.runInBackground = true;
        if (room3TagReference == null)
            room3TagReference = FindFirstObjectByType<AagRoom3TagReference>();
        if (fixedSpaceOffset == null)
            fixedSpaceOffset = GetComponent<AagFixedSpaceOffset>()
                ?? FindFirstObjectByType<AagFixedSpaceOffset>();
        if (showRuntimeHud) CreateHud();
        CreateCameraAccess();
        Debug.Log(
            $"[AAG AprilTag Align] Started. family=tagStandard41h12 id={ExpectedTagId} "
            + $"tagSize={TagSizeMeters:F3}m previewOnly={previewOnly} horizontalOnly={horizontalOnly}",
            this);
    }

    private void Update()
    {
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
        if (Time.unscaledTime - lastDetectionTime > MarkerFreshnessSeconds)
        {
            failure = "apriltag_translation_marker_stale";
            return false;
        }
        if (!hasStablePreview)
        {
            failure = "apriltag_translation_preview_unstable";
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

        var contentFineTuneMeters = ContentFineTuneMeters;
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
            room3TagReference.transform.position = medianDetectedWorldPosition;

        fixedSpaceOffset.Configure(true, contentOffsetMeters, horizontalOnly);
        fixedSpaceOffset.ApplyCorrection();
        isApplied = true;
        runtimeStatus = $"APPLIED CONTENT {contentOffsetMeters:F3}";
        var residual = Vector3.Distance(
            expectedReferenceWorldPosition + previewOffsetMeters,
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
            + $"horizontalOnly={horizontalOnly}",
            this);
        SetHudVisible(false);
        return true;
    }

    [ContextMenu("Reset Applied Alignment")]
    public void ResetAppliedAlignment()
    {
        fixedSpaceOffset?.ResetCorrection();
        isApplied = false;
        runtimeStatus = hasStablePreview ? "STABLE PREVIEW - NOT APPLIED" : "ALIGNMENT RESET";
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
            detector.ProcessImage(pixels, verticalFovRadians, TagSizeMeters);
            var detections = detector.DetectedTags.ToArray();
            lastSeenIds = detections.Length == 0
                ? "none"
                : string.Join(",", detections.Select(value => value.ID.ToString()));
            var found = false;
            TagPose tag = default;
            foreach (var candidate in detections)
            {
                if (candidate.ID != ExpectedTagId) continue;
                tag = candidate;
                found = true;
                break;
            }
            if (!found) return;

            var detectedWorld = frameCameraPose.position
                + frameCameraPose.rotation * tag.Position;
            if (!IsFinite(detectedWorld)) return;

            lastCameraPosition = frameCameraPose.position;
            lastCameraLocalTagPosition = tag.Position;
            lastDetectionTime = Time.unscaledTime;
            samples.Add(new ObservationSample
            {
                Time = Time.unscaledTime,
                WorldPosition = detectedWorld,
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

        expectedReferenceWorldPosition = referencePose.position;
        previewOffsetMeters = medianDetectedWorldPosition - expectedReferenceWorldPosition;
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
            "<b>ROOM3 APRILTAG TRANSLATION</b>\n"
            + $"<color={color}><b>{runtimeStatus}</b></color>\n\n"
            + $"Detected median: {medianDetectedWorldPosition:F3}\n"
            + $"MRUK reference:  {expectedReferenceWorldPosition:F3}\n"
            + $"Tag offset:      {previewOffsetMeters:F3} ({previewOffsetMeters.magnitude:F2}m)\n"
            + $"Content fine:    {ContentFineTuneMeters:F3}\n"
            + $"Room3 axes:      base {contentAlongWallBaselineMeters:F2}m + adjust "
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

    private Vector3 ResolveContentFineTuneMeters()
    {
        if (room3TagReference == null) return Vector3.zero;
        return ResolveHorizontalReferenceFineTune(
            room3TagReference.transform.rotation,
            ContentAlongWallBackMeters,
            contentWallClearanceMeters);
    }

    public static Vector3 ResolveHorizontalReferenceFineTune(
        Quaternion referenceRotation,
        float alongWallBackMeters,
        float wallClearanceMeters)
    {
        // The board's local +Z points away from its wall. Its local -X is the
        // authored Room3 "back" direction. Rebuild a level basis so small MRUK
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

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}
