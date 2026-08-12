using System;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

/// <summary>
/// Shows the MRUK room containing the HMD. A room is accepted only when the
/// HMD projects inside one of that room's floor polygons; room proximity is
/// never used as a fallback.
/// </summary>
public class AagCurrentRoomDisplay : MonoBehaviour
{
    private const string NoRoomDetectedText = "NO ROOM DETECTED";

    [Header("Scene references")]
    [SerializeField] private Transform hmdTransform;
    [SerializeField] private TextMeshProUGUI displayText;

    [Header("Refresh")]
    [Tooltip("How often to test the HMD against MRUK floor polygons.")]
    [SerializeField, Min(0.05f)] private float refreshIntervalSeconds = 0.1f;

    private float nextRefreshTime;
    private string lastReportedRoomId;

    private void OnEnable()
    {
        nextRefreshTime = 0f;
        lastReportedRoomId = null;
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
        {
            return;
        }

        nextRefreshTime = Time.unscaledTime + refreshIntervalSeconds;
        UpdateCurrentRoomDisplay();
    }

    private void UpdateCurrentRoomDisplay()
    {
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace
            && AagMrukSpaceCorrection.IsApplied
            && hmdTransform != null)
        {
            var canonicalPosition = AagMrukSpaceCorrection.ObservedToMrukPosition(
                hmdTransform.position);
            var bakedRoomId = AagFp2BakedSpace.TryResolveRoom(
                canonicalPosition, out var bakedRoomUuid)
                ? bakedRoomUuid.ToString()
                : null;
            if (string.IsNullOrEmpty(bakedRoomId))
                SetDisplay(NoRoomDetectedText, null);
            else
                SetDisplay(
                    $"CURRENT ROOM UUID\n{bakedRoomId}\nSHORT: {bakedRoomId.Substring(0, 8)}",
                    bakedRoomId);
            return;
        }

        var room = FindContainingRoom();
        var roomId = room == null || room.Anchor.Uuid == Guid.Empty
            ? null
            : room.Anchor.Uuid.ToString();

        if (string.IsNullOrEmpty(roomId))
        {
            SetDisplay(NoRoomDetectedText, null);
            return;
        }

        SetDisplay($"CURRENT ROOM UUID\n{roomId}\nSHORT: {roomId.Substring(0, 8)}", roomId);
    }

    private MRUKRoom FindContainingRoom()
    {
        if (hmdTransform == null || MRUK.Instance == null)
        {
            return null;
        }

        MRUKRoom containingRoom = null;
        foreach (var room in MRUK.Instance.Rooms)
        {
            if (!IsHmdInsideAnyFloorPolygon(room))
            {
                continue;
            }

            // Overlapping room polygons are ambiguous. Do not choose one based
            // on distance, list order, or any other arbitrary fallback.
            if (containingRoom != null)
            {
                return null;
            }

            containingRoom = room;
        }

        return containingRoom;
    }

    private bool IsHmdInsideAnyFloorPolygon(MRUKRoom room)
    {
        var mrukQueryPosition = AagMrukSpaceCorrection.ObservedToMrukPosition(
            hmdTransform.position);
        foreach (var floor in room.FloorAnchors)
        {
            if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3)
            {
                continue;
            }

            var hmdInFloorSpace = floor.transform.InverseTransformPoint(mrukQueryPosition);
            if (floor.IsPositionInBoundary(new Vector2(hmdInFloorSpace.x, hmdInFloorSpace.y)))
            {
                return true;
            }
        }

        return false;
    }

    private void SetDisplay(string text, string roomId)
    {
        if (displayText != null)
        {
            displayText.text = text;
        }

        if (lastReportedRoomId == roomId)
        {
            return;
        }

        lastReportedRoomId = roomId;
        Debug.Log($"[AAG Current Room] {(roomId ?? NoRoomDetectedText)}");
    }
}
