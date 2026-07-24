using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public string LastValidationSummary { get; private set; } = "not_validated";

    private IEnumerator Start()
    {
        var elapsed = 0f;
        // MRUK fills Rooms progressively.  Do not validate a partial list before
        // the definitive SceneLoadedEvent has completed.
        while ((MRUK.Instance == null || !MRUK.Instance.IsInitialized) && elapsed < waitForMrukSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        ValidateLoadedRooms();
    }

    public void Configure(string configuredFloorPlanId)
    {
        floorPlanId = string.IsNullOrWhiteSpace(configuredFloorPlanId)
            ? AagExperimentSpaceCatalog.Fp1Id
            : configuredFloorPlanId.Trim().ToUpperInvariant();
        IsValidationComplete = false;
        IsValidationPassed = false;
        ValidatedFloorPlan = null;
        LastValidationSummary = "not_validated";
    }

    public void ValidateLoadedRooms()
    {
        IsValidationComplete = false;
        IsValidationPassed = false;
        ValidatedFloorPlan = null;

        var floorPlan = string.Equals(
                ExperimentSpaceRuntime.FloorPlan?.FloorPlanId,
                floorPlanId,
                StringComparison.Ordinal)
            ? ExperimentSpaceRuntime.FloorPlan
            : null;
        if (floorPlan == null
            && !AagExperimentSpaceCatalog.TryGetFloorPlan(floorPlanId, out floorPlan))
        {
            LastValidationSummary = $"unknown_floor_plan_{floorPlanId}";
            Debug.LogError($"[AAG Space] Unknown floor plan '{floorPlanId}'.");
            IsValidationComplete = true;
            return;
        }

        if (floorPlan.RoomIds.Count == 0)
        {
            LastValidationSummary = $"floor_plan_unconfigured_{floorPlan.FloorPlanId}";
            Debug.LogError($"[AAG Space] {floorPlan.FloorPlanId} has no registered Room UUIDs.");
            IsValidationComplete = true;
            return;
        }

        if (MRUK.Instance == null || !MRUK.Instance.IsInitialized)
        {
            LastValidationSummary = "mruk_scene_load_incomplete";
            Debug.LogError($"[AAG Space] {floorPlan.FloorPlanId} validation failed: MRUK SceneLoadedEvent has not completed.");
            IsValidationComplete = true;
            return;
        }

        if (MRUK.Instance.Rooms.Count == 0)
        {
            LastValidationSummary = "no_mruk_rooms_loaded";
            Debug.LogError($"[AAG Space] {floorPlan.FloorPlanId} validation failed: no MRUK rooms loaded.");
            IsValidationComplete = true;
            return;
        }

        var loadedRoomIds = new HashSet<Guid>();
        var unexpectedRoomIds = new List<Guid>();
        var duplicateRoomIds = new List<Guid>();
        var missingFloorRoomIds = new List<Guid>();

        foreach (var room in MRUK.Instance.Rooms)
        {
            if (room == null || room.Anchor == null) continue;
            var roomId = room.Anchor.Uuid;
            if (!loadedRoomIds.Add(roomId))
            {
                duplicateRoomIds.Add(roomId);
            }

            if (!floorPlan.ContainsRoom(roomId) && !floorPlan.IsExcludedRoom(roomId))
            {
                unexpectedRoomIds.Add(roomId);
            }

            if (floorPlan.ContainsRoom(roomId)
                && !(room.FloorAnchors?.Any(floor =>
                    floor != null && floor.Anchor != null) ?? false))
            {
                missingFloorRoomIds.Add(roomId);
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
            && duplicateRoomIds.Count == 0
            && missingFloorRoomIds.Count == 0;

        var summary = $"allowed={floorPlan.RoomIds.Count - missingRoomIds.Count}/{floorPlan.RoomIds.Count}, "
            + $"excluded={floorPlan.ExcludedRoomIds.Count - missingExcludedRoomIds.Count}/{floorPlan.ExcludedRoomIds.Count}, "
            + $"floors={floorPlan.RoomIds.Count - missingFloorRoomIds.Count}/{floorPlan.RoomIds.Count}, "
            + $"unexpected={unexpectedRoomIds.Count}, duplicates={duplicateRoomIds.Count}, sets={string.Join(",", floorPlan.SetIds)}";

        if (valid)
        {
            ValidatedFloorPlan = floorPlan;
            IsValidationPassed = true;
            IsValidationComplete = true;
            LastValidationSummary = $"passed_{summary}";
            Debug.Log($"[AAG Space] {floorPlan.FloorPlanId} validation passed: {summary}");
            return;
        }

        IsValidationComplete = true;
        LastValidationSummary = $"failed_{summary};missing={string.Join("|", missingRoomIds)};"
            + $"missingExcluded={string.Join("|", missingExcludedRoomIds)};"
            + $"missingFloors={string.Join("|", missingFloorRoomIds)};"
            + $"unexpected={string.Join("|", unexpectedRoomIds)};duplicates={string.Join("|", duplicateRoomIds)}";
        Debug.LogError($"[AAG Space] {floorPlan.FloorPlanId} validation failed: {summary}; "
            + $"missing=[{string.Join(",", missingRoomIds)}], "
            + $"missingExcluded=[{string.Join(",", missingExcludedRoomIds)}], "
            + $"missingFloors=[{string.Join(",", missingFloorRoomIds)}], "
            + $"unexpected=[{string.Join(",", unexpectedRoomIds)}], "
            + $"duplicates=[{string.Join(",", duplicateRoomIds)}]");
    }

    /// <summary>
    /// Fail-closed gate used immediately before every participant session.  An
    /// exact UUID match detects a deleted/recreated Space Setup.  Requiring the
    /// HMD to be physically inside the known Room3 floor also detects a rigidly
    /// shifted Meta scene whose UUIDs themselves have not changed.
    /// </summary>
    public bool TryValidateSessionStart(
        Transform headTransform,
        Guid expectedStartRoomUuid,
        out string failure)
    {
        return TryValidateSessionStart(
            headTransform,
            expectedStartRoomUuid,
            false,
            out failure);
    }

    /// <summary>
    /// The fiducial overload keeps the exact nine-room/floor, input-focus, and
    /// World-Lock gates. It skips only MRUK's HMD-in-Room3 test after a fresh,
    /// calibrated physical Room3 fiducial marker has independently established the
    /// start zone.
    /// </summary>
    public bool TryValidateSessionStart(
        Transform headTransform,
        Guid expectedStartRoomUuid,
        bool independentlyValidatedStartMarker,
        out string failure)
    {
        failure = string.Empty;
        ValidateLoadedRooms();
        if (!IsValidationPassed)
        {
            failure = $"space_catalog_mismatch_{Sanitize(LastValidationSummary)}";
            return false;
        }
        if (!OVRManager.hasInputFocus)
        {
            failure = "vr_input_focus_unavailable";
            return false;
        }
        if (MRUK.Instance == null || !MRUK.Instance.IsWorldLockActive)
        {
            failure = "mruk_world_lock_inactive";
            return false;
        }
        if (headTransform == null)
        {
            failure = "center_eye_unavailable";
            return false;
        }

        var startRoom = MRUK.Instance.Rooms.FirstOrDefault(room =>
            room != null && room.Anchor != null && room.Anchor.Uuid == expectedStartRoomUuid);
        if (startRoom == null)
        {
            failure = $"start_room_missing_{expectedStartRoomUuid}";
            return false;
        }

        if (independentlyValidatedStartMarker) return true;

        try
        {
            if (!startRoom.IsPositionInRoom(headTransform.position, true))
            {
                failure = $"hmd_not_inside_room3_{expectedStartRoomUuid}";
                return false;
            }
        }
        catch (Exception exception)
        {
            failure = $"room3_containment_error_{Sanitize(exception.GetType().Name)}";
            return false;
        }

        return true;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character == '-' || character == '_'
                ? character
                : '_').ToArray());
    }
}
