using System;
using System.Collections.Generic;
using System.Linq;
using AprilTag;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Quest passthrough-camera AprilTag range check for the AAG Room3 reference board.
/// The expected tag is tagStandard41h12 ID 0 with a 95 mm detection-border size.
/// </summary>
public sealed class AagAprilTagRangeValidator : MonoBehaviour
{
    private const int ExpectedTagId = 0;
    private const float TagSizeMeters = 0.095f;
    private const int Decimation = 2;
    private const float ProcessingIntervalSeconds = 0.10f;
    private const float SampleWindowSeconds = 2.0f;
    private const int MinimumStableSamples = 8;

    private readonly List<RangeSample> _samples = new List<RangeSample>(32);

    private PassthroughCameraAccess _cameraAccess;
    private TagDetector _detector;
    private Color32[] _pixels;
    private Text _hud;
    private Vector2Int _detectorResolution;
    private float _verticalFovRadians;
    private float _nextProcessTime;
    private float _lastDetectionTime = float.NegativeInfinity;
    private Vector3 _lastPosition;
    private string _lastSeenIds = "none";
    private string _runtimeMessage = "Waiting for headset camera permission...";

    private struct RangeSample
    {
        public float Time;
        public Vector3 Position;
        public float Distance;
    }

    private void Awake()
    {
        Application.runInBackground = true;
        CreateHud();
        CreateCameraAccess();
        Debug.Log($"[AAG AprilTag Range] Started. family=tagStandard41h12 id={ExpectedTagId} tagSize={TagSizeMeters:F3}m");
    }

    private void Update()
    {
        PruneSamples();

        if (_cameraAccess == null)
        {
            _runtimeMessage = "ERROR: PassthroughCameraAccess is missing.";
            UpdateHud();
            return;
        }

        if (!_cameraAccess.IsPlaying)
        {
            _runtimeMessage = PassthroughCameraAccess.IsSupported
                ? "Waiting for camera... Allow HEADSET CAMERA permission in Quest."
                : "ERROR: Passthrough Camera Access is not supported on this headset/OS.";
            UpdateHud();
            return;
        }

        EnsureDetector();

        if (_detector != null && _cameraAccess.IsUpdatedThisFrame && Time.unscaledTime >= _nextProcessTime)
        {
            _nextProcessTime = Time.unscaledTime + ProcessingIntervalSeconds;
            ProcessLatestFrame();
        }

        UpdateHud();
    }

    private void OnDestroy()
    {
        _detector?.Dispose();
        _detector = null;
    }

    private void CreateCameraAccess()
    {
        var cameraObject = new GameObject("AAG Left Passthrough Camera");
        cameraObject.transform.SetParent(transform, false);
        _cameraAccess = cameraObject.AddComponent<PassthroughCameraAccess>();
        _cameraAccess.enabled = false;
        _cameraAccess.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        _cameraAccess.RequestedResolution = new Vector2Int(1280, 960);
        _cameraAccess.MaxFramerate = 30;
        _cameraAccess.enabled = true;
    }

    private void EnsureDetector()
    {
        var resolution = _cameraAccess.CurrentResolution;
        if (resolution.x <= 0 || resolution.y <= 0)
        {
            _runtimeMessage = "Camera opened, waiting for a valid resolution...";
            return;
        }

        if (_detector != null && resolution == _detectorResolution)
        {
            return;
        }

        _detector?.Dispose();
        _detectorResolution = resolution;
        _pixels = new Color32[resolution.x * resolution.y];
        _verticalFovRadians = CalculateVerticalFov(_cameraAccess.Intrinsics, resolution);
        _detector = new TagDetector(resolution.x, resolution.y, Decimation);
        _samples.Clear();

        Debug.Log(
            $"[AAG AprilTag Range] Camera ready: {resolution.x}x{resolution.y}, " +
            $"verticalFov={_verticalFovRadians * Mathf.Rad2Deg:F2}deg, decimation={Decimation}");
    }

