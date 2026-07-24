using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Exports the MRUK rooms currently loaded from Quest Space Setup without creating
/// any candidate object locations. The export is intentionally a capture of the
/// current MRUK world frame, not a room-to-floor-plan transform.
/// </summary>
public class AagRoomCoordinateExporter : MonoBehaviour
{
    private const string ExportFolderName = "AagRoomExports";
    private const string SchemaVersion = "aag-room-coordinate-export/v1";

    [Serializable]
    public class RoomMappingEntry
    {
        [Tooltip("Copy MRUKRoom.Anchor.Uuid from a first export. Leave empty until a real room ID is known.")]
        public string roomId;

        [Tooltip("Researcher-assigned name only; this never changes the MRUK room ID or its coordinates.")]
        public string displayName;
    }

    [Header("Export timing")]
    [Tooltip("Exports once after MRUK reports that the complete Scene has loaded. Call ExportNow() from a UnityEvent to export again.")]
    [SerializeField] private bool exportOnStart = true;

    [Tooltip("Maximum time to wait for MRUK scene data on device.")]
    [SerializeField] private float waitForMrukSeconds = 20f;

    [Header("Runtime diagnostics")]
    [Tooltip("Shows the exporter state in the running app as well as writing [AAG Room Export] logcat messages.")]
    [SerializeField] private bool showRuntimeDiagnostics = true;

    [Header("Optional researcher mapping")]
    [Tooltip("Optional labels keyed by the real room UUID. No sample IDs or room names are supplied by this project.")]
    [SerializeField] private List<RoomMappingEntry> manualRoomMappings = new List<RoomMappingEntry>();

    private bool isExporting;
    private string diagnosticStatus = "Exporter not started";
    private string diagnosticDetail = string.Empty;
    private int lastObservedRoomCount = int.MinValue;
    private float lastWaitingLogTime = float.NegativeInfinity;

    private void Awake()
    {
        SetDiagnostic("Exporter Awake", $"active={gameObject.activeInHierarchy}, enabled={enabled}, path={Application.persistentDataPath}");
    }

    private void Start()
    {
        SetDiagnostic("Exporter started", $"exportOnStart={exportOnStart}, timeout={waitForMrukSeconds:F1}s");
        if (exportOnStart)
        {
            ExportNow();
        }
    }

    /// <summary>Starts a new export. Safe to call from the Inspector or a UnityEvent.</summary>
    public void ExportNow()
    {
        if (isExporting)
        {
            Log("ExportNow ignored because an export is already waiting or running.");
            return;
        }

        Log("ExportNow requested; starting MRUK readiness check.");
        StartCoroutine(ExportWhenMrukReady());
    }

    private IEnumerator ExportWhenMrukReady()
    {
        isExporting = true;
        var elapsed = 0f;
        lastObservedRoomCount = int.MinValue;
        lastWaitingLogTime = float.NegativeInfinity;
        UpdateWaitingDiagnostics(elapsed, true);

        // Rooms are populated incrementally while MRUK loads a device Scene.  Waiting
        // only for Rooms.Count > 0 can therefore export a truncated room list.  MRUK's
        // IsInitialized flag is set only after its final SceneLoadedEvent has fired.
        while ((MRUK.Instance == null || !MRUK.Instance.IsInitialized) && elapsed < waitForMrukSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            UpdateWaitingDiagnostics(elapsed, false);
            yield return null;
        }

        if (MRUK.Instance == null)
        {
            SetDiagnostic("No MRUK instance", $"Timed out after {elapsed:F1}s. No files were written.", true);
            isExporting = false;
            yield break;
        }

        if (!MRUK.Instance.IsInitialized)
        {
            SetDiagnostic("MRUK load incomplete", $"Timed out after {elapsed:F1}s before SceneLoadedEvent. No files were written.", true);
            isExporting = false;
            yield break;
        }

        if (MRUK.Instance.Rooms.Count == 0)
        {
            SetDiagnostic("No rooms", $"MRUK instance found, but room count stayed 0 for {elapsed:F1}s. No files were written.", true);
            isExporting = false;
            yield break;
        }

        try
        {
            SetDiagnostic("Exporting", $"MRUK SceneLoadedEvent complete; room count={MRUK.Instance.Rooms.Count}");
            WriteExport(MRUK.Instance.Rooms);
            SetDiagnostic("Export succeeded", $"rooms={MRUK.Instance.Rooms.Count}; files written under {ExportFolderName}");
        }
        catch (Exception exception)
        {
            SetDiagnostic("Export failed", exception.ToString(), true);
        }
        finally
        {
            isExporting = false;
        }
    }

