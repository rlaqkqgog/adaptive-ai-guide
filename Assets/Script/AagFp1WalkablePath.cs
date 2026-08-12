using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// FP1 floor-local waypoints reconstructed from eleven successfully completed
/// P15-P20 sessions. Every retained cell was traversed in at least two sessions.
/// Placements farther than the allowed radius are moved onto the audited path.
/// </summary>
public static class AagFp1WalkablePath
{
    public const float MaximumDistanceMeters = 0.45f;
    public const float MinimumWallClearanceMeters = 0.30f;
    public const float SupplementalWallClearanceMeters = 0.45f;
    private const float SafetySearchStepMeters = 0.05f;
    private const int SafetySearchAngularSamples = 72;
    private const float MaximumSafetySearchRadiusMeters = 2.0f;

    private static readonly Dictionary<Guid, Vector2[]> Waypoints =
        new Dictionary<Guid, Vector2[]>
        {
            [Guid.Parse("0d537c33-3e47-2606-3ea9-897c2bc9f1ce")] = new[]
            {
                new Vector2(-3.10f, 1.76f), new Vector2(-2.52f, 1.27f), new Vector2(-1.79f, 0.95f),
                new Vector2(-1.76f, 2.10f), new Vector2(-1.38f, 0.41f), new Vector2(-0.83f, 0.45f),
                new Vector2(-0.71f, 1.07f), new Vector2(-0.69f, 2.54f), new Vector2(-0.41f, 0.03f),
                new Vector2(-0.38f, 1.62f), new Vector2(0.25f, 1.76f), new Vector2(0.35f, 2.49f),
                new Vector2(0.36f, 1.06f), new Vector2(0.38f, 0.34f), new Vector2(1.00f, 0.55f),
                new Vector2(1.03f, 2.53f), new Vector2(1.05f, 1.40f), new Vector2(1.64f, 0.41f),
                new Vector2(1.67f, 1.12f), new Vector2(1.75f, 2.53f), new Vector2(2.13f, 1.64f),
                new Vector2(2.45f, 2.47f), new Vector2(2.76f, 1.83f), new Vector2(3.50f, 2.13f),
            },
            [Guid.Parse("5faa1907-d2e2-7605-7b01-5149a34a4c6d")] = new[]
            {
                new Vector2(-3.11f, -3.94f), new Vector2(-3.07f, -1.31f), new Vector2(-2.61f, -1.89f),
                new Vector2(-2.58f, 4.92f), new Vector2(-2.48f, -3.84f), new Vector2(-2.46f, -3.13f),
                new Vector2(-2.45f, -1.31f), new Vector2(-2.42f, 4.24f), new Vector2(-2.40f, 1.76f),
                new Vector2(-2.36f, 5.57f), new Vector2(-2.34f, 0.73f), new Vector2(-2.16f, -0.02f),
                new Vector2(-2.16f, -0.69f), new Vector2(-2.11f, 2.42f), new Vector2(-2.07f, -2.15f),
                new Vector2(-1.76f, -3.84f), new Vector2(-1.75f, 3.65f), new Vector2(-1.63f, 1.15f),
                new Vector2(-1.46f, 2.51f), new Vector2(-1.39f, -2.47f), new Vector2(-1.32f, 4.19f),
                new Vector2(-1.18f, -3.72f), new Vector2(-1.08f, 0.91f), new Vector2(-1.06f, 3.54f),
                new Vector2(-1.05f, 2.89f), new Vector2(-0.67f, -2.73f), new Vector2(-0.66f, 4.24f),
                new Vector2(-0.63f, 0.34f), new Vector2(-0.57f, -3.29f), new Vector2(-0.37f, -2.13f),
                new Vector2(-0.34f, 2.85f), new Vector2(-0.32f, 3.50f), new Vector2(-0.27f, 5.58f),
                new Vector2(-0.07f, 4.86f), new Vector2(-0.04f, 1.75f), new Vector2(-0.02f, -0.72f),
                new Vector2(-0.01f, 0.70f), new Vector2(0.00f, -1.40f), new Vector2(0.00f, -2.85f),
                new Vector2(0.00f, -0.01f), new Vector2(0.00f, 4.15f), new Vector2(0.05f, -3.43f),
                new Vector2(0.30f, 2.50f), new Vector2(0.39f, -2.07f), new Vector2(0.68f, -3.10f),
                new Vector2(0.68f, 0.76f), new Vector2(0.69f, 3.81f), new Vector2(0.70f, 3.12f),
                new Vector2(1.06f, 4.44f), new Vector2(1.06f, -3.50f), new Vector2(1.24f, 2.69f),
                new Vector2(1.28f, 1.18f), new Vector2(1.39f, 3.26f), new Vector2(1.40f, 3.87f),
                new Vector2(1.46f, 1.73f), new Vector2(1.74f, 0.70f), new Vector2(1.75f, -3.89f),
                new Vector2(1.83f, 2.81f), new Vector2(1.99f, -4.52f), new Vector2(2.03f, 2.09f),
                new Vector2(2.04f, -2.43f), new Vector2(2.11f, 4.23f), new Vector2(2.13f, 5.61f),
                new Vector2(2.14f, 3.47f), new Vector2(2.15f, 4.87f), new Vector2(2.42f, 0.67f),
                new Vector2(2.43f, -1.40f), new Vector2(2.44f, -0.02f), new Vector2(2.44f, 2.81f),
                new Vector2(2.46f, 1.40f), new Vector2(2.50f, -0.69f), new Vector2(2.55f, -2.77f),
                new Vector2(2.55f, -2.07f), new Vector2(2.76f, 4.17f), new Vector2(3.07f, 0.61f),
                new Vector2(3.51f, 4.59f), new Vector2(3.84f, 0.44f), new Vector2(4.07f, 4.94f),
            },
            [Guid.Parse("7423d1c6-d1e7-3316-6714-6a9b282a396e")] = new[]
            {
                new Vector2(-7.61f, 0.39f), new Vector2(-7.38f, -0.33f), new Vector2(-6.67f, 0.38f),
                new Vector2(-6.21f, -0.31f), new Vector2(-5.85f, -0.93f), new Vector2(-5.61f, -0.35f),
                new Vector2(-5.60f, 0.36f), new Vector2(-4.91f, -0.32f), new Vector2(-4.89f, 0.39f),
                new Vector2(-4.26f, -1.01f), new Vector2(-4.20f, -0.33f), new Vector2(-4.19f, 0.41f),
                new Vector2(-3.49f, 0.33f), new Vector2(-3.47f, -0.34f), new Vector2(-2.85f, -0.29f),
                new Vector2(-2.79f, 0.37f), new Vector2(-2.08f, 0.69f), new Vector2(-2.07f, 1.42f),
                new Vector2(-2.02f, 0.06f), new Vector2(-1.43f, 0.71f), new Vector2(-1.39f, 0.07f),
                new Vector2(-0.68f, 0.33f), new Vector2(0.01f, 0.32f), new Vector2(0.70f, 0.29f),
                new Vector2(1.43f, 0.29f), new Vector2(2.10f, 0.34f), new Vector2(2.80f, 0.32f),
                new Vector2(3.49f, 0.36f), new Vector2(4.18f, 0.40f), new Vector2(4.21f, 0.98f),
                new Vector2(4.47f, 1.72f), new Vector2(4.60f, -0.22f), new Vector2(4.78f, 1.02f),
                new Vector2(4.83f, 0.40f),
            },
            [Guid.Parse("7e4d3e1f-3247-602b-3822-c21df384e947")] = new[]
            {
                new Vector2(-4.60f, -0.71f), new Vector2(-3.51f, -0.66f), new Vector2(-3.14f, -1.10f),
                new Vector2(-2.86f, 0.72f), new Vector2(-2.82f, -0.05f), new Vector2(-2.44f, -0.67f),
                new Vector2(-2.10f, 0.36f), new Vector2(-2.00f, -1.40f), new Vector2(-1.75f, -0.73f),
                new Vector2(-1.40f, 0.21f), new Vector2(-1.04f, -0.36f), new Vector2(-1.04f, -0.95f),
                new Vector2(-0.72f, 0.29f), new Vector2(-0.66f, -1.41f), new Vector2(-0.33f, -0.30f),
                new Vector2(-0.01f, 0.21f), new Vector2(0.03f, -0.83f), new Vector2(0.32f, -0.26f),
                new Vector2(0.71f, -0.77f), new Vector2(0.72f, 0.22f), new Vector2(1.05f, -0.29f),
                new Vector2(1.41f, -0.76f), new Vector2(1.44f, 0.21f), new Vector2(1.76f, -0.32f),
                new Vector2(2.10f, 0.22f), new Vector2(2.12f, -0.97f), new Vector2(2.46f, -0.36f),
                new Vector2(2.79f, 0.08f), new Vector2(2.80f, -1.00f), new Vector2(3.54f, -0.35f),
            },
            [Guid.Parse("880b5e63-6438-a8d5-c156-2ddffc48a6d4")] = new[]
            {
                new Vector2(-2.04f, 0.15f), new Vector2(-1.42f, 0.05f), new Vector2(-1.35f, 0.68f),
                new Vector2(-0.76f, 0.32f), new Vector2(-0.70f, -0.35f), new Vector2(0.03f, 0.33f),
                new Vector2(0.41f, -0.25f), new Vector2(0.69f, 0.29f), new Vector2(1.41f, 0.35f),
                new Vector2(1.78f, -0.20f), new Vector2(2.08f, 0.37f), new Vector2(2.46f, -0.45f),
                new Vector2(2.81f, 0.38f), new Vector2(2.86f, 0.98f), new Vector2(3.11f, -0.26f),
                new Vector2(3.48f, 0.38f), new Vector2(4.52f, 0.04f), new Vector2(4.56f, 0.66f),
                new Vector2(4.78f, 1.42f), new Vector2(5.23f, -0.33f), new Vector2(5.25f, 0.43f),
                new Vector2(5.65f, -0.76f), new Vector2(5.94f, 0.64f), new Vector2(6.22f, 0.10f),
                new Vector2(6.53f, 0.59f),
            },
            [Guid.Parse("98332012-20ba-e0ba-78ec-8440113270cd")] = new[]
            {
                new Vector2(-3.75f, 0.75f), new Vector2(-3.14f, 0.77f), new Vector2(-2.46f, 0.65f),
                new Vector2(-2.34f, -0.72f), new Vector2(-2.09f, -0.01f), new Vector2(-1.76f, 0.57f),
                new Vector2(-1.02f, 0.67f), new Vector2(-0.68f, -1.04f), new Vector2(-0.27f, 0.63f),
                new Vector2(0.00f, -1.03f), new Vector2(0.57f, -0.93f), new Vector2(1.32f, -0.59f),
                new Vector2(1.74f, -0.14f),
            },
            [Guid.Parse("ad306342-a794-cffd-be87-d9aea02c5823")] = new[]
            {
                new Vector2(-1.40f, 0.37f), new Vector2(-1.37f, -1.05f), new Vector2(-1.36f, -0.32f),
                new Vector2(-1.26f, -2.10f), new Vector2(-1.00f, -2.80f), new Vector2(-0.96f, -3.46f),
                new Vector2(-0.82f, -4.05f), new Vector2(-0.38f, -3.52f), new Vector2(-0.27f, 0.71f),
                new Vector2(-0.25f, 2.07f), new Vector2(-0.23f, 0.02f), new Vector2(-0.05f, 1.34f),
                new Vector2(-0.03f, -0.71f), new Vector2(0.02f, 3.16f), new Vector2(0.05f, -2.84f),
                new Vector2(0.27f, -1.38f), new Vector2(0.41f, 2.14f),
            },
            [Guid.Parse("b4131884-79b1-7db8-5529-4875ea40bdd5")] = new[]
            {
                new Vector2(-5.92f, -0.32f), new Vector2(-5.58f, 0.38f), new Vector2(-5.21f, -0.38f),
                new Vector2(-4.85f, 0.19f), new Vector2(-4.54f, -0.69f), new Vector2(-4.19f, -0.05f),
                new Vector2(-4.13f, -1.40f), new Vector2(-3.84f, -0.68f), new Vector2(-3.15f, -0.01f),
                new Vector2(-3.13f, -0.65f), new Vector2(-2.46f, -0.27f), new Vector2(-2.46f, 0.32f),
                new Vector2(-2.12f, -0.73f), new Vector2(-1.75f, 0.01f), new Vector2(-1.41f, -0.64f),
                new Vector2(-1.06f, 0.04f), new Vector2(-0.75f, -0.58f), new Vector2(-0.61f, 0.63f),
                new Vector2(-0.34f, -0.01f), new Vector2(0.00f, 0.70f), new Vector2(0.05f, -1.30f),
                new Vector2(0.36f, -0.76f), new Vector2(0.70f, -0.09f), new Vector2(1.07f, -0.68f),
                new Vector2(1.40f, -0.03f), new Vector2(1.76f, -0.60f), new Vector2(2.11f, -0.02f),
                new Vector2(2.43f, -0.60f), new Vector2(2.80f, 0.02f), new Vector2(3.14f, -0.66f),
                new Vector2(3.51f, 0.04f), new Vector2(3.87f, -0.66f), new Vector2(4.19f, -0.01f),
                new Vector2(4.51f, -0.65f), new Vector2(4.82f, 0.02f),
            },
            [Guid.Parse("ce00a9e3-c3dc-48cd-a503-e928584055f2")] = new[]
            {
                new Vector2(0.35f, 0.33f), new Vector2(0.94f, 0.29f), new Vector2(1.40f, -3.43f),
                new Vector2(1.43f, 0.66f), new Vector2(1.68f, -2.74f), new Vector2(1.70f, 2.47f),
                new Vector2(1.84f, -0.32f), new Vector2(1.99f, -1.74f), new Vector2(2.02f, -1.04f),
                new Vector2(2.05f, 1.06f), new Vector2(2.10f, 1.75f), new Vector2(2.24f, 0.41f),
            },
        };