    private static float CalculateVerticalFov(
        PassthroughCameraAccess.CameraIntrinsics intrinsics,
        Vector2Int outputResolution)
    {
        var sensor = (Vector2)intrinsics.SensorResolution;
        var output = (Vector2)outputResolution;

        if (sensor.x <= 0f || sensor.y <= 0f || intrinsics.FocalLength.y <= 0f)
        {
            return 60f * Mathf.Deg2Rad;
        }

        var scale = new Vector2(output.x / sensor.x, output.y / sensor.y);
        scale /= Mathf.Max(scale.x, scale.y);
        var croppedSensorHeight = sensor.y * scale.y;
        return 2f * Mathf.Atan(croppedSensorHeight / (2f * intrinsics.FocalLength.y));
    }

    private void ProcessLatestFrame()
    {
        try
        {
            var colors = _cameraAccess.GetColors();
            var pixelCount = _detectorResolution.x * _detectorResolution.y;
            if (!colors.IsCreated || colors.Length < pixelCount)
            {
                _runtimeMessage = $"ERROR: Camera pixel buffer is invalid ({colors.Length}/{pixelCount}).";
                return;
            }

            NativeArray<Color32>.Copy(colors, _pixels, pixelCount);
            _detector.ProcessImage(_pixels, _verticalFovRadians, TagSizeMeters);

            var detections = _detector.DetectedTags.ToArray();
            _lastSeenIds = detections.Length == 0
                ? "none"
                : string.Join(",", detections.Select(tag => tag.ID.ToString()));

            var found = false;
            foreach (var tag in detections)
            {
                if (tag.ID != ExpectedTagId)
                {
                    continue;
                }

                found = true;
                _lastPosition = tag.Position;
                _lastDetectionTime = Time.unscaledTime;
                _samples.Add(new RangeSample
                {
                    Time = Time.unscaledTime,
                    Position = tag.Position,
                    Distance = tag.Position.magnitude
                });

                Debug.Log(
                    $"[AAG AprilTag Range] id={tag.ID} " +
                    $"x={tag.Position.x:F4} y={tag.Position.y:F4} z={tag.Position.z:F4} " +
                    $"distance={tag.Position.magnitude:F4}m samples={_samples.Count}");
                break;
            }

            _runtimeMessage = found
                ? "Tag ID 0 detected. Hold still and face the tag squarely."
                : $"Find tag ID 0. Seen IDs: {_lastSeenIds}";
        }
        catch (Exception exception)
        {
            _runtimeMessage = $"ERROR: {exception.GetType().Name}: {exception.Message}";
            Debug.LogException(exception, this);
        }
    }

    private void PruneSamples()
    {
        var cutoff = Time.unscaledTime - SampleWindowSeconds;
        _samples.RemoveAll(sample => sample.Time < cutoff);
    }

    private void CreateHud()
    {
        var anchor = GameObject.Find("CenterEyeAnchor")?.transform;
        if (anchor == null && Camera.main != null)
        {
            anchor = Camera.main.transform;
        }

        var canvasObject = new GameObject(
            "AAG AprilTag Range HUD",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));

        var canvasTransform = canvasObject.GetComponent<RectTransform>();
        if (anchor != null)
        {
            canvasTransform.SetParent(anchor, false);
            canvasTransform.localPosition = new Vector3(0f, 0f, 1.25f);
            canvasTransform.localRotation = Quaternion.identity;
        }

        canvasTransform.sizeDelta = new Vector2(920f, 600f);
        canvasTransform.localScale = Vector3.one * 0.001f;

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = anchor != null ? anchor.GetComponent<Camera>() : Camera.main;
        canvas.sortingOrder = 100;

        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;
        scaler.referencePixelsPerUnit = 100f;

