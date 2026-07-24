using System;
using System.IO;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AagFp1RescanValidationBuild
{
    // Build request revision: room-constellation-diagnostic-only-20260721.
    private const string ScenePath = "Assets/Scenes/MainTest_FP1.unity";
    private const string ConfigPath = "Assets/Experiment/FP1ExperimentConfig.asset";
    private const string PlacementSeedPath = "Assets/Resources/AAG/fp1_floor_anchor_local_placements.json";
    private const string OutputPath = "Builds/main_test_fp1_constellation_warning_fixed_20260721.apk";
    private const string RequestPath = "Temp/AagFp1RescanValidationBuild.request";
    private const string ResultPath = "Temp/AagFp1RescanValidationBuild.result";

    [InitializeOnLoadMethod]
    private static void BuildOnceWhenRequested()
    {
        if (!File.Exists(RequestPath)) return;
        File.Delete(RequestPath);
        EditorApplication.delayCall += Build;
    }

    [MenuItem("AAG/Build FP1 2026-07-21 Rescan Validation APK")]
    public static void Build()
    {
        try
        {
            ValidateConfiguration();
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? "Builds");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException(
                    $"FP1 rescan build failed: {report.summary.result}; errors={report.summary.totalErrors}");
            File.WriteAllText(ResultPath,
                $"SUCCESS|{DateTime.UtcNow:O}|{report.summary.totalSize}|{OutputPath}");
            Debug.Log($"[AAG FP1 Rescan Build] SUCCESS output={OutputPath}; bytes={report.summary.totalSize}");
        }
        catch (Exception exception)
        {
            File.WriteAllText(ResultPath, $"FAILED|{DateTime.UtcNow:O}|{exception}");
            Debug.LogException(exception);
            throw;
        }
    }

    private static void ValidateConfiguration()
    {
        ValidateConstellationWarningPolicy();
        if (!File.Exists(ScenePath)) throw new InvalidOperationException($"Missing scene: {ScenePath}");
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var mrukInstances = UnityEngine.Object.FindObjectsByType<MRUK>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (mrukInstances.Length != 1)
            throw new InvalidOperationException($"Expected exactly one MRUK in {scene.path}; actual={mrukInstances.Length}");
        if (!mrukInstances[0].SceneSettings.LoadSceneOnStartup
            || mrukInstances[0].SceneSettings.DataSource != MRUK.SceneDataSource.Device)
            throw new InvalidOperationException(
                "MainTest_FP1 MRUK must load the device scene on startup.");
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(ConfigPath);
        if (config == null) throw new InvalidOperationException($"Missing config: {ConfigPath}");
        if (config.fiducialMarkerAlignmentEnabled)
            throw new InvalidOperationException(
                "FP1 rescan validation build requires QR/fiducial alignment to remain disabled.");
        if (AagS3IncidentalRoomLocalCatalog.Count != AagIncidentalAnchorStore.RequiredObjectsPerSet)
            throw new InvalidOperationException(
                $"S3 incidental room-local catalog must contain {AagIncidentalAnchorStore.RequiredObjectsPerSet} objects; "
                + $"actual={AagS3IncidentalRoomLocalCatalog.Count}");
        var expected = AagExperimentSpaceCatalog.Fp1.RoomIds;
        var placementSeed = AssetDatabase.LoadAssetAtPath<TextAsset>(PlacementSeedPath);
        var placementCatalog = placementSeed == null
            ? null
            : JsonUtility.FromJson<MrukRoomLocalPlacementStore.Catalog>(placementSeed.text);
        var s3Placements = placementCatalog?.sets?.SingleOrDefault(value =>
            string.Equals(value.setId, AagExperimentSpaceCatalog.Fp1S3, StringComparison.Ordinal));
        if (placementCatalog == null
            || !string.Equals(
                placementCatalog.schemaVersion, MrukRoomLocalPlacementStore.SchemaVersion, StringComparison.Ordinal)
            || s3Placements?.placements == null
            || s3Placements.placements.Count != 12
            || s3Placements.placements.Any(value =>
                !Guid.TryParse(value.roomUuid, out var roomUuid) || !expected.Contains(roomUuid)))
            throw new InvalidOperationException("S3 floor-local placement seed is missing, stale, or incomplete.");
        var configured = (config.rooms ?? Array.Empty<ExperimentRoomMapping>())
            .Select(room => Guid.Parse(room.roomUuid))
            .ToArray();
        if (configured.Length != expected.Count || configured.Distinct().Count() != expected.Count)
            throw new InvalidOperationException(
                $"FP1 config must contain {expected.Count} unique rooms; actual={configured.Length}/{configured.Distinct().Count()}");
        var missing = expected.Where(roomId => !configured.Contains(roomId)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"FP1 config missing room UUIDs: {string.Join(",", missing)}");
        if (!AagExperimentSpaceCatalog.IsFp1Room2(AagExperimentSpaceCatalog.Fp1Room2Part1Uuid)
            || !AagExperimentSpaceCatalog.IsFp1Room2(AagExperimentSpaceCatalog.Fp1Room2Part2Uuid))
            throw new InvalidOperationException("Split Room2 catalog mapping is invalid.");
        Debug.Log("[AAG FP1 Rescan Build] Configuration validation passed: 9 unique experiment rooms.");
    }

    private static void ValidateConstellationWarningPolicy()
    {
        var references = new[]
        {
            new AagRigidPoseRecovery.Reference("room3", new Vector3(1.133f, 0.079f, 2.906f), new Vector3(0.151f, -0.015f, 1.292f)),
            new AagRigidPoseRecovery.Reference("room2-1", new Vector3(-13.106f, 0.036f, -0.403f), new Vector3(-15.026f, 0.029f, 0.831f)),
            new AagRigidPoseRecovery.Reference("hall1-3", new Vector3(-0.681f, -0.049f, -2.859f), new Vector3(-3.787f, -0.021f, -3.797f)),
            new AagRigidPoseRecovery.Reference("hall1-1", new Vector3(-22.695f, -0.005f, -7.886f), new Vector3(-25.206f, 0.003f, -4.416f)),
            new AagRigidPoseRecovery.Reference("hall2-1", new Vector3(10.916f, 0.006f, -0.139f), new Vector3(9.443f, -0.068f, -3.533f)),
            new AagRigidPoseRecovery.Reference("room1", new Vector3(-26.704f, -0.002f, -3.716f), new Vector3(-28.124f, 0.017f, 0.247f)),
            new AagRigidPoseRecovery.Reference("hall1-2", new Vector3(-12.484f, -0.003f, -5.448f), new Vector3(-15.259f, -0.005f, -4.052f)),
            new AagRigidPoseRecovery.Reference("hall2-2", new Vector3(6.964f, 0.018f, 3.705f), new Vector3(6.129f, -0.038f, 0.916f)),
        };

        if (AagRigidPoseRecovery.TrySolve(references, out _, out _))
            throw new InvalidOperationException(
                "Current field constellation must remain outside the strict RMS quality gate.");
        if (!AagRigidPoseRecovery.TrySolveBestEffort(
                references, out var solution, out var warning)
            || solution.InlierIds.Length != 5
            || solution.RmsResidualMeters <= AagRigidPoseRecovery.MaximumRmsResidualMeters
            || string.IsNullOrWhiteSpace(warning))
        {
            throw new InvalidOperationException(
                $"Constellation diagnostic-only fallback validation failed: {warning}");
        }
    }
}
