using System;
using System.IO;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AagRoomUuidScannerBuild
{
    private const string ScenePath = "Assets/Scenes/RoomUUIDScan.unity";
    private const string OutputPath = "Builds/room_uuid_scanner.apk";
    private const string BuildRequestPath = "Temp/AagRoomUuidScannerBuild.request";

    [InitializeOnLoadMethod]
    private static void BuildOnceWhenRequested()
    {
        if (!File.Exists(BuildRequestPath))
        {
            return;
        }

        File.Delete(BuildRequestPath);
        EditorApplication.delayCall += BuildFromCommandLine;
    }

    [MenuItem("AAG/Build Room UUID Scanner APK")]
    public static void BuildFromCommandLine()
    {
        ValidateScene();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? "Builds");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        Debug.Log($"[AAG Room UUID Build] result={report.summary.result}; " +
                  $"errors={report.summary.totalErrors}; output={OutputPath}");

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException($"Room UUID Scanner build failed: {report.summary.result}");
        }
    }

    [MenuItem("AAG/Validate Room UUID Scanner Scene")]
    public static void ValidateScene()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var manager = FindExactlyOne<OVRManager>(scene.path);
        Require(manager.isInsightPassthroughEnabled, "OVRManager Insight Passthrough must be enabled.");
        Require(manager.trackingOriginType == OVRManager.TrackingOrigin.Stage,
            "OVRManager tracking origin must be Stage.");
        Require(!manager.AllowRecenter, "OVRManager Allow Recenter must be disabled.");

        var passthrough = FindExactlyOne<OVRPassthroughLayer>(scene.path);
        Require(passthrough.enabled && passthrough.gameObject.activeInHierarchy,
            "OVRPassthroughLayer must be active and enabled.");

        var mruk = FindExactlyOne<MRUK>(scene.path);
        Require(mruk.SceneSettings.LoadSceneOnStartup, "MRUK Load Scene On Startup must be enabled.");
        Require(mruk.SceneSettings.DataSource == MRUK.SceneDataSource.Device,
            "MRUK Data Source must be Device so the Quest Space Setup UUIDs are read.");

        var scanner = GameObject.Find("RoomUUIDScanner");
        Require(scanner != null, "RoomUUIDScanner GameObject is missing.");
        Require(scanner.GetComponent<AagCurrentRoomDisplay>() != null,
            "RoomUUIDScanner requires AagCurrentRoomDisplay.");
        Require(scanner.GetComponent<AagRoomCoordinateExporter>() != null,
            "RoomUUIDScanner requires AagRoomCoordinateExporter.");

        Debug.Log("[AAG Room UUID Build] Scene validation passed.");
    }

    private static T FindExactlyOne<T>(string scenePath) where T : UnityEngine.Object
    {
        var objects = UnityEngine.Object.FindObjectsByType<T>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);

        if (objects.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one {typeof(T).Name} in '{scenePath}', found {objects.Length}.");
        }

        return objects.Single();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
