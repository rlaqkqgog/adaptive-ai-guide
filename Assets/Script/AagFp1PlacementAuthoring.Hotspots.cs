using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Networking;

public sealed partial class AagFp1PlacementAuthoring
{
    private const string ManualHotspotPlacementSource = "USER_APPROVED_MANUAL_HOTSPOT";
    private const string ProceduralPlacementSource = "PROCEDURAL";
    private const int ManualMarkersPerSet = 12;
    private const int ProceduralMarkersPerSet = 0;
    private const int UniqueHotspotsRequiredForTriplet = ManualMarkersPerSet * 3;
    private const string BundledCatalogSchema = "aag-fp1-user-approved-manual-hotspots/v1";
    private const string BundledCatalogRelativePath = "AAG/fp1_preferred_hotspot_catalog.json";
    private const string RoomLocalCatalogSchema = "aag-fp1-manual-hotspots-room-local/v1";
    private const string RoomLocalCatalogRelativePath = "AAG/fp1_manual_hotspots_room_local_v1.json";
    private const string RuntimeOverrideCatalogFileName = "fp1_hotspot_runtime_override.json";
    private const string LocalizationReportFileName = "fp1_preferred_hotspot_catalog_v2.json";
    private const string UserApprovedSource = "USER_APPROVED_MANUAL_HOTSPOT";
    private const string SpatialAnchorLocalizedSource = "SPATIAL_ANCHOR_LOCALIZED";
    private const string RoomLocalRecoverySource = "MRUK_ROOM_LOCAL_RECOVERY";
    private const string UnavailableRecoverySource = "UNAVAILABLE";
    private static readonly Guid Room2Uuid = Guid.Parse("3342022d-32d9-c32f-cc67-a6993db345ef");
    private static readonly Guid Room3Uuid = Guid.Parse("36f65d12-3dd9-957c-8535-9a774780e5f6");

    [Header("PROVISIONAL manual hotspot persistence")]
    [SerializeField, Min(0.01f)] private float manualHotspotAdjacencyMeters = 0.75f;
    [SerializeField, Min(0f)] private float hotspotAnchorLoadTimeoutSeconds = 15f;
    [Tooltip("Development verification mode. The bundled/override UUID catalog is still used; legacy PlayerPrefs fallback is disabled.")]
    [SerializeField] private bool ignoreLegacyPlayerPrefsForPersistenceTest;
    [SerializeField, Min(1)] private int room2MaximumMarkers = 5;
    [SerializeField, Min(1)] private int room3MaximumMarkers = 3;

    [Serializable]
    private sealed class SourceCatalog
    {
        public string schema_version = string.Empty;
        public string catalog_hash = string.Empty;
        public string source_catalog_schema = string.Empty;
        public string source_anchor_list_hash = string.Empty;
        public string source_catalog_hash = string.Empty;
        public string source_catalog_file_sha256 = string.Empty;
        public int hotspot_count;
        public string source = string.Empty;
        public bool user_approved;
        public List<SourceCatalogEntry> anchors = new List<SourceCatalogEntry>();
    }

    [Serializable]
    private sealed class SourceCatalogEntry
    {
        public string uuid = string.Empty;
        public string original_room_uuid = string.Empty;
        public string zone_group = string.Empty;
        public DiagnosticVector3 diagnostic_world_position = new DiagnosticVector3();
        public DiagnosticQuaternion diagnostic_world_rotation = new DiagnosticQuaternion();
        public bool user_approved;
        public string source = string.Empty;
    }

    [Serializable]
    private sealed class RoomLocalCatalog
    {
        public string schema_version = string.Empty;
        public string catalog_hash = string.Empty;
        public string source_catalog_hash = string.Empty;
        public string source_anchor_list_hash = string.Empty;
        public string source_room_export_id = string.Empty;
        public string source_room_export_hash = string.Empty;
        public string source = string.Empty;
        public bool user_approved;
        public int hotspot_count;
        public List<RoomLocalCatalogEntry> hotspots = new List<RoomLocalCatalogEntry>();
    }

    [Serializable]
    private sealed class RoomLocalCatalogEntry
    {
        public string uuid = string.Empty;
        public string room_uuid = string.Empty;
        public string zone_group = string.Empty;
        public DiagnosticVector3 room_local_position = new DiagnosticVector3();
        public DiagnosticQuaternion room_local_rotation = new DiagnosticQuaternion();
        public DiagnosticVector3 original_world_position = new DiagnosticVector3();
        public DiagnosticQuaternion original_world_rotation = new DiagnosticQuaternion();
        public DiagnosticVector3 source_room_world_position = new DiagnosticVector3();
        public DiagnosticQuaternion source_room_world_rotation = new DiagnosticQuaternion();
        public string source_room_pose_hash = string.Empty;
        public string source_room_export_id = string.Empty;
        public bool user_approved;
        public string source = string.Empty;
    }

    [Serializable]
    private sealed class DiagnosticVector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    private sealed class DiagnosticQuaternion
    {
        public float x;
        public float y;
        public float z;
        public float w = 1f;
        public Quaternion ToQuaternion()
        {
            var value = new Quaternion(x, y, z, w);
            var magnitudeSquared = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return magnitudeSquared > 0.000001f ? Quaternion.Normalize(value) : Quaternion.identity;
        }
    }

    [Serializable]
    private sealed class LocalizationReport
    {
        public string schema_version = "aag-fp1-hotspot-localization-report/v1";
        public string selected_source = string.Empty;
        public string selected_catalog_hash = string.Empty;
        public string source_anchor_list_hash = string.Empty;
        public int catalog_count;
        public int load_requested;
        public int localized;
        public int missing;
        public int found_in_quest_store;
        public int load_succeeded;
        public int spatial_anchor_localized;
        public int mruk_room_local_recovered;
        public int unavailable;
        public string recovery_mode = string.Empty;
        public bool legacy_player_prefs_ignored;
        public List<LocalizationReportEntry> anchors = new List<LocalizationReportEntry>();
    }

    [Serializable]
    private sealed class LocalizationReportEntry
    {
        public string uuid = string.Empty;
        public string original_room_uuid = string.Empty;
        public string zone_group = string.Empty;
        public string source = UserApprovedSource;
        public bool user_approved;
        public bool localized;
        public string pose_source = string.Empty;
        public bool usable;
        public string rejection_reason = string.Empty;
        public float diagnostic_x;
        public float diagnostic_y;
        public float diagnostic_z;
        public float localized_x;
        public float localized_y;
        public float localized_z;
        public float diagnostic_delta_meters;
    }

    private sealed class RuntimePreferredHotspot
    {
        public Guid uuid;
        public Guid originalRoomUuid;
        public string zoneGroup;
        public Vector3 diagnosticPosition;
        public Vector3 position;
        public Quaternion rotation;
        public string dataSource;
    }

