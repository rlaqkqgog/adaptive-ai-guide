using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class AagSpaceOffsetReferencePoint
{
    [Tooltip("Unique human-readable ID, for example room1_door_left.")]
    public string referenceId = string.Empty;
    [Tooltip("Virtual point that should coincide with the touched physical landmark.")]
    public Transform expectedPoint;
    [Tooltip("Used only when Expected Point is not assigned.")]
    public Vector3 fallbackExpectedWorldPosition;

    public Vector3 ExpectedWorldPosition =>
        expectedPoint != null ? expectedPoint.position : fallbackExpectedWorldPosition;
}

/// <summary>
/// Captures controller/probe positions at known physical landmarks and exports
/// evidence showing whether one translation explains all rooms.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(8400)]
public sealed class AagSpaceOffsetDiagnostic : MonoBehaviour
{
    private const string ExportFolderName = "AagSpaceDiagnostics";
    private const string SchemaVersion = "aag-space-offset-diagnostic/v1";

    [Serializable]
    private sealed class CapturedPoint
    {
        public string referenceId = string.Empty;
        public Vector3 expected;
        public Vector3 observed;
        public string capturedAtUtc = string.Empty;
    }

    [Serializable]
    private sealed class VectorRecord
    {
        public float x;
        public float y;
        public float z;

        public VectorRecord(Vector3 value)
        {
            x = value.x;
            y = value.y;
            z = value.z;
        }
    }

    [Serializable]
    private sealed class ExportPoint
    {
        public string referenceId = string.Empty;
        public VectorRecord expected;
        public VectorRecord observed;
        public VectorRecord individualOffset;
        public VectorRecord translationResidual;
        public float translationResidualMeters;
        public string capturedAtUtc = string.Empty;
    }

    [Serializable]
    private sealed class ExportResult
    {
        public string diagnosis = string.Empty;
        public string detail = string.Empty;
        public int sampleCount;
        public float baselineMeters;
        public VectorRecord recommendedTranslation;
        public float translationRmsMeters;
        public float translationMaxResidualMeters;
        public float fittedYawDegrees;
        public VectorRecord fittedRigidTranslation;
        public float rigidRmsMeters;
        public float rigidMaxResidualMeters;
    }

    [Serializable]
    private sealed class ExportDocument
    {
        public string schemaVersion = SchemaVersion;
        public string exportedAtUtc = string.Empty;
        public string coordinateConvention =
            "correction = observed physical probe world position - expected virtual world position";
        public bool horizontalOnly;
        public ExportResult result;
        public List<ExportPoint> points = new List<ExportPoint>();
    }

    [Header("Measurement mode")]
    [SerializeField] private bool measurementModeEnabled;
    [SerializeField] private Transform probeTransform;
    [Tooltip("Offset from the assigned controller transform to the physical tip used to touch landmarks.")]
    [SerializeField] private Vector3 probeLocalOffset;
    [Min(0.05f)] [SerializeField] private float captureAveragingSeconds = 0.35f;

    [Header("Expected reference points")]
    [Tooltip("Use landmarks spread across at least three rooms and at least two metres apart.")]
    [SerializeField] private List<AagSpaceOffsetReferencePoint> referencePoints =
        new List<AagSpaceOffsetReferencePoint>();
    [Tooltip("If the list above is empty, each direct child becomes a reference using its name and world position.")]
    [SerializeField] private Transform referencePointsRoot;

    [Header("Optional Quest controls while measurement mode is enabled")]
    [SerializeField] private bool questControllerInputEnabled = true;
    [SerializeField] private OVRInput.RawButton captureNextButton = OVRInput.RawButton.RIndexTrigger;
    [SerializeField] private OVRInput.RawButton undoButton = OVRInput.RawButton.B;
    [SerializeField] private OVRInput.RawButton exportButton = OVRInput.RawButton.A;
    [SerializeField] private OVRInput.Controller inputController = OVRInput.Controller.RTouch;

    [Header("Diagnosis thresholds")]
    [SerializeField] private bool horizontalOnly = true;
    [Min(2)] [SerializeField] private int minimumSamples = 3;
    [Min(0.1f)] [SerializeField] private float minimumBaselineMeters = 2f;
    [Min(0.001f)] [SerializeField] private float residualToleranceMeters = 0.10f;
    [Min(0.1f)] [SerializeField] private float rotationEvidenceDegrees = 2f;
    [Range(0.05f, 0.95f)] [SerializeField] private float requiredRigidImprovementRatio = 0.65f;

