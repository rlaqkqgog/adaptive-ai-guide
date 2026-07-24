using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pure-math diagnosis for deciding whether physical observations differ from
/// saved Unity points by one translation, by a yaw plus translation, or by an
/// inconsistent/non-rigid error. This class has no dependency on MRUK.
/// </summary>
public static class AagSpaceOffsetSolver
{
    public enum Diagnosis
    {
        InsufficientData,
        FixedTranslation,
        YawAndTranslation,
        Inconsistent,
    }

    public readonly struct Sample
    {
        public readonly string Id;
        public readonly Vector3 Expected;
        public readonly Vector3 Observed;

        public Sample(string id, Vector3 expected, Vector3 observed)
        {
            Id = id ?? string.Empty;
            Expected = expected;
            Observed = observed;
        }
    }

    public readonly struct Settings
    {
        public readonly bool HorizontalOnly;
        public readonly int MinimumSamples;
        public readonly float MinimumBaselineMeters;
        public readonly float TranslationResidualToleranceMeters;
        public readonly float RotationEvidenceDegrees;
        public readonly float RequiredRigidImprovementRatio;

        public Settings(
            bool horizontalOnly,
            int minimumSamples,
            float minimumBaselineMeters,
            float translationResidualToleranceMeters,
            float rotationEvidenceDegrees,
            float requiredRigidImprovementRatio)
        {
            HorizontalOnly = horizontalOnly;
            MinimumSamples = Mathf.Max(2, minimumSamples);
            MinimumBaselineMeters = Mathf.Max(0.01f, minimumBaselineMeters);
            TranslationResidualToleranceMeters = Mathf.Max(0.001f, translationResidualToleranceMeters);
            RotationEvidenceDegrees = Mathf.Max(0.1f, rotationEvidenceDegrees);
            RequiredRigidImprovementRatio = Mathf.Clamp(requiredRigidImprovementRatio, 0.05f, 0.95f);
        }

        public static Settings Default => new Settings(true, 3, 2f, 0.10f, 2f, 0.65f);
    }

    public sealed class Result
    {
        public Diagnosis Diagnosis { get; internal set; }
        public int SampleCount { get; internal set; }
        public float BaselineMeters { get; internal set; }
        public Vector3 TranslationOffset { get; internal set; }
        public float TranslationRmsMeters { get; internal set; }
        public float TranslationMaxResidualMeters { get; internal set; }
        public float YawDegrees { get; internal set; }
        public Vector3 RigidTranslation { get; internal set; }
        public float RigidRmsMeters { get; internal set; }
        public float RigidMaxResidualMeters { get; internal set; }
        public string Detail { get; internal set; } = string.Empty;
    }

    public static bool TryAnalyze(
        IReadOnlyList<Sample> samples,
        Settings settings,
        out Result result,
        out string failure)
    {
        result = new Result();
        failure = string.Empty;
        if (samples == null || samples.Count == 0)
        {
            failure = "no_samples";
            result.Detail = failure;
            return false;
        }

        var valid = new List<Sample>(samples.Count);
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (!IsFinite(sample.Expected) || !IsFinite(sample.Observed))
            {
                failure = $"non_finite_sample_{index}_{sample.Id}";
                result.Detail = failure;
                return false;
            }
            valid.Add(sample);
        }

        result.SampleCount = valid.Count;
        result.BaselineMeters = MaximumHorizontalSeparation(valid);
        result.TranslationOffset = MeanOffset(valid, settings.HorizontalOnly);
        MeasureResiduals(
            valid,
            0f,
            result.TranslationOffset,
            settings.HorizontalOnly,
            out var translationRms,
            out var translationMax);
        result.TranslationRmsMeters = translationRms;
        result.TranslationMaxResidualMeters = translationMax;

        SolveYaw(valid, settings.HorizontalOnly, out var yaw, out var rigidTranslation);
        result.YawDegrees = Mathf.DeltaAngle(0f, yaw);
        result.RigidTranslation = rigidTranslation;
        MeasureResiduals(
            valid,
            result.YawDegrees,
            rigidTranslation,
            settings.HorizontalOnly,
            out var rigidRms,
            out var rigidMax);
        result.RigidRmsMeters = rigidRms;
        result.RigidMaxResidualMeters = rigidMax;

