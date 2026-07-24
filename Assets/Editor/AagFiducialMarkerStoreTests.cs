using NUnit.Framework;
using UnityEngine;

public sealed class AagFiducialMarkerStoreTests
{
    [TestCase("AAG-FP1-ZONE:room1", "room1")]
    [TestCase("AAG-FP1-ZONE:room3", "room3")]
    [TestCase("AAG-FP1-ZONE:hall1-1", "hall1-1")]
    [TestCase("AAG-FP1-ZONE:hall2-2", "hall2-2")]
    public void PayloadParserAcceptsOnlyNamespacedFp1Markers(string payload, string expected)
    {
        Assert.That(AagFiducialMarkerStore.TryParsePayload(payload, out var roomId), Is.True);
        Assert.That(roomId, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("room3")]
    [TestCase("AAG-FP1-ZONE:")]
    [TestCase("AAG-FP1-ZONE:room 3")]
    [TestCase("aag-fp1-zone:room3")]
    public void PayloadParserRejectsUntrustedValues(string payload)
    {
        Assert.That(AagFiducialMarkerStore.TryParsePayload(payload, out _), Is.False);
    }

    [Test]
    public void MarkerObservationRecoversOriginalFloorPose()
    {
        var floor = new Pose(
            new Vector3(-8.2f, 0.1f, 4.7f),
            Quaternion.Euler(0f, 31f, 0f));
        var markerInFloor = new Pose(
            new Vector3(1.4f, -0.8f, 1.55f),
            Quaternion.Euler(0f, -90f, 0f));
        var observedMarker = AagFiducialMarkerStore.Compose(floor, markerInFloor);

        var recoveredFloor = AagFiducialMarkerStore.Compose(
            observedMarker,
            AagFiducialMarkerStore.Inverse(markerInFloor));

        Assert.That(Vector3.Distance(recoveredFloor.position, floor.position), Is.LessThan(0.0001f));
        Assert.That(Quaternion.Angle(recoveredFloor.rotation, floor.rotation), Is.LessThan(0.001f));
    }

    [Test]
    public void ZoneCorrectionDoesNotChangeMarkerLocalPose()
    {
        var originalFloor = new Pose(
            new Vector3(3f, 0f, -2f),
            Quaternion.Euler(0f, 12f, 0f));
        var markerLocal = new Pose(
            new Vector3(-0.4f, 0.8f, 1.6f),
            Quaternion.Euler(0f, 77f, 0f));
        var shiftedFloor = new Pose(
            new Vector3(9f, 0.05f, 5f),
            Quaternion.Euler(0f, -18f, 0f));
        var shiftedObservation = AagFiducialMarkerStore.Compose(shiftedFloor, markerLocal);

        var recoveredFloor = AagFiducialMarkerStore.Compose(
            shiftedObservation,
            AagFiducialMarkerStore.Inverse(markerLocal));
        var recoveredMarkerLocal = AagFiducialMarkerStore.RelativeTo(
            recoveredFloor,
            shiftedObservation);

        Assert.That(Vector3.Distance(recoveredMarkerLocal.position, markerLocal.position), Is.LessThan(0.0001f));
        Assert.That(Quaternion.Angle(recoveredMarkerLocal.rotation, markerLocal.rotation), Is.LessThan(0.001f));
        Assert.That(Vector3.Distance(originalFloor.position, recoveredFloor.position), Is.GreaterThan(1f));
    }

    [Test]
    public void ContentKeepsItsFloorLocalPoseWhenZoneIsCorrected()
    {
        var shiftedMrukFloor = new Pose(
            new Vector3(12f, 0f, -4f),
            Quaternion.Euler(0f, -32f, 0f));
        var markerCorrectedFloor = new Pose(
            new Vector3(1.5f, 0f, 3.2f),
            Quaternion.Euler(0f, 8f, 0f));
        var contentLocal = new Pose(
            new Vector3(-0.8f, 0.15f, 2.1f),
            Quaternion.Euler(0f, 115f, 0f));
        var spawnedContent = AagFiducialMarkerStore.Compose(
            shiftedMrukFloor,
            contentLocal);

        var capturedLocal = AagFiducialMarkerStore.RelativeTo(
            shiftedMrukFloor,
            spawnedContent);
        var correctedContent = AagFiducialMarkerStore.Compose(
            markerCorrectedFloor,
            capturedLocal);
        var expectedContent = AagFiducialMarkerStore.Compose(
            markerCorrectedFloor,
            contentLocal);

        Assert.That(
            Vector3.Distance(correctedContent.position, expectedContent.position),
            Is.LessThan(0.0001f));
        Assert.That(
            Quaternion.Angle(correctedContent.rotation, expectedContent.rotation),
            Is.LessThan(0.001f));
    }
}