    private void WriteExport(List<MRUKRoom> rooms)
    {
        var export = new AagRoomCoordinateExport
        {
            schemaVersion = SchemaVersion,
            exportedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            source = "MRUK rooms loaded on this device from the Meta Quest Space Setup scene",
            coordinateSystem = new CoordinateSystemDescription(),
            persistentLogStatus = ReadExistingLogStatus(),
        };

        foreach (var room in rooms)
        {
            export.rooms.Add(BuildRoomRecord(room, export.validationWarnings));
        }

        var folderPath = Path.Combine(Application.persistentDataPath, ExportFolderName);
        Directory.CreateDirectory(folderPath);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(folderPath, $"aag_room_coordinates_{timestamp}.json");
        var csvPath = Path.Combine(folderPath, $"aag_room_coordinates_{timestamp}.csv");

        File.WriteAllText(jsonPath, JsonUtility.ToJson(export, true), Encoding.UTF8);
        File.WriteAllText(csvPath, BuildCsv(export), Encoding.UTF8);

        Debug.Log($"[AAG Room Export] Wrote {export.rooms.Count} room(s). JSON: {jsonPath}");
        Debug.Log($"[AAG Room Export] CSV: {csvPath}");
        foreach (var warning in export.validationWarnings)
        {
            Debug.LogWarning($"[AAG Room Export] {warning}");
        }
    }

    private AagRoomRecord BuildRoomRecord(MRUKRoom room, List<string> warnings)
    {
        var roomId = GetAnchorId(room.Anchor);
        var record = new AagRoomRecord
        {
            roomId = roomId,
            roomIdSource = "MRUKRoom.Anchor.Uuid",
            roomName = room.name,
            manualMapping = FindManualMapping(roomId),
            roomAnchorWorld = ToVector3Record(room.transform.position),
            roomAnchorRotation = ToQuaternionRecord(room.transform.rotation),
            anchorCount = room.Anchors.Count,
        };

        if (string.IsNullOrEmpty(roomId))
        {
            warnings.Add($"Room named '{room.name}' has no readable MRUK room UUID.");
        }

        var floorVertices = new List<Vector3>();
        foreach (var anchor in room.Anchors)
        {
            var surface = BuildSurfaceRecord(anchor);
            record.surfaces.Add(surface);

            if (surface.isFloor)
            {
                record.floorAnchorIds.Add(surface.anchorId);
                foreach (var vertex in surface.planeBoundaryWorld)
                {
                    floorVertices.Add(ToUnityVector3(vertex));
                }

                if (surface.planeBoundaryWorld.Count == 0)
                {
                    warnings.Add($"Room {RoomWarningName(record)} floor anchor {surface.anchorId} has no PlaneBoundary2D vertices.");
                }
            }
        }

        if (record.floorAnchorIds.Count == 0)
        {
            warnings.Add($"Room {RoomWarningName(record)} has no MRUK floor anchor.");
        }

        if (floorVertices.Count > 0)
        {
            var bounds = new Bounds(floorVertices[0], Vector3.zero);
            for (var index = 1; index < floorVertices.Count; index++)
            {
                bounds.Encapsulate(floorVertices[index]);
            }

            record.centerWorld = ToVector3Record(bounds.center);
            record.centerMethod = "axis-aligned bounding-box center of all exported floor-boundary vertices";
        }
        else
        {
            record.centerWorld = ToVector3Record(room.transform.position);
            record.centerMethod = "MRUK room-anchor transform position; no floor-boundary vertices were available";
        }

        return record;
    }

    private void UpdateWaitingDiagnostics(float elapsed, bool forceLog)
    {
        var hasMrukInstance = MRUK.Instance != null;
        var roomCount = hasMrukInstance ? MRUK.Instance.Rooms.Count : -1;
        var shouldLog = forceLog || roomCount != lastObservedRoomCount || elapsed - lastWaitingLogTime >= 2f;
        if (!shouldLog)
        {
            return;
        }

        lastObservedRoomCount = roomCount;
        lastWaitingLogTime = elapsed;
        var detail = hasMrukInstance
            ? $"MRUK instance found; roomCount={roomCount}; elapsed={elapsed:F1}s/{waitForMrukSeconds:F1}s"
            : $"MRUK instance=null; elapsed={elapsed:F1}s/{waitForMrukSeconds:F1}s";
        SetDiagnostic("Waiting for MRUK", detail);
    }

