using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class AagRoom3AprilTagSceneSetupTests
{
    private const string ScenePath = "Assets/Scenes/MainTest_FP1.unity";
    private const string Fp2ScenePath = "Assets/Scenes/MainTest_FP2.unity";

    [Test]
    public void MainTestFp1_HasWiredArmedRoom3AlignmentSetup()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var preview = Object.FindFirstObjectByType<AagRoom3ReferencePreview>(
            FindObjectsInactive.Include);
        var reference = Object.FindFirstObjectByType<AagRoom3TagReference>(
            FindObjectsInactive.Include);
        var aligner = Object.FindFirstObjectByType<AagAprilTagTranslationAligner>(
            FindObjectsInactive.Include);
        var fixedOffset = Object.FindFirstObjectByType<AagFixedSpaceOffset>(
            FindObjectsInactive.Include);

        Assert.That(preview, Is.Not.Null);
        Assert.That(reference, Is.Not.Null);
        Assert.That(aligner, Is.Not.Null);
        Assert.That(fixedOffset, Is.Not.Null);
        Assert.That(reference.transform.parent.name, Is.EqualTo("MRUKAlignmentRoot"));
        Assert.That(reference.ExpectedRoomUuid, Is.EqualTo(AagRoom3TagReference.Room3Uuid));
        Assert.That(reference.ExpectedFloorAnchorUuid, Is.EqualTo(AagRoom3TagReference.Room3FloorAnchorUuid));
        Assert.That(aligner.Room3TagReference, Is.SameAs(reference));
        Assert.That(aligner.FixedSpaceOffset, Is.SameAs(fixedOffset));
        Assert.That(aligner.ExpectedTagId, Is.Zero);
        Assert.That(aligner.TagSizeMeters, Is.EqualTo(0.095f).Within(0.0001f));
        Assert.That(aligner.PreviewOnly, Is.False);
        Assert.That(aligner.AllowQuestControllerApply, Is.True);
        Assert.That(aligner.HorizontalOnly, Is.False);
        Assert.That(aligner.ApplyDetectedYawRotation, Is.False);
        Assert.That(fixedOffset.HorizontalOnly, Is.False);
        Assert.That(aligner.ContentAlongWallBaselineMeters, Is.EqualTo(-0.65f).Within(0.0001f));
        Assert.That(aligner.ContentAlongWallAdjustmentMeters, Is.EqualTo(0.15f).Within(0.0001f));
        Assert.That(aligner.ContentAlongWallBackMeters, Is.EqualTo(-0.50f).Within(0.0001f));
        Assert.That(aligner.ContentWallClearanceMeters, Is.EqualTo(0.25f).Within(0.0001f));
        Assert.That(
            Vector3.Distance(
                aligner.ContentFineTuneMeters,
                new Vector3(0.25f, 0f, -0.50f)),
            Is.LessThan(0.0001f));
        Assert.That(Mathf.Abs(aligner.ContentFineTuneMeters.y), Is.LessThan(0.0001f));

        var fixedOffsetSerialized = new SerializedObject(fixedOffset);
        Assert.That(
            fixedOffsetSerialized.FindProperty("sceneContentRoots").arraySize,
            Is.Zero,
            "The tag reference must not receive the experiment-only fine tune.");

        var cube = reference.transform.Find("Room3_TagReference_RedCube_10cm");
        Assert.That(cube, Is.Not.Null);
        Assert.That(Vector3.Distance(cube.localPosition, Vector3.forward * 0.08f), Is.LessThan(0.0001f));
        Assert.That(Vector3.Distance(cube.localScale, Vector3.one * 0.1f), Is.LessThan(0.0001f));
        Assert.That(cube.GetComponent<Collider>(), Is.Null);
        var renderer = cube.GetComponent<MeshRenderer>();
        Assert.That(renderer, Is.Not.Null);
        Assert.That(renderer.sharedMaterial, Is.Not.Null);
        Assert.That(renderer.sharedMaterial.color.r, Is.GreaterThan(0.95f));
        Assert.That(renderer.sharedMaterial.color.g, Is.LessThan(0.05f));
        Assert.That(renderer.sharedMaterial.color.b, Is.LessThan(0.05f));
    }

    [Test]
    public void MainTestFp2_HasFailClosedRoom8IdOneAlignmentSetup()
    {
        EditorSceneManager.OpenScene(Fp2ScenePath, OpenSceneMode.Single);

        var preview = Object.FindFirstObjectByType<AagFp2Room8ReferencePreview>(
            FindObjectsInactive.Include);
        var reference = Object.FindFirstObjectByType<AagRoom3TagReference>(
            FindObjectsInactive.Include);
        var aligner = Object.FindFirstObjectByType<AagAprilTagTranslationAligner>(
            FindObjectsInactive.Include);
        var fixedOffset = Object.FindFirstObjectByType<AagFixedSpaceOffset>(
            FindObjectsInactive.Include);
        var experimentMain = Object.FindFirstObjectByType<ExperimentMain>(
            FindObjectsInactive.Include);

        Assert.That(preview, Is.Not.Null);
        Assert.That(reference, Is.Not.Null);
        Assert.That(aligner, Is.Not.Null);
        Assert.That(fixedOffset, Is.Not.Null);
        Assert.That(experimentMain, Is.Not.Null);
        Assert.That(reference.ReferenceLabel, Is.EqualTo("FP2 Room8"));
        Assert.That(reference.ReferencePlacementConfirmed, Is.True);
        Assert.That(reference.UseBakedFloorPose, Is.True);
        Assert.That(
            AagFp2BakedSpace.TryGetFloor(
                System.Guid.Parse(reference.ExpectedFloorAnchorUuid),
                out _,
                out var bakedBoundary,
                out var bakedFailure),
            Is.True,
            bakedFailure);
        Assert.That(bakedBoundary, Has.Count.GreaterThanOrEqualTo(3));
        Assert.That(AagIncidentalRoomLocalStore.Fp2FloorHeightMeters, Is.EqualTo(0.20f));
        Assert.That(
            reference.ExpectedRoomUuid,
            Is.EqualTo("2d4f4c7d-9189-0198-a0ff-ecd07d843c6a"));
        Assert.That(
            reference.ExpectedFloorAnchorUuid,
            Is.EqualTo("fcd5cb7d-e3ad-844a-9a18-9a6a165124b3"));
        Assert.That(aligner.Room3TagReference, Is.SameAs(reference));
        Assert.That(aligner.FixedSpaceOffset, Is.SameAs(fixedOffset));
        Assert.That(aligner.ExpectedTagId, Is.EqualTo(1));
        Assert.That(aligner.TagSizeMeters, Is.EqualTo(0.095f).Within(0.0001f));
        Assert.That(aligner.AlignmentLabel, Is.EqualTo("FP2 ROOM8"));
        Assert.That(aligner.PreviewOnly, Is.False);
        Assert.That(aligner.AllowQuestControllerApply, Is.True);
        Assert.That(aligner.RequireAppliedAlignmentBeforeSession, Is.True);
        Assert.That(aligner.RecordDetectedYaw, Is.True);
        Assert.That(aligner.ApplyDetectedYawRotation, Is.True);
        Assert.That(aligner.MaximumStableYawJitterDegrees, Is.EqualTo(3f).Within(0.001f));
        Assert.That(aligner.ContentFineTuneMeters.sqrMagnitude, Is.LessThan(0.000001f));

        var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP2ExperimentConfig.asset");
        Assert.That(config, Is.Not.Null);
        Assert.That(config.startRoomId, Is.EqualTo("room8"));
        Assert.That(config.rooms, Has.Length.EqualTo(8));
        Assert.That(config.TryGetStartRoomUuid(out var startRoomUuid), Is.True);
        Assert.That(
            startRoomUuid.ToString(),
            Is.EqualTo("2d4f4c7d-9189-0198-a0ff-ecd07d843c6a"));

        var cube = reference.transform.Find("FP2_Room8_TagReference_RedCube_10cm");
        Assert.That(cube, Is.Not.Null);
        Assert.That(cube.GetComponent<Collider>(), Is.Null);
    }

    [Test]
    public void ReferenceFineTune_FollowsRoomYawWithoutChangingHeight()
    {
        var yaw = Quaternion.Euler(0f, 37f, 0f);
        var result = AagAprilTagTranslationAligner.ResolveHorizontalReferenceFineTune(
            yaw,
            -0.50f,
            0.25f);
        var expected = yaw * new Vector3(0.50f, 0f, 0.25f);

        Assert.That(Vector3.Distance(result, expected), Is.LessThan(0.0001f));
        Assert.That(Mathf.Abs(result.y), Is.LessThan(0.0001f));
    }

    [Test]
    public void StableTagYaw_ResolvesSmallCorrectionAndRejectsPoseFlipAmbiguity()
    {
        var expected = Quaternion.Euler(0f, 42f, 0f);
        var observed = new[]
        {
            Quaternion.Euler(0f, 224.0f, 0f),
            Quaternion.Euler(0f, 224.2f, 0f),
            Quaternion.Euler(0f, 223.8f, 0f),
        };

        Assert.That(
            AagAprilTagTranslationAligner.TryResolveStableYawCorrection(
                expected,
                observed,
                out var correction,
                out var yaw,
                out var jitter),
            Is.True);
        Assert.That(yaw, Is.EqualTo(2f).Within(0.01f));
        Assert.That(jitter, Is.LessThan(0.21f));
        Assert.That(
            Quaternion.Angle(correction, Quaternion.Euler(0f, 2f, 0f)),
            Is.LessThan(0.01f));
    }

    [Test]
    public void StableTagYaw_AcceptsLargeBakedFrameHeadingDifference()
    {
        var expected = Quaternion.Euler(0f, 12f, 0f);
        var observed = new[]
        {
            Quaternion.Euler(0f, -66.4f, 0f),
            Quaternion.Euler(0f, -66.2f, 0f),
            Quaternion.Euler(0f, -66.6f, 0f),
        };

        Assert.That(
            AagAprilTagTranslationAligner.TryResolveStableYawCorrection(
                expected,
                observed,
                out var correction,
                out var yaw,
                out var jitter),
            Is.True);
        Assert.That(yaw, Is.EqualTo(-78.4f).Within(0.01f));
        Assert.That(jitter, Is.LessThan(0.21f));
        Assert.That(
            Mathf.Abs(yaw),
            Is.LessThanOrEqualTo(
                AagAprilTagTranslationAligner.MaximumAcceptedYawCorrectionDegrees));
        Assert.That(
            Quaternion.Angle(correction, Quaternion.Euler(0f, -78.4f, 0f)),
            Is.LessThan(0.01f));
    }

    [Test]
    public void Room3Reference_FloorLocalPoseRoundTripsToSceneTransform()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var reference = Object.FindFirstObjectByType<AagRoom3TagReference>(
            FindObjectsInactive.Include);
        Assert.That(reference, Is.Not.Null);

        var exportFloorPose = new Pose(
            new Vector3(0.15373255f, -0.01663506f, 1.2934226f),
            new Quaternion(0.5104028f, -0.48937613f, -0.48937613f, -0.51040286f));
        var reconstructed = AagFiducialMarkerStore.Compose(
            exportFloorPose,
            new Pose(reference.FloorLocalPosition, reference.FloorLocalRotation));

        Assert.That(Vector3.Distance(reconstructed.position, reference.transform.position), Is.LessThan(0.0005f));
        Assert.That(Quaternion.Angle(reconstructed.rotation, reference.transform.rotation), Is.LessThan(0.05f));
    }

    [Test]
    public void FixedOffset_AppliesTagOffsetAndContentFineTuneToExperimentContent()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var aligner = Object.FindFirstObjectByType<AagAprilTagTranslationAligner>(
            FindObjectsInactive.Include);
        var fixedOffset = Object.FindFirstObjectByType<AagFixedSpaceOffset>(
            FindObjectsInactive.Include);
        Assert.That(aligner, Is.Not.Null);
        Assert.That(fixedOffset, Is.Not.Null);

        var runtimeReference = new Vector3(5.374f, 1.462f, -6.911f);
        var detectedTag = new Vector3(2.071f, 1.327f, 3.049f);
        var content = new GameObject("AAG Test Experiment Content");
        content.transform.position = new Vector3(1f, 2f, 3f);
        var baseline = content.transform.position;

        Assert.That(
            fixedOffset.TryRegisterContent(content.transform, "test:experiment-content", out var failure),
            Is.True,
            failure);
        var tagOffset = detectedTag - runtimeReference;
        var expectedContentOffset = tagOffset + aligner.ContentFineTuneMeters;
        fixedOffset.Configure(true, expectedContentOffset, false);
        fixedOffset.ApplyCorrection();

        Assert.That(
            Vector3.Distance(content.transform.position, baseline + expectedContentOffset),
            Is.LessThan(0.0005f));
        Object.DestroyImmediate(content);
    }
}
