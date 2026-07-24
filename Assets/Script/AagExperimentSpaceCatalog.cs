using System;
using System.Collections.Generic;

/// <summary>
/// Immutable experiment-space metadata. It intentionally contains room IDs and
/// supported set IDs only; MRUK remains the source of room geometry.
/// </summary>
public sealed class AagFloorPlanDefinition
{
    private readonly HashSet<Guid> roomIds;
    private readonly HashSet<Guid> excludedRoomIds;
    private readonly List<string> setIds;
    private readonly HashSet<string> setIdLookup;

    public string FloorPlanId { get; }
    public IReadOnlyCollection<Guid> RoomIds => roomIds;
    public IReadOnlyCollection<Guid> ExcludedRoomIds => excludedRoomIds;
    public IReadOnlyList<string> SetIds => setIds;

    public AagFloorPlanDefinition(
        string floorPlanId,
        IEnumerable<Guid> roomIds,
        IEnumerable<Guid> excludedRoomIds,
        IEnumerable<string> setIds)
    {
        FloorPlanId = floorPlanId;
        this.roomIds = new HashSet<Guid>(roomIds);
        this.excludedRoomIds = new HashSet<Guid>(excludedRoomIds);
        this.setIds = new List<string>(setIds);
        setIdLookup = new HashSet<string>(this.setIds, StringComparer.Ordinal);
    }

    public bool ContainsRoom(Guid roomId) => roomIds.Contains(roomId);
    public bool IsExcludedRoom(Guid roomId) => excludedRoomIds.Contains(roomId);
    public bool SupportsSet(string setId) => setIdLookup.Contains(setId);
}

/// <summary>
/// Catalog of scanned floor plans. Add a new definition only after its MRUK
/// export exists; no placeholder geometry or room IDs belong here.
/// </summary>
public static class AagExperimentSpaceCatalog
{
    public const string Fp1Id = "FP1";
    public const string Fp1S1 = "FP1-S1";
    public const string Fp1S2 = "FP1-S2";
    public const string Fp1S3 = "FP1-S3";
    public static readonly Guid Fp1Room2Part1Uuid = Guid.Parse("5faa1907-d2e2-7605-7b01-5149a34a4c6d");
    public static readonly Guid Fp1Room2Part2Uuid = Guid.Parse("ad306342-a794-cffd-be87-d9aea02c5823");
    public static readonly Guid Fp1Room3Uuid = Guid.Parse("0d537c33-3e47-2606-3ea9-897c2bc9f1ce");

    public static readonly AagFloorPlanDefinition Fp1 = new AagFloorPlanDefinition(
        Fp1Id,
        new[]
        {
            Guid.Parse("98332012-20ba-e0ba-78ec-8440113270cd"),
            Fp1Room2Part1Uuid,
            Fp1Room2Part2Uuid,
            Fp1Room3Uuid,
            Guid.Parse("7e4d3e1f-3247-602b-3822-c21df384e947"),
            Guid.Parse("b4131884-79b1-7db8-5529-4875ea40bdd5"),
            Guid.Parse("7423d1c6-d1e7-3316-6714-6a9b282a396e"),
            Guid.Parse("880b5e63-6438-a8d5-c156-2ddffc48a6d4"),
            Guid.Parse("ce00a9e3-c3dc-48cd-a503-e928584055f2"),
        },
        Array.Empty<Guid>(),
        new[]
        {
            Fp1S1,
            Fp1S2,
            Fp1S3,
        });

    public static bool IsFp1Room2(Guid roomId) =>
        roomId == Fp1Room2Part1Uuid || roomId == Fp1Room2Part2Uuid;

    private static readonly AagFloorPlanDefinition[] FloorPlans =
    {
        Fp1,
    };

    public static bool TryGetFloorPlan(string floorPlanId, out AagFloorPlanDefinition floorPlan)
    {
        foreach (var candidate in FloorPlans)
        {
            if (string.Equals(candidate.FloorPlanId, floorPlanId, StringComparison.Ordinal))
            {
                floorPlan = candidate;
                return true;
            }
        }

        floorPlan = null;
        return false;
    }

    public static bool TryGetFloorPlanForRoom(Guid roomId, out AagFloorPlanDefinition floorPlan)
    {
        foreach (var candidate in FloorPlans)
        {
            if (candidate.ContainsRoom(roomId))
            {
                floorPlan = candidate;
                return true;
            }
        }

        floorPlan = null;
        return false;
    }
}