    private readonly List<RuntimePreferredHotspot> preferredHotspots = new List<RuntimePreferredHotspot>();
    private SourceCatalog activeSourceCatalog;
    private RoomLocalCatalog activeRoomLocalCatalog;
    private string activeSourceCatalogKind = "NONE";
    private string activeSourceCatalogFailure = "catalog not loaded";
    private string preferredHotspotSourceHash = "UNINITIALIZED";
    private string preferredHotspotCatalogHash = "UNINITIALIZED";
    private int preferredHotspotSourceAnchorCount;
    private int preferredHotspotLoadedAnchorCount;
    private int preferredHotspotAllowedFp1Count;
    private int preferredHotspotExplicitApprovalCount;
    private bool preferredHotspotApprovalPassed;
    private string preferredHotspotApprovalFailure = "catalog not loaded";
    private int hardValidManualHotspotCount;
    private bool hotspotAnchorLoadAttempted;

    private string RuntimeOverrideCatalogPath => Path.Combine(StorageFolderPath, RuntimeOverrideCatalogFileName);
    private string LocalizationReportPath => Path.Combine(StorageFolderPath, LocalizationReportFileName);

    private IEnumerator LoadSavedHotspotAnchorsReadOnly(string phase)
    {
        yield return ResolveSourceCatalog();
        yield return ResolveRoomLocalCatalog();
        var loader = GetComponent<AnchorLoader>();
        var requested = activeSourceCatalog?.anchors
            .Select(entry => Guid.TryParse(entry.uuid, out var uuid) ? uuid : Guid.Empty)
            .Where(uuid => uuid != Guid.Empty)
            .Distinct()
            .ToList() ?? new List<Guid>();

        if (requested.Count == 0)
            Debug.LogWarning("[AAG Hotspot Persistence] catalog has no UUIDs; PlayerPrefs fallback is disabled");

        const int playerPrefsCount = 0;
        if (hotspotAnchorLoadAttempted)
        {
            Debug.LogWarning($"[AAG Hotspot Recovery] phase={phase}; anchorQuery=SKIPPED_ALREADY_ATTEMPTED; requested={requested.Count}; playerPrefs={playerPrefsCount}");
            yield break;
        }
        hotspotAnchorLoadAttempted = true;

        if (requested.Count > 0)
        {
            var focusWaitExpiresAt = Time.unscaledTime + hotspotAnchorLoadTimeoutSeconds;
            while (!OVRManager.hasInputFocus && Time.unscaledTime < focusWaitExpiresAt)
                yield return null;
            Debug.Log($"[AAG Hotspot Recovery] phase={phase}; anchorQuery=ONCE; hasInputFocus={BoolText(OVRManager.hasInputFocus)} requested={requested.Count}");
        }

        loader?.LoadAnchorsByUuid(requested, activeSourceCatalogKind);
        var expiresAt = Time.unscaledTime + hotspotAnchorLoadTimeoutSeconds;
        while (loader != null && loader.IsReadOnlyLoadInProgress && Time.unscaledTime < expiresAt)
            yield return null;
        if (loader != null && loader.IsReadOnlyLoadInProgress)
            loader.FinalizePendingAsTimedOut();

        var found = loader?.FoundInQuestStoreRequestedCount ?? 0;
        var loadSucceeded = loader?.LoadSucceededRequestedCount ?? 0;
        var localized = loader?.LocalizedRequestedCount ?? 0;
        var missing = Mathf.Max(0, requested.Count - localized);
        Debug.Log($"[AAG Hotspot Recovery] requested={requested.Count} found={found} loadSucceeded={loadSucceeded} localized={localized} missing={missing} "
            + $"bundledCatalog={BoolText(activeSourceCatalogKind == "BUNDLED")} playerPrefs={playerPrefsCount}; phase={phase}");
        if (loader != null)
        {
            foreach (var uuid in requested.Where(uuid => !loader.LocalizedAnchorsReadOnly.ContainsKey(uuid)))
            {
                var reason = loader.LocalizationFailuresReadOnly.TryGetValue(uuid, out var recorded)
                    ? recorded
                    : "LOCALIZATION_DID_NOT_COMPLETE";
                Debug.LogError($"[AAG Hotspot Persistence Missing] phase={phase}; uuid={uuid}; reason=\"{reason}\"; source={activeSourceCatalogKind}");
            }
        }
    }

    private IEnumerator InitializeHotspotRecoveryCatalogs(string phase)
    {
        yield return ResolveSourceCatalog();
        yield return ResolveRoomLocalCatalog();
        var manager = GetComponent<SpatialAnchorManager>();
        var requested = activeSourceCatalog?.anchors.Select(entry => entry.uuid).Distinct().Count() ?? 0;
        var playerPrefs = manager?.GetSavedAnchorUuidsReadOnly().Distinct().Count() ?? 0;
        Debug.Log($"[AAG Hotspot Recovery] requested={requested} found=0 loadSucceeded=0 localized=0 "
            + $"bundledCatalog={BoolText(activeSourceCatalogKind == "BUNDLED")} playerPrefs={playerPrefs}; phase={phase}; anchorQuery=DEFERRED_UNTIL_FP1_VALIDATION");
        LogRecoveryRoomUuidStatus(phase);
    }

    private IEnumerator ResolveSourceCatalog()
    {
        activeSourceCatalog = null;
        activeSourceCatalogKind = "NONE";
        activeSourceCatalogFailure = string.Empty;
        if (File.Exists(RuntimeOverrideCatalogPath))
        {
            try
            {
                var text = File.ReadAllText(RuntimeOverrideCatalogPath);
                var candidate = JsonUtility.FromJson<SourceCatalog>(text);
                if (ValidateSourceCatalog(candidate, out var reason))
                {
                    activeSourceCatalog = candidate;
                    activeSourceCatalogKind = "RUNTIME_OVERRIDE";
                    Debug.Log($"[AAG Hotspot Persistence] selected=RUNTIME_OVERRIDE path={RuntimeOverrideCatalogPath} hash={candidate.catalog_hash}");
                    yield break;
                }
                Debug.LogError($"[AAG Hotspot Persistence] runtime override rejected reason=\"{reason}\"; fallingBack=BUNDLED");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[AAG Hotspot Persistence] runtime override read failed reason=\"{exception.Message}\"; fallingBack=BUNDLED");
            }
        }