        var panelObject = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        var panelTransform = panelObject.GetComponent<RectTransform>();
        panelTransform.SetParent(canvasTransform, false);
        panelTransform.anchorMin = Vector2.zero;
        panelTransform.anchorMax = Vector2.one;
        panelTransform.offsetMin = Vector2.zero;
        panelTransform.offsetMax = Vector2.zero;
        panelObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.82f);

        var textObject = new GameObject("Readout", typeof(RectTransform), typeof(Text));
        var textTransform = textObject.GetComponent<RectTransform>();
        textTransform.SetParent(panelTransform, false);
        textTransform.anchorMin = Vector2.zero;
        textTransform.anchorMax = Vector2.one;
        textTransform.offsetMin = new Vector2(34f, 28f);
        textTransform.offsetMax = new Vector2(-34f, -28f);

        _hud = textObject.GetComponent<Text>();
        _hud.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _hud.fontSize = 32;
        _hud.alignment = TextAnchor.UpperLeft;
        _hud.horizontalOverflow = HorizontalWrapMode.Wrap;
        _hud.verticalOverflow = VerticalWrapMode.Overflow;
        _hud.supportRichText = true;
        _hud.color = Color.white;
        _hud.text = "AAG APRILTAG 1m CHECK\nStarting...";
    }

    private void UpdateHud()
    {
        if (_hud == null)
        {
            return;
        }

        var recentDetection = Time.unscaledTime - _lastDetectionTime < 0.5f;
        var medianPosition = MedianPosition();
        var medianDistance = Median(_samples.Select(sample => sample.Distance));
        var status = EvaluateStatus(medianDistance, _samples.Count, recentDetection);
        var color = status.StartsWith("PASS") ? "#55FF88" :
            status.StartsWith("FAIL") ? "#FF6666" : "#FFD966";

        _hud.text =
            "<b>AAG APRILTAG 1m CHECK</b>\n" +
            $"<color={color}><b>{status}</b></color>\n\n" +
            $"Tag: standard41h12 ID {ExpectedTagId} | size {TagSizeMeters:F3} m\n" +
            $"Camera: LEFT | {_detectorResolution.x}x{_detectorResolution.y} | vFOV {_verticalFovRadians * Mathf.Rad2Deg:F1} deg\n" +
            $"Current X/Y/Z: {_lastPosition.x,7:F3}  {_lastPosition.y,7:F3}  {_lastPosition.z,7:F3} m\n" +
            $"Median  X/Y/Z: {medianPosition.x,7:F3}  {medianPosition.y,7:F3}  {medianPosition.z,7:F3} m\n" +
            $"Median distance: <b>{medianDistance:F3} m</b> | samples: {_samples.Count}\n\n" +
            "Target: 1.000 m from tag plane to LEFT camera\n" +
            "Excellent 0.970-1.030 | usable 0.940-1.060\n" +
            _runtimeMessage;
    }

    private static string EvaluateStatus(float medianDistance, int sampleCount, bool recentDetection)
    {
        if (!recentDetection)
        {
            return "WAIT - AIM AT TAG ID 0";
        }

        if (sampleCount < MinimumStableSamples)
        {
            return $"COLLECTING 2s WINDOW ({sampleCount}/{MinimumStableSamples})";
        }

        if (medianDistance >= 0.97f && medianDistance <= 1.03f)
        {
            return "PASS - EXCELLENT";
        }

        if (medianDistance >= 0.94f && medianDistance <= 1.06f)
        {
            return "PASS - PROTOTYPE USABLE";
        }

        return "FAIL - CHECK SIZE / INTRINSICS / MEASUREMENT";
    }

    private Vector3 MedianPosition()
    {
        if (_samples.Count == 0)
        {
            return Vector3.zero;
        }

        return new Vector3(
            Median(_samples.Select(sample => sample.Position.x)),
            Median(_samples.Select(sample => sample.Position.y)),
            Median(_samples.Select(sample => sample.Position.z)));
    }

    private static float Median(IEnumerable<float> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0)
        {
            return 0f;
        }

        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) * 0.5f
            : values[middle];
    }
}
