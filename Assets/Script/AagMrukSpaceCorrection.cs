using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Converts between the unmodified/baked MRUK Scene frame and the physically
/// aligned frame observed after AprilTag rigid correction. MRUK, TrackingSpace,
/// and the camera rig remain untouched; callers must opt in only for positions
/// that already include the physical/tag correction.
/// </summary>
public static class AagMrukSpaceCorrection
{
    public const float MaximumAcceptedTranslationMeters = 40f;

    private static bool isApplied;
    private static Vector3 translationMeters;
    private static Quaternion rotation = Quaternion.identity;

    public static bool IsApplied => isApplied;
    public static Vector3 TranslationMeters => isApplied ? translationMeters : Vector3.zero;
    public static Quaternion Rotation => isApplied ? rotation : Quaternion.identity;
    public static float YawDegrees => IsApplied
        ? Mathf.DeltaAngle(0f, rotation.eulerAngles.y)
        : 0f;

    public static bool TryApplyTranslation(Vector3 value, out string failure)
    {
        if (!IsFinite(value))
        {
            failure = "mruk_query_translation_non_finite";
            return false;
        }
        return TryApplyRigid(value, Quaternion.identity, out failure);
    }

    public static bool TryApplyRigid(
        Vector3 translation,
        Quaternion yawRotation,
        out string failure)
    {
        failure = string.Empty;
        if (!IsFinite(translation) || !IsFinite(yawRotation))
        {
            failure = "mruk_query_rigid_transform_non_finite";
            return false;
        }
        if (translation.magnitude > MaximumAcceptedTranslationMeters)
        {
            failure = $"mruk_query_translation_too_large_{translation.magnitude:F3}m";
            return false;
        }

        var forward = Vector3.ProjectOnPlane(yawRotation * Vector3.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.000001f)
        {
            failure = "mruk_query_yaw_invalid";
            return false;
        }

        translationMeters = translation;
        rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        isApplied = true;
        return true;
    }

    public static void Reset()
    {
        translationMeters = Vector3.zero;
        rotation = Quaternion.identity;
        isApplied = false;
    }

    /// <summary>
    /// Maps a physically observed or already-aligned content position back into
    /// the unchanged MRUK Scene frame before floor/volume containment queries.
    /// </summary>
    public static Vector3 ObservedToMrukPosition(Vector3 observedWorldPosition) =>
        Quaternion.Inverse(Rotation) * (observedWorldPosition - TranslationMeters);

    /// <summary>Maps an MRUK Scene position into the physically aligned frame.</summary>
    public static Vector3 MrukToObservedPosition(Vector3 mrukWorldPosition) =>
        Rotation * mrukWorldPosition + TranslationMeters;

    public static Quaternion ObservedToMrukRotation(Quaternion observedWorldRotation) =>
        Quaternion.Inverse(Rotation) * observedWorldRotation;

    public static Quaternion MrukToObservedRotation(Quaternion mrukWorldRotation) =>
        Rotation * mrukWorldRotation;

    private static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>
/// Immutable FP2 reference space captured when placements were authored.
/// Runtime device MRUK transforms are deliberately excluded: the complete
/// captured layout is moved as one rigid body by the Room8 AprilTag.
/// </summary>
public static class AagFp2BakedSpace
{
    private const string ResourcePath = "AAG/fp2_baked_mruk_scene";

    private readonly struct FloorRecord
    {
        public FloorRecord(Guid roomUuid, Guid floorUuid, Pose pose, Vector2[] boundary)
        {
            RoomUuid = roomUuid;
            FloorUuid = floorUuid;
            Pose = pose;
            Boundary = boundary;
        }

        public Guid RoomUuid { get; }
        public Guid FloorUuid { get; }
        public Pose Pose { get; }
        public Vector2[] Boundary { get; }
    }

    private static Dictionary<Guid, FloorRecord> floorsByUuid;
    private static Dictionary<Guid, FloorRecord> floorsByRoomUuid;
    private static string loadFailure;

    public static bool TryGetFloor(
        Guid floorUuid,
        out Pose pose,
        out IReadOnlyList<Vector2> boundary,
        out string failure)
    {
        pose = default;
        boundary = null;
        if (!EnsureLoaded(out failure)) return false;
        if (!floorsByUuid.TryGetValue(floorUuid, out var floor))
        {
            failure = $"fp2_baked_floor_missing_{floorUuid}";
            return false;
        }
        pose = floor.Pose;
        boundary = floor.Boundary;
        return true;
    }

    public static bool TryGetRoomFloor(
        Guid roomUuid,
        out Pose pose,
        out IReadOnlyList<Vector2> boundary,
        out string failure)
    {
        pose = default;
        boundary = null;
        if (!EnsureLoaded(out failure)) return false;
        if (!floorsByRoomUuid.TryGetValue(roomUuid, out var floor))
        {
            failure = $"fp2_baked_room_floor_missing_{roomUuid}";
            return false;
        }
        pose = floor.Pose;
        boundary = floor.Boundary;
        return true;
    }