    [Header("Output and optional hand-off")]
    [SerializeField] private bool autoExportAfterCapture = true;
    [SerializeField] private bool showRuntimePanel = true;
    [SerializeField] private AagFixedSpaceOffset fixedSpaceOffset;

    private readonly List<CapturedPoint> captures = new List<CapturedPoint>();
    private Coroutine captureRoutine;
    private AagSpaceOffsetSolver.Result lastResult;
    private string status = "MEASUREMENT MODE OFF";
    private string lastExportPath = string.Empty;

    public bool MeasurementModeEnabled => measurementModeEnabled;
    public int CapturedCount => captures.Count;
    public AagSpaceOffsetSolver.Result LastResult => lastResult;
    public string StatusLine => status;

    private void Awake()
    {
        if (probeTransform == null)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) probeTransform = rig.rightControllerAnchor;
        }
        if (fixedSpaceOffset == null) fixedSpaceOffset = GetComponent<AagFixedSpaceOffset>();
        RefreshAnalysis();
    }

    private void Update()
    {
        if (!measurementModeEnabled || captureRoutine != null) return;

        if (questControllerInputEnabled)
        {
            if (OVRInput.GetDown(captureNextButton, inputController)) CaptureNextReference();
            else if (OVRInput.GetDown(undoButton, inputController)) UndoLastCapture();
            else if (OVRInput.GetDown(exportButton, inputController)) ExportNow();
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.C)) CaptureNextReference();
        if (Input.GetKeyDown(KeyCode.Backspace)) UndoLastCapture();
        if (Input.GetKeyDown(KeyCode.E)) ExportNow();
