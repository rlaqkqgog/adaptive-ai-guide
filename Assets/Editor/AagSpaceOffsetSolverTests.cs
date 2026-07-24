using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class AagSpaceOffsetSolverTests
{
    [Test]
    public void TryAnalyze_RecognizesOneHorizontalTranslation()
    {
        var offset = new Vector3(-3.82f, 0f, 0.15f);
        var samples = new List<AagSpaceOffsetSolver.Sample>
        {
            Sample("room1", new Vector3(-20f, 0f, -5f), offset, new Vector3(0.01f, 0f, -0.01f)),
            Sample("room4", new Vector3(-5f, 0f, 2f), offset, new Vector3(-0.01f, 0f, 0.01f)),
            Sample("room8", new Vector3(10f, 0f, 7f), offset, Vector3.zero),
        };

        Assert.That(AagSpaceOffsetSolver.TryAnalyze(
            samples, AagSpaceOffsetSolver.Settings.Default, out var result, out var failure),
            Is.True, failure);
        Assert.That(result.Diagnosis, Is.EqualTo(AagSpaceOffsetSolver.Diagnosis.FixedTranslation));
        Assert.That(Vector3.Distance(result.TranslationOffset, offset), Is.LessThan(0.001f));
        Assert.That(result.TranslationMaxResidualMeters, Is.LessThan(0.03f));
    }

    [Test]
    public void TryAnalyze_SeparatesYawFromTranslationOnly()
    {
        const float yaw = 12f;
        var translation = new Vector3(2.4f, 0f, -1.7f);
        var samples = new List<AagSpaceOffsetSolver.Sample>
        {
            RigidSample("A", new Vector3(-12f, 0f, -4f), yaw, translation),
            RigidSample("B", new Vector3(0f, 0f, 9f), yaw, translation),
            RigidSample("C", new Vector3(15f, 0f, -2f), yaw, translation),
            RigidSample("D", new Vector3(4f, 0f, -14f), yaw, translation),
        };

        Assert.That(AagSpaceOffsetSolver.TryAnalyze(
            samples, AagSpaceOffsetSolver.Settings.Default, out var result, out var failure),
            Is.True, failure);
        Assert.That(result.Diagnosis, Is.EqualTo(AagSpaceOffsetSolver.Diagnosis.YawAndTranslation));
        Assert.That(result.YawDegrees, Is.EqualTo(12f).Within(0.01f));
        Assert.That(Vector3.Distance(result.RigidTranslation, translation), Is.LessThan(0.001f));
        Assert.That(result.RigidMaxResidualMeters, Is.LessThan(0.001f));
    }

    [Test]
    public void TryAnalyze_RejectsRoomSpecificOffsetsAsInconsistent()
    {
        var samples = new List<AagSpaceOffsetSolver.Sample>
        {
            new("room1", new Vector3(-10f, 0f, 0f), new Vector3(-6f, 0f, 0f)),
            new("room2", Vector3.zero, new Vector3(1f, 0f, 4f)),
            new("room3", new Vector3(10f, 0f, 0f), new Vector3(13f, 0f, -3f)),
            new("room4", new Vector3(0f, 0f, 10f), new Vector3(-4f, 0f, 12f)),
        };

        Assert.That(AagSpaceOffsetSolver.TryAnalyze(
            samples, AagSpaceOffsetSolver.Settings.Default, out var result, out var failure),
            Is.True, failure);
        Assert.That(result.Diagnosis, Is.EqualTo(AagSpaceOffsetSolver.Diagnosis.Inconsistent));
        Assert.That(result.TranslationMaxResidualMeters, Is.GreaterThan(1f));
    }

    [Test]
    public void TryAnalyze_RequiresPointsWithUsefulBaseline()
    {
        var offset = new Vector3(3f, 0f, -2f);
        var samples = new List<AagSpaceOffsetSolver.Sample>
        {
            Sample("A", Vector3.zero, offset, Vector3.zero),
            Sample("B", Vector3.right * 0.1f, offset, Vector3.zero),
            Sample("C", Vector3.forward * 0.1f, offset, Vector3.zero),
        };

        Assert.That(AagSpaceOffsetSolver.TryAnalyze(
            samples, AagSpaceOffsetSolver.Settings.Default, out var result, out var failure),
            Is.True, failure);
        Assert.That(result.Diagnosis, Is.EqualTo(AagSpaceOffsetSolver.Diagnosis.InsufficientData));
        StringAssert.Contains("baseline", result.Detail);
    }

    [Test]
    public void FixedSpaceOffset_AppliesOnceAndCanResetToBaseline()
    {
        var managerObject = new GameObject("Fixed offset test manager");
        var contentObject = new GameObject("Fixed offset test content");
        try
        {
            var manager = managerObject.AddComponent<AagFixedSpaceOffset>();
            var baseline = new Vector3(4f, 1.25f, -7f);
            var configured = new Vector3(-3.5f, 9f, 0.75f);
            contentObject.transform.position = baseline;
            manager.Configure(true, configured, true);

            Assert.That(manager.TryRegisterContent(
                contentObject.transform, "test", out var failure), Is.True, failure);
            var expected = baseline + new Vector3(configured.x, 0f, configured.z);
            Assert.That(Vector3.Distance(contentObject.transform.position, expected), Is.LessThan(0.0001f));

            manager.ApplyCorrection();
            Assert.That(Vector3.Distance(contentObject.transform.position, expected), Is.LessThan(0.0001f),
                "The correction must be absolute, not cumulative.");

            manager.ResetCorrection();
            Assert.That(Vector3.Distance(contentObject.transform.position, baseline), Is.LessThan(0.0001f));
        }
        finally
        {
            Object.DestroyImmediate(contentObject);
            Object.DestroyImmediate(managerObject);
        }
    }

    private static AagSpaceOffsetSolver.Sample Sample(
        string id,
        Vector3 expected,
        Vector3 offset,
        Vector3 noise) =>
        new AagSpaceOffsetSolver.Sample(id, expected, expected + offset + noise);

    private static AagSpaceOffsetSolver.Sample RigidSample(
        string id,
        Vector3 expected,
        float yawDegrees,
        Vector3 translation) =>
        new AagSpaceOffsetSolver.Sample(id, expected, RotateYaw(expected, yawDegrees) + translation);

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
}
