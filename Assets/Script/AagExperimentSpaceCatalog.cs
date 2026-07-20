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
    public static readonly Guid Fp1Room2Uuid = Guid.Parse("316e933e-d06d-5af0-8919-10578ccd3900");
    public static readonly Guid Fp1Room3Uuid = Guid.Parse("5768d95b-6710-cc01-1e25-cd03767ab1dd");

    public static readonly AagFloorPlanDefinition Fp1 = new AagFloorPlanDefinition(
        Fp1Id,
        new[]
        {
            Guid.Parse("cb3f5613-94eb-b618-8ba8-1bc3f24cbbc6"),
            Fp1Room2Uuid,
            Fp1Room3Uuid,
            Guid.Parse("6ac2d59f-e9a6-5fc7-ecf7-3617f7bf7133"),
            Guid.Parse("b887c5f7-5e25-95b0-b1d7-a2d13703e00a"),
            Guid.Parse("3342022d-32d9-c32f-cc67-a6993db345ef"),
            Guid.Parse("36f65d12-3dd9-957c-8535-9a774780e5f6"),
            Guid.Parse("93ad5dfc-c2c2-aab8-e032-758172c5a70d"),
        },
        new[]
        {
            Guid.Parse("e919462c-b583-81fd-826a-a1dabc2022e4"),
        },
        new[]
        {
            Fp1S1,
            Fp1S2,
            Fp1S3,
        });

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
