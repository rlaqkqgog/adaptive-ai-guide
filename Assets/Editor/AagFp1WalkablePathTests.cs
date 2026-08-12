using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class AagFp1WalkablePathTests
{
    [SetUp]
    public void SetUp()
    {
        var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP1ExperimentConfig.asset");
        Assert.That(config, Is.Not.Null);
        ExperimentSpaceRuntime.Configure(config);
    }

    [Test]
    public void Fp1BakedSpaceContainsAllNineRoomsAndRoom6TagFloor()
    {
        foreach (var roomUuid in AagExperimentSpaceCatalog.Fp1.RoomIds)
        {
            Assert.That(
                AagFp2BakedSpace.TryGetRoomFloor(
                    roomUuid, out _, out var boundary, out var failure),
                Is.True,
                failure);
            Assert.That(boundary.Count, Is.GreaterThanOrEqualTo(3));
        }
        Assert.That(
            AagFp2BakedSpace.TryGetFloor(
                Guid.Parse(AagRoom3TagReference.Room6FloorAnchorUuid),
                out _, out _, out var room6Failure),
            Is.True,
            room6Failure);
    }

    [Test]
    public void EveryFp1RoomHasAuditedWaypoints()
    {
        foreach (var roomUuid in AagExperimentSpaceCatalog.Fp1.RoomIds)
        {
            Assert.That(
                AagFp1WalkablePath.TryGetWaypoints(roomUuid, out var waypoints),
                Is.True,
                $"Missing FP1 walkable path for {roomUuid}");
            Assert.That(waypoints.Count, Is.GreaterThanOrEqualTo(2));
            foreach (var point in waypoints)
            {
                Assert.That(float.IsNaN(point.x) || float.IsInfinity(point.x), Is.False);
                Assert.That(float.IsNaN(point.y) || float.IsInfinity(point.y), Is.False);
            }
            Assert.That(
                AagFp1WalkablePath.TryFindNearest(
                    roomUuid, waypoints[0], out _, out _),
                Is.True,
                $"No wall-safe FP1 waypoint remains for {roomUuid}");
        }
    }

    [Test]
    public void EveryFp1RoomHasCentralPointSafeInBothScans()
    {
        foreach (var roomUuid in AagExperimentSpaceCatalog.Fp1.RoomIds)
        {
            Assert.That(
                AagFp1WalkablePath.TryGetCentralSafePoint(
                    roomUuid, out var center, out var clearance),
                Is.True,
                roomUuid.ToString());
            Assert.That(
                clearance,
                Is.GreaterThanOrEqualTo(AagFp1WalkablePath.MinimumWallClearanceMeters),
                $"{roomUuid}: {center}");
            Assert.That(
                AagFp1SupplementalWallMap.TryIsSafe(
                    roomUuid,
                    center,
                    AagFp1WalkablePath.SupplementalWallClearanceMeters,
                    out var safe,
                    out var reason),
                Is.True,
                roomUuid.ToString());
            Assert.That(safe, Is.True, $"{roomUuid}: {reason}");
        }
    }

    [Test]
    public void FieldReportedOverridesAreLimitedToSixKnownPlacements()
    {
        Assert.That(AagFp1FieldSafetyOverrides.TargetCount, Is.EqualTo(4));
        Assert.That(AagFp1FieldSafetyOverrides.IncidentalCount, Is.EqualTo(2));

        Assert.That(
            AagFp1FieldSafetyOverrides.TryGetTargetRoom(
                "FP1-S1", "yellow_3", out var hall21),
            Is.True);
        Assert.That(
            hall21,
            Is.EqualTo(Guid.Parse("880b5e63-6438-a8d5-c156-2ddffc48a6d4")));
        Assert.That(
            AagFp1FieldSafetyOverrides.TryGetTargetRoom(
                "FP1-S3", "yellow_2", out var hall22),
            Is.True);
        Assert.That(
            hall22,
            Is.EqualTo(Guid.Parse("ce00a9e3-c3dc-48cd-a503-e928584055f2")));
        Assert.That(
            AagFp1FieldSafetyOverrides.TryGetIncidentalRoom(
                "incidental_02_02_jar", out var room22),
            Is.True);
        Assert.That(
            room22,
            Is.EqualTo(Guid.Parse("ad306342-a794-cffd-be87-d9aea02c5823")));
        Assert.That(
            AagFp1FieldSafetyOverrides.TryGetIncidentalRoom(
                "incidental_02_02_hairDryer", out var hall11),
            Is.True);
        Assert.That(
            hall11,
            Is.EqualTo(Guid.Parse("7e4d3e1f-3247-602b-3822-c21df384e947")));
        Assert.That(
            AagFp1FieldSafetyOverrides.TryGetTargetRoom(
                "FP1-S2", "yellow_2", out _),
            Is.False);
    }

    [Test]
    public void SupplementalRescanMapsAllNineOriginalRoomsToProvidedNames()
    {
        var expected = new Dictionary<string, (string newRoomId, string newName)>
        {
            ["98332012-20ba-e0ba-78ec-8440113270cd"] =
                ("3bb04ce0-d25b-0bc7-9b21-d6385158a0ac", "이름없는 룸"),
            ["5faa1907-d2e2-7605-7b01-5149a34a4c6d"] =
                ("0239a778-ea89-55d5-8cf1-26b7cdc58490", "이름없는 룸 3"),
            ["ad306342-a794-cffd-be87-d9aea02c5823"] =
                ("ad2e0299-6afb-aff2-1acc-7eb24a4d6513", "이름없는 룸 4"),
            ["0d537c33-3e47-2606-3ea9-897c2bc9f1ce"] =
                ("e5da934c-8d63-05d5-ca0b-48f6ca01ab94", "이름없는 룸 6"),
            ["7e4d3e1f-3247-602b-3822-c21df384e947"] =
                ("602041c1-5031-cec0-5b52-fb36da9748d2", "이름없는 룸 2"),
            ["b4131884-79b1-7db8-5529-4875ea40bdd5"] =
                ("241fca56-f1c0-7905-8f73-105a18c300bd", "이름없는 룸 5"),
            ["7423d1c6-d1e7-3316-6714-6a9b282a396e"] =
                ("f7977287-8ef5-ad3e-6413-a48998cef936", "거실"),
            ["880b5e63-6438-a8d5-c156-2ddffc48a6d4"] =
                ("3e654fbe-c458-dac1-c25d-c2fc0e5ffaa1", "이름없는 룸 7"),
            ["ce00a9e3-c3dc-48cd-a503-e928584055f2"] =
                ("6343e8d6-2e59-e74f-7eb8-ad1347750774", "이름없는 룸 8"),
        };

        Assert.That(AagFp1SupplementalWallMap.LoadFailure, Is.Empty);
        Assert.That(AagFp1SupplementalWallMap.RoomCount, Is.EqualTo(9));
        var mappedRooms = new HashSet<Guid>();
        foreach (var pair in expected)
        {
            Assert.That(
                AagFp1SupplementalWallMap.TryGetMapping(
                    Guid.Parse(pair.Key),
                    out var newRoomUuid,
                    out var newFloorUuid,
                    out _,
                    out var newName),
                Is.True,
                pair.Key);
            Assert.That(newRoomUuid, Is.EqualTo(Guid.Parse(pair.Value.newRoomId)));
            Assert.That(newFloorUuid, Is.Not.EqualTo(Guid.Empty));
            Assert.That(newName, Is.EqualTo(pair.Value.newName));
            Assert.That(mappedRooms.Add(newRoomUuid), Is.True, newRoomUuid.ToString());
        }
    }

    [TestCase("0d537c33-3e47-2606-3ea9-897c2bc9f1ce", -3.293014f, 2.085275f,
        TestName = "Room3Session1PillarPlacementIsMoved")]
    [TestCase("ce00a9e3-c3dc-48cd-a503-e928584055f2", 2.723064f, 2.774896f,
        TestName = "Room8Session3RightWallPlacementIsMoved")]
    [TestCase("7e4d3e1f-3247-602b-3822-c21df384e947", -3.443050f, 1.040581f,
        TestName = "Hall11TowerAtRoom1BoundaryIsMoved")]
    public void ReportedRescanWallCasesAreMovedToSupplementalSafePath(
        string roomUuidText,
        float localX,
        float localY)
    {
        var roomUuid = Guid.Parse(roomUuidText);
        var original = new Vector2(localX, localY);
        Assert.That(
            AagFp1SupplementalWallMap.TryIsSafe(
                roomUuid,
                original,
                AagFp1WalkablePath.SupplementalWallClearanceMeters,
                out var originalSafe,
                out _),
            Is.True);
        Assert.That(originalSafe, Is.False);

        Assert.That(
            AagFp1WalkablePath.TryConstrain(
                roomUuid,
                original,
                out var resolved,
                out _,
                out var moved),
            Is.True);
        Assert.That(moved, Is.GreaterThan(0.01f));
        Assert.That(moved, Is.LessThanOrEqualTo(0.60f));
        Assert.That(
            AagFp1SupplementalWallMap.TryIsSafe(
                roomUuid,
                resolved,
                AagFp1WalkablePath.SupplementalWallClearanceMeters,
                out var resolvedSafe,
                out var reason),
            Is.True);
        Assert.That(resolvedSafe, Is.True, reason);
    }

    [Test]
    public void EveryAuthoredTargetAndTowerResolvesInsideRescanWallMargin()
    {
        var seed = Resources.Load<TextAsset>("AAG/fp1_floor_anchor_local_placements");
        Assert.That(seed, Is.Not.Null);
        var catalog = JsonUtility.FromJson<MrukRoomLocalPlacementStore.Catalog>(seed.text);
        Assert.That(catalog, Is.Not.Null);
        Assert.That(catalog.sets, Has.Count.EqualTo(3));

        foreach (var set in catalog.sets)
        foreach (var placement in set.placements)
        {
            var roomUuid = Guid.Parse(placement.roomUuid);
            Assert.That(
                AagFp1WalkablePath.TryConstrain(
                    roomUuid,
                    new Vector2(placement.localX, placement.localY),
                    out var resolved,
                    out _,
                    out _),
                Is.True,
                $"{set.setId}/{placement.objectId}");
            Assert.That(
                AagFp1SupplementalWallMap.TryIsSafe(
                    roomUuid,
                    resolved,
                    AagFp1WalkablePath.SupplementalWallClearanceMeters,
                    out var safe,
                    out var reason),
                Is.True,
                $"{set.setId}/{placement.objectId}");
            Assert.That(safe, Is.True, $"{set.setId}/{placement.objectId}: {reason}");
        }

        for (var towerIndex = 1; towerIndex <= 4; towerIndex++)
        {
            var towerId = $"Tower-{towerIndex}";
            Assert.That(AagFixedTowerRoomLocalCatalog.TryGet(towerId, out var tower), Is.True);
            Assert.That(
                AagFp1WalkablePath.TryConstrain(
                    tower.RoomUuid,
                    new Vector2(tower.FloorLocalPosition.x, tower.FloorLocalPosition.y),
                    out var resolved,
                    out _,
                    out _),
                Is.True,
                towerId);
            Assert.That(
                AagFp1SupplementalWallMap.TryIsSafe(
                    tower.RoomUuid,
                    resolved,
                    AagFp1WalkablePath.SupplementalWallClearanceMeters,
                    out var safe,
                    out var reason),
                Is.True,
                towerId);
            Assert.That(safe, Is.True, $"{towerId}: {reason}");
        }
    }

    [Test]
    public void PointInsideMaximumDistanceIsNotMoved()
    {
        var roomUuid = AagExperimentSpaceCatalog.Fp1Room3Uuid;
        Assert.That(AagFp1WalkablePath.TryGetWaypoints(roomUuid, out var waypoints), Is.True);
        Assert.That(
            AagFp1WalkablePath.TryFindNearest(
                roomUuid, waypoints[0], out var original, out _),
            Is.True);

        Assert.That(
            AagFp1WalkablePath.TryConstrain(
                roomUuid,
                original,
                out var resolved,
                out var distance,
                out var moved),
            Is.True);
        Assert.That(distance, Is.LessThanOrEqualTo(AagFp1WalkablePath.MaximumDistanceMeters));
        Assert.That(resolved, Is.EqualTo(original));
        Assert.That(moved, Is.Zero);
    }

    [Test]
    public void FarPointIsProjectedToAuditedPath()
    {
        var roomUuid = AagExperimentSpaceCatalog.Fp1Room3Uuid;
        var original = new Vector2(100f, -100f);

        Assert.That(
            AagFp1WalkablePath.TryConstrain(
                roomUuid,
                original,
                out var resolved,
                out var distance,
                out var moved),
            Is.True);
        Assert.That(distance, Is.GreaterThan(AagFp1WalkablePath.MaximumDistanceMeters));
        Assert.That(moved, Is.EqualTo(distance).Within(0.0001f));
        Assert.That(resolved, Is.Not.EqualTo(original));
        Assert.That(
            AagFp1WalkablePath.TryFindNearest(roomUuid, resolved, out _, out var finalDistance),
            Is.True);
        Assert.That(finalDistance, Is.LessThan(0.0001f));
    }

    [Test]
    public void UnknownRoomIsRejected()
    {
        Assert.That(
            AagFp1WalkablePath.TryConstrain(
                Guid.NewGuid(),
                Vector2.zero,
                out _,
                out _,
                out _),
            Is.False);
    }
}