    public static bool TryGetWaypoints(Guid roomUuid, out IReadOnlyList<Vector2> waypoints)
    {
        if (Waypoints.TryGetValue(roomUuid, out var values) && values.Length > 0)
        {
            waypoints = values;
            return true;
        }
        waypoints = Array.Empty<Vector2>();
        return false;
    }

    public static bool TryFindNearest(
        Guid roomUuid,
        Vector2 point,
        out Vector2 nearest,
        out float distanceMeters)
    {
        nearest = point;
        distanceMeters = float.PositiveInfinity;
        if (!TryGetWaypoints(roomUuid, out var waypoints)) return false;
        var bestDistanceSquared = float.PositiveInfinity;
        foreach (var waypoint in waypoints)
        {
            if (!IsActiveSafeFloorPoint(roomUuid, waypoint)) continue;
            var distanceSquared = (waypoint - point).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            nearest = waypoint;
        }
        distanceMeters = Mathf.Sqrt(bestDistanceSquared);
        return !float.IsInfinity(distanceMeters);
    }

    /// <summary>
    /// Selects the audited point with the greatest minimum clearance across
    /// both the authoritative baked scan and the supplemental rescan. This is
    /// used only by explicit field-issue overrides that must be moved away
    /// from windows, pillars, doors, and inter-room boundaries.
    /// </summary>
    public static bool TryGetCentralSafePoint(
        Guid roomUuid,
        out Vector2 centralPoint,
        out float clearanceMeters)
    {
        centralPoint = Vector2.zero;
        clearanceMeters = float.NegativeInfinity;
        if (!TryGetWaypoints(roomUuid, out var waypoints)
            || !AagFp2BakedSpace.TryGetRoomFloor(
                roomUuid, out var floorPose, out var boundary, out _)
            || boundary == null
            || boundary.Count < 3)
            return false;

        foreach (var waypoint in waypoints)
        {
            if (!IsActiveSafeFloorPoint(roomUuid, waypoint)
                || !AagFp1SupplementalWallMap.TryGetClearance(
                    roomUuid, waypoint, out var supplementalClearance))
                continue;

            var bakedClearance = DistanceToBoundary(waypoint, boundary);
            var canonical = floorPose.position
                + floorPose.rotation * new Vector3(waypoint.x, waypoint.y, 0f);
            if (AagFp2BakedSpace.TryGetMinimumWallClearance(
                    roomUuid, canonical, out var bakedWallClearance))
                bakedClearance = Mathf.Min(bakedClearance, bakedWallClearance);
            var combinedClearance = Mathf.Min(bakedClearance, supplementalClearance);
            if (combinedClearance <= clearanceMeters) continue;
            centralPoint = waypoint;
            clearanceMeters = combinedClearance;
        }
        return !float.IsNegativeInfinity(clearanceMeters);
    }