#endif
    }

    public void BeginMeasurement()
    {
        measurementModeEnabled = true;
        status = $"READY | captured={captures.Count}/{BuildReferenceSnapshot().Count}";
    }

    public void EndMeasurement()
    {
        measurementModeEnabled = false;
        status = "MEASUREMENT MODE OFF";
    }

    public void ClearMeasurements()
    {
        if (captureRoutine != null)
        {
            StopCoroutine(captureRoutine);
            captureRoutine = null;
        }
        captures.Clear();
        RefreshAnalysis();
        status = "MEASUREMENTS CLEARED";
    }

    public void CaptureNextReference()
    {
        if (!measurementModeEnabled)
        {
            status = "CAPTURE BLOCKED | enable measurement mode";
            return;
        }
        if (captureRoutine != null) return;
        if (probeTransform == null)
        {
            status = "CAPTURE BLOCKED | probe transform missing";
            Debug.LogError("[AAG Offset Diagnostic] Probe Transform is not assigned.", this);
            return;
        }

        var references = BuildReferenceSnapshot();
        if (references.Count == 0)
        {
            status = "CAPTURE BLOCKED | no reference points";
            Debug.LogError("[AAG Offset Diagnostic] No expected reference points are configured.", this);
            return;
        }

        var next = references.FirstOrDefault(reference => captures.All(capture =>
            !string.Equals(capture.referenceId, reference.referenceId, StringComparison.Ordinal)));
        if (next == null)
        {
            status = "ALL REFERENCES CAPTURED | clear or recapture by ID";
            return;
        }
        captureRoutine = StartCoroutine(CaptureAveraged(next));
    }

    public bool CaptureReferenceNow(string referenceId)
    {
        if (!measurementModeEnabled || captureRoutine != null || probeTransform == null) return false;
        var reference = BuildReferenceSnapshot().FirstOrDefault(value =>
            string.Equals(value.referenceId, referenceId, StringComparison.Ordinal));
        if (reference == null) return false;
        StoreCapture(reference, ProbeWorldPosition);
        return true;
    }

    public void UndoLastCapture()
    {
        if (captures.Count == 0)
        {
            status = "UNDO IGNORED | no captures";
            return;
        }
        var removed = captures[captures.Count - 1].referenceId;
        captures.RemoveAt(captures.Count - 1);
        RefreshAnalysis();
        status = $"REMOVED {removed} | captured={captures.Count}";
    }

    public bool ApplyRecommendedFixedOffset()
    {
        RefreshAnalysis();
        if (lastResult == null
            || lastResult.Diagnosis != AagSpaceOffsetSolver.Diagnosis.FixedTranslation)
        {
            status = "APPLY BLOCKED | diagnosis is not FixedTranslation";
            return false;
        }
        if (fixedSpaceOffset == null)
        {
            status = "APPLY BLOCKED | AagFixedSpaceOffset missing";
            return false;
        }

        fixedSpaceOffset.SetCorrectionOffset(lastResult.TranslationOffset, true, true);
        status = $"FIXED OFFSET APPLIED {lastResult.TranslationOffset:F3}";
        return true;
    }

    public void SetFixedSpaceOffset(AagFixedSpaceOffset value) => fixedSpaceOffset = value;

    public void ExportNow()
    {
        if (captures.Count == 0)
        {
            status = "EXPORT BLOCKED | no captures";
            return;
        }

        RefreshAnalysis();
        try
        {
            var folder = Path.Combine(Application.persistentDataPath, ExportFolderName);
            Directory.CreateDirectory(folder);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var jsonPath = Path.Combine(folder, $"aag_space_offset_{timestamp}.json");
            var csvPath = Path.Combine(folder, $"aag_space_offset_{timestamp}.csv");
            File.WriteAllText(jsonPath, JsonUtility.ToJson(BuildExport(), true), Encoding.UTF8);
            File.WriteAllText(csvPath, BuildCsv(), Encoding.UTF8);
            lastExportPath = jsonPath;
            status = $"EXPORTED | {lastResult?.Diagnosis} | {jsonPath}";
            Debug.Log($"[AAG Offset Diagnostic] JSON: {jsonPath}", this);
            Debug.Log($"[AAG Offset Diagnostic] CSV: {csvPath}", this);
        }
        catch (Exception exception)
        {
            status = $"EXPORT FAILED | {exception.Message}";
            Debug.LogError($"[AAG Offset Diagnostic] Export failed: {exception}", this);
        }
    }

    private IEnumerator CaptureAveraged(AagSpaceOffsetReferencePoint reference)
    {
        status = $"HOLD STILL AT {reference.referenceId}...";
        var start = Time.unscaledTime;
        var sum = Vector3.zero;
        var count = 0;
        do
        {
            sum += ProbeWorldPosition;
            count++;
            yield return null;
        }
        while (Time.unscaledTime - start < captureAveragingSeconds);

        StoreCapture(reference, sum / Mathf.Max(1, count));
        captureRoutine = null;
    }

    private void StoreCapture(AagSpaceOffsetReferencePoint reference, Vector3 observed)
    {
        var existing = captures.FirstOrDefault(value =>
            string.Equals(value.referenceId, reference.referenceId, StringComparison.Ordinal));
        if (existing == null)
        {
            existing = new CapturedPoint { referenceId = reference.referenceId };
            captures.Add(existing);
        }
        existing.expected = reference.ExpectedWorldPosition;
        existing.observed = observed;
        existing.capturedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        RefreshAnalysis();
        status = $"CAPTURED {reference.referenceId} | {BuildResultSummary()}";
        Debug.Log(
            $"[AAG Offset Diagnostic] id={reference.referenceId}; expected={existing.expected:F4}; "
            + $"observed={existing.observed:F4}; offset={(existing.observed - existing.expected):F4}",
            this);
        if (autoExportAfterCapture) ExportNow();
    }

    private Vector3 ProbeWorldPosition =>
        probeTransform.TransformPoint(probeLocalOffset);

    private List<AagSpaceOffsetReferencePoint> BuildReferenceSnapshot()
    {
        if (referencePoints != null && referencePoints.Count > 0)
        {
            return referencePoints
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.referenceId))
                .ToList();
        }

        var result = new List<AagSpaceOffsetReferencePoint>();
        if (referencePointsRoot == null) return result;
        for (var index = 0; index < referencePointsRoot.childCount; index++)
        {
            var child = referencePointsRoot.GetChild(index);
            result.Add(new AagSpaceOffsetReferencePoint
            {
                referenceId = child.name,
                expectedPoint = child,
                fallbackExpectedWorldPosition = child.position,
            });
        }
        return result;
    }

    private void RefreshAnalysis()
    {
        var samples = captures.Select(value => new AagSpaceOffsetSolver.Sample(
            value.referenceId, value.expected, value.observed)).ToArray();
        var settings = new AagSpaceOffsetSolver.Settings(
            horizontalOnly,
            minimumSamples,
            minimumBaselineMeters,
            residualToleranceMeters,
            rotationEvidenceDegrees,
            requiredRigidImprovementRatio);
        if (!AagSpaceOffsetSolver.TryAnalyze(samples, settings, out lastResult, out var failure))
        {
            lastResult = null;
            if (captures.Count > 0) status = $"ANALYSIS FAILED | {failure}";
        }
    }

    private string BuildResultSummary()
    {
        if (lastResult == null) return $"captured={captures.Count}";
        return $"{lastResult.Diagnosis} | offset={lastResult.TranslationOffset:F3} | "
            + $"translation RMS/max={lastResult.TranslationRmsMeters:F3}/{lastResult.TranslationMaxResidualMeters:F3}m | "
            + $"yaw={lastResult.YawDegrees:F2}deg";
    }

    private ExportDocument BuildExport()
    {
        var document = new ExportDocument
        {
            exportedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            horizontalOnly = horizontalOnly,
            result = lastResult == null ? null : new ExportResult
            {
                diagnosis = lastResult.Diagnosis.ToString(),
                detail = lastResult.Detail,
                sampleCount = lastResult.SampleCount,
                baselineMeters = lastResult.BaselineMeters,
                recommendedTranslation = new VectorRecord(lastResult.TranslationOffset),
                translationRmsMeters = lastResult.TranslationRmsMeters,
                translationMaxResidualMeters = lastResult.TranslationMaxResidualMeters,
                fittedYawDegrees = lastResult.YawDegrees,
                fittedRigidTranslation = new VectorRecord(lastResult.RigidTranslation),
                rigidRmsMeters = lastResult.RigidRmsMeters,
                rigidMaxResidualMeters = lastResult.RigidMaxResidualMeters,
            },
        };

        foreach (var capture in captures)
        {
            var residual = lastResult == null
                ? Vector3.zero
                : capture.observed - (capture.expected + lastResult.TranslationOffset);
            if (horizontalOnly) residual.y = 0f;
            document.points.Add(new ExportPoint
            {
                referenceId = capture.referenceId,
                expected = new VectorRecord(capture.expected),
                observed = new VectorRecord(capture.observed),
                individualOffset = new VectorRecord(capture.observed - capture.expected),
                translationResidual = new VectorRecord(residual),
                translationResidualMeters = residual.magnitude,
                capturedAtUtc = capture.capturedAtUtc,
            });
        }
        return document;
    }

    private string BuildCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "reference_id,expected_x,expected_y,expected_z,observed_x,observed_y,observed_z,"
            + "offset_x,offset_y,offset_z,translation_residual_m,diagnosis,recommended_x,recommended_y,recommended_z,captured_at_utc");
        foreach (var capture in captures)
        {
            var offset = capture.observed - capture.expected;
            var recommended = lastResult?.TranslationOffset ?? Vector3.zero;
            var residual = capture.observed - (capture.expected + recommended);
            if (horizontalOnly) residual.y = 0f;
            builder.Append(Csv(capture.referenceId)).Append(',')
                .Append(Number(capture.expected.x)).Append(',')
                .Append(Number(capture.expected.y)).Append(',')
                .Append(Number(capture.expected.z)).Append(',')
                .Append(Number(capture.observed.x)).Append(',')
                .Append(Number(capture.observed.y)).Append(',')
                .Append(Number(capture.observed.z)).Append(',')
                .Append(Number(offset.x)).Append(',')
                .Append(Number(offset.y)).Append(',')
                .Append(Number(offset.z)).Append(',')
                .Append(Number(residual.magnitude)).Append(',')
                .Append(Csv(lastResult?.Diagnosis.ToString() ?? string.Empty)).Append(',')
                .Append(Number(recommended.x)).Append(',')
                .Append(Number(recommended.y)).Append(',')
                .Append(Number(recommended.z)).Append(',')
                .Append(Csv(capture.capturedAtUtc)).AppendLine();
        }
        return builder.ToString();
    }

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Csv(string value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private void OnGUI()
    {
        if (!showRuntimePanel || !measurementModeEnabled) return;
        var previousColor = GUI.color;
        GUI.color = Color.black;
        GUI.Box(new Rect(16, 140, 1160, 150), GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(new Rect(28, 150, 1120, 28), "[AAG SPACE OFFSET DIAGNOSTIC]");
        GUI.Label(new Rect(28, 180, 1120, 48), status);
        GUI.Label(new Rect(28, 230, 1120, 48),
            $"R trigger: capture next | B: undo | A: export | last export: {lastExportPath}");
        GUI.color = previousColor;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        foreach (var reference in BuildReferenceSnapshot())
        {
            if (reference != null) Gizmos.DrawWireSphere(reference.ExpectedWorldPosition, 0.08f);
        }
        Gizmos.color = Color.magenta;
        foreach (var capture in captures) Gizmos.DrawSphere(capture.observed, 0.04f);
    }
}
