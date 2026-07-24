using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AagCoreAndroidBuild
{
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

        EnforceStableTrackingOrigin(scenes);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        Debug.Log($"[AAG Core Build] result={report.summary.result} errors={report.summary.totalErrors} output={outputPath}");
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