    public static bool TryConstrain(
        Guid roomUuid,
        Vector2 original,
        out Vector2 resolved,
        out float distanceToPathMeters,
        out float movedMeters)
    {
        resolved = original;
        movedMeters = 0f;
        if (!TryFindNearest(roomUuid, original, out var nearest, out distanceToPathMeters))
            return false;
        var originalIsSafe = IsActiveSafeFloorPoint(roomUuid, original);
        // Authored Baked positions are preserved whenever both the original and
        // mapped rescan agree they are safe. The audited path is a fallback for
        // an unsafe spawn, not a reason to move a safe object several metres.
        if (originalIsSafe) return true;

        var nearestSafe = nearest;
        var nearestSafeDistance = Vector2.Distance(original, nearest);
        for (var radius = SafetySearchStepMeters;
             radius <= MaximumSafetySearchRadiusMeters;
             radius += SafetySearchStepMeters)
        {
            var foundAtRadius = false;
            for (var index = 0; index < SafetySearchAngularSamples; index++)
            {
                var angle = index * Mathf.PI * 2f / SafetySearchAngularSamples;
                var candidate = original
                    + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                if (!IsActiveSafeFloorPoint(roomUuid, candidate)) continue;
                var candidateDistance = Vector2.Distance(original, candidate);
                if (candidateDistance >= nearestSafeDistance) continue;
                nearestSafe = candidate;
                nearestSafeDistance = candidateDistance;
                foundAtRadius = true;
            }
            if (foundAtRadius) break;
        }

        resolved = nearestSafe;
        movedMeters = nearestSafeDistance;
        return true;
    }