    public static bool TryResolveFloorLocalPose(
        Guid floorUuid,
        Vector3 localPosition,
        Quaternion localRotation,
        out Vector3 canonicalPosition,
        out Quaternion canonicalRotation,
        out string failure)
    {
        canonicalPosition = Vector3.zero;
        canonicalRotation = Quaternion.identity;
        if (!TryGetFloor(floorUuid, out var floorPose, out _, out failure)) return false;
        canonicalPosition = floorPose.position + floorPose.rotation * localPosition;
        canonicalRotation = floorPose.rotation * localRotation;
        return IsFinite(canonicalPosition) && IsFinite(canonicalRotation);
    }

    public static bool TryResolveRoom(
        Vector3 canonicalPosition,
        out Guid roomUuid)
    {
        roomUuid = Guid.Empty;
        if (!EnsureLoaded(out _)) return false;
        foreach (var floor in floorsByRoomUuid.Values)
        {
            var local = Quaternion.Inverse(floor.Pose.rotation)
                * (canonicalPosition - floor.Pose.position);
            if (!IsPointInPolygon(new Vector2(local.x, local.y), floor.Boundary)) continue;
            if (roomUuid != Guid.Empty) return false;
            roomUuid = floor.RoomUuid;
        }
        return roomUuid != Guid.Empty;
    }

    private static bool EnsureLoaded(out string failure)
    {
        if (floorsByUuid != null)
        {
            failure = loadFailure;
            return string.IsNullOrEmpty(failure);
        }

        floorsByUuid = new Dictionary<Guid, FloorRecord>();
        floorsByRoomUuid = new Dictionary<Guid, FloorRecord>();
        var asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null)
        {
            loadFailure = "fp2_baked_scene_resource_missing";
            failure = loadFailure;
            return false;
        }

        JObject scene;
        try
        {
            scene = JObject.Parse(asset.text.TrimStart('\uFEFF'));
        }
        catch (Exception exception)
        {
            loadFailure = $"fp2_baked_scene_json_{exception.GetType().Name}";
            failure = loadFailure;
            return false;
        }

        if (!(scene["Rooms"] is JArray rooms))
        {
            loadFailure = "fp2_baked_scene_rooms_missing";
            failure = loadFailure;
            return false;
        }

        foreach (var roomToken in rooms)
        {
            if (!(roomToken is JObject room)
                || !TryParseUuid((string)room["UUID"], out var roomUuid)
                || !TryParseUuid((string)room["RoomLayout"]?["FloorUuid"], out var floorUuid)
                || !(room["Anchors"] is JArray anchors))
                continue;
            JObject floorAnchor = null;
            foreach (var anchorToken in anchors)
            {
                if (anchorToken is JObject anchor
                    && TryParseUuid((string)anchor["UUID"], out var anchorUuid)
                    && anchorUuid == floorUuid)
                {
                    floorAnchor = anchor;
                    break;
                }
            }
            if (!TryBuildPose(floorAnchor?["Transform"], out var floorPose)
                || !TryBuildBoundary(floorAnchor?["PlaneBoundary2D"], out var boundary))
                continue;
            var record = new FloorRecord(
                roomUuid,
                floorUuid,
                floorPose,
                boundary);
            floorsByUuid[floorUuid] = record;
            floorsByRoomUuid[roomUuid] = record;
        }

        if (floorsByRoomUuid.Count != 8)
            loadFailure = $"fp2_baked_scene_requires_8_rooms_actual_{floorsByRoomUuid.Count}";
        failure = loadFailure;
        return string.IsNullOrEmpty(failure);
    }

    private static bool TryBuildPose(JToken value, out Pose pose)
    {
        pose = default;
        if (!(value?["Translation"] is JArray translation) || translation.Count < 3
            || !(value["Rotation"] is JArray rotationValues) || rotationValues.Count < 3)
            return false;
        var position = new Vector3(
            (float)translation[0], (float)translation[1], (float)translation[2]);
        var rotation = Quaternion.Euler(
            (float)rotationValues[0], (float)rotationValues[1], (float)rotationValues[2]);
        if (!IsFinite(position) || !IsFinite(rotation)) return false;
        pose = new Pose(position, rotation);
        return true;
    }

    private static bool TryBuildBoundary(JToken value, out Vector2[] boundary)
    {
        boundary = null;
        if (!(value is JArray points) || points.Count < 3) return false;
        var result = new Vector2[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            if (!(points[index] is JArray point) || point.Count < 2) return false;
            result[index] = new Vector2((float)point[0], (float)point[1]);
        }
        boundary = result;
        return true;
    }

    private static bool TryParseUuid(string value, out Guid uuid) =>
        Guid.TryParse(value, out uuid)
        || Guid.TryParseExact(value, "N", out uuid);

    private static bool IsPointInPolygon(Vector2 point, IReadOnlyList<Vector2> boundary)
    {
        var inside = false;
        for (int current = 0, previous = boundary.Count - 1;
             current < boundary.Count;
             previous = current++)
        {
            var a = boundary[current];
            var b = boundary[previous];
            if ((a.y > point.y) == (b.y > point.y)) continue;
            var crossingX = (b.x - a.x) * (point.y - a.y)
                / (b.y - a.y) + a.x;
            if (point.x < crossingX) inside = !inside;
        }
        return inside;
    }

    private static bool IsFinite(Vector3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(Quaternion value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);
}
