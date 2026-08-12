using System;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AagCoreAndroidBuild
{
    private const string Fp1ScenePath = "Assets/Scenes/MainTest_FP1.unity";
    private const string Fp2ScenePath = "Assets/Scenes/MainTest_FP2.unity";

    [MenuItem("AAG/Build Android APK")]
    public static void BuildFromCommandLine()
    {
        const string outputPath = "Builds/aag_core_loader.apk";
        var scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
            throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");

        Build(scenes, outputPath, "Core");
    }

    [MenuItem("AAG/Build FP2 Android APK")]
    public static void BuildFp2AndroidApk()
    {
        ValidateFp2AprilTagScene();
        Build(new[] { Fp2ScenePath }, "Builds/aag_fp2.apk", "FP2");
    }

    [MenuItem("AAG/Build FP1 Android APK")]
    public static void BuildFp1AndroidApk()
    {
        ValidateFp1AprilTagScene();
        Build(new[] { Fp1ScenePath }, "Builds/aag_fp1.apk", "FP1");
    }

    [MenuItem("AAG/Validate FP1 AprilTag Scene")]
    public static void ValidateFp1AprilTagScene()
    {
        var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP1ExperimentConfig.asset");
        if (config == null) throw new InvalidOperationException("FP1 config is missing.");
        ExperimentSpaceRuntime.Configure(config);

        var scene = EditorSceneManager.OpenScene(Fp1ScenePath, OpenSceneMode.Single);
        var aligner = UnityEngine.Object.FindFirstObjectByType<AagAprilTagTranslationAligner>(
            FindObjectsInactive.Include);
        var reference = UnityEngine.Object.FindFirstObjectByType<AagRoom3TagReference>(
            FindObjectsInactive.Include);
        var offset = UnityEngine.Object.FindFirstObjectByType<AagFixedSpaceOffset>(
            FindObjectsInactive.Include);
        var mruk = UnityEngine.Object.FindFirstObjectByType<MRUK>(FindObjectsInactive.Include);
        if (aligner == null || reference == null || offset == null || mruk == null)
            throw new InvalidOperationException("FP1 AprilTag scene wiring is incomplete.");
        if (mruk.SceneSettings.DataSource != MRUK.SceneDataSource.Device
            || !mruk.SceneSettings.LoadSceneOnStartup
            || !mruk.EnableWorldLock)
            throw new InvalidOperationException(
                "FP1 baked-tag mode still requires device MRUK startup and World Lock.");
        if (aligner.ExpectedTagId != 0
            || Mathf.Abs(aligner.TagSizeMeters - 0.095f) > 0.0001f)
            throw new InvalidOperationException("FP1 must use tagStandard41h12 ID 0 at 0.095 m.");
        if (!string.Equals(reference.ExpectedRoomUuid, AagRoom3TagReference.Room6Uuid,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reference.ExpectedFloorAnchorUuid,
                AagRoom3TagReference.Room6FloorAnchorUuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FP1 tag reference must be the confirmed Room6 floor pose.");
        if (!reference.ReferencePlacementConfirmed || !reference.UseBakedFloorPose)
            throw new InvalidOperationException("FP1 Room6 baked tag reference is not confirmed.");
        var bakedFailure = "reference_floor_uuid_invalid";
        if (!Guid.TryParse(reference.ExpectedFloorAnchorUuid, out var floorUuid)
            || !AagFp2BakedSpace.TryGetFloor(
                floorUuid, out _, out var boundary, out bakedFailure)
            || boundary == null || boundary.Count < 3)
            throw new InvalidOperationException(
                $"FP1 baked reference is invalid: {bakedFailure ?? "floor_boundary_missing"}.");
        if (aligner.PreviewOnly || !aligner.AllowQuestControllerApply
            || !aligner.RequireAppliedAlignmentBeforeSession
            || !aligner.RecordDetectedYaw || !aligner.ApplyDetectedYawRotation
            || aligner.MaximumStableYawJitterDegrees > 3f)
            throw new InvalidOperationException("FP1 rigid AprilTag safety gates are not armed.");
        if (aligner.ContentFineTuneMeters.sqrMagnitude > 0.000001f)
            throw new InvalidOperationException(
                "FP1 baked-space mode must not reuse the former Room3 content fine tune.");
        Debug.Log(
            $"[AAG FP1 Build] Validation passed: scene={scene.path}; tag=standard41h12/0; "
            + $"reference=Room6/{reference.ExpectedRoomUuid}; rooms={config.rooms.Length}; "
            + "mode=bundled_baked_mruk+live_wall_veto+rigid_apriltag.");
    }

    [MenuItem("AAG/Validate FP2 AprilTag Scene")]
    public static void ValidateFp2AprilTagScene()
    {
        var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
            "Assets/Experiment/FP2ExperimentConfig.asset");
        if (config == null) throw new InvalidOperationException("FP2 config is missing.");
        ExperimentSpaceRuntime.Configure(config);
        var scene = EditorSceneManager.OpenScene(Fp2ScenePath, OpenSceneMode.Single);
        var aligners = UnityEngine.Object.FindObjectsByType<AagAprilTagTranslationAligner>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        var references = UnityEngine.Object.FindObjectsByType<AagRoom3TagReference>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        var offsets = UnityEngine.Object.FindObjectsByType<AagFixedSpaceOffset>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        var mrukInstances = UnityEngine.Object.FindObjectsByType<MRUK>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        var alignmentOverlays = UnityEngine.Object.FindObjectsByType<AagFp2SpaceAlignmentOverlay>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        var placementAuthoring = UnityEngine.Object.FindObjectsByType<AagFp2StonePlacementAuthoring>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        if (aligners.Length != 1 || references.Length != 1 || offsets.Length != 1)
            throw new InvalidOperationException(
                $"FP2 AprilTag setup count mismatch: aligners={aligners.Length}, "
                + $"references={references.Length}, offsets={offsets.Length}.");
        if (mrukInstances.Length != 1)
            throw new InvalidOperationException($"FP2 requires exactly one MRUK, found {mrukInstances.Length}.");

        var mruk = mrukInstances[0];
        if (mruk.SceneSettings.DataSource != MRUK.SceneDataSource.Device
            || !mruk.SceneSettings.LoadSceneOnStartup)
            throw new InvalidOperationException(
                "FP2 hybrid mode must load the Quest device MRUK scene on startup.");
        if (!mruk.EnableWorldLock)
            throw new InvalidOperationException(
                "FP2 hybrid mode must keep MRUK World Lock enabled.");

        var aligner = aligners[0];
        var reference = references[0];
        if (aligner.ExpectedTagId != 1)
            throw new InvalidOperationException(
                $"FP2 must detect tagStandard41h12 ID 1, found {aligner.ExpectedTagId}.");
        if (Mathf.Abs(aligner.TagSizeMeters - 0.095f) > 0.0001f)
            throw new InvalidOperationException(
                $"FP2 AprilTag size must be 0.095 m, found {aligner.TagSizeMeters:F4} m.");
        if (!string.Equals(
                reference.ExpectedRoomUuid,
                "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"FP2 reference must belong to Room8, found {reference.ExpectedRoomUuid}.");
        if (!string.Equals(
                reference.ExpectedFloorAnchorUuid,
                "fcd5cb7d-e3ad-844a-9a18-9a6a165124b3",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"FP2 Room8 floor anchor mismatch: {reference.ExpectedFloorAnchorUuid}.");
        if (!reference.ReferencePlacementConfirmed)
            throw new InvalidOperationException(
                "FP2 Room8 tag reference is not confirmed. Run the FP2 setup after installing the tag.");
        if (!reference.UseBakedFloorPose)
            throw new InvalidOperationException(
                "FP2 Room8 tag must align the bundled baked space, not live per-room MRUK transforms.");
        var bakedSpaceFailure = "reference_floor_uuid_invalid";
        if (!Guid.TryParse(reference.ExpectedFloorAnchorUuid, out var referenceFloorUuid)
            || !AagFp2BakedSpace.TryGetFloor(
                referenceFloorUuid,
                out _,
                out var bakedBoundary,
                out bakedSpaceFailure)
            || bakedBoundary == null
            || bakedBoundary.Count < 3)
            throw new InvalidOperationException(
                $"FP2 bundled baked space is invalid: {bakedSpaceFailure ?? "floor_boundary_missing"}.");
        if (aligner.PreviewOnly || !aligner.AllowQuestControllerApply)
            throw new InvalidOperationException(
                "FP2 field validation must be armed for deliberate two-thumbstick manual apply.");
        if (!aligner.RecordDetectedYaw)
            throw new InvalidOperationException(
                "FP2 must record AprilTag yaw as a diagnostic value.");
        if (!aligner.ApplyDetectedYawRotation)
            throw new InvalidOperationException(
                "FP2 requires AprilTag yaw correction so the displaced MRUK frame is rigidly aligned.");
        if (aligner.MaximumStableYawJitterDegrees > 3f)
            throw new InvalidOperationException(
                $"FP2 AprilTag yaw jitter gate is too loose: {aligner.MaximumStableYawJitterDegrees:F2} degrees.");
        if (!aligner.ShowRuntimeHud || !aligner.HideReferenceVisualsAfterApply)
            throw new InvalidOperationException(
                "FP2 production mode requires setup HUD before apply and hidden reference visuals after apply.");
        if (aligner.Room3TagReference != reference || aligner.FixedSpaceOffset != offsets[0])
            throw new InvalidOperationException("FP2 AprilTag scene references are not wired together.");
        if (alignmentOverlays.Length != 1 || alignmentOverlays[0].enabled)
            throw new InvalidOperationException(
                "FP2 production build requires exactly one disabled diagnostic space overlay.");
        if (placementAuthoring.Length != 1 || placementAuthoring[0].enabled)
            throw new InvalidOperationException(
                "FP2 production build requires exactly one disabled field-placement authoring component.");

        var incidentalSeed = Resources.Load<TextAsset>("AAG/fp2_incidental_anchor_sets");
        var incidentalManifest = incidentalSeed == null
            ? null
            : JsonUtility.FromJson<AagIncidentalAnchorManifest>(
                incidentalSeed.text.TrimStart('\uFEFF'));
        if (incidentalManifest?.sets == null
            || incidentalManifest.sets.Count != 3
            || incidentalManifest.sets.Any(value =>
                value?.objects == null
                || value.objects.Count != AagIncidentalAnchorStore.Fp2ObjectsPerSet))
            throw new InvalidOperationException(
                "FP2 production build requires three complete 8-room incidental-object sets.");

        var experimentMain = UnityEngine.Object.FindFirstObjectByType<ExperimentMain>(
            FindObjectsInactive.Include);
        if (experimentMain == null)
            throw new InvalidOperationException("FP2 scene is missing ExperimentMain.");

        Debug.Log(
            $"[AAG FP2 Build] Validation passed: scene={scene.path}; mode=device-mruk+apriltag; tag=standard41h12/1; "
            + $"size={aligner.TagSizeMeters:F3}m; room={reference.ExpectedRoomUuid}; "
            + $"confirmed={reference.ReferencePlacementConfirmed}; previewOnly={aligner.PreviewOnly}; "
            + "referenceFrame=bundled_baked_mruk.");
    }

    private static void Build(string[] scenes, string outputPath, string label)
    {
        EnforceStableTrackingOrigin(scenes);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        Debug.Log($"[AAG {label} Build] result={report.summary.result} errors={report.summary.totalErrors} output={outputPath}");
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Android build failed: {report.summary.result}");
    }

    private static void EnforceStableTrackingOrigin(string[] scenePaths)
    {
        foreach (var scenePath in scenePaths)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var managers = UnityEngine.Object.FindObjectsByType<OVRManager>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            if (managers.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one OVRManager in '{scenePath}', found {managers.Length}.");

            var manager = managers[0];
            manager.trackingOriginType = OVRManager.TrackingOrigin.Stage;
            manager.AllowRecenter = false;
            EditorUtility.SetDirty(manager);

            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Failed to save stable tracking settings in '{scenePath}'.");

            Debug.Log($"[AAG Core Build] Enforced stable tracking: scene={scenePath}; origin=Stage; allowRecenter=false");
        }
    }
}
