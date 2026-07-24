using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class ExperimentSpaceIsolationTests
{
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
    public void Fp2CatalogIsRegisteredButFailsClosedUntilUuidImport()
    {
        Assert.That(AagExperimentSpaceCatalog.TryGetFloorPlan("FP2", out var floorPlan), Is.True);
        Assert.That(floorPlan.RoomIds, Is.Empty);
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
}
