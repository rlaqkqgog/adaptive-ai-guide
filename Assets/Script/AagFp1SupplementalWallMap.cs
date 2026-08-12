using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Secondary FP1 wall reference built from the 2026-08-12 rescan.
///
/// The original baked scan remains authoritative for AprilTag alignment,
/// room/floor poses, and authored placements. This map only vetoes an original
/// floor-local point when the corresponding rescanned room says that point is
/// outside its floor or too close to a changed wall/pillar.
/// </summary>
public static class AagFp1SupplementalWallMap
{
    public const string ResourcePath = "AAG/fp1_supplemental_wall_map";

    [Serializable]
    private sealed class FileData
    {
        public int schemaVersion;
        public RoomData[] rooms;
    }

    [Serializable]
    private sealed class RoomData
    {
        public string logicalName;
        public string oldRoomId;
        public string newRoomName;
        public string newRoomId;
        public string newFloorAnchorId;
        public Transform2D oldToNew;
        public Vector2[] newFloorBoundary;
        public WallSegment[] newWallSegments;
    }

    [Serializable]
    private sealed class Transform2D
    {
        public float cos;
        public float sin;
        public float tx;
        public float ty;
    }

    [Serializable]
    private sealed class WallSegment
    {
        public string anchorId;
        public string label;
        public Vector2 start;
        public Vector2 end;
    }

    private static Dictionary<Guid, RoomData> roomsByOldUuid;
    private static string loadFailure;

    public static int RoomCount
    {
        get
        {
            EnsureLoaded();
            return roomsByOldUuid?.Count ?? 0;
        }
    }

    public static string LoadFailure
    {
        get
        {
            EnsureLoaded();
            return loadFailure ?? string.Empty;
        }
    }

    public static bool TryGetMapping(
        Guid oldRoomUuid,
        out Guid newRoomUuid,
        out Guid newFloorAnchorUuid,
        out string logicalName,
        out string newRoomName)
    {
        newRoomUuid = Guid.Empty;
        newFloorAnchorUuid = Guid.Empty;
        logicalName = string.Empty;
        newRoomName = string.Empty;
        if (!TryGetRoom(oldRoomUuid, out var room)
            || !Guid.TryParse(room.newRoomId, out newRoomUuid)
            || !Guid.TryParse(room.newFloorAnchorId, out newFloorAnchorUuid))
            return false;
        logicalName = room.logicalName ?? string.Empty;
        newRoomName = room.newRoomName ?? string.Empty;
        return true;
    }

    public static bool TryMapOldFloorPoint(
        Guid oldRoomUuid,
        Vector2 oldFloorLocalPoint,
        out Vector2 newFloorLocalPoint)
    {
        newFloorLocalPoint = oldFloorLocalPoint;
        if (!TryGetRoom(oldRoomUuid, out var room) || room.oldToNew == null)
            return false;
        var transform = room.oldToNew;
        newFloorLocalPoint = new Vector2(
            transform.cos * oldFloorLocalPoint.x
                - transform.sin * oldFloorLocalPoint.y + transform.tx,
            transform.sin * oldFloorLocalPoint.x
                + transform.cos * oldFloorLocalPoint.y + transform.ty);
        return IsFinite(newFloorLocalPoint);
    }

    /// <returns>
    /// True when the rescan mapping was available and evaluated. The safety
    /// result is returned separately so missing supplemental data never
    /// replaces or invalidates the original baked reference.
    /// </returns>
    public static bool TryIsSafe(
        Guid oldRoomUuid,
        Vector2 oldFloorLocalPoint,
        float clearanceMeters,
        out bool safe,
        out string reason)
    {
        safe = false;
        reason = string.Empty;
        if (!TryGetRoom(oldRoomUuid, out var room)
            || room.newFloorBoundary == null
            || room.newFloorBoundary.Length < 3
            || !TryMapOldFloorPoint(oldRoomUuid, oldFloorLocalPoint, out var mapped))
            return false;

        if (!IsPointInPolygon(mapped, room.newFloorBoundary))
        {
            reason = "outside_rescanned_floor";
            return true;
        }

        if (DistanceToBoundary(mapped, room.newFloorBoundary) < clearanceMeters)
        {
            reason = "near_rescanned_floor_boundary";
            return true;
        }

        foreach (var wall in room.newWallSegments ?? Array.Empty<WallSegment>())
        {
            if (wall == null) continue;
            if (DistancePointToSegment(mapped, wall.start, wall.end) >= clearanceMeters)
                continue;
            reason = $"near_rescanned_wall_{wall.anchorId}";
            return true;
        }

        safe = true;
        reason = "rescan_clear";
        return true;
    }