    private static bool IsActiveSafeFloorPoint(Guid roomUuid, Vector2 point)
    {
        var bakedSafe = IsBakedSafeFloorPoint(roomUuid, point);
        if (!bakedSafe) return false;

        // FP1 keeps the verified baked room/tag frame as its source of truth.
        // The mapped 2026-08-12 rescan is only a secondary veto for locally
        // changed walls, pillars, and windows. It never replaces or moves the
        // canonical room/tag layout and does not depend on current MRUK UUIDs.
        if (ExperimentSpaceRuntime.IsFp1)
        {
            // Safety is fail-closed: a missing/corrupt room mapping aborts FP1
            // placement instead of silently falling back to a potentially
            // outdated wall boundary.
            if (!AagFp1SupplementalWallMap.TryIsSafe(
                roomUuid,
                point,
                SupplementalWallClearanceMeters,
                out var supplementalSafe,
                out _))
                return false;
            return supplementalSafe;
        }
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace) return true;

        // EditMode tests and editor previews do not have a localized device
        // scene. Keep the bundled export only as an offline validation fallback;
        // FP1 runtime reaches this code after live MRUK validation has passed.
        return true;
    }

    private static bool IsBakedSafeFloorPoint(Guid roomUuid, Vector2 point)
    {
        if (!AagFp2BakedSpace.TryGetRoomFloor(
                roomUuid, out var floorPose, out var boundary, out _)
            || boundary == null
            || boundary.Count < 3
            || !IsPointInPolygon(point, boundary)
            || DistanceToBoundary(point, boundary) < MinimumWallClearanceMeters)
            return false;
        var canonical = floorPose.position
            + floorPose.rotation * new Vector3(point.x, point.y, 0f);
        return !AagFp2BakedSpace.TryGetMinimumWallClearance(
                roomUuid, canonical, out var wallClearance)
            || wallClearance >= MinimumWallClearanceMeters;
    }

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

    private static float DistanceToBoundary(Vector2 point, IReadOnlyList<Vector2> boundary)
    {
        var result = float.PositiveInfinity;
        for (var index = 0; index < boundary.Count; index++)
        {
            var start = boundary[index];
            var end = boundary[(index + 1) % boundary.Count];
            var segment = end - start;
            var denominator = segment.sqrMagnitude;
            var amount = denominator <= 0.000001f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(point - start, segment) / denominator);
            result = Mathf.Min(result, Vector2.Distance(point, start + segment * amount));
        }
        return result;
    }

    public static bool TryConstrainObservedWorldPosition(
        Vector3 originalObservedPosition,
        out Vector3 resolvedObservedPosition,
        out Guid roomUuid,
        out float distanceToPathMeters,
        out float movedMeters,
        out string failure)
    {
        resolvedObservedPosition = originalObservedPosition;
        roomUuid = Guid.Empty;
        distanceToPathMeters = float.PositiveInfinity;
        movedMeters = 0f;
        failure = string.Empty;
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace
            && AagMrukSpaceCorrection.IsApplied)
        {
            var originalCanonical = AagMrukSpaceCorrection.ObservedToMrukPosition(
                originalObservedPosition);
            Pose selectedFloorPose = default;
            var bakedSelectedLocal = Vector3.zero;
            foreach (var candidateRoomUuid in Waypoints.Keys)
            {
                if (!AagFp2BakedSpace.TryGetRoomFloor(
                        candidateRoomUuid, out var floorPose, out _, out _)
                    || !TryFindNearest(
                        candidateRoomUuid,
                        ToFloorLocal(floorPose, originalCanonical),
                        out var point,
                        out var distance)
                    || distance >= distanceToPathMeters)
                    continue;
                distanceToPathMeters = distance;
                selectedFloorPose = floorPose;
                bakedSelectedLocal = Quaternion.Inverse(floorPose.rotation)
                    * (originalCanonical - floorPose.position);
                roomUuid = candidateRoomUuid;
            }
            if (roomUuid == Guid.Empty)
            {
                failure = "fp1_baked_walkable_floor_or_path_missing";
                return false;
            }
            var originalPoint = new Vector2(bakedSelectedLocal.x, bakedSelectedLocal.y);
            if (!TryConstrain(
                    roomUuid,
                    originalPoint,
                    out var constrainedPoint,
                    out distanceToPathMeters,
                    out var localMove))
            {
                failure = $"fp1_baked_walkable_path_missing_{roomUuid}";
                return false;
            }
            if (localMove < 0.01f) return true;
            bakedSelectedLocal.x = constrainedPoint.x;
            bakedSelectedLocal.y = constrainedPoint.y;
            var resolvedCanonical = selectedFloorPose.position
                + selectedFloorPose.rotation * bakedSelectedLocal;
            resolvedObservedPosition = AagMrukSpaceCorrection.MrukToObservedPosition(
                resolvedCanonical);
            movedMeters = Vector3.Distance(originalObservedPosition, resolvedObservedPosition);
            return AagRigidPoseRecovery.IsFinite(resolvedObservedPosition);
        }
        var rooms = MRUK.Instance?.Rooms;
        if (rooms == null)
        {
            failure = "fp1_walkable_mruk_rooms_unavailable";
            return false;
        }

        var originalMrukPosition = AagMrukSpaceCorrection.ObservedToMrukPosition(
            originalObservedPosition);
        MRUKAnchor selectedFloor = null;
        var selectedLocal = Vector3.zero;
        var selectedPoint = Vector2.zero;
        foreach (var room in rooms)
        {
            if (room == null || room.Anchor == null
                || !AagExperimentSpaceCatalog.Fp1.ContainsRoom(room.Anchor.Uuid)
                || !TryGetWaypoints(room.Anchor.Uuid, out var waypoints))
                continue;
            var floor = room.FloorAnchors?.FirstOrDefault(value => value != null);
            if (floor == null) continue;
            var local = floor.transform.InverseTransformPoint(originalMrukPosition);
            foreach (var point in waypoints)
            {
                var distance = Vector2.Distance(new Vector2(local.x, local.y), point);
                if (distance >= distanceToPathMeters) continue;
                distanceToPathMeters = distance;
                selectedFloor = floor;
                selectedLocal = local;
                selectedPoint = point;
                roomUuid = room.Anchor.Uuid;
            }
        }
        if (selectedFloor == null || roomUuid == Guid.Empty)
        {
            failure = "fp1_walkable_floor_or_path_missing";
            return false;
        }
        if (distanceToPathMeters <= MaximumDistanceMeters) return true;

        selectedLocal.x = selectedPoint.x;
        selectedLocal.y = selectedPoint.y;
        var resolvedMrukPosition = selectedFloor.transform.TransformPoint(selectedLocal);
        resolvedObservedPosition = AagMrukSpaceCorrection.MrukToObservedPosition(resolvedMrukPosition);
        movedMeters = Vector3.Distance(originalObservedPosition, resolvedObservedPosition);
        return AagRigidPoseRecovery.IsFinite(resolvedObservedPosition);
    }

    public static bool TryResolveObservedRoomCenter(
        Guid roomUuid,
        Vector3 originalObservedPosition,
        out Vector3 resolvedObservedPosition,
        out Vector2 centralPoint,
        out float clearanceMeters,
        out float movedMeters,
        out string failure)
    {
        resolvedObservedPosition = originalObservedPosition;
        centralPoint = Vector2.zero;
        clearanceMeters = float.NegativeInfinity;
        movedMeters = 0f;
        failure = string.Empty;
        if (!ExperimentSpaceRuntime.UsesBakedReferenceSpace)
        {
            failure = "fp1_room_center_requires_baked_reference";
            return false;
        }
        if (!TryGetCentralSafePoint(roomUuid, out centralPoint, out clearanceMeters))
        {
            failure = $"fp1_room_center_missing_{roomUuid}";
            return false;
        }
        if (!AagFp2BakedSpace.TryGetRoomFloor(
                roomUuid, out var floorPose, out _, out failure))
        {
            failure = $"fp1_room_center_floor_missing_{roomUuid}_{failure}";
            return false;
        }

        var originalCanonical = AagMrukSpaceCorrection.ObservedToMrukPosition(
            originalObservedPosition);
        var local = Quaternion.Inverse(floorPose.rotation)
            * (originalCanonical - floorPose.position);
        local.x = centralPoint.x;
        local.y = centralPoint.y;
        var resolvedCanonical = floorPose.position + floorPose.rotation * local;
        resolvedObservedPosition = AagMrukSpaceCorrection.MrukToObservedPosition(
            resolvedCanonical);
        movedMeters = Vector3.Distance(originalObservedPosition, resolvedObservedPosition);
        if (!AagRigidPoseRecovery.IsFinite(resolvedObservedPosition))
        {
            failure = $"fp1_room_center_non_finite_{roomUuid}";
            return false;
        }
        return true;
    }

    public static bool TryResolveObservedFloorLocalPosition(
        Guid roomUuid,
        Guid floorAnchorUuid,
        Vector3 originalLocalPosition,
        out Vector3 resolvedObservedPosition,
        out float distanceToPathMeters,
        out float movedMeters,
        out string failure)
    {
        resolvedObservedPosition = Vector3.zero;
        distanceToPathMeters = float.PositiveInfinity;
        movedMeters = 0f;
        failure = string.Empty;
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace)
        {
            if (!AagFp2BakedSpace.TryGetFloor(
                    floorAnchorUuid, out var floorPose, out _, out failure))
                return false;
            if (!TryConstrain(
                    roomUuid,
                    new Vector2(originalLocalPosition.x, originalLocalPosition.y),
                    out var bakedResolvedPoint,
                    out distanceToPathMeters,
                    out movedMeters))
            {
                failure = $"fp1_baked_walkable_path_missing_{roomUuid}";
                return false;
            }
            var bakedResolvedLocal = new Vector3(
                bakedResolvedPoint.x, bakedResolvedPoint.y, originalLocalPosition.z);
            var canonical = floorPose.position + floorPose.rotation * bakedResolvedLocal;
            resolvedObservedPosition = AagMrukSpaceCorrection.MrukToObservedPosition(canonical);
            return AagRigidPoseRecovery.IsFinite(resolvedObservedPosition);
        }
        var room = MRUK.Instance?.Rooms?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == roomUuid);
        var floor = room?.FloorAnchors?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == floorAnchorUuid);
        if (floor == null)
        {
            failure = $"fp1_walkable_floor_missing_{roomUuid}_{floorAnchorUuid}";
            return false;
        }
        if (!TryConstrain(
                roomUuid,
                new Vector2(originalLocalPosition.x, originalLocalPosition.y),
                out var resolvedPoint,
                out distanceToPathMeters,
                out movedMeters))
        {
            failure = $"fp1_walkable_path_missing_{roomUuid}";
            return false;
        }

        var resolvedLocal = new Vector3(
            resolvedPoint.x,
            resolvedPoint.y,
            originalLocalPosition.z);
        resolvedObservedPosition = AagMrukSpaceCorrection.MrukToObservedPosition(
            floor.transform.TransformPoint(resolvedLocal));
        if (!AagRigidPoseRecovery.IsFinite(resolvedObservedPosition))
        {
            failure = $"fp1_walkable_non_finite_{roomUuid}";
            return false;
        }
        return true;
    }

    private static Vector2 ToFloorLocal(Pose floorPose, Vector3 canonicalPosition)
    {
        var local = Quaternion.Inverse(floorPose.rotation)
            * (canonicalPosition - floorPose.position);
        return new Vector2(local.x, local.y);
    }
}