        if (valid.Count < settings.MinimumSamples)
        {
            result.Diagnosis = Diagnosis.InsufficientData;
            result.Detail = $"requires_{settings.MinimumSamples}_samples_actual_{valid.Count}";
            return true;
        }
        if (result.BaselineMeters < settings.MinimumBaselineMeters)
        {
            result.Diagnosis = Diagnosis.InsufficientData;
            result.Detail = $"baseline_{result.BaselineMeters:F3}m_below_{settings.MinimumBaselineMeters:F3}m";
            return true;
        }
        if (translationMax <= settings.TranslationResidualToleranceMeters)
        {
            result.Diagnosis = Diagnosis.FixedTranslation;
            result.Detail = "all_points_agree_with_one_translation";
            return true;
        }

        var meaningfulYaw = Mathf.Abs(result.YawDegrees) >= settings.RotationEvidenceDegrees;
        var rigidImproved = rigidRms <= translationRms * settings.RequiredRigidImprovementRatio;
        if (meaningfulYaw
            && rigidImproved
            && rigidMax <= settings.TranslationResidualToleranceMeters)
        {
            result.Diagnosis = Diagnosis.YawAndTranslation;
            result.Detail = "yaw_fit_materially_better_than_translation_only";
            return true;
        }

        result.Diagnosis = Diagnosis.Inconsistent;
        result.Detail = "residuals_exceed_fixed_transform_tolerance";
        return true;
    }

    private static Vector3 MeanOffset(IReadOnlyList<Sample> samples, bool horizontalOnly)
    {
        var sum = Vector3.zero;
        foreach (var sample in samples) sum += sample.Observed - sample.Expected;
        var mean = sum / samples.Count;
        if (horizontalOnly) mean.y = 0f;
        return mean;
    }

    private static void SolveYaw(
        IReadOnlyList<Sample> samples,
        bool horizontalOnly,
        out float yawDegrees,
        out Vector3 translation)
    {
        var expectedCenter = Vector3.zero;
        var observedCenter = Vector3.zero;
        foreach (var sample in samples)
        {
            expectedCenter += sample.Expected;
            observedCenter += sample.Observed;
        }
        expectedCenter /= samples.Count;
        observedCenter /= samples.Count;

        double dot = 0d;
        double cross = 0d;
        foreach (var sample in samples)
        {
            var expected = sample.Expected - expectedCenter;
            var observed = sample.Observed - observedCenter;
            dot += expected.x * observed.x + expected.z * observed.z;
            // Unity positive yaw maps +X toward -Z.
            cross += expected.z * observed.x - expected.x * observed.z;
        }

        yawDegrees = (float)(Math.Atan2(cross, dot) * Mathf.Rad2Deg);
        translation = observedCenter - RotateYaw(expectedCenter, yawDegrees);
        if (horizontalOnly) translation.y = 0f;
    }

    private static void MeasureResiduals(
        IReadOnlyList<Sample> samples,
        float yawDegrees,
        Vector3 translation,
        bool horizontalOnly,
        out float rms,
        out float maximum)
    {
        double squaredSum = 0d;
        maximum = 0f;
        foreach (var sample in samples)
        {
            var residual = sample.Observed - (RotateYaw(sample.Expected, yawDegrees) + translation);
            if (horizontalOnly) residual.y = 0f;
            var magnitude = residual.magnitude;
            squaredSum += magnitude * magnitude;
            maximum = Mathf.Max(maximum, magnitude);
        }
        rms = Mathf.Sqrt((float)(squaredSum / samples.Count));
    }

    private static Vector3 RotateYaw(Vector3 point, float yawDegrees)
    {
        var radians = yawDegrees * Mathf.Deg2Rad;
        var cosine = Mathf.Cos(radians);
        var sine = Mathf.Sin(radians);
        return new Vector3(
            cosine * point.x + sine * point.z,
            point.y,
            -sine * point.x + cosine * point.z);
    }

    private static float MaximumHorizontalSeparation(IReadOnlyList<Sample> samples)
    {
        var maximum = 0f;
        for (var left = 0; left < samples.Count; left++)
        {
            for (var right = left + 1; right < samples.Count; right++)
            {
                var delta = samples[left].Expected - samples[right].Expected;
                delta.y = 0f;
                maximum = Mathf.Max(maximum, delta.magnitude);
            }
        }
        return maximum;
    }

    public static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