        var bundledPath = Path.Combine(Application.streamingAssetsPath, BundledCatalogRelativePath);
        string bundledText = null;
        string bundledReadFailure = null;
        yield return ReadStreamingAssetText(bundledPath, (text, failure) =>
        {
            bundledText = text;
            bundledReadFailure = failure;
        });
        if (!string.IsNullOrEmpty(bundledText))
        {
            try
            {
                var candidate = JsonUtility.FromJson<SourceCatalog>(bundledText);
                if (ValidateSourceCatalog(candidate, out var reason))
                {
                    activeSourceCatalog = candidate;
                    activeSourceCatalogKind = "BUNDLED";
                    activeSourceCatalogFailure = string.Empty;
                    Debug.Log($"[AAG Hotspot Persistence] selected=BUNDLED path={bundledPath} catalog={candidate.hotspot_count} hash={candidate.catalog_hash}");
                    yield break;
                }
                activeSourceCatalogFailure = $"bundled catalog validation failed: {reason}";
            }
            catch (Exception exception)
            {
                activeSourceCatalogFailure = $"bundled catalog parse failed: {exception.Message}";
            }
        }
        else
        {
            activeSourceCatalogFailure = $"bundled catalog read failed: {bundledReadFailure}";
        }
        Debug.LogError($"[AAG Hotspot Persistence] source resolution failed reason=\"{activeSourceCatalogFailure}\"");
    }

    private IEnumerator ResolveRoomLocalCatalog()
    {
        activeRoomLocalCatalog = null;
        var bundledPath = Path.Combine(Application.streamingAssetsPath, RoomLocalCatalogRelativePath);
        string text = null;
        string readFailure = null;
        yield return ReadStreamingAssetText(bundledPath, (loaded, failure) =>
        {
            text = loaded;
            readFailure = failure;
        });
        if (string.IsNullOrEmpty(text))
        {
            Debug.LogError($"[AAG Hotspot Recovery] ROOM_LOCAL_RECOVERY_BLOCKED file={bundledPath}; reason=READ_FAILED:{readFailure}");
            yield break;
        }
        try
        {
            var candidate = JsonUtility.FromJson<RoomLocalCatalog>(text);
            if (!ValidateRoomLocalCatalog(candidate, out var reason))
            {
                Debug.LogError($"[AAG Hotspot Recovery] ROOM_LOCAL_RECOVERY_BLOCKED file={bundledPath}; reason={reason}");
                yield break;
            }
            activeRoomLocalCatalog = candidate;
            Debug.Log($"[AAG Hotspot Recovery] roomLocalCatalog=true path={bundledPath} catalog={candidate.hotspot_count} "
                + $"sourceExport={candidate.source_room_export_id} hash={candidate.catalog_hash}");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Hotspot Recovery] ROOM_LOCAL_RECOVERY_BLOCKED file={bundledPath}; reason=PARSE_FAILED:{exception.Message}");
        }
    }

    private static IEnumerator ReadStreamingAssetText(string path, Action<string, string> completed)
    {
        if (!path.Contains("://", StringComparison.Ordinal))
        {
            try
            {
                completed(File.ReadAllText(path), null);
            }
            catch (Exception exception)
            {
                completed(null, exception.Message);
            }
            yield break;
        }

        using (var request = UnityWebRequest.Get(path))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
                completed(request.downloadHandler.text, null);
            else
                completed(null, request.error);
        }
    }

    private static bool ValidateSourceCatalog(SourceCatalog catalog, out string failure)
    {
        if (catalog == null)
        {
            failure = "catalog is null";
            return false;
        }
        if (!string.Equals(catalog.schema_version, BundledCatalogSchema, StringComparison.Ordinal)
            || !catalog.user_approved
            || !string.Equals(catalog.source, UserApprovedSource, StringComparison.Ordinal)
            || catalog.anchors == null
            || catalog.anchors.Count != catalog.hotspot_count
            || catalog.hotspot_count <= 0)
        {
            failure = "schema/source/count/approval header invalid";
            return false;
        }
        var parsed = new HashSet<Guid>();
        foreach (var entry in catalog.anchors)
        {
            if (entry == null || !Guid.TryParse(entry.uuid, out var uuid) || !parsed.Add(uuid)
                || !Guid.TryParse(entry.original_room_uuid, out var roomUuid)
                || !AagExperimentSpaceCatalog.Fp1.ContainsRoom(roomUuid)
                || AagExperimentSpaceCatalog.Fp1.IsExcludedRoom(roomUuid)
                || string.IsNullOrWhiteSpace(entry.zone_group)
                || !entry.user_approved
                || !string.Equals(entry.source, UserApprovedSource, StringComparison.Ordinal))
            {
                failure = $"invalid or duplicate approved entry uuid={entry?.uuid ?? "NULL"}";
                return false;
            }
        }
        var computed = ComputeSourceCatalogIdentityHash(catalog);
        if (!string.Equals(computed, catalog.catalog_hash, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"catalog hash mismatch declared={catalog.catalog_hash}, computed={computed}";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private bool ValidateRoomLocalCatalog(RoomLocalCatalog catalog, out string failure)
    {
        if (catalog == null
            || !string.Equals(catalog.schema_version, RoomLocalCatalogSchema, StringComparison.Ordinal)
            || !catalog.user_approved
            || !string.Equals(catalog.source, UserApprovedSource, StringComparison.Ordinal)
            || catalog.hotspots == null
            || catalog.hotspots.Count != catalog.hotspot_count
            || catalog.hotspot_count <= 0
            || string.IsNullOrWhiteSpace(catalog.source_room_export_id)
            || string.IsNullOrWhiteSpace(catalog.source_room_export_hash))
        {
            failure = "schema/source/count/export header invalid";
            return false;
        }
        if (activeSourceCatalog != null && !string.Equals(catalog.source_catalog_hash, activeSourceCatalog.catalog_hash, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"source catalog hash mismatch roomLocal={catalog.source_catalog_hash} active={activeSourceCatalog.catalog_hash}";
            return false;
        }
        var sourceEntries = activeSourceCatalog?.anchors.ToDictionary(entry => entry.uuid, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Guid>();
        foreach (var entry in catalog.hotspots)
        {
            if (entry == null || !Guid.TryParse(entry.uuid, out var uuid) || !seen.Add(uuid)
                || !Guid.TryParse(entry.room_uuid, out var roomUuid)
                || !AagExperimentSpaceCatalog.Fp1.ContainsRoom(roomUuid)
                || AagExperimentSpaceCatalog.Fp1.IsExcludedRoom(roomUuid)
                || string.IsNullOrWhiteSpace(entry.zone_group)
                || string.IsNullOrWhiteSpace(entry.source_room_pose_hash)
                || !HasFinitePose(entry.room_local_position, entry.room_local_rotation)
                || !HasFinitePose(entry.source_room_world_position, entry.source_room_world_rotation)
                || !entry.user_approved
                || !string.Equals(entry.source, UserApprovedSource, StringComparison.Ordinal))
            {
                failure = $"invalid room-local entry uuid={entry?.uuid ?? "NULL"}";
                return false;
            }
            if (sourceEntries != null && (!sourceEntries.TryGetValue(entry.uuid, out var source)
                || !string.Equals(source.original_room_uuid, entry.room_uuid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(source.zone_group, entry.zone_group, StringComparison.Ordinal)))
            {
                failure = $"room-local/source catalog identity mismatch uuid={entry.uuid}";
                return false;
            }
        }
        failure = string.Empty;
        return true;
    }

    private static bool HasFinitePose(DiagnosticVector3 position, DiagnosticQuaternion rotation)
    {
        if (position == null || rotation == null) return false;
        var values = new[] { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
        if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value))) return false;
        var magnitudeSquared = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        return magnitudeSquared > 0.000001f;
    }

    private void LogRecoveryRoomUuidStatus(string phase)
    {
        var catalogRooms = new HashSet<string>(activeSourceCatalog?.anchors
            .Select(entry => entry.original_room_uuid) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var runtimeRooms = GetLoadedAllowedRooms().Select(room => room.Anchor.Uuid.ToString()).ToList();
        var matching = runtimeRooms.Count(uuid => catalogRooms.Contains(uuid));
        var missing = catalogRooms.Where(uuid => !runtimeRooms.Contains(uuid, StringComparer.OrdinalIgnoreCase)).ToList();
        Debug.Log($"[AAG Hotspot Recovery Rooms] phase={phase}; catalogRoomUuids={catalogRooms.Count}; currentMrukRoomUuids={runtimeRooms.Count}; matching={matching}; "
            + $"missing=[{string.Join(",", missing)}]");
    }

    private static string ComputeSourceCatalogIdentityHash(SourceCatalog catalog)
    {
        var builder = new StringBuilder(catalog.source_anchor_list_hash).Append('|');
        foreach (var entry in catalog.anchors.OrderBy(value => value.uuid, StringComparer.Ordinal))
        {
            builder.Append(entry.uuid).Append(':').Append(entry.original_room_uuid).Append(':')
                .Append(entry.zone_group).Append(":true:").Append(UserApprovedSource).Append(';');
        }
        return Sha256(builder.ToString());
    }

    private void RefreshPreferredHotspotCatalog(string phase, bool saveAndLog = true)
    {
        preferredHotspots.Clear();
        var loader = GetComponent<AnchorLoader>();
        var report = new LocalizationReport
        {
            selected_source = activeSourceCatalogKind,
            selected_catalog_hash = activeSourceCatalog?.catalog_hash ?? string.Empty,
            source_anchor_list_hash = activeSourceCatalog?.source_anchor_list_hash ?? string.Empty,
            catalog_count = activeSourceCatalog?.hotspot_count ?? 0,
            load_requested = loader?.RequestedLocalizationReadOnly.Count ?? 0,
            localized = loader?.LocalizedRequestedCount ?? 0,
            missing = loader?.MissingRequestedCount ?? 0,
            found_in_quest_store = loader?.FoundInQuestStoreRequestedCount ?? 0,
            load_succeeded = loader?.LoadSucceededRequestedCount ?? 0,
            recovery_mode = "SPATIAL_ANCHOR_LOCALIZED>MRUK_ROOM_LOCAL_RECOVERY>UNAVAILABLE",
            legacy_player_prefs_ignored = ignoreLegacyPlayerPrefsForPersistenceTest,
        };

        var liveHash = new StringBuilder(activeSourceCatalog?.catalog_hash ?? "NO_CATALOG");
        foreach (var entry in activeSourceCatalog?.anchors ?? new List<SourceCatalogEntry>())
        {
            var item = new LocalizationReportEntry
            {
                uuid = entry.uuid,
                original_room_uuid = entry.original_room_uuid,
                zone_group = entry.zone_group,
                source = entry.source,
                user_approved = entry.user_approved,
                diagnostic_x = entry.diagnostic_world_position.x,
                diagnostic_y = entry.diagnostic_world_position.y,
                diagnostic_z = entry.diagnostic_world_position.z,
            };
            if (!Guid.TryParse(entry.uuid, out var uuid)
                || !Guid.TryParse(entry.original_room_uuid, out var originalRoomUuid))
            {
                item.rejection_reason = "INVALID_BUNDLED_UUID_OR_ROOM";
                report.anchors.Add(item);
                continue;
            }
            var roomLocalEntry = activeRoomLocalCatalog?.hotspots.FirstOrDefault(value => string.Equals(value.uuid, entry.uuid, StringComparison.OrdinalIgnoreCase));
            OVRSpatialAnchor localizedAnchor = null;
            var recoverySource = UnavailableRecoverySource;
            Vector3 livePosition = Vector3.zero;
            Quaternion liveRotation = Quaternion.identity;
            if (loader != null && loader.LocalizedAnchorsReadOnly.TryGetValue(uuid, out localizedAnchor) && localizedAnchor != null)
            {
                livePosition = localizedAnchor.transform.position;
                liveRotation = localizedAnchor.transform.rotation;
                recoverySource = SpatialAnchorLocalizedSource;
                item.localized = true;
                report.spatial_anchor_localized++;
            }
            else if (roomLocalEntry != null && Guid.TryParse(roomLocalEntry.room_uuid, out var recoveryRoomUuid))
            {
                var recoveryRoom = FindLoadedRoom(recoveryRoomUuid);
                if (recoveryRoom != null && recoveryRoom.Anchor != null)
                {
                    livePosition = recoveryRoom.transform.TransformPoint(roomLocalEntry.room_local_position.ToVector3());
                    liveRotation = recoveryRoom.transform.rotation * roomLocalEntry.room_local_rotation.ToQuaternion();
                    var localRoundTrip = recoveryRoom.transform.InverseTransformPoint(livePosition);
                    if (Vector3.Distance(localRoundTrip, roomLocalEntry.room_local_position.ToVector3()) <= 0.001f)
                    {
                        recoverySource = RoomLocalRecoverySource;
                        report.mruk_room_local_recovered++;
                    }
                    else
                    {
                        item.rejection_reason = "ROOM_LOCAL_RECOVERY_BLOCKED:ROUND_TRIP_INCONSISTENT";
                    }
                }
                else
                {
                    item.rejection_reason = "ROOM_LOCAL_RECOVERY_BLOCKED:CURRENT_MRUK_ROOM_UUID_MISSING";
                }
            }
            else
            {
                item.rejection_reason = roomLocalEntry == null
                    ? "ROOM_LOCAL_RECOVERY_BLOCKED:MATCHING_SOURCE_EXPORT_ENTRY_MISSING"
                    : "ROOM_LOCAL_RECOVERY_BLOCKED:INVALID_ROOM_LOCAL_ENTRY";
            }
            if (recoverySource == UnavailableRecoverySource)
            {
                item.pose_source = UnavailableRecoverySource;
                item.rejection_reason = string.IsNullOrEmpty(item.rejection_reason)
                    ? loader != null && loader.LocalizationFailuresReadOnly.TryGetValue(uuid, out var reason) ? reason : "UNAVAILABLE"
                    : item.rejection_reason;
                report.unavailable++;
                report.anchors.Add(item);
                if (saveAndLog) Debug.LogError($"[AAG Hotspot Recovery Failure] uuid={uuid}; stage=RECOVERY; source={UnavailableRecoverySource}; "
                    + $"reason={item.rejection_reason}; requiredFile={Path.Combine(Application.streamingAssetsPath, RoomLocalCatalogRelativePath)}; "
                    + $"requiredSourceExport={activeRoomLocalCatalog?.source_room_export_id ?? "UNAVAILABLE"}");
                continue;
            }

            item.pose_source = recoverySource;
            item.localized_x = livePosition.x;
            item.localized_y = livePosition.y;
            item.localized_z = livePosition.z;
            item.diagnostic_delta_meters = Vector3.Distance(entry.diagnostic_world_position.ToVector3(), livePosition);
            var actualRoom = FindLoadedRoom(originalRoomUuid);
            if (actualRoom == null || !TryFindContainingFloor(actualRoom, livePosition, out _))
            {
                item.rejection_reason = $"{recoverySource}_OUTSIDE_ORIGINAL_ALLOWED_ROOM";
                report.anchors.Add(item);
                Debug.LogError($"[AAG Hotspot Catalog Rejection] uuid={uuid}; reason={item.rejection_reason}; originalRoom={originalRoomUuid}");
                continue;
            }

            item.usable = true;
            preferredHotspots.Add(new RuntimePreferredHotspot
            {
                uuid = uuid,
                originalRoomUuid = originalRoomUuid,
                zoneGroup = entry.zone_group,
                diagnosticPosition = entry.diagnostic_world_position.ToVector3(),
                position = livePosition,
                rotation = liveRotation,
                dataSource = recoverySource,
            });
            liveHash.Append(uuid).Append(':').Append(originalRoomUuid).Append(':')
                .Append(livePosition.x.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(livePosition.y.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(livePosition.z.ToString("F3", CultureInfo.InvariantCulture)).Append(';');
            report.anchors.Add(item);
            if (saveAndLog)
            {
                Debug.Log($"[AAG Hotspot Pose] uuid={uuid}; room={originalRoomUuid}; zone={entry.zone_group}; source={item.pose_source}; "
                    + $"resolved=({livePosition.x:F6},{livePosition.y:F6},{livePosition.z:F6}); "
                    + $"diagnostic=({item.diagnostic_x:F6},{item.diagnostic_y:F6},{item.diagnostic_z:F6}); delta={item.diagnostic_delta_meters:F4}m");
            }
        }

        preferredHotspotSourceAnchorCount = activeSourceCatalog?.hotspot_count ?? 0;
        preferredHotspotLoadedAnchorCount = report.spatial_anchor_localized + report.mruk_room_local_recovered;
        preferredHotspotAllowedFp1Count = activeSourceCatalog?.anchors.Count ?? 0;
        preferredHotspotExplicitApprovalCount = activeSourceCatalog?.anchors.Count(entry => entry.user_approved) ?? 0;
        preferredHotspotSourceHash = activeSourceCatalog?.source_anchor_list_hash ?? "NO_VALID_SOURCE_CATALOG";
        preferredHotspotCatalogHash = Sha256(liveHash.ToString());
        preferredHotspotApprovalPassed = activeSourceCatalog != null && preferredHotspots.Count >= MarkerCountPerSet;
        preferredHotspotApprovalFailure = preferredHotspotApprovalPassed
            ? string.Empty
            : activeSourceCatalog == null ? activeSourceCatalogFailure : $"usable catalog hotspots={preferredHotspots.Count}/{MarkerCountPerSet}";

        if (saveAndLog)
        {
            try
            {
                Directory.CreateDirectory(StorageFolderPath);
                File.WriteAllText(LocalizationReportPath, JsonUtility.ToJson(report, true));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[AAG Hotspot Persistence] localization report save failed: {exception.Message}");
            }
            Debug.Log($"[AAG Hotspot Recovery] source=MRUK_ROOM_LOCAL restored={report.mruk_room_local_recovered} rejected={report.unavailable}; "
                + $"spatialAnchorLocalized={report.spatial_anchor_localized}; report={LocalizationReportPath}");
            Debug.Log($"[AAG Hotspot Recovery] source={activeSourceCatalogKind} catalog={report.catalog_count} "
                + $"loadRequested={report.load_requested} found={report.found_in_quest_store} loadSucceeded={report.load_succeeded} "
                + $"localized={report.localized} missing={report.missing}; recoveryMode={report.recovery_mode}");
            LogRecoveryRoomUuidStatus(phase);
            Debug.Log($"[AAG Hotspot Approval] source={preferredHotspotSourceAnchorCount}, resolved={preferredHotspotLoadedAnchorCount}, "
                + $"allowedFp1={preferredHotspotAllowedFp1Count}, explicitlyApproved={preferredHotspotExplicitApprovalCount}, "
                + $"usable={preferredHotspots.Count}; status={(preferredHotspotApprovalPassed ? "PASSED" : "FAILED")}; "
                + $"reason=\"{SanitizeLogReason(preferredHotspotApprovalFailure)}\"");
        }
    }

    private RoomCandidatePool BuildManualHotspotCandidatePool(
        MRUKRoom room,
        Dictionary<Guid, List<Vector3>> observationCache,
        int seed)
    {
        var pool = new RoomCandidatePool { room = room };
        var observations = GetObservationPoints(room, observationCache);
        var entranceObservations = GetEntranceObservationPoints(room);
        var roomHotspots = preferredHotspots.Where(hotspot => hotspot.originalRoomUuid == room.Anchor.Uuid).ToList();
        pool.initialCount = roomHotspots.Count;
        foreach (var hotspot in roomHotspots)
        {
            if (!TryFindContainingFloor(room, hotspot.position, out var floor, out var floorWorld))
            {
                Debug.LogWarning($"[AAG Hotspot Candidate Rejection] uuid={hotspot.uuid}; stage=FLOOR; reason=OUTSIDE_FLOOR");
                continue;
            }
            pool.floorPassCount++;
            var local3 = floor.transform.InverseTransformPoint(floorWorld);
            var localPoint = new Vector2(local3.x, local3.y);
            var clearances = MeasureClearances(room, floor, floorWorld);
            if (FailsGenerationClearance(clearances.wall, minimumWallDistanceMeters + generationSafetyMarginMeters))
            {
                Debug.LogWarning($"[AAG Hotspot Candidate Rejection] uuid={hotspot.uuid}; stage=WALL_CLEARANCE; measured={clearances.wall:F3}");
                continue;
            }
            pool.wallPassCount++;
            if (FailsGenerationClearance(clearances.doorway, minimumDoorwayDistanceMeters))
            {
                Debug.LogWarning($"[AAG Hotspot Candidate Rejection] uuid={hotspot.uuid}; stage=DOORWAY_CLEARANCE; measured={clearances.doorway:F3}");
                continue;
            }
            pool.doorwayPassCount++;
            if (FailsGenerationClearance(clearances.obstacle, minimumObstacleDistanceMeters))
            {
                Debug.LogWarning($"[AAG Hotspot Candidate Rejection] uuid={hotspot.uuid}; stage=OBSTACLE_CLEARANCE; measured={clearances.obstacle:F3}");
                continue;
            }
            pool.obstaclePassCount++;

            var markerWorld = new Vector3(
                hotspot.position.x,
                floorWorld.y + PreviewObjectBottomOffsetMeters + floorGapMeters,
                hotspot.position.z);
            if (!EvaluateRoomVisibility(room, new List<Vector3> { markerWorld }, observations, out var discoveryCounts, out _)
                || discoveryCounts[0] < minimumDiscoverableObservationCount)
            {
                Debug.LogWarning($"[AAG Hotspot Candidate Rejection] uuid={hotspot.uuid}; stage=DISCOVERABILITY; count={discoveryCounts[0]}");
                continue;
            }
            pool.discoverablePassCount++;
            GetLocalBounds(floor.PlaneBoundary2D, out var min, out var max);
            var validCorners = GetValidCornerIndices(floor);
            var nearestCorner = GetNearestValidCornerIndex(floor, floorWorld, validCorners, out var cornerDistance);
            if (nearestCorner < 0) cornerDistance = GetNearestFloorVertexDistance(floor, floorWorld);
            var entranceVisibleCount = entranceObservations.Count(observation => HasMrukLineOfSight(room, observation, markerWorld));
            var candidate = new PlacementCandidate
            {
                room = room,
                floor = floor,
                localPoint = localPoint,
                floorWorld = floorWorld,
                markerWorld = markerWorld,
                zoneKey = BuildZoneKey(room, floor, localPoint, min, max),
                nearestWallSegment = GetNearestBoundarySegmentIndex(floor.PlaneBoundary2D, localPoint),
                cornerDistance = cornerDistance,
                wallClearance = clearances.wall,
                doorwayClearance = clearances.doorway,
                obstacleClearance = clearances.obstacle,
                walkingPathClearance = DistanceToMainWalkingPaths(floor, floorWorld) - PreviewMarkerRadiusMeters,
                discoverableObservationCount = discoveryCounts[0],
                visibleFromEntrance = entranceVisibleCount > 0,
                entranceVisibleObservationCount = entranceVisibleCount,
                placementType = ClassifyPlacementType(nearestCorner, cornerDistance, clearances.wall),
                deterministicVariation = GetDeterministicVariation(seed, room.Anchor.Uuid, localPoint),
                placementSource = ManualHotspotPlacementSource,
                hotspotUuid = hotspot.uuid.ToString(),
                hotspotOffsetMeters = 0f,
                hotspotSectorAngle = 0f,
                hotspotZoneGroup = hotspot.zoneGroup,
                isNearManualHotspot = true,
            };
            candidate.spatialSlotKey = BuildPlacementCandidateSlotKey(candidate);
            pool.candidates.Add(candidate);
            Debug.Log($"[AAG Hotspot Candidate] uuid={hotspot.uuid}; room={room.Anchor.Uuid}; zone={hotspot.zoneGroup}; "
                + $"anchorXZ=({hotspot.position.x:F4},{hotspot.position.z:F4}); marker=({markerWorld.x:F4},{markerWorld.y:F4},{markerWorld.z:F4}); offsetXZ=0");
        }
        pool.minimumWidthMeters = 0f;
        return pool;
    }

    private bool TryBuildManualHotspotZoneMap(IEnumerable<MRUKRoom> rooms, out Dictionary<Guid, string> result, out string failure)
    {
        result = new Dictionary<Guid, string>();
        foreach (var room in rooms)
        {
            var zones = activeSourceCatalog?.anchors
                .Where(entry => string.Equals(entry.original_room_uuid, room.Anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.zone_group)
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();
            if (zones.Count != 1)
            {
                failure = $"ROOM_CAP_MAPPING_REQUIRED room={room.Anchor.Uuid} zoneMappings={zones.Count}";
                return false;
            }
            result[room.Anchor.Uuid] = zones[0];
        }
        if (!result.TryGetValue(Room2Uuid, out var room2Zone) || room2Zone != "ZG-02"
            || !result.TryGetValue(Room3Uuid, out var room3Zone) || room3Zone != "ZG-03")
        {
            failure = $"ROOM_CAP_MAPPING_REQUIRED Room2={Room2Uuid}:ZG-02 Room3={Room3Uuid}:ZG-03";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private List<MRUKRoom> BuildManualHotspotRoomSequence(
        string setId,
        List<MRUKRoom> rooms,
        Dictionary<Guid, RoomCandidatePool> pools,
        System.Random random,
        out string failure)
    {
        var shuffled = rooms.Where(room => pools.TryGetValue(room.Anchor.Uuid, out var pool) && pool.candidates.Count > 0)
            .OrderBy(_ => random.Next()).ThenBy(room => room.Anchor.Uuid.ToString(), StringComparer.Ordinal).ToList();
        var capacities = shuffled.ToDictionary(
            room => room.Anchor.Uuid,
            room => Mathf.Min(
                pools[room.Anchor.Uuid].candidates.Count,
                room.Anchor.Uuid == Room2Uuid ? room2MaximumMarkers : room.Anchor.Uuid == Room3Uuid ? room3MaximumMarkers : int.MaxValue));
        if (capacities.Sum(pair => pair.Value) < MarkerCountPerSet)
        {
            failure = $"manual hotspot room capacity={capacities.Sum(pair => pair.Value)}/{MarkerCountPerSet}";
            return new List<MRUKRoom>();
        }

        var used = capacities.Keys.ToDictionary(uuid => uuid, _ => 0);
        var sequence = new List<MRUKRoom>(MarkerCountPerSet);
        while (sequence.Count < MarkerCountPerSet)
        {
            var progressed = false;
            foreach (var room in shuffled.OrderBy(room => used[room.Anchor.Uuid]).ThenBy(_ => random.Next()))
            {
                if (used[room.Anchor.Uuid] >= capacities[room.Anchor.Uuid]) continue;
                sequence.Add(room);
                used[room.Anchor.Uuid]++;
                progressed = true;
                if (sequence.Count == MarkerCountPerSet) break;
            }
            if (!progressed) break;
        }
        failure = sequence.Count == MarkerCountPerSet
            ? string.Empty
            : $"manual room sequence incomplete={sequence.Count}/{MarkerCountPerSet}";
        Debug.Log($"[AAG Manual Room Quota] set={setId}; counts=[{string.Join(",", used.Where(pair => pair.Value > 0).Select(pair => pair.Key + "=" + pair.Value))}]; "
            + $"Room2Max={room2MaximumMarkers}; Room3Max={room3MaximumMarkers}");
        return sequence;
    }

    private bool ValidateManualHotspotFeasibility(
        Dictionary<Guid, RoomCandidatePool> pools,
        Dictionary<Guid, int> requiredRoomCounts,
        out string failure)
    {
        foreach (var pair in requiredRoomCounts)
        {
            if (!pools.TryGetValue(pair.Key, out var pool) || pool.candidates.Count < pair.Value)
            {
                failure = $"manual hotspot capacity room={pair.Key} candidates={pool?.candidates.Count ?? 0} required={pair.Value}";
                return false;
            }
        }
        if (requiredRoomCounts.TryGetValue(Room2Uuid, out var room2Count) && room2Count > room2MaximumMarkers)
        {
            failure = $"Room2 count={room2Count}>{room2MaximumMarkers}";
            return false;
        }
        if (requiredRoomCounts.TryGetValue(Room3Uuid, out var room3Count) && room3Count > room3MaximumMarkers)
        {
            failure = $"Room3 count={room3Count}>{room3MaximumMarkers}";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private bool TryPrepareMixedPlacement(
        string setId,
        List<MRUKRoom> roomSequence,
        Dictionary<Guid, RoomCandidatePool> pools,
        out HashSet<int> manualMarkerIndices,
        out string failure)
    {
        manualMarkerIndices = new HashSet<int>(Enumerable.Range(0, MarkerCountPerSet));
        if (!preferredHotspotApprovalPassed)
        {
            failure = $"hotspot source/localization failed: {preferredHotspotApprovalFailure}";
            LogCandidateGenerationStage(setId, "HOTSPOT_PERSISTENCE", false, failure);
            return false;
        }
        var candidateCount = pools.Values.Sum(pool => pool.candidates.Count);
        hardValidManualHotspotCount = candidateCount;
        if (candidateCount < MarkerCountPerSet)
        {
            failure = $"hard-valid localized manual hotspots={candidateCount}/{MarkerCountPerSet}; procedural fallback disabled";
            LogCandidateGenerationStage(setId, "MANUAL_HOTSPOT_PREFLIGHT", false, failure);
            return false;
        }
        LogCandidateGenerationStage(setId, "MANUAL_HOTSPOT_PREFLIGHT", true,
            $"catalog={preferredHotspotSourceAnchorCount}, resolved={preferredHotspotLoadedAnchorCount}, recoveryPriority=SPATIAL_ANCHOR_LOCALIZED>MRUK_ROOM_LOCAL_RECOVERY>UNAVAILABLE, hardValid={candidateCount}, manual=12, procedural=0");
        failure = string.Empty;
        return true;
    }

    private static void LogCandidateGenerationStage(string setId, string stage, bool passed, string reason)
    {
        var message = $"[AAG Candidate Stage] set={setId}; stage={stage}; passed={BoolText(passed)}; "
            + $"rejectionReason=\"{(passed ? string.Empty : SanitizeLogReason(reason))}\"; detail=\"{(passed ? SanitizeLogReason(reason) : string.Empty)}\"";
        if (passed) Debug.Log(message); else Debug.LogError(message);
    }

    private bool IsCandidateEligibleForMixedSource(
        string setId,
        string color,
        PlacementCandidate candidate,
        bool manualRequired,
        List<PlacementCandidate> selectedCandidates)
    {
        if (!manualRequired || !IsManualHotspotCandidate(candidate)) return false;
        if (selectedCandidates.Any(existing => string.Equals(existing.hotspotUuid, candidate.hotspotUuid, StringComparison.Ordinal)
            || AreAdjacentHotspots(existing, candidate))) return false;
        return PassesHotspotCrossSetRules(setId, color, candidate);
    }

    private bool PassesHotspotCrossSetRules(string setId, string color, PlacementCandidate candidate)
    {
        var other = GetOtherSetPlacements(setId).ToList();
        var enforceGlobalUnique = hardValidManualHotspotCount >= UniqueHotspotsRequiredForTriplet;
        foreach (var record in other)
        {
            var sameUuid = string.Equals(record.hotspot_uuid, candidate.hotspotUuid, StringComparison.Ordinal);
            var adjacent = AreAdjacentHotspotRecordAndCandidate(record, candidate);
            if (enforceGlobalUnique && sameUuid) return false;
            if (AreConsecutiveSets(setId, record.set_id) && (sameUuid || adjacent)) return false;
            if (string.Equals(record.color, color, StringComparison.OrdinalIgnoreCase) && (sameUuid || adjacent)) return false;
        }
        return true;
    }

    private bool AreAdjacentHotspots(PlacementCandidate left, PlacementCandidate right)
    {
        return left.room.Anchor.Uuid == right.room.Anchor.Uuid
            && string.Equals(left.hotspotZoneGroup, right.hotspotZoneGroup, StringComparison.Ordinal)
            && HorizontalDistance(left.markerWorld, right.markerWorld) <= manualHotspotAdjacencyMeters + validationEpsilonMeters;
    }

    private bool AreAdjacentHotspotRecordAndCandidate(AagPlacementRecord record, PlacementCandidate candidate)
    {
        if (!Guid.TryParse(record.room_uuid, out var roomUuid) || roomUuid != candidate.room.Anchor.Uuid) return false;
        var source = activeSourceCatalog?.anchors.FirstOrDefault(entry => string.Equals(entry.uuid, record.hotspot_uuid, StringComparison.Ordinal));
        return source != null
            && string.Equals(source.zone_group, candidate.hotspotZoneGroup, StringComparison.Ordinal)
            && HorizontalDistance(ToVector3(record), candidate.markerWorld) <= manualHotspotAdjacencyMeters + validationEpsilonMeters;
    }

    private bool AreAdjacentHotspotRecords(AagPlacementRecord left, AagPlacementRecord right)
    {
        if (!string.Equals(left.room_uuid, right.room_uuid, StringComparison.Ordinal)) return false;
        var leftSource = activeSourceCatalog?.anchors.FirstOrDefault(entry => string.Equals(entry.uuid, left.hotspot_uuid, StringComparison.Ordinal));
        var rightSource = activeSourceCatalog?.anchors.FirstOrDefault(entry => string.Equals(entry.uuid, right.hotspot_uuid, StringComparison.Ordinal));
        return leftSource != null && rightSource != null
            && string.Equals(leftSource.zone_group, rightSource.zone_group, StringComparison.Ordinal)
            && HorizontalDistance(ToVector3(left), ToVector3(right)) <= manualHotspotAdjacencyMeters + validationEpsilonMeters;
    }

    private static bool AreConsecutiveSets(string left, string right)
    {
        var leftIndex = Array.IndexOf(SetIds, left);
        var rightIndex = Array.IndexOf(SetIds, right);
        return leftIndex >= 0 && rightIndex >= 0 && Mathf.Abs(leftIndex - rightIndex) == 1;
    }

    private IEnumerable<AagPlacementRecord> GetOtherSetPlacements(string setId)
    {
        foreach (var otherSet in SetIds.Where(value => !string.Equals(value, setId, StringComparison.Ordinal)))
        {
            if (confirmedBySet.TryGetValue(otherSet, out var confirmed))
                foreach (var record in confirmed) yield return record;
            else if (unconfirmedBySet.TryGetValue(otherSet, out var draft))
                foreach (var record in draft) yield return record;
        }
    }

    private int GetHotspotPriorUseCount(string setId, string hotspotUuid)
    {
        return GetOtherSetPlacements(setId).Count(record => string.Equals(record.hotspot_uuid, hotspotUuid, StringComparison.Ordinal));
    }

    private static bool IsManualHotspotCandidate(PlacementCandidate candidate)
    {
        return candidate != null && string.Equals(candidate.placementSource, ManualHotspotPlacementSource, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(candidate.hotspotUuid);
    }

    private static bool IsManualHotspotRecord(AagPlacementRecord record)
    {
        return record != null && string.Equals(record.placementSource, ManualHotspotPlacementSource, StringComparison.Ordinal);
    }

    private static bool MatchesMixedPlacementIdentity(PlacementCandidate candidate, PlacementCandidate original)
    {
        return candidate != null && original != null
            && string.Equals(candidate.placementSource, original.placementSource, StringComparison.Ordinal)
            && string.Equals(candidate.hotspotUuid, original.hotspotUuid, StringComparison.Ordinal);
    }

    private static void ApplyCandidateMetadata(AagPlacementRecord record, PlacementCandidate candidate)
    {
        record.placementSource = ManualHotspotPlacementSource;
        record.hotspot_uuid = candidate.hotspotUuid;
        record.hotspot_offset_meters = 0f;
        record.hotspot_sector_angle = 0f;
        record.spatial_slot_key = candidate.spatialSlotKey;
    }

    private bool ValidateMixedPlacementComposition(string setId, List<AagPlacementRecord> placements, out string failure)
    {
        var manual = placements.Where(IsManualHotspotRecord).ToList();
        var procedural = placements.Where(record => string.Equals(record.placementSource, ProceduralPlacementSource, StringComparison.Ordinal)).ToList();
        var unique = manual.Select(record => record.hotspot_uuid).Distinct(StringComparer.Ordinal).Count();
        if (manual.Count != ManualMarkersPerSet || procedural.Count != 0 || unique != ManualMarkersPerSet)
        {
            failure = $"manual hotspot composition invalid manual={manual.Count}/12 procedural={procedural.Count}/0 unique={unique}/12";
            return false;
        }
        for (var left = 0; left < manual.Count; left++)
        for (var right = left + 1; right < manual.Count; right++)
        {
            if (AreAdjacentHotspotRecords(manual[left], manual[right]))
            {
                failure = $"same-set adjacent hotspots={manual[left].hotspot_uuid}:{manual[right].hotspot_uuid}";
                return false;
            }
        }
        failure = string.Empty;
        foreach (var record in placements)
            Debug.Log($"[AAG Manual Hotspot Marker] set={setId}; marker={record.answer_marker_id}; hotspot={record.hotspot_uuid.Substring(0, 8)}; offsetXZ=0");
        return true;
    }

    private void LogMixedPlacementResult(string setId, List<AagPlacementRecord> placements, bool ready, string reason)
    {
        var manual = placements.Count(IsManualHotspotRecord);
        var procedural = placements.Count(record => string.Equals(record.placementSource, ProceduralPlacementSource, StringComparison.Ordinal));
        var unique = placements.Where(IsManualHotspotRecord).Select(record => record.hotspot_uuid).Distinct(StringComparer.Ordinal).Count();
        Debug.Log($"[AAG Manual Hotspot Placement] set={setId} manual={manual} procedural={procedural} uniqueHotspots={unique} "
            + $"ready={BoolText(ready)} reason=\"{SanitizeLogReason(reason)}\"");
    }

    private bool EvaluateMixedTripletGate(CandidateSnapshot[] triplet, out string reason)
    {
        var failures = new List<string>();
        foreach (var candidate in triplet)
        {
            var manual = candidate.placements.Where(IsManualHotspotRecord).ToList();
            var procedural = candidate.placements.Count(record => string.Equals(record.placementSource, ProceduralPlacementSource, StringComparison.Ordinal));
            if (manual.Count != 12 || procedural != 0 || manual.Select(record => record.hotspot_uuid).Distinct(StringComparer.Ordinal).Count() != 12)
                failures.Add($"{candidate.set_id}:expectedManual12Procedural0Unique12");
            for (var left = 0; left < manual.Count; left++)
            for (var right = left + 1; right < manual.Count; right++)
                if (AreAdjacentHotspotRecords(manual[left], manual[right])) failures.Add($"{candidate.set_id}:sameSetAdjacent");
            var roomCounts = manual.GroupBy(record => record.room_uuid).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            if (roomCounts.TryGetValue(Room2Uuid.ToString(), out var room2) && room2 > room2MaximumMarkers) failures.Add($"{candidate.set_id}:Room2={room2}>{room2MaximumMarkers}");
            if (roomCounts.TryGetValue(Room3Uuid.ToString(), out var room3) && room3 > room3MaximumMarkers) failures.Add($"{candidate.set_id}:Room3={room3}>{room3MaximumMarkers}");
        }

        var all = triplet.SelectMany(candidate => candidate.placements).Where(IsManualHotspotRecord).ToList();
        if (hardValidManualHotspotCount >= UniqueHotspotsRequiredForTriplet
            && all.Select(record => record.hotspot_uuid).Distinct(StringComparer.Ordinal).Count() != UniqueHotspotsRequiredForTriplet)
            failures.Add("globalUniqueHotspotReuseDespite36Available");
        for (var left = 0; left < all.Count; left++)
        for (var right = left + 1; right < all.Count; right++)
        {
            if (string.Equals(all[left].set_id, all[right].set_id, StringComparison.Ordinal)) continue;
            var same = string.Equals(all[left].hotspot_uuid, all[right].hotspot_uuid, StringComparison.Ordinal);
            var adjacent = AreAdjacentHotspotRecords(all[left], all[right]);
            if (AreConsecutiveSets(all[left].set_id, all[right].set_id) && (same || adjacent)) failures.Add("consecutiveSetHotspotCarryover");
            if (string.Equals(all[left].color, all[right].color, StringComparison.OrdinalIgnoreCase) && (same || adjacent)) failures.Add("sameColorHotspotCarryover");
        }
        reason = failures.Count == 0 ? "manualHotspotTripletPassed" : string.Join(",", failures.Distinct());
        return failures.Count == 0;
    }

    private static string BuildPlacementCandidateSlotKey(PlacementCandidate candidate)
    {
        if (candidate == null || candidate.floor == null) return null;
        return $"{candidate.floor.Anchor.Uuid}:HOTSPOT:{candidate.hotspotUuid}";
    }

    private void CreatePlacementSourcePreviewIcon(GameObject marker, AagPlacementRecord placement)
    {
        var icon = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        icon.name = "User Approved Manual Hotspot Icon";
        icon.transform.SetParent(marker.transform, false);
        SetLayerRecursively(icon, PreviewMarkerLayer);
        icon.transform.localPosition = new Vector3(0f, 0.75f, 0f);
        icon.transform.localScale = new Vector3(0.45f, 0.08f, 0.45f);
        var collider = icon.GetComponent<Collider>();
        if (collider != null) { collider.enabled = false; Destroy(collider); }
        var renderer = icon.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = new Color(0.1f, 1f, 1f, 1f);
    }
}
