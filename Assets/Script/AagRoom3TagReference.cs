using System;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Authoring marker for the physical Room3 hybrid board. The scene transform is
/// an editor preview; runtime resolution uses the saved Room3 floor-local pose
/// so MRUK World Lock can move the live floor without invalidating the marker.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class AagRoom3TagReference : MonoBehaviour
{
    public const string Room3Uuid = "0d537c33-3e47-2606-3ea9-897c2bc9f1ce";
    public const string Room3FloorAnchorUuid = "62768480-a2ae-7bb5-3d77-e8de8355e2cb";
    public const float PrintedBoardSizeMeters = 0.171f;
    public const float DetectionBorderSizeMeters = 0.095f;

    [Header("Confirm after placing this transform on the Room3 wall")]
    [Tooltip("Alignment is fail-closed until this is checked after the tag center has been positioned.")]
    [SerializeField] private bool referencePlacementConfirmed;

    [Header("Identity (invalidate after a new Space Setup scan)")]
    [SerializeField] private string roomUuid = Room3Uuid;
    [SerializeField] private string floorAnchorUuid = Room3FloorAnchorUuid;

    [Header("2026-07-21 Room3 export frame")]
    [SerializeField] private Vector3 exportFloorWorldPosition =
        new Vector3(0.15373255f, -0.01663506f, 1.2934226f);
    [SerializeField] private Quaternion exportFloorWorldRotation =
        new Quaternion(0.5104028f, -0.48937613f, -0.48937613f, -0.51040286f);

    [Header("Runtime Room3 floor-local reference")]
    [SerializeField] private Vector3 floorLocalPosition;
    [SerializeField] private Quaternion floorLocalRotation = Quaternion.identity;

    public bool ReferencePlacementConfirmed => referencePlacementConfirmed;
    public string ExpectedRoomUuid => roomUuid;
    public string ExpectedFloorAnchorUuid => floorAnchorUuid;
    public Vector3 FloorLocalPosition => floorLocalPosition;
    public Quaternion FloorLocalRotation => floorLocalRotation;

    private void Reset()
    {
        CaptureFloorLocalPoseFromSceneTransform();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
            CaptureFloorLocalPoseFromSceneTransform();
    }

    [ContextMenu("Capture Floor-Local Pose From Scene Transform")]
    public void CaptureFloorLocalPoseFromSceneTransform()
    {
        var exportFloorPose = new Pose(exportFloorWorldPosition, exportFloorWorldRotation);
        var referenceWorldPose = new Pose(transform.position, transform.rotation);
        var localPose = AagFiducialMarkerStore.RelativeTo(exportFloorPose, referenceWorldPose);
        floorLocalPosition = localPose.position;
        floorLocalRotation = localPose.rotation;
    }

    public bool TryResolveExpectedWorldPose(out Pose pose, out string failure)
    {
        pose = default;
        failure = string.Empty;

        if (!referencePlacementConfirmed)
        {
            failure = "room3_tag_reference_not_confirmed";
            return false;
        }
        if (!Guid.TryParse(roomUuid, out var expectedRoomUuid)
            || !Guid.TryParse(floorAnchorUuid, out var expectedFloorUuid))
        {
            failure = "room3_tag_reference_uuid_invalid";
            return false;
        }
        if (MRUK.Instance == null || !MRUK.Instance.IsInitialized)
        {
            failure = "mruk_not_initialized";
            return false;
        }

        var room = MRUK.Instance.Rooms.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == expectedRoomUuid);
        if (room == null)
        {
            failure = $"room3_missing_{expectedRoomUuid}";
            return false;
        }

        var floor = room.FloorAnchors?.FirstOrDefault(value =>
            value != null && value.Anchor != null && value.Anchor.Uuid == expectedFloorUuid);
        if (floor == null)
        {
            failure = $"room3_floor_changed_or_missing_{expectedFloorUuid}";
            return false;
        }

        var floorPose = new Pose(floor.transform.position, floor.transform.rotation);
        pose = AagFiducialMarkerStore.Compose(
            floorPose,
            new Pose(floorLocalPosition, floorLocalRotation));
        return AagFiducialMarkerStore.IsFinite(pose);
    }

    private void OnDrawGizmos()
    {
        var matrixBefore = Gizmos.matrix;
        var colorBefore = Gizmos.color;
        Gizmos.matrix = transform.localToWorldMatrix;

        Gizmos.color = referencePlacementConfirmed
            ? new Color(0.15f, 1f, 0.35f, 0.95f)
            : new Color(1f, 0.65f, 0.1f, 0.95f);
        DrawSquare(PrintedBoardSizeMeters);

        Gizmos.color = Color.white;
        DrawSquare(DetectionBorderSizeMeters);
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.25f);
        Gizmos.DrawSphere(Vector3.zero, 0.012f);

        Gizmos.matrix = matrixBefore;
        Gizmos.color = colorBefore;
    }

    private static void DrawSquare(float size)
    {
        var half = size * 0.5f;
        var a = new Vector3(-half, -half, 0f);
        var b = new Vector3(half, -half, 0f);
        var c = new Vector3(half, half, 0f);
        var d = new Vector3(-half, half, 0f);
        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine(b, c);
        Gizmos.DrawLine(c, d);
        Gizmos.DrawLine(d, a);
    }
}
