using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class AagCoreAndroidBuild
{
    public static void BuildFromCommandLine()
    {
        const string outputPath = "Builds/aag_core_loader.apk";
        var scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
            throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");

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
}
