using UnityEngine;

/// <summary>
/// Scene-view-only preview of FP2 Room8 from the confirmed 2026-07-25 export.
/// The highlighted wall is the Room8-facing wall selected for the permanent tag.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class AagFp2Room8ReferencePreview : MonoBehaviour
{
    public const string ExportSource =
        "fp2_room_coordinates_20260725_143215_213.json";

    [SerializeField] private bool showFloor = true;
    [SerializeField] private bool showWalls = true;
    [SerializeField] private bool highlightSelectedTagWall = true;

    private static readonly Vector3[] Floor =
    {
        new Vector3(-4.016209f, -0.04048958f, 3.324534f),
        new Vector3(-0.6626978f, -0.04049005f, 6.346589f),
        new Vector3(3.875747f, -0.04049005f, 1.320845f),
        new Vector3(0.577921f, -0.04048958f, -1.715547f),
    };

    private static readonly Vector3[][] Walls =
    {
        Quad(
            new Vector3(-4.016208f, 2.696541f, 3.324534f),
            new Vector3(-0.6626964f, 2.696540f, 6.346589f),
            new Vector3(-0.6626973f, -0.040490f, 6.346588f),
            new Vector3(-4.016209f, -0.040489f, 3.324534f)),
        Quad(
            new Vector3(3.875748f, 2.696538f, 1.320844f),
            new Vector3(0.577923f, 2.696539f, -1.715548f),
            new Vector3(0.577922f, -0.040490f, -1.715548f),
            new Vector3(3.875748f, -0.040491f, 1.320844f)),
        Quad(
            new Vector3(-0.662697f, 2.696539f, 6.346591f),
            new Vector3(3.875749f, 2.696539f, 1.320844f),
            new Vector3(3.875748f, -0.040490f, 1.320844f),
            new Vector3(-0.662698f, -0.040489f, 6.346590f)),
    };

    private static readonly Vector3[] SelectedTagWall = Quad(
        new Vector3(0.577923f, 2.696540f, -1.715548f),
        new Vector3(-4.016207f, 2.696541f, 3.324533f),
        new Vector3(-4.016207f, -0.040490f, 3.324533f),
        new Vector3(0.577922f, -0.040490f, -1.715548f));

    private void OnDrawGizmos()
    {
        var previous = Gizmos.color;
        if (showFloor)
        {
            Gizmos.color = new Color(0.55f, 0.65f, 0.75f, 0.8f);
            DrawClosed(Floor);
        }
        if (showWalls)
        {
            Gizmos.color = new Color(0.1f, 0.85f, 1f, 0.8f);
            foreach (var wall in Walls) DrawClosed(wall);
        }
        if (highlightSelectedTagWall)
        {
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 1f);
            DrawClosed(SelectedTagWall);
        }
        Gizmos.color = previous;
    }

    private void DrawClosed(Vector3[] points)
    {
        for (var index = 0; index < points.Length; index++)
        {
            var start = transform.TransformPoint(points[index]);
            var end = transform.TransformPoint(points[(index + 1) % points.Length]);
            Gizmos.DrawLine(start, end);
        }
    }

    private static Vector3[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        new[] { a, b, c, d };
}
