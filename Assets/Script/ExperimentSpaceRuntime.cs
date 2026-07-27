using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Process-wide experiment-space selection. FP1 keeps every legacy path/key;
/// additional spaces receive an isolated namespace and never fall back to FP1 data.
/// ExperimentMain configures this before any participant session can start.
/// </summary>
public static class ExperimentSpaceRuntime
{
    private static string spaceId = AagExperimentSpaceCatalog.Fp1Id;
    private static string floorPlanId = AagExperimentSpaceCatalog.Fp1Id;
    private static string storageKey = "fp1";
    private static string[] setIds =
    {
        AagExperimentSpaceCatalog.Fp1S1,
        AagExperimentSpaceCatalog.Fp1S2,
        AagExperimentSpaceCatalog.Fp1S3,
    };
    private static string[] assetSetIds =
    {
        AagExperimentSpaceCatalog.Fp1S1,
        AagExperimentSpaceCatalog.Fp1S2,
        AagExperimentSpaceCatalog.Fp1S3,
    };
    private static AagFloorPlanDefinition activeFloorPlan = AagExperimentSpaceCatalog.Fp1;

    public static string SpaceId => spaceId;
    public static string FloorPlanId => floorPlanId;
    public static string StorageKey => storageKey;
    public static IReadOnlyList<string> SetIds => setIds;
    public static bool IsFp2 =>
        string.Equals(spaceId, AagExperimentSpaceCatalog.Fp2Id, StringComparison.Ordinal);
    public static bool UsesLegacyFp1Storage =>
        string.Equals(storageKey, "fp1", StringComparison.Ordinal);
    public static string LogFolderName => $"{spaceId}Logs";
    public static string PlayerPrefsPrefix => $"AAG.{spaceId}.";

    public static AagFloorPlanDefinition FloorPlan => activeFloorPlan;

    public static void Configure(ExperimentConfig config)
    {
        if (config == null)
        {
            ResetToFp1();
            return;
        }

        spaceId = NormalizeId(config.spaceId, AagExperimentSpaceCatalog.Fp1Id).ToUpperInvariant();
        floorPlanId = NormalizeId(config.floorPlanId, spaceId).ToUpperInvariant();
        storageKey = NormalizeStorageKey(config.storageNamespace, spaceId.ToLowerInvariant());
        setIds = (config.setIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        assetSetIds = (config.assetSetIds ?? Array.Empty<string>())
            .Select(value => value?.Trim() ?? string.Empty)
            .ToArray();
        AagExperimentSpaceCatalog.TryGetFloorPlan(floorPlanId, out var catalogFloorPlan);
        var configuredRoomIds = (config.rooms ?? Array.Empty<ExperimentRoomMapping>())
            .Where(room => room != null && Guid.TryParse(room.roomUuid, out var uuid) && uuid != Guid.Empty)
            .Select(room => Guid.Parse(room.roomUuid))
            .Distinct()
            .ToArray();
        activeFloorPlan = new AagFloorPlanDefinition(
            floorPlanId,
            configuredRoomIds.Length > 0
                ? configuredRoomIds
                : catalogFloorPlan?.RoomIds ?? Array.Empty<Guid>(),
            catalogFloorPlan?.ExcludedRoomIds ?? Array.Empty<Guid>(),
            setIds);
    }

    public static void ResetToFp1()
    {
        spaceId = AagExperimentSpaceCatalog.Fp1Id;
        floorPlanId = AagExperimentSpaceCatalog.Fp1Id;
        storageKey = "fp1";
        setIds = new[]
        {
            AagExperimentSpaceCatalog.Fp1S1,
            AagExperimentSpaceCatalog.Fp1S2,
            AagExperimentSpaceCatalog.Fp1S3,
        };
        assetSetIds = setIds.ToArray();
        activeFloorPlan = AagExperimentSpaceCatalog.Fp1;
    }

    public static void ConfigureTool(string toolSpaceId, string toolStorageNamespace)
    {
        spaceId = NormalizeId(toolSpaceId, AagExperimentSpaceCatalog.Fp1Id).ToUpperInvariant();
        floorPlanId = spaceId;
        storageKey = NormalizeStorageKey(toolStorageNamespace, spaceId.ToLowerInvariant());
        setIds = AagExperimentSpaceCatalog.TryGetFloorPlan(floorPlanId, out var floorPlan)
            ? floorPlan.SetIds.ToArray()
            : Array.Empty<string>();
        assetSetIds = setIds.ToArray();
        activeFloorPlan = floorPlan;
    }

    public static string NamespacedFileName(string suffix, string extension = "json") =>
        $"{storageKey}_{suffix}.{extension.TrimStart('.')}";

    public static string PlayerPrefsCountKey => UsesLegacyFp1Storage
        ? "numUuids"
        : PlayerPrefsPrefix + "numUuids";

    public static string PlayerPrefsUuidKey(int index) => UsesLegacyFp1Storage
        ? "uuid" + index
        : PlayerPrefsPrefix + "uuid." + index;

    public static string ResolveAssetSetId(string experimentSetId)
    {
        var index = Array.FindIndex(setIds, value =>
            string.Equals(value, experimentSetId, StringComparison.Ordinal));
        return index >= 0 && index < assetSetIds.Length && !string.IsNullOrWhiteSpace(assetSetIds[index])
            ? assetSetIds[index]
            : experimentSetId;
    }

    private static string NormalizeId(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeStorageKey(string value, string fallback)
    {
        var source = NormalizeId(value, fallback).ToLowerInvariant();
        var normalized = new string(source
            .Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_')
            .ToArray());
        return string.IsNullOrEmpty(normalized) ? fallback : normalized;
    }
}
