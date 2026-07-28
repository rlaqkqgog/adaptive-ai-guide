using NUnit.Framework;
using UnityEngine;

public sealed class AagMrukSpaceCorrectionTests
{
    [SetUp]
    public void SetUp() => AagMrukSpaceCorrection.Reset();

    [TearDown]
    public void TearDown() => AagMrukSpaceCorrection.Reset();

    [Test]
    public void AppliedTranslation_RoundTripsBetweenMrukAndObservedFrames()
    {
        var translation = new Vector3(0.42f, -0.07f, -0.31f);
        var mrukPosition = new Vector3(3.5f, 1.6f, -8.2f);

        Assert.That(
            AagMrukSpaceCorrection.TryApplyTranslation(translation, out var failure),
            Is.True,
            failure);
        var observed = AagMrukSpaceCorrection.MrukToObservedPosition(mrukPosition);
        var recovered = AagMrukSpaceCorrection.ObservedToMrukPosition(observed);

        Assert.That(Vector3.Distance(observed, mrukPosition + translation), Is.LessThan(0.0001f));
        Assert.That(Vector3.Distance(recovered, mrukPosition), Is.LessThan(0.0001f));
    }

    [Test]
    public void AppliedRigidYaw_RoundTripsPositionAndRotation()
    {
        var yaw = Quaternion.Euler(0f, 37f, 0f);
        var translation = new Vector3(2.5f, -0.1f, -4f);
        Assert.That(
            AagMrukSpaceCorrection.TryApplyRigid(translation, yaw, out var failure),
            Is.True,
            failure);

        var mrukPosition = new Vector3(1.2f, 0.8f, -3.4f);
        var mrukRotation = Quaternion.Euler(0f, 12f, 0f);
        var observedPosition = AagMrukSpaceCorrection.MrukToObservedPosition(mrukPosition);
        var observedRotation = AagMrukSpaceCorrection.MrukToObservedRotation(mrukRotation);

        Assert.That(
            Vector3.Distance(
                AagMrukSpaceCorrection.ObservedToMrukPosition(observedPosition),
                mrukPosition),
            Is.LessThan(0.00001f));
        Assert.That(
            Quaternion.Angle(
                AagMrukSpaceCorrection.ObservedToMrukRotation(observedRotation),
                mrukRotation),
            Is.LessThan(0.001f));
    }

    [Test]
    public void Reset_DisablesPreviouslyAppliedTranslation()
    {
        Assert.That(
            AagMrukSpaceCorrection.TryApplyTranslation(Vector3.right, out var failure),
            Is.True,
            failure);
        AagMrukSpaceCorrection.Reset();

        var position = new Vector3(2f, 3f, 4f);
        Assert.That(AagMrukSpaceCorrection.IsApplied, Is.False);
        Assert.That(AagMrukSpaceCorrection.TranslationMeters, Is.EqualTo(Vector3.zero));
        Assert.That(AagMrukSpaceCorrection.ObservedToMrukPosition(position), Is.EqualTo(position));
    }

    [Test]
    public void InvalidTranslation_IsRejectedWithoutChangingState()
    {
        Assert.That(
            AagMrukSpaceCorrection.TryApplyTranslation(
                new Vector3(float.NaN, 0f, 0f), out var failure),
            Is.False);
        Assert.That(failure, Is.EqualTo("mruk_query_translation_non_finite"));
        Assert.That(AagMrukSpaceCorrection.IsApplied, Is.False);
    }

    [Test]
    public void Fp2BakedSpace_ExposesRoomWallClearance()
    {
        var roomUuid = new System.Guid("96a223f3-baf3-7044-2958-6f2468b35c72");
        Assert.That(
            AagFp2BakedSpace.TryGetRoomFloor(
                roomUuid, out var floorPose, out _, out var failure),
            Is.True,
            failure);
        var corridorCenter = floorPose.position
            + floorPose.rotation * new Vector3(3.75f, 0f, 0f);

        Assert.That(
            AagFp2BakedSpace.TryGetMinimumWallClearance(
                roomUuid, corridorCenter, out var clearance),
            Is.True);
        Assert.That(clearance, Is.GreaterThan(0.70f));
    }
}
