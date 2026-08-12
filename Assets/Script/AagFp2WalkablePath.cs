using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// FP2 floor-local waypoints reconstructed from successfully tracked participant
/// trajectories in FP2-S1/S2/S3. Placements farther than the allowed radius are
/// projected to the nearest point that a participant physically traversed.
/// </summary>
public static class AagFp2WalkablePath
{
    public const float MaximumDistanceMeters = 0.45f;

    private static readonly Dictionary<Guid, Vector2[]> Waypoints =
        new Dictionary<Guid, Vector2[]>
        {
            [Guid.Parse("0e4e8223-3c13-735b-a552-4acf2ba915a7")] = new[]
            {
                new Vector2(-2.38f, 1.10f), new Vector2(-2.31f, 0.24f), new Vector2(-2.11f, 2.30f),
                new Vector2(-2.03f, -0.71f), new Vector2(-1.41f, -0.40f), new Vector2(-1.38f, 2.92f),
                new Vector2(-1.14f, -0.77f), new Vector2(-0.99f, 3.24f), new Vector2(-0.93f, -4.18f),
                new Vector2(-0.40f, 1.22f), new Vector2(-0.32f, -0.98f), new Vector2(-0.14f, -2.50f),
                new Vector2(0.00f, 3.31f), new Vector2(0.05f, 2.64f), new Vector2(0.07f, -0.22f),
                new Vector2(0.12f, -3.71f), new Vector2(1.05f, 1.04f), new Vector2(1.16f, -1.45f),
                new Vector2(1.25f, 3.31f), new Vector2(1.30f, -2.29f), new Vector2(1.31f, -4.02f),
                new Vector2(1.40f, -0.25f), new Vector2(1.52f, 2.44f), new Vector2(2.38f, 1.26f),
                new Vector2(2.41f, 3.26f), new Vector2(2.46f, -0.18f), new Vector2(2.47f, -3.85f),
                new Vector2(2.71f, 2.67f), new Vector2(3.66f, -3.47f), new Vector2(3.69f, 0.08f),
                new Vector2(3.70f, 2.53f), new Vector2(3.85f, 3.52f), new Vector2(3.97f, 1.23f),
            },
            [Guid.Parse("d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b")] = new[]
            {
                new Vector2(-2.59f, 0.17f), new Vector2(-2.47f, -2.58f), new Vector2(-2.44f, -1.30f),
                new Vector2(-2.43f, 1.11f), new Vector2(-2.35f, 2.44f), new Vector2(-1.29f, -2.85f),
                new Vector2(-1.23f, 0.47f), new Vector2(-1.05f, 2.30f), new Vector2(-1.01f, 1.26f),
                new Vector2(-0.04f, 2.37f), new Vector2(0.01f, -2.76f), new Vector2(0.21f, 0.71f),
                new Vector2(0.56f, 0.57f), new Vector2(0.97f, 0.52f), new Vector2(1.23f, 1.14f),
                new Vector2(1.25f, -2.68f), new Vector2(1.25f, 2.40f), new Vector2(2.36f, 1.32f),
                new Vector2(2.39f, 2.35f), new Vector2(2.56f, -2.45f), new Vector2(3.26f, 1.33f),
                new Vector2(3.31f, -1.30f), new Vector2(3.36f, 2.74f), new Vector2(3.38f, 0.14f),
            },
            [Guid.Parse("28e81069-81b3-b60a-0166-50599f88ce42")] = new[]
            {
                new Vector2(0.41f, -0.21f), new Vector2(1.24f, -0.38f), new Vector2(2.68f, -0.19f),
                new Vector2(3.40f, 0.06f),
            },
            [Guid.Parse("133adc09-ce31-302f-1b53-788b59deeb4f")] = new[]
            {
                new Vector2(-4.96f, -1.16f), new Vector2(-4.85f, 0.05f), new Vector2(-4.78f, -2.00f),
                new Vector2(-4.69f, -5.99f),
                new Vector2(-4.64f, 3.81f), new Vector2(-4.60f, -5.42f), new Vector2(-4.47f, 0.78f),
                new Vector2(-3.89f, 3.51f), new Vector2(-3.82f, -5.85f), new Vector2(-3.78f, 1.23f),
                new Vector2(-3.65f, -4.78f), new Vector2(-3.65f, -1.37f), new Vector2(-3.65f, 2.54f),
                new Vector2(-3.61f, -0.04f), new Vector2(-3.60f, -2.22f), new Vector2(-3.47f, -3.84f),
                new Vector2(-2.81f, 0.12f), new Vector2(-2.81f, 1.44f), new Vector2(-2.73f, 2.56f),
                new Vector2(-2.72f, -2.61f), new Vector2(-2.60f, -5.77f), new Vector2(-2.56f, -5.01f),
                new Vector2(-2.53f, -3.71f), new Vector2(-2.44f, 3.85f), new Vector2(-2.38f, -1.40f),
                new Vector2(-2.03f, 4.63f), new Vector2(-1.74f, 5.72f), new Vector2(-1.56f, -1.19f),
                new Vector2(-1.44f, 4.93f), new Vector2(-1.43f, 4.03f), new Vector2(-1.37f, -2.45f),
                new Vector2(-1.30f, -4.89f), new Vector2(-1.19f, -3.90f), new Vector2(-0.46f, 5.12f),
                new Vector2(-0.36f, 3.69f), new Vector2(-0.30f, 5.96f), new Vector2(-0.16f, -1.20f),
                new Vector2(-0.16f, 2.49f), new Vector2(-0.12f, -4.92f), new Vector2(-0.05f, 1.28f),
                new Vector2(-0.04f, -2.56f), new Vector2(-0.04f, 0.02f), new Vector2(0.04f, -3.97f),
                new Vector2(0.84f, -4.23f), new Vector2(1.24f, -4.70f), new Vector2(2.51f, -4.87f),
                new Vector2(3.71f, -4.83f),
            },
            [Guid.Parse("7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b")] = new[]
            {
                new Vector2(-4.65f, 0.09f), new Vector2(-4.61f, 0.97f), new Vector2(-3.60f, 0.77f),
                new Vector2(-3.59f, 0.47f), new Vector2(-2.59f, 0.84f), new Vector2(-2.45f, 0.25f),
                new Vector2(-1.26f, 0.91f), new Vector2(-1.14f, -0.09f), new Vector2(-0.26f, -0.94f),
                new Vector2(-0.19f, -0.26f), new Vector2(0.03f, 0.89f), new Vector2(1.17f, 0.18f),
                new Vector2(1.24f, 0.88f), new Vector2(2.44f, 0.90f), new Vector2(2.55f, 0.48f),
                new Vector2(3.66f, 1.14f), new Vector2(3.77f, 0.07f),
                new Vector2(3.81f, 3.73f), new Vector2(3.89f, 2.46f), new Vector2(4.05f, -1.22f),
                new Vector2(4.17f, -2.38f), new Vector2(4.29f, -3.36f), new Vector2(4.44f, -2.90f),
                new Vector2(4.47f, 1.74f), new Vector2(4.60f, -3.61f),
            },
            [Guid.Parse("e2e79df2-facb-0150-67b4-a43dfaad9218")] = new[]
            {
                new Vector2(-0.72f, 0.44f), new Vector2(-0.01f, 0.17f),
            },
            [Guid.Parse("96a223f3-baf3-7044-2958-6f2468b35c72")] = new[]
            {
                new Vector2(-6.13f, 0.74f), new Vector2(-5.91f, 0.50f), new Vector2(-5.51f, 0.02f),
                new Vector2(-5.09f, 0.88f), new Vector2(-3.91f, 0.84f), new Vector2(0.39f, 0.88f),
                new Vector2(0.57f, 0.44f), new Vector2(0.94f, 0.79f), new Vector2(2.59f, 0.90f),
                new Vector2(2.79f, -0.10f),
            },
            [Guid.Parse("2d4f4c7d-9189-0198-a0ff-ecd07d843c6a")] = new[]
            {
                new Vector2(-2.69f, 0.02f), new Vector2(-2.40f, -1.26f), new Vector2(-2.27f, 1.08f),
                new Vector2(-1.28f, 1.68f), new Vector2(-0.01f, 1.81f), new Vector2(0.05f, -1.60f),
                new Vector2(1.28f, -1.49f), new Vector2(1.63f, 1.31f), new Vector2(2.33f, 1.19f),
                new Vector2(2.37f, -1.01f), new Vector2(2.80f, -0.50f),
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
            var distanceSquared = (waypoint - point).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            nearest = waypoint;
        }
        distanceMeters = Mathf.Sqrt(bestDistanceSquared);
        return !float.IsInfinity(distanceMeters);
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
        if (!TryFindNearest(
                roomUuid,
                original,
                out var nearest,
                out distanceToPathMeters))
            return false;

        if (distanceToPathMeters <= MaximumDistanceMeters) return true;
        resolved = nearest;
        movedMeters = distanceToPathMeters;
        return true;
    }
}
