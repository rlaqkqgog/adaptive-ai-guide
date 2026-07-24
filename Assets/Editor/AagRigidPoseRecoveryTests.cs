using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class AagRigidPoseRecoveryTests
{
    [Test]
    public void TrySolve_RequiresThreeConsistentReferences()
    {
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new AagRigidPoseRecovery.Reference("A", Vector3.zero, Vector3.one),
            new AagRigidPoseRecovery.Reference("B", Vector3.right * 4f, Vector3.right * 4f + Vector3.one),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out _, out var failure), Is.False);
        StringAssert.Contains("minimum_3", failure);
    }

    [Test]
    public void TrySolveTwoReference_RecoversDedicatedIncidentalPair()
    {
        var rotation = Quaternion.Euler(0f, 63f, 0f);
        var translation = new Vector3(-4.2f, 0.18f, 7.6f);
        var first = new Vector3(-27.04f, 0.87f, -3.63f);
        var second = new Vector3(-26.59f, 0.93f, -7.01f);
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            Reference("riceCooker", first, rotation, translation),
            Reference("sandClock", second, rotation, translation),
        };

        Assert.That(AagRigidPoseRecovery.TrySolveTwoReference(
            references, out var solution, out var failure), Is.True, failure);
        var missingGlobe = new Vector3(-17.75f, 0.85f, 1.92f);
        var expected = rotation * missingGlobe + translation;
        Assert.That(Vector3.Distance(solution.TransformPoint(missingGlobe), expected), Is.LessThan(0.001f));
        CollectionAssert.AreEqual(new[] { "riceCooker", "sandClock" }, solution.InlierIds);
    }

    [Test]
    public void TrySolveTwoReference_RejectsChangedPairDistance()
    {
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new AagRigidPoseRecovery.Reference("A", Vector3.zero, Vector3.zero),
            new AagRigidPoseRecovery.Reference("B", Vector3.right * 3f, Vector3.right * 5f),
        };

        Assert.That(AagRigidPoseRecovery.TrySolveTwoReference(
            references, out _, out var failure), Is.False);
        StringAssert.Contains("pair_distance_delta", failure);
    }

    [Test]
    public void TrySolve_UsesConsensusAndRejectsStaleBindings()
    {
        var rotation = Quaternion.Euler(0f, 37f, 0f);
        var translation = new Vector3(-8.5f, 0.32f, 4.7f);
        var capturedA = new Vector3(-2f, 0.8f, 1f);
        var capturedB = new Vector3(5f, 1.1f, 3f);
        var capturedC = new Vector3(1f, 0.4f, -8f);
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            Reference("A", capturedA, rotation, translation),
            Reference("B", capturedB, rotation, translation),
            Reference("C", capturedC, rotation, translation),
            new AagRigidPoseRecovery.Reference("STALE_1", new Vector3(9f, 1f, 7f), new Vector3(40f, 2f, -30f)),
            new AagRigidPoseRecovery.Reference("STALE_2", new Vector3(-4f, 2f, 11f), new Vector3(-25f, 0f, 35f)),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out var solution, out var failure),
            Is.True, failure);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, solution.InlierIds);
        CollectionAssert.AreEqual(new[] { "STALE_1", "STALE_2" }, solution.OutlierIds);
        Assert.That(solution.RmsResidualMeters, Is.LessThan(0.001f));

        var unseenCapturedPoint = new Vector3(7.2f, 1.7f, -3.4f);
        var expected = rotation * unseenCapturedPoint + translation;
        Assert.That(Vector3.Distance(solution.TransformPoint(unseenCapturedPoint), expected), Is.LessThan(0.001f));
    }

    [Test]
    public void TrySolve_PreservesCapturedDistancesWithoutHalfScaleCollapse()
    {
        var rotation = Quaternion.Euler(0f, -72f, 0f);
        var translation = new Vector3(13f, -0.15f, -5f);
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            Reference("A", new Vector3(0f, 0.2f, 0f), rotation, translation),
            Reference("B", new Vector3(6f, 1.3f, 0f), rotation, translation),
            Reference("C", new Vector3(1f, 0.6f, 9f), rotation, translation),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out var solution, out var failure),
            Is.True, failure);
        var first = new Vector3(-3f, 0.8f, 2f);
        var second = new Vector3(7f, 1.6f, -4f);
        var capturedDistance = Vector3.Distance(first, second);
        var recoveredDistance = Vector3.Distance(solution.TransformPoint(first), solution.TransformPoint(second));
        Assert.That(recoveredDistance, Is.EqualTo(capturedDistance).Within(0.001f));
    }

    [Test]
    public void TrySolve_PostRescanEightRoomConstellation_IsWithinStrictThresholds()
    {
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new("room1", new Vector3(-26.704f, -0.002f, -3.716f), new Vector3(-3.025242f, 0.349068f, -22.232548f)),
            new("room2", new Vector3(-13.106f, 0.036f, -0.403f), new Vector3(-0.439978f, 0.384134f, -8.409974f)),
            new("room3", new Vector3(1.133f, 0.079f, 2.906f), new Vector3(2.281196f, 0.342008f, 5.908213f)),
            new("hall1-1", new Vector3(-22.695f, -0.005f, -7.886f), new Vector3(2.327605f, 0.347336f, -20.014044f)),
            new("hall1-2", new Vector3(-12.484f, -0.003f, -5.448f), new Vector3(4.510054f, 0.355273f, -9.838497f)),
            new("hall1-3", new Vector3(-0.681f, -0.049f, -2.859f), new Vector3(6.769153f, 0.343031f, 1.970263f)),
            new("hall2-1", new Vector3(10.916f, 0.006f, -0.139f), new Vector3(9.202517f, 0.323666f, 13.738680f)),
            new("hall2-2", new Vector3(6.964f, 0.018f, 3.705f), new Vector3(3.960026f, 0.403465f, 11.574716f)),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out var solution, out var failure),
            Is.True, failure);
        Assert.That(solution.InlierIds.Length, Is.GreaterThanOrEqualTo(3));
        Assert.That(solution.RmsResidualMeters,
            Is.LessThanOrEqualTo(AagRigidPoseRecovery.MaximumRmsResidualMeters));
        Assert.That(solution.MaxResidualMeters,
            Is.LessThanOrEqualTo(AagRigidPoseRecovery.InlierToleranceMeters));
    }

    [Test]
    public void TrySolveBestEffort_CurrentNineRoomRelocalization_ContinuesWithQualityWarning()
    {
        // Runtime field sample from 2026-07-21 13:49. The ninth split room has
        // no legacy canonical origin; five of the remaining eight form a valid
        // consensus whose 16.89 cm RMS is diagnostic, not a session-start gate.
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new("room3", new Vector3(1.133f, 0.079f, 2.906f), new Vector3(0.151f, -0.015f, 1.292f)),
            new("room2-1", new Vector3(-13.106f, 0.036f, -0.403f), new Vector3(-15.026f, 0.029f, 0.831f)),
            new("hall1-3", new Vector3(-0.681f, -0.049f, -2.859f), new Vector3(-3.787f, -0.021f, -3.797f)),
            new("hall1-1", new Vector3(-22.695f, -0.005f, -7.886f), new Vector3(-25.206f, 0.003f, -4.416f)),
            new("hall2-1", new Vector3(10.916f, 0.006f, -0.139f), new Vector3(9.443f, -0.068f, -3.533f)),
            new("room1", new Vector3(-26.704f, -0.002f, -3.716f), new Vector3(-28.124f, 0.017f, 0.247f)),
            new("hall1-2", new Vector3(-12.484f, -0.003f, -5.448f), new Vector3(-15.259f, -0.005f, -4.052f)),
            new("hall2-2", new Vector3(6.964f, 0.018f, 3.705f), new Vector3(6.129f, -0.038f, 0.916f)),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out _, out var strictFailure), Is.False);
        StringAssert.Contains("rms_0.1689_maximum_0.1500", strictFailure);

        Assert.That(AagRigidPoseRecovery.TrySolveBestEffort(
            references, out var solution, out var qualityWarning), Is.True);
        StringAssert.Contains("rms_0.1689_maximum_0.1500", qualityWarning);
        Assert.That(solution.InlierIds.Length, Is.EqualTo(5));
        Assert.That(solution.RmsResidualMeters, Is.EqualTo(0.1689f).Within(0.0002f));
        Assert.That(solution.MaxResidualMeters,
            Is.LessThanOrEqualTo(AagRigidPoseRecovery.InlierToleranceMeters));
    }

    [Test]
    public void TrySolve_FieldS3DataSelectsKnownRigidTriplet()
    {
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new AagRigidPoseRecovery.Reference("green_1", new Vector3(6.127f, 1.098f, 22.764f), new Vector3(-8.864f, 1.797f, 5.926f)),
            new AagRigidPoseRecovery.Reference("blue_1", new Vector3(2.509f, 1.088f, 17.483f), new Vector3(-22.690f, 0.955f, 1.112f)),
            new AagRigidPoseRecovery.Reference("red_2", new Vector3(3.751f, 0.263f, 9.766f), new Vector3(-21.702f, 0.779f, -1.396f)),
            new AagRigidPoseRecovery.Reference("green_2", new Vector3(7.463f, 0.447f, 3.701f), new Vector3(-8.506f, 0.454f, 4.720f)),
            new AagRigidPoseRecovery.Reference("yellow_3", new Vector3(-0.183f, 0.883f, -10.576f), new Vector3(5.041f, 0.828f, -4.064f)),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out var solution, out var failure),
            Is.True, failure);
        CollectionAssert.AreEqual(new[] { "blue_1", "green_2", "yellow_3" }, solution.InlierIds);
        CollectionAssert.AreEqual(new[] { "green_1", "red_2" }, solution.OutlierIds);
        Assert.That(solution.RmsResidualMeters, Is.LessThan(0.1f));
        Assert.That(solution.MaxResidualMeters, Is.LessThan(AagRigidPoseRecovery.InlierToleranceMeters));
    }

    [Test]
    public void TrySolveValidatedPair_FieldRetryDataUsesThirdPointWithoutRelaxingThresholds()
    {
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new AagRigidPoseRecovery.Reference("green_1", new Vector3(6.127f, 1.098f, 22.764f), new Vector3(-10.065f, 1.873f, 2.416f)),
            new AagRigidPoseRecovery.Reference("blue_1", new Vector3(2.509f, 1.088f, 17.483f), new Vector3(-20.619f, 1.212f, -7.320f)),
            new AagRigidPoseRecovery.Reference("red_2", new Vector3(3.751f, 0.263f, 9.766f), new Vector3(-18.676f, 1.031f, -9.241f)),
            new AagRigidPoseRecovery.Reference("green_2", new Vector3(7.463f, 0.447f, 3.701f), new Vector3(-9.209f, 0.487f, 1.489f)),
            new AagRigidPoseRecovery.Reference("yellow_3", new Vector3(-0.183f, 0.883f, -10.576f), new Vector3(6.809f, 0.860f, -0.763f)),
        };

        Assert.That(AagRigidPoseRecovery.TrySolve(references, out _, out _), Is.False);
        Assert.That(AagRigidPoseRecovery.TrySolveValidatedPair(
            references,
            out var solution,
            out var referencePair,
            out var validatorId,
            out var maximumTriangleDelta,
            out var failure), Is.True, failure);
        Assert.That(referencePair, Is.EqualTo("blue_1|yellow_3"));
        Assert.That(validatorId, Is.EqualTo("green_2"));
        Assert.That(maximumTriangleDelta, Is.LessThanOrEqualTo(AagRigidPoseRecovery.InlierToleranceMeters));
        Assert.That(solution.RmsResidualMeters, Is.LessThanOrEqualTo(AagRigidPoseRecovery.MaximumRmsResidualMeters));
        CollectionAssert.AreEquivalent(new[] { "blue_1", "green_2", "yellow_3" }, solution.InlierIds);
    }

    [Test]
    public void TrySolveValidatedPair_RejectsMirroredThirdReference()
    {
        var references = new List<AagRigidPoseRecovery.Reference>
        {
            new AagRigidPoseRecovery.Reference("A", new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f)),
            new AagRigidPoseRecovery.Reference("B", new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 0f)),
            new AagRigidPoseRecovery.Reference("C", new Vector3(2f, 0f, 4f), new Vector3(2f, 0f, -4f)),
        };

        Assert.That(AagRigidPoseRecovery.TrySolveValidatedPair(
            references, out _, out _, out _, out _, out var failure), Is.False);
        Assert.That(failure, Is.EqualTo("no_validated_reference_pair"));
    }

    private static AagRigidPoseRecovery.Reference Reference(
        string id,
        Vector3 captured,
        Quaternion rotation,
        Vector3 translation) =>
        new AagRigidPoseRecovery.Reference(id, captured, rotation * captured + translation);
}
