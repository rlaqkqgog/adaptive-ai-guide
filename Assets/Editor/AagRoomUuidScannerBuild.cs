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
    private const string Fp1ScenePath = "Assets/Scenes/RoomUUIDScan.unity";
    private const string Fp2ScenePath = "Assets/Scenes/RoomUUIDScan_FP2.unity";
    private const string Fp1OutputPath = "Builds/fp1_room_uuid_scanner.apk";
    private const string Fp2OutputPath = "Builds/fp2_room_uuid_scanner.apk";
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

    [MenuItem("AAG/Build FP1 Room UUID Scanner APK")]
    public static void BuildFromCommandLine()
    {
        Build(Fp1ScenePath, Fp1OutputPath, "FP1");
    }

    [MenuItem("AAG/Build FP2 Room UUID Scanner APK")]
    public static void BuildFp2()
    {
        Build(Fp2ScenePath, Fp2OutputPath, "FP2");
    }

    private static void Build(string scenePath, string outputPath, string spaceId)
    {
        ValidateScene(scenePath, spaceId);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? "Builds");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        Debug.Log($"[AAG Room UUID Build] space={spaceId}; result={report.summary.result}; " +
                  $"errors={report.summary.totalErrors}; output={outputPath}");

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException($"Room UUID Scanner build failed: {report.summary.result}");
        }
    }

    [MenuItem("AAG/Validate FP1 Room UUID Scanner Scene")]
    public static void ValidateScene()
    {
        ValidateScene(Fp1ScenePath, "FP1");
    }

    [MenuItem("AAG/Validate FP2 Room UUID Scanner Scene")]
    public static void ValidateFp2Scene()
    {
        ValidateScene(Fp2ScenePath, "FP2");
    }

    private static void ValidateScene(string scenePath, string expectedSpaceId)
    {
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

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
        var exporter = scanner.GetComponent<AagRoomCoordinateExporter>();
        Require(exporter != null,
            "RoomUUIDScanner requires AagRoomCoordinateExporter.");

        var serializedExporter = new SerializedObject(exporter);
        Require(string.Equals(
                serializedExporter.FindProperty("exportSpaceId")?.stringValue,
                expectedSpaceId,
                StringComparison.Ordinal),
            $"Room UUID exporter must target {expectedSpaceId}.");

        Debug.Log($"[AAG Room UUID Build] {expectedSpaceId} scene validation passed.");
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