    /// <summary>
    /// Returns the actual free-space margin in the supplemental rescan. A
    /// negative result means the mapped point is outside the rescanned floor.
    /// </summary>
    public static bool TryGetClearance(
        Guid oldRoomUuid,
        Vector2 oldFloorLocalPoint,
        out float clearanceMeters)
    {
        clearanceMeters = float.NegativeInfinity;
        if (!TryGetRoom(oldRoomUuid, out var room)
            || room.newFloorBoundary == null
            || room.newFloorBoundary.Length < 3
            || !TryMapOldFloorPoint(oldRoomUuid, oldFloorLocalPoint, out var mapped))
            return false;

        if (!IsPointInPolygon(mapped, room.newFloorBoundary)) return true;

        clearanceMeters = DistanceToBoundary(mapped, room.newFloorBoundary);
        foreach (var wall in room.newWallSegments ?? Array.Empty<WallSegment>())
        {
            if (wall == null) continue;
            clearanceMeters = Mathf.Min(
                clearanceMeters,
                DistancePointToSegment(mapped, wall.start, wall.end));
        }
        return true;
    }

    private static bool TryGetRoom(Guid oldRoomUuid, out RoomData room)
    {
        EnsureLoaded();
        if (roomsByOldUuid != null && roomsByOldUuid.TryGetValue(oldRoomUuid, out room))
            return true;
        room = null;
        return false;
    }

    private static void EnsureLoaded()
    {
        if (roomsByOldUuid != null || !string.IsNullOrEmpty(loadFailure)) return;
        var asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null)
        {
            loadFailure = $"resource_missing_{ResourcePath}";
            return;
        }

        FileData loaded;
        try
        {
            loaded = JsonUtility.FromJson<FileData>(asset.text.TrimStart('\uFEFF'));
        }
        catch (Exception exception)
        {
            loadFailure = $"json_invalid_{exception.GetType().Name}";
            return;
        }

        if (loaded == null || loaded.schemaVersion != 1 || loaded.rooms == null)
        {
            loadFailure = "schema_or_rooms_invalid";
            return;
        }

        var parsed = new Dictionary<Guid, RoomData>();
        foreach (var room in loaded.rooms)
        {
            if (room == null
                || !Guid.TryParse(room.oldRoomId, out var oldRoomUuid)
                || room.oldToNew == null
                || room.newFloorBoundary == null
                || room.newFloorBoundary.Length < 3)
                continue;
            parsed[oldRoomUuid] = room;
        }
        if (parsed.Count == 0)
        {
            loadFailure = "no_valid_room_mappings";
            return;
        }
        roomsByOldUuid = parsed;
    }

    private static bool IsPointInPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1;
             current < polygon.Count;
             previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            var crosses = (a.y > point.y) != (b.y > point.y)
                && point.x < (b.x - a.x) * (point.y - a.y)
                    / (b.y - a.y + Mathf.Epsilon) + a.x;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static float DistanceToBoundary(
        Vector2 point,
        IReadOnlyList<Vector2> polygon)
    {
        var minimum = float.PositiveInfinity;
        for (var index = 0; index < polygon.Count; index++)
            minimum = Mathf.Min(
                minimum,
                DistancePointToSegment(
                    point, polygon[index], polygon[(index + 1) % polygon.Count]));
        return minimum;
    }

    private static float DistancePointToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        var segment = end - start;
        var denominator = segment.sqrMagnitude;
        var amount = denominator <= 0.000001f
            ? 0f
            : Mathf.Clamp01(Vector2.Dot(point - start, segment) / denominator);
        return Vector2.Distance(point, start + segment * amount);
    }

    private static bool IsFinite(Vector2 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x)
        && !float.IsNaN(value.y) && !float.IsInfinity(value.y);
}
