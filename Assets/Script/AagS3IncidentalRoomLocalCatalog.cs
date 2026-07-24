using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Audited FP1-S3 incidental poses in the 2026-07-21 MRUK floor-anchor frames.
/// These replace stale tracking-space fallback poses after the nine-room rescan.
/// </summary>
public static class AagS3IncidentalRoomLocalCatalog
{
    public sealed class Entry
    {
        public string ObjectId { get; }
        public Guid RoomUuid { get; }
        public Guid FloorAnchorUuid { get; }
        public Vector3 FloorLocalPosition { get; }
        public Quaternion FloorLocalRotation { get; }

        public Entry(
            string objectId,
            string roomUuid,
            string floorAnchorUuid,
            Vector3 floorLocalPosition,
            Quaternion floorLocalRotation)
        {
            ObjectId = objectId;
            RoomUuid = Guid.Parse(roomUuid);
            FloorAnchorUuid = Guid.Parse(floorAnchorUuid);
            FloorLocalPosition = floorLocalPosition;
            FloorLocalRotation = floorLocalRotation;
        }
    }

    private static readonly IReadOnlyDictionary<string, Entry> Entries =
        new Dictionary<string, Entry>(StringComparer.Ordinal)
        {
            ["incidental_01_01_Globe"] = new Entry(
                "incidental_01_01_Globe",
                "0d537c33-3e47-2606-3ea9-897c2bc9f1ce",
                "62768480-a2ae-7bb5-3d77-e8de8355e2cb",
                new Vector3(-0.8519073f, -0.6408310f, 0.0828323f),
                new Quaternion(-0.4378319f, -0.5552506f, -0.5552506f, -0.4378319f)),
            ["incidental_02_02_hairDryer"] = new Entry(
                "incidental_02_02_hairDryer",
                "7e4d3e1f-3247-602b-3822-c21df384e947",
                "d43b0dd3-7e89-4970-e816-3a1c72ad338e",
                new Vector3(-1.3661965f, 0.1952929f, 0.0327045f),
                new Quaternion(0.6457532f, 0.2881021f, 0.2881021f, 0.6457532f)),
            ["incidental_03_03_heamer"] = new Entry(
                "incidental_03_03_heamer",
                "b4131884-79b1-7db8-5529-4875ea40bdd5",
                "ed29cadc-780a-d75c-4e0b-32b6a583c447",
                new Vector3(4.3961738f, 1.2786720f, -0.0292574f),
                new Quaternion(-0.1278659f, 0.6954498f, 0.6954497f, -0.1278659f)),
            ["incidental_04_04_riceCooker"] = new Entry(
                "incidental_04_04_riceCooker",
                "7423d1c6-d1e7-3316-6714-6a9b282a396e",
                "85e0011d-1982-5123-f505-13234a57f03b",
                new Vector3(-4.8565543f, 0.1677478f, -0.0710405f),
                new Quaternion(-0.6873366f, -0.1660375f, -0.1660375f, -0.6873365f)),
            ["incidental_05_05_sandClock"] = new Entry(
                "incidental_05_05_sandClock",
                "880b5e63-6438-a8d5-c156-2ddffc48a6d4",
                "52c1b6e2-ecce-d89f-b4bd-9f1a6d660496",
                new Vector3(-4.0890654f, 0.8630089f, 0.0515290f),
                new Quaternion(-0.4135435f, 0.5735693f, 0.5735693f, -0.4135435f)),
        };

    public static int Count => Entries.Count;

    public static bool TryResolve(
        string objectId,
        out Vector3 worldPosition,
        out Quaternion worldRotation,
        out string failure)
    {
        worldPosition = Vector3.zero;
        worldRotation = Quaternion.identity;
        failure = string.Empty;
        if (!Entries.TryGetValue(objectId ?? string.Empty, out var entry))
        {
            failure = $"s3_room_local_entry_missing_{objectId}";
            return false;
        }
        if (MRUK.Instance == null)
        {
            failure = "s3_room_local_mruk_unavailable";
            return false;
        }
        var room = MRUK.Instance.Rooms.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == entry.RoomUuid);
        if (room == null)
        {
            failure = $"s3_room_local_room_missing_{entry.RoomUuid}";
            return false;
        }
        var floor = room.FloorAnchors?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == entry.FloorAnchorUuid);
        if (floor == null)
        {
            failure = $"s3_room_local_floor_missing_{entry.FloorAnchorUuid}";
            return false;
        }
        worldPosition = floor.transform.TransformPoint(entry.FloorLocalPosition);
        worldRotation = floor.transform.rotation * entry.FloorLocalRotation;
        if (!AagRigidPoseRecovery.IsFinite(worldPosition)
            || !AagRigidPoseRecovery.IsFinite(worldRotation))
        {
            failure = $"s3_room_local_non_finite_{objectId}";
            return false;
        }
        return true;
    }
}