/// <summary>
/// Narrow overrides for placements confirmed unsafe during the 2026-08-12
/// FP1 on-device inspection. These entries intentionally affect only the
/// reported set/object pairs; all other authored placements remain unchanged.
/// </summary>
public static class AagFp1FieldSafetyOverrides
{
    private static readonly IReadOnlyDictionary<string, Guid> TargetRooms =
        new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            [TargetKey("FP1-S1", "yellow_3")] =
                Guid.Parse("880b5e63-6438-a8d5-c156-2ddffc48a6d4"), // Hall 2-1 / 이름없는 룸 7
            [TargetKey("FP1-S1", "yellow_1")] =
                Guid.Parse("5faa1907-d2e2-7605-7b01-5149a34a4c6d"), // Room2-1 / 이름없는 룸 3
            [TargetKey("FP1-S2", "yellow_1")] =
                Guid.Parse("880b5e63-6438-a8d5-c156-2ddffc48a6d4"), // Hall 2-1 / 이름없는 룸 7
            [TargetKey("FP1-S3", "yellow_2")] =
                Guid.Parse("ce00a9e3-c3dc-48cd-a503-e928584055f2"), // Hall 2-2 / 이름없는 룸 8
        };

    private static readonly IReadOnlyDictionary<string, Guid> IncidentalRooms =
        new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["incidental_02_02_jar"] =
                Guid.Parse("ad306342-a794-cffd-be87-d9aea02c5823"), // Room2-2 / 이름없는 룸 4
            ["incidental_02_02_hairDryer"] =
                Guid.Parse("7e4d3e1f-3247-602b-3822-c21df384e947"), // Hall 1-1 / 이름없는 룸 2
        };

    public static int TargetCount => TargetRooms.Count;
    public static int IncidentalCount => IncidentalRooms.Count;

    public static bool TryGetTargetRoom(
        string setId,
        string objectId,
        out Guid roomUuid) =>
        TargetRooms.TryGetValue(TargetKey(setId, objectId), out roomUuid);

    public static bool TryGetIncidentalRoom(string objectId, out Guid roomUuid) =>
        IncidentalRooms.TryGetValue(objectId ?? string.Empty, out roomUuid);

    private static string TargetKey(string setId, string objectId) =>
        $"{setId ?? string.Empty}\n{objectId ?? string.Empty}";
}