    private void SetDiagnostic(string status, string detail, bool isError = false)
    {
        diagnosticStatus = status;
        diagnosticDetail = detail;
        if (isError)
        {
            Debug.LogError($"[AAG Room Export] {status}: {detail}");
        }
        else
        {
            Debug.Log($"[AAG Room Export] {status}: {detail}");
        }
    }

    private void Log(string message)
    {
        Debug.Log($"[AAG Room Export] {message}");
    }

    private void OnGUI()
    {
        if (!showRuntimeDiagnostics)
        {
            return;
        }

        var previousColor = GUI.color;
        GUI.color = Color.black;
        GUI.Box(new Rect(16, 16, 980, 112), GUIContent.none);
        GUI.color = Color.white;
        GUI.Label(new Rect(28, 26, 950, 28), $"[AAG Room Export] {diagnosticStatus}");
        GUI.Label(new Rect(28, 58, 950, 54), diagnosticDetail);
        GUI.color = previousColor;
    }

    private AagSurfaceRecord BuildSurfaceRecord(MRUKAnchor anchor)
    {
        var record = new AagSurfaceRecord
        {
            anchorId = GetAnchorId(anchor.Anchor),
            label = anchor.Label.ToString(),
            isFloor = (anchor.Label & MRUKAnchor.SceneLabels.FLOOR) != 0,
            worldPosition = ToVector3Record(anchor.transform.position),
            worldRotation = ToQuaternionRecord(anchor.transform.rotation),
        };

        if (anchor.PlaneRect.HasValue)
        {
            var planeRect = anchor.PlaneRect.Value;
            record.hasPlaneRect = true;
            record.planeRectX = planeRect.x;
            record.planeRectY = planeRect.y;
            record.planeRectWidth = planeRect.width;
            record.planeRectHeight = planeRect.height;
        }

        if (anchor.PlaneBoundary2D == null)
        {
            return record;
        }

        foreach (var localVertex in anchor.PlaneBoundary2D)
        {
            record.planeBoundaryLocal.Add(new AagVector2 { x = localVertex.x, y = localVertex.y });
            var worldVertex = anchor.transform.TransformPoint(new Vector3(localVertex.x, localVertex.y, 0f));
            record.planeBoundaryWorld.Add(ToVector3Record(worldVertex));
        }

        return record;
    }

    private PersistentLogStatus ReadExistingLogStatus()
    {
        return new PersistentLogStatus
        {
            persistentDataPath = Application.persistentDataPath,
            anchorLogPath = Path.Combine(Application.persistentDataPath, "anchor_log.json"),
            anchorLogExists = File.Exists(Path.Combine(Application.persistentDataPath, "anchor_log.json")),
            trackLogPath = Path.Combine(Application.persistentDataPath, "track_log.json"),
            trackLogExists = File.Exists(Path.Combine(Application.persistentDataPath, "track_log.json")),
            sceneLogPath = Path.Combine(Application.persistentDataPath, "scene_log.txt"),
            sceneLogExists = File.Exists(Path.Combine(Application.persistentDataPath, "scene_log.txt")),
        };
    }

