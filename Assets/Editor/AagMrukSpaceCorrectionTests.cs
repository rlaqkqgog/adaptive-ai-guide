using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class AagMrukSpaceCorrectionTests
{
    [SetUp]
    public void SetUp()
    {
        AagMrukSpaceCorrection.Reset();
        var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP2ExperimentConfig.asset");
        Assert.That(config, Is.Not.Null);
        ExperimentSpaceRuntime.Configure(config);
    }

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

    [Test]
    public void Fp2Room7_NoSpawnZoneBlocksBathroomEndButKeepsCorridorPoint()
    {
        var room7 = Guid.Parse("96a223f3-baf3-7044-2958-6f2468b35c72");

        Assert.That(
            ExperimentMain.IsFp2ExplicitNoSpawnPoint(
                room7,
                new Vector2(3.75f, 0f),
                out var blockedZone),
            Is.True);
        Assert.That(blockedZone, Is.EqualTo("room7_mens_restroom_hidden_end"));
        Assert.That(
            ExperimentMain.IsFp2ExplicitNoSpawnPoint(
                room7,
                new Vector2(0.657f, -0.064f),
                out var hiddenWallZone),
            Is.True);
        Assert.That(hiddenWallZone, Is.EqualTo("room7_observed_hidden_wall_pocket"));
        Assert.That(
            ExperimentMain.IsFp2ExplicitNoSpawnPoint(
                room7,
                new Vector2(2.65f, 0.10f),
                out var allowedZone),
            Is.False);
        Assert.That(allowedZone, Is.Empty);
    }

    [Test]
    public void Fp2Room7_WallSafetyKeepsProblemStoneNearItsAuthoredPosition()
    {
        var room7 = Guid.Parse("96a223f3-baf3-7044-2958-6f2468b35c72");
        Assert.That(
            AagFp2BakedSpace.TryGetRoomFloor(
                room7,
                out var floorPose,
                out var boundary,
                out var floorFailure),
            Is.True,
            floorFailure);
        var method = typeof(ExperimentMain).GetMethod(
            "TryResolveFp2WallSafeFloorPoint",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        var authored = new Vector2(-4.2199798f, 0.2687663f);
        var arguments = new object[]
        {
            room7,
            floorPose,
            boundary,
            authored,
            Vector2.zero,
            0f,
            string.Empty,
        };

        Assert.That((bool)method.Invoke(null, arguments), Is.True, arguments[6] as string);
        var resolved = (Vector2)arguments[4];
        Assert.That(Vector2.Distance(authored, resolved), Is.LessThan(1f));
        Assert.That(
            ExperimentMain.IsFp2ExplicitNoSpawnPoint(room7, resolved, out _),
            Is.False);
    }

    [Test]
    public void Fp2WalkablePath_CoversEveryExperimentRoom()
    {
        var invalidWaypoints = new System.Collections.Generic.List<string>();
        var rooms = new[]
        {
            "0e4e8223-3c13-735b-a552-4acf2ba915a7",
            "d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b",
            "28e81069-81b3-b60a-0166-50599f88ce42",
            "133adc09-ce31-302f-1b53-788b59deeb4f",
            "7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b",
            "e2e79df2-facb-0150-67b4-a43dfaad9218",
            "96a223f3-baf3-7044-2958-6f2468b35c72",
            "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a",
        };

        foreach (var room in rooms)
        {
            var roomUuid = Guid.Parse(room);
            Assert.That(
                AagFp2WalkablePath.TryGetWaypoints(roomUuid, out var waypoints),
                Is.True,
                room);
            Assert.That(waypoints.Count, Is.GreaterThan(0), room);
            Assert.That(
                AagFp2BakedSpace.TryGetRoomFloor(
                    roomUuid, out _, out var boundary, out var floorFailure),
                Is.True,
                floorFailure);
            var polygonMethod = typeof(ExperimentMain).GetMethod(
                "IsPointInPolygon",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(polygonMethod, Is.Not.Null);
            foreach (var waypoint in waypoints)
            {
                if (!(bool)polygonMethod.Invoke(null, new object[] { waypoint, boundary }))
                    invalidWaypoints.Add($"outside:{room}:{waypoint}");
                if (ExperimentMain.IsFp2ExplicitNoSpawnPoint(
                        roomUuid, waypoint, out var zoneId))
                    invalidWaypoints.Add($"blocked:{room}:{waypoint}:{zoneId}");
            }
        }
        Assert.That(invalidWaypoints, Is.Empty, string.Join("|", invalidWaypoints));
    }

    [TestCase("133adc09-ce31-302f-1b53-788b59deeb4f", -4.5158768f, -4.3706164f, "red_3")]
    [TestCase("7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b", -1.022497f, -0.7783515f, "yellow_3")]
    [TestCase("e2e79df2-facb-0150-67b4-a43dfaad9218", -1.474131f, -0.002032f, "incidental_06_01_drill")]
    public void Fp2WalkablePath_MovesObservedProblemObjectsOntoParticipantRoute(
        string roomText,
        float localX,
        float localY,
        string objectId)
    {
        var authored = new Vector2(localX, localY);
        Assert.That(
            AagFp2WalkablePath.TryConstrain(
                Guid.Parse(roomText),
                authored,
                out var resolved,
                out var distanceToPath,
                out var movedMeters),
            Is.True,
            objectId);
        Assert.That(distanceToPath, Is.GreaterThan(AagFp2WalkablePath.MaximumDistanceMeters), objectId);
        Assert.That(movedMeters, Is.EqualTo(distanceToPath).Within(0.0001f), objectId);
        Assert.That(
            AagFp2WalkablePath.TryFindNearest(
                Guid.Parse(roomText), resolved, out _, out var resolvedDistance),
            Is.True,
            objectId);
        Assert.That(resolvedDistance, Is.LessThan(0.0001f), objectId);
    }

    [Test]
    public void Fp2WalkablePath_KeepsAdjustedBlueTowerWhenAlreadyNearRoute()
    {
        var room7 = Guid.Parse("96a223f3-baf3-7044-2958-6f2468b35c72");
        var adjustedTower = new Vector2(2.65f, 0.10f);

        Assert.That(
            AagFp2WalkablePath.TryConstrain(
                room7,
                adjustedTower,
                out var resolved,
                out var distanceToPath,
                out var movedMeters),
            Is.True);
        Assert.That(distanceToPath, Is.LessThanOrEqualTo(AagFp2WalkablePath.MaximumDistanceMeters));
        Assert.That(movedMeters, Is.Zero);
        Assert.That(resolved, Is.EqualTo(adjustedTower));
    }
}
