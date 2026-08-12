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
    public const string Fp2Id = "FP2";
    public const string Fp2S1 = "FP2-S1";
    public const string Fp2S2 = "FP2-S2";
    public const string Fp2S3 = "FP2-S3";
    public static readonly Guid Fp1Room2Part1Uuid = Guid.Parse("5faa1907-d2e2-7605-7b01-5149a34a4c6d");
    public static readonly Guid Fp1Room2Part2Uuid = Guid.Parse("ad306342-a794-cffd-be87-d9aea02c5823");
    public static readonly Guid Fp1Room3Uuid = Guid.Parse("0d537c33-3e47-2606-3ea9-897c2bc9f1ce");
    public static readonly Guid Fp2UnnamedRoomUuid = Guid.Parse("0e4e8223-3c13-735b-a552-4acf2ba915a7");
    public static readonly Guid Fp2UnnamedRoom2Uuid = Guid.Parse("d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b");
    public static readonly Guid Fp2UnnamedRoom3Uuid = Guid.Parse("28e81069-81b3-b60a-0166-50599f88ce42");
    public static readonly Guid Fp2UnnamedRoom4Uuid = Guid.Parse("133adc09-ce31-302f-1b53-788b59deeb4f");
    public static readonly Guid Fp2UnnamedRoom5Uuid = Guid.Parse("7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b");
    public static readonly Guid Fp2UnnamedRoom6Uuid = Guid.Parse("e2e79df2-facb-0150-67b4-a43dfaad9218");
    public static readonly Guid Fp2UnnamedRoom7Uuid = Guid.Parse("96a223f3-baf3-7044-2958-6f2468b35c72");
    public static readonly Guid Fp2UnnamedRoom8Uuid = Guid.Parse("2d4f4c7d-9189-0198-a0ff-ecd07d843c6a");

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

    // Confirmed from the 2026-07-25 FP2 Quest export. Room 2 is the sole UUID
    // remaining after seven room-labelled screenshots were matched to the same
    // stable eight-room export.
    public static readonly AagFloorPlanDefinition Fp2 = new AagFloorPlanDefinition(
        Fp2Id,
        new[]
        {
            Fp2UnnamedRoomUuid,
            Fp2UnnamedRoom2Uuid,
            Fp2UnnamedRoom3Uuid,
            Fp2UnnamedRoom4Uuid,
            Fp2UnnamedRoom5Uuid,
            Fp2UnnamedRoom6Uuid,
            Fp2UnnamedRoom7Uuid,
            Fp2UnnamedRoom8Uuid,
        },
        Array.Empty<Guid>(),
        new[]
        {
            Fp2S1,
            Fp2S2,
            Fp2S3,
        });

    private static readonly AagFloorPlanDefinition[] FloorPlans =
    {
        Fp1,
        Fp2,
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