    private string FindManualMapping(string roomId)
    {
        if (string.IsNullOrEmpty(roomId))
        {
            return string.Empty;
        }

        foreach (var mapping in manualRoomMappings)
        {
            if (mapping != null && string.Equals(mapping.roomId?.Trim(), roomId, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.displayName?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string GetAnchorId(OVRAnchor anchor)
    {
        return anchor.Uuid == Guid.Empty ? string.Empty : anchor.Uuid.ToString();
    }

    private static string RoomWarningName(AagRoomRecord room)
    {
        return string.IsNullOrEmpty(room.roomId) ? $"'{room.roomName}'" : room.roomId;
    }

    private static AagVector3 ToVector3Record(Vector3 vector)
    {
        return new AagVector3 { x = vector.x, y = vector.y, z = vector.z };
    }

    private static Vector3 ToUnityVector3(AagVector3 vector)
    {
        return new Vector3(vector.x, vector.y, vector.z);
    }

    private static AagQuaternion ToQuaternionRecord(Quaternion quaternion)
    {
        return new AagQuaternion { x = quaternion.x, y = quaternion.y, z = quaternion.z, w = quaternion.w };
    }

    private static string BuildCsv(AagRoomCoordinateExport export)
    {
        var csv = new StringBuilder();
        csv.AppendLine("record_type,room_id,room_name,manual_mapping,center_method,anchor_id,anchor_label,vertex_index,world_x,world_y,world_z,local_x,local_y");

        foreach (var room in export.rooms)
        {
            AppendCsvRow(csv, "room_center", room, string.Empty, string.Empty, -1, room.centerWorld, null);
            foreach (var surface in room.surfaces)
            {
                for (var index = 0; index < surface.planeBoundaryWorld.Count; index++)
                {
                    AppendCsvRow(csv, "plane_boundary_vertex", room, surface.anchorId, surface.label, index, surface.planeBoundaryWorld[index], surface.planeBoundaryLocal[index]);
                }
            }
        }

        return csv.ToString();
    }

    private static void AppendCsvRow(StringBuilder csv, string recordType, AagRoomRecord room, string anchorId, string anchorLabel, int vertexIndex, AagVector3 world, AagVector2? local)
    {
        var fields = new[]
        {
            recordType,
            room.roomId,
            room.roomName,
            room.manualMapping,
            room.centerMethod,
            anchorId,
            anchorLabel,
            vertexIndex < 0 ? string.Empty : vertexIndex.ToString(CultureInfo.InvariantCulture),
            world.x.ToString("R", CultureInfo.InvariantCulture),
            world.y.ToString("R", CultureInfo.InvariantCulture),
            world.z.ToString("R", CultureInfo.InvariantCulture),
            local.HasValue ? local.Value.x.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
            local.HasValue ? local.Value.y.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
        };

        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                csv.Append(',');
            }

            csv.Append(CsvEscape(fields[index]));
        }

        csv.AppendLine();
    }

    private static string CsvEscape(string value)
    {
        return $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
    }

    [Serializable]
    private class AagRoomCoordinateExport
    {
        public string schemaVersion;
        public string exportedAtUtc;
        public string source;
        public CoordinateSystemDescription coordinateSystem;
        public PersistentLogStatus persistentLogStatus;
        public List<AagRoomRecord> rooms = new List<AagRoomRecord>();
        public List<string> validationWarnings = new List<string>();
    }

    [Serializable]
    private class CoordinateSystemDescription
    {
        public string frame = "Unity world coordinates read from MRUK transforms at export time";
        public string axes = "Unity convention: x=right, y=up, z=forward";
        public string units = "Unity world units; Quest/MRUK scene geometry is expected at meter scale (treat 1 unit as 1 meter)";
        public string boundaryVertexFrame = "planeBoundaryLocal is MRUKAnchor local XY; planeBoundaryWorld is TransformPoint(localX, localY, 0) in Unity world";
        public string reuseReference = "MRUK room UUID and floor-anchor UUID/pose are recorded. Raw Unity world coordinates are not automatically guaranteed to share one origin after a new Space Setup, a changed scan, or a different device.";
        public string automaticAlignment = "not performed";
    }

    [Serializable]
    private class PersistentLogStatus
    {
        public string persistentDataPath;
        public string anchorLogPath;
        public bool anchorLogExists;
        public string trackLogPath;
        public bool trackLogExists;
        public string sceneLogPath;
        public bool sceneLogExists;
    }

    [Serializable]
    private class AagRoomRecord
    {
        public string roomId;
        public string roomIdSource;
        public string roomName;
        public string manualMapping;
        public AagVector3 centerWorld;
        public string centerMethod;
        public AagVector3 roomAnchorWorld;
        public AagQuaternion roomAnchorRotation;
        public int anchorCount;
        public List<string> floorAnchorIds = new List<string>();
        public List<AagSurfaceRecord> surfaces = new List<AagSurfaceRecord>();
    }

    [Serializable]
    private class AagSurfaceRecord
    {
        public string anchorId;
        public string label;
        public bool isFloor;
        public AagVector3 worldPosition;
        public AagQuaternion worldRotation;
        public bool hasPlaneRect;
        public float planeRectX;
        public float planeRectY;
        public float planeRectWidth;
        public float planeRectHeight;
        public List<AagVector2> planeBoundaryLocal = new List<AagVector2>();
        public List<AagVector3> planeBoundaryWorld = new List<AagVector3>();
    }

    [Serializable]
    private struct AagVector2
    {
        public float x;
        public float y;
    }

    [Serializable]
    private struct AagVector3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    private struct AagQuaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }
}
