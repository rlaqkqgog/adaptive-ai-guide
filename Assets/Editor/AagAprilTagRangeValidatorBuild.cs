using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AagAprilTagRangeValidatorBuild
{
    private const string SourceScenePath = "Assets/Scenes/MainTest_FP1.unity";
    private const string ValidatorScenePath = "Assets/Scenes/AprilTagRangeValidator.unity";
    private const string OutputApkPath = "Builds/aag_apriltag_range_validator.apk";
    private const string ApplicationId = "com.aag.apriltagrangevalidator";

    [MenuItem("AAG/Build AprilTag 1m Validator APK")]
    public static void BuildFromMenu()
    {
        Build();
    }

    public static void BuildFromCommandLine()
    {
        Build();
    }

    private static void Build()
    {
        CreateValidatorScene();

        var namedTarget = NamedBuildTarget.Android;
        var oldApplicationId = PlayerSettings.GetApplicationIdentifier(namedTarget);
        var oldProductName = PlayerSettings.productName;

        try
        {
            PlayerSettings.SetApplicationIdentifier(namedTarget, ApplicationId);
            PlayerSettings.productName = "AAG AprilTag 1m Validator";
            Directory.CreateDirectory(Path.GetDirectoryName(OutputApkPath) ?? "Builds");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ValidatorScenePath },
                locationPathName = OutputApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"AprilTag validator build failed: {report.summary.result}, " +
                    $"errors={report.summary.totalErrors}");
            }

            Debug.Log(
                $"[AAG AprilTag Range Build] SUCCESS: {Path.GetFullPath(OutputApkPath)} " +
                $"({report.summary.totalSize} bytes)");
        }
        finally
        {
            PlayerSettings.SetApplicationIdentifier(namedTarget, oldApplicationId);
            PlayerSettings.productName = oldProductName;
            AssetDatabase.SaveAssets();
        }
    }

    private static void CreateValidatorScene()
    {
        if (!File.Exists(SourceScenePath))
        {
            throw new FileNotFoundException("Source camera-rig scene is missing.", SourceScenePath);
        }

        var sourceScene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);
        var targetScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

        CloneRoot(sourceScene, targetScene, "[BuildingBlock] Camera Rig");
        CloneRoot(sourceScene, targetScene, "[BuildingBlock] Passthrough");

        var validatorObject = new GameObject("AAG AprilTag 1m Range Validator");
        validatorObject.AddComponent<AagAprilTagRangeValidator>();
        SceneManager.MoveGameObjectToScene(validatorObject, targetScene);

        EditorSceneManager.CloseScene(sourceScene, true);
        SceneManager.SetActiveScene(targetScene);
        DisableUnneededScenePermission(targetScene);

        if (!EditorSceneManager.SaveScene(targetScene, ValidatorScenePath))
        {
            throw new InvalidOperationException($"Could not save validator scene: {ValidatorScenePath}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void CloneRoot(Scene sourceScene, Scene targetScene, string rootName)
    {
        var source = sourceScene.GetRootGameObjects().FirstOrDefault(root => root.name == rootName);
        if (source == null)
        {
            throw new InvalidOperationException($"Required source root not found: {rootName}");
        }

        var clone = UnityEngine.Object.Instantiate(source);
        clone.name = source.name;
        SceneManager.MoveGameObjectToScene(clone, targetScene);
    }

    private static void DisableUnneededScenePermission(Scene targetScene)
    {
        var ovrManager = targetScene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<OVRManager>(true))
            .FirstOrDefault();

        if (ovrManager == null)
        {
            throw new InvalidOperationException("Cloned camera rig does not contain OVRManager.");
        }

        var serializedManager = new SerializedObject(ovrManager);
        serializedManager.FindProperty("requestScenePermissionOnStartup").boolValue = false;
        serializedManager.FindProperty("requestPassthroughCameraAccessPermissionOnStartup").boolValue = true;
        serializedManager.ApplyModifiedPropertiesWithoutUndo();
    }
}
