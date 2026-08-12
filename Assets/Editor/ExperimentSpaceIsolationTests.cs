using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class ExperimentSpaceIsolationTests
{
    [System.Serializable]
    private sealed class TowerFloorLocalSeed
    {
        public string schemaVersion = string.Empty;
        public string floorPlanId = string.Empty;
        public TowerFloorLocalPlacement[] placements = System.Array.Empty<TowerFloorLocalPlacement>();
    }

    [System.Serializable]
    private sealed class TowerFloorLocalPlacement
    {
        public string towerId = string.Empty;
        public string roomUuid = string.Empty;
        public string floorAnchorUuid = string.Empty;
    }

    [SetUp]
    public void SetUp() => ExperimentSpaceRuntime.ResetToFp1();

    [TearDown]
    public void TearDown() => ExperimentSpaceRuntime.ResetToFp1();

    [Test]
    public void Fp1PreservesLegacyStorageContract()
    {
        Assert.That(ExperimentSpaceRuntime.SpaceId, Is.EqualTo("FP1"));
        Assert.That(ExperimentSpaceRuntime.LogFolderName, Is.EqualTo("FP1Logs"));
        Assert.That(ExperimentSpaceRuntime.PlayerPrefsCountKey, Is.EqualTo("numUuids"));
        Assert.That(ExperimentSpaceRuntime.PlayerPrefsUuidKey(2), Is.EqualTo("uuid2"));
        Assert.That(AagManualAnchorSetStore.ManifestFileName, Is.EqualTo("fp1_manual_anchor_sets.json"));
        Assert.That(Path.GetFileName(MrukRoomLocalPlacementStore.CatalogPath),
            Is.EqualTo("fp1_floor_anchor_local_placements.json"));
    }

    [Test]
    public void Fp2UsesIndependentStorageAndSharedPrefabFolders()
    {
        var config = ScriptableObject.CreateInstance<Fp1ExperimentConfig>();
        try
        {
            config.spaceId = "FP2";
            config.floorPlanId = "FP2";
            config.storageNamespace = "fp2";
            config.setIds = new[] { "FP2-S1", "FP2-S2", "FP2-S3" };
            config.assetSetIds = new[] { "FP1-S1", "FP1-S2", "FP1-S3" };
            ExperimentSpaceRuntime.Configure(config);

            Assert.That(ExperimentSpaceRuntime.LogFolderName, Is.EqualTo("FP2Logs"));
            Assert.That(ExperimentSpaceRuntime.PlayerPrefsCountKey, Is.EqualTo("AAG.FP2.numUuids"));
            Assert.That(ExperimentSpaceRuntime.PlayerPrefsUuidKey(2), Is.EqualTo("AAG.FP2.uuid.2"));
            Assert.That(AagManualAnchorSetStore.ManifestFileName, Is.EqualTo("fp2_manual_anchor_sets.json"));
            Assert.That(AagManualAnchorSetStore.FolderPath.Replace('\\', '/'),
                Does.EndWith("/AagManualAnchorSets/FP2"));
            Assert.That(Path.GetFileName(MrukRoomLocalPlacementStore.CatalogPath),
                Is.EqualTo("fp2_floor_anchor_local_placements.json"));
            Assert.That(AagIncidentalAnchorStore.ResourceFolderForSet("FP2-S2"),
                Is.EqualTo("IncidentalObjects/FP1-S2"));
        }
        finally
        {
            Object.DestroyImmediate(config);
        }
    }

    [Test]
    public void Fp2CatalogContainsConfirmedEightRoomScan()
    {
        Assert.That(AagExperimentSpaceCatalog.TryGetFloorPlan("FP2", out var floorPlan), Is.True);
        Assert.That(floorPlan.RoomIds, Has.Count.EqualTo(8));
        Assert.That(floorPlan.RoomIds, Does.Contain(AagExperimentSpaceCatalog.Fp2UnnamedRoomUuid));
        Assert.That(floorPlan.RoomIds, Does.Contain(AagExperimentSpaceCatalog.Fp2UnnamedRoom8Uuid));
        Assert.That(floorPlan.RoomIds, Is.Not.EquivalentTo(AagExperimentSpaceCatalog.Fp1.RoomIds));
        Assert.That(floorPlan.SetIds,
            Is.EqualTo(new[] { "FP2-S1", "FP2-S2", "FP2-S3" }));
    }

    [Test]
    public void Fp2AssetAndSceneAreSeparatedFromFp1()
    {
        var fp1 = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP1ExperimentConfig.asset");
        var fp2 = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP2ExperimentConfig.asset");

        Assert.That(fp1, Is.Not.Null);
        Assert.That(fp2, Is.Not.Null);
        Assert.That(fp1, Is.Not.SameAs(fp2));
        Assert.That(fp1.spaceId, Is.EqualTo("FP1"));
        Assert.That(fp2.spaceId, Is.EqualTo("FP2"));
        Assert.That(fp2.rooms, Is.Empty);
        Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(
            "Assets/Scenes/MainTest_FP2.unity"), Is.Not.Null);
        Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(
            "Assets/Scenes/RoomUUIDScan_FP2.unity"), Is.Not.Null);
    }

    [Test]
    public void Fp2BundledFieldPlacementsContainThreeSetsAndFourTowers()
    {
        var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP2ExperimentConfig.asset");
        Assert.That(config, Is.Not.Null);
        ExperimentSpaceRuntime.Configure(config);

        var stoneSeed = Resources.Load<TextAsset>("AAG/fp2_floor_anchor_local_placements");
        Assert.That(stoneSeed, Is.Not.Null);
        var stoneCatalog = JsonUtility.FromJson<MrukRoomLocalPlacementStore.Catalog>(
            stoneSeed.text.TrimStart('\uFEFF'));
        Assert.That(stoneCatalog, Is.Not.Null);
        Assert.That(stoneCatalog.sets, Has.Count.EqualTo(3));
        foreach (var set in stoneCatalog.sets)
            Assert.That(
                MrukRoomLocalPlacementStore.TryValidateSet(set, out var failure),
                Is.True,
                failure);

        var manualSeed = Resources.Load<TextAsset>("AAG/fp2_manual_anchor_sets");
        Assert.That(manualSeed, Is.Not.Null);
        var manualManifest = JsonUtility.FromJson<AagManualAnchorSetManifest>(
            manualSeed.text.TrimStart('\uFEFF'));
        Assert.That(manualManifest.floor_plan_id, Is.EqualTo("FP2"));
        Assert.That(manualManifest.sets, Has.Count.EqualTo(3));
        Assert.That(manualManifest.sets, Has.All.Matches<AagManualAnchorSetRecord>(set =>
            set.locked && set.anchors != null && set.anchors.Count == 12));

        var towerAnchorSeed = Resources.Load<TextAsset>("AAG/fp2_fixed_tower_anchors");
        Assert.That(towerAnchorSeed, Is.Not.Null);
        var towerManifest = JsonUtility.FromJson<AagFixedTowerAnchorManifest>(
            towerAnchorSeed.text.TrimStart('\uFEFF'));
        Assert.That(towerManifest.floor_plan_id, Is.EqualTo("FP2"));
        Assert.That(towerManifest.towers, Has.Count.EqualTo(4));

        var towerFloorSeed = Resources.Load<TextAsset>("AAG/fp2_fixed_tower_floor_local");
        Assert.That(towerFloorSeed, Is.Not.Null);
        var towerFloorCatalog = JsonUtility.FromJson<TowerFloorLocalSeed>(
            towerFloorSeed.text.TrimStart('\uFEFF'));
        Assert.That(towerFloorCatalog.schemaVersion,
            Is.EqualTo(AagFixedTowerRoomLocalStore.SchemaVersion));
        Assert.That(towerFloorCatalog.floorPlanId, Is.EqualTo("FP2"));
        Assert.That(towerFloorCatalog.placements, Has.Length.EqualTo(4));
        Assert.That(towerFloorCatalog.placements, Has.All.Matches<TowerFloorLocalPlacement>(placement =>
            !string.IsNullOrWhiteSpace(placement.towerId)
            && System.Guid.TryParse(placement.roomUuid, out _)
            && System.Guid.TryParse(placement.floorAnchorUuid, out _)));

        var incidentalAnchorSeed = Resources.Load<TextAsset>("AAG/fp2_incidental_anchor_sets");
        Assert.That(incidentalAnchorSeed, Is.Not.Null);
        var incidentalManifest = JsonUtility.FromJson<AagIncidentalAnchorManifest>(
            incidentalAnchorSeed.text.TrimStart('\uFEFF'));
        Assert.That(incidentalManifest.floor_plan_id, Is.EqualTo("FP2"));
        Assert.That(incidentalManifest.sets, Has.Count.EqualTo(3));
        Assert.That(incidentalManifest.sets, Has.All.Matches<AagIncidentalAnchorSet>(set =>
            set.objects != null
            && set.objects.Count == AagIncidentalAnchorStore.Fp2ObjectsPerSet
            && set.objects.Select(value => value.object_id).Distinct().Count()
                == AagIncidentalAnchorStore.Fp2ObjectsPerSet));

        var incidentalFloorSeed = Resources.Load<TextAsset>("AAG/fp2_incidental_floor_local");
        Assert.That(incidentalFloorSeed, Is.Not.Null);
        var incidentalFloorText = incidentalFloorSeed.text.TrimStart('\uFEFF');
        foreach (var room in config.rooms)
            Assert.That(incidentalFloorText, Does.Contain(room.roomUuid));
    }
}
