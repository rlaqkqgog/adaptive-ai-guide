using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Validates the loaded MRUK room IDs against a scanned floor-plan definition.
/// This component never generates geometry, boundaries, or placement points.
/// </summary>
public sealed class AagExperimentSpaceValidator : MonoBehaviour
{
    [SerializeField] private string floorPlanId = AagExperimentSpaceCatalog.Fp1Id;
    [SerializeField, Min(1f)] private float waitForMrukSeconds = 20f;

    public bool IsValidationComplete { get; private set; }
    public bool IsValidationPassed { get; private set; }
    public AagFloorPlanDefinition ValidatedFloorPlan { get; private set; }

    private IEnumerator Start()
    {
        var elapsed = 0f;
        while ((MRUK.Instance == null || MRUK.Instance.Rooms.Count == 0) && elapsed < waitForMrukSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        ValidateLoadedRooms();
    }

    public void ValidateLoadedRooms()
    {
        IsValidationComplete = false;
        IsValidationPassed = false;
        ValidatedFloorPlan = null;

        if (!AagExperimentSpaceCatalog.TryGetFloorPlan(floorPlanId, out var floorPlan))
        {
            Debug.LogError($"[AAG Space] Unknown floor plan '{floorPlanId}'.");
            IsValidationComplete = true;
            return;
        }

        if (MRUK.Instance == null || MRUK.Instance.Rooms.Count == 0)
        {
            Debug.LogError($"[AAG Space] {floorPlan.FloorPlanId} validation failed: no MRUK rooms loaded.");
            IsValidationComplete = true;
            return;
        }

        var loadedRoomIds = new HashSet<Guid>();
        var unexpectedRoomIds = new List<Guid>();
        var duplicateRoomIds = new List<Guid>();

        foreach (var room in MRUK.Instance.Rooms)
        {
            var roomId = room.Anchor.Uuid;
            if (!loadedRoomIds.Add(roomId))
            {
                duplicateRoomIds.Add(roomId);
            }

            if (!floorPlan.ContainsRoom(roomId) && !floorPlan.IsExcludedRoom(roomId))
            {
                unexpectedRoomIds.Add(roomId);
            }
        }

        var missingRoomIds = new List<Guid>();
        foreach (var expectedRoomId in floorPlan.RoomIds)
        {
            if (!loadedRoomIds.Contains(expectedRoomId))
            {
                missingRoomIds.Add(expectedRoomId);
            }
        }

        var missingExcludedRoomIds = new List<Guid>();
        foreach (var expectedExcludedRoomId in floorPlan.ExcludedRoomIds)
        {
            if (!loadedRoomIds.Contains(expectedExcludedRoomId))
            {
                missingExcludedRoomIds.Add(expectedExcludedRoomId);
            }
        }

        var valid = missingRoomIds.Count == 0
            && missingExcludedRoomIds.Count == 0
            && unexpectedRoomIds.Count == 0
            && duplicateRoomIds.Count == 0;

        var summary = $"allowed={floorPlan.RoomIds.Count - missingRoomIds.Count}/{floorPlan.RoomIds.Count}, "
            + $"excluded={floorPlan.ExcludedRoomIds.Count - missingExcludedRoomIds.Count}/{floorPlan.ExcludedRoomIds.Count}, "
            + $"unexpected={unexpectedRoomIds.Count}, duplicates={duplicateRoomIds.Count}, sets={string.Join(",", floorPlan.SetIds)}";

        if (valid)
        {
            ValidatedFloorPlan = floorPlan;
            IsValidationPassed = true;
            IsValidationComplete = true;
            Debug.Log($"[AAG Space] {floorPlan.FloorPlanId} validation passed: {summary}");
            return;
        }

        IsValidationComplete = true;
        Debug.LogError($"[AAG Space] {floorPlan.FloorPlanId} validation failed: {summary}; "
            + $"missing=[{string.Join(",", missingRoomIds)}], "
            + $"missingExcluded=[{string.Join(",", missingExcludedRoomIds)}], "
            + $"unexpected=[{string.Join(",", unexpectedRoomIds)}], "
            + $"duplicates=[{string.Join(",", duplicateRoomIds)}]");
    }
}
