using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class MrukRoomLocalPlacementStoreTests
{
    [Test]
    public void BundledPostRescanCatalog_HasThreeValidSetsAndCurrentSchema()
    {
        var seed = Resources.Load<TextAsset>("AAG/fp1_floor_anchor_local_placements");
        Assert.That(seed, Is.Not.Null);
        var catalog = JsonUtility.FromJson<MrukRoomLocalPlacementStore.Catalog>(seed.text);
        Assert.That(catalog, Is.Not.Null);
        Assert.That(catalog.schemaVersion, Is.EqualTo(MrukRoomLocalPlacementStore.SchemaVersion));
        Assert.That(catalog.sets, Has.Count.EqualTo(3));
        foreach (var set in catalog.sets)
            Assert.That(MrukRoomLocalPlacementStore.TryValidateSet(set, out var failure), Is.True, failure);
    }

    [Test]
    public void FloorAnchorPose_RoundTripsAcrossRelocalization()
    {
        var source = new GameObject("source floor");
        var relocalized = new GameObject("relocalized floor");
        try
        {
            source.transform.SetPositionAndRotation(
                new Vector3(4.2f, -0.1f, 8.4f), Quaternion.Euler(2f, 37f, -1f));
            relocalized.transform.SetPositionAndRotation(
                new Vector3(-12f, 0.3f, 3.5f), Quaternion.Euler(-1f, 128f, 2f));
            var sourceWorldPosition = source.transform.TransformPoint(new Vector3(1.5f, -2f, 0.8f));
            var sourceWorldRotation = source.transform.rotation * Quaternion.Euler(15f, 22f, 7f);

            MrukRoomLocalPlacementStore.ToLocalPose(
                source.transform, sourceWorldPosition, sourceWorldRotation,
                out var localPosition, out var localRotation);
            MrukRoomLocalPlacementStore.ToWorldPose(
                relocalized.transform, localPosition, localRotation,
                out var recoveredPosition, out var recoveredRotation);

            Assert.That(Vector3.Distance(
                recoveredPosition, relocalized.transform.TransformPoint(localPosition)), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(
                recoveredRotation, relocalized.transform.rotation * localRotation), Is.LessThan(0.001f));
        }
        finally
        {
            Object.DestroyImmediate(source);
            Object.DestroyImmediate(relocalized);
        }
    }

    [Test]
    public void ValidateSet_RejectsDuplicateObjectIds()
    {
        var record = new MrukRoomLocalPlacementStore.SetRecord
        {
            setId = "FP1-S1",
            placements = new List<MrukRoomLocalPlacementStore.Placement>(),
        };
        for (var index = 0; index < 12; index++)
        {
            record.placements.Add(new MrukRoomLocalPlacementStore.Placement
            {
                objectId = index < 2 ? "red_1" : $"object_{index}",
                color = "Red",
                roomUuid = "5faa1907-d2e2-7605-7b01-5149a34a4c6d",
                floorAnchorUuid = "a285cf0f-0955-7828-6885-67fcd03d7db6",
                localRotationW = 1f,
            });
        }

        Assert.That(MrukRoomLocalPlacementStore.TryValidateSet(record, out var failure), Is.False);
        StringAssert.Contains("duplicate", failure);
    }

    [Test]
    public void ValidateSet_RejectsCorruptZeroQuaternion()
    {
        var record = CreateValidSet();
        record.placements[4].localRotationW = 0f;

        Assert.That(MrukRoomLocalPlacementStore.TryValidateSet(record, out var failure), Is.False);
        StringAssert.Contains(record.placements[4].objectId, failure);
    }

    private static MrukRoomLocalPlacementStore.SetRecord CreateValidSet()
    {
        var record = new MrukRoomLocalPlacementStore.SetRecord
        {
            setId = "FP1-S1",
            placements = new List<MrukRoomLocalPlacementStore.Placement>(),
        };
        for (var index = 0; index < 12; index++)
        {
            record.placements.Add(new MrukRoomLocalPlacementStore.Placement
            {
                objectId = $"object_{index}",
                color = "Red",
                roomUuid = "5faa1907-d2e2-7605-7b01-5149a34a4c6d",
                floorAnchorUuid = "a285cf0f-0955-7828-6885-67fcd03d7db6",
                localRotationW = 1f,
            });
        }
        return record;
    }
}
