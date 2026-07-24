using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stable FP1 destination-tower poses in the post-rescan MRUK floor frames.
///
/// The original tower fallback positions were captured in a tracking frame
/// that did not match the canonical room frame. Applying one global
/// transform to those raw values put the distant towers outside their rooms.
/// These positions were recovered from the boundary crossings of three
/// successful P08 sessions, then moved only as far as necessary to provide
/// clearance inside the 2026-07-21 nine-room rescan floor polygons.
/// </summary>
public static class AagFixedTowerRoomLocalCatalog
{
    public sealed class Entry
    {
        public string TowerId { get; }
        public Guid RoomUuid { get; }
        public Vector3 FloorLocalPosition { get; }
        public Quaternion CanonicalRotation { get; }

        public Entry(
            string towerId,
            string roomUuid,
            Vector3 floorLocalPosition,
            Quaternion canonicalRotation)
        {
            TowerId = towerId;
            RoomUuid = Guid.Parse(roomUuid);
            FloorLocalPosition = floorLocalPosition;
            CanonicalRotation = canonicalRotation;
        }
    }

    private static readonly IReadOnlyDictionary<string, Entry> Placements =
        new Dictionary<string, Entry>(StringComparer.Ordinal)
        {
            ["Tower-1"] = new Entry(
                "Tower-1",
                "0d537c33-3e47-2606-3ea9-897c2bc9f1ce", // room3 / spare bedroom
                new Vector3(-0.8308470f, 1.9016886f, 0.0726156f),
                new Quaternion(-0.0000043f, -0.0202698f, 0.0001170f, 0.9997946f)),
            ["Tower-2"] = new Entry(
                "Tower-2",
                "880b5e63-6438-a8d5-c156-2ddffc48a6d4", // hall2-1 / family space
                new Vector3(-1.6449537f, 1.0249676f, 0.1687589f),
                new Quaternion(0.0000013f, -0.0998836f, -0.0000009f, 0.9949991f)),
            ["Tower-3"] = new Entry(
                "Tower-3",
                "5faa1907-d2e2-7605-7b01-5149a34a4c6d", // room2-1 / guest space
                new Vector3(-0.5078287f, 5.2909595f, 0.0978889f),
                new Quaternion(0.0000080f, -0.3798595f, 0.0000232f, 0.9250442f)),
            ["Tower-4"] = new Entry(
                "Tower-4",
                "7e4d3e1f-3247-602b-3822-c21df384e947", // hall1-1 / living room
                new Vector3(-3.4430504f, 1.0405808f, 0.2075847f),
                new Quaternion(0.0000021f, -0.7369624f, -0.0000013f, 0.6759337f)),
        };

    public static int Count => Placements.Count;

    public static bool TryGet(string towerId, out Entry entry)
    {
        return Placements.TryGetValue(towerId ?? string.Empty, out entry);
    }
}
