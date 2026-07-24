using UnityEngine;

/// <summary>
/// Scene-view-only wire preview of the confirmed 2026-07-21 FP1 Room3 export.
/// It lets the tag reference be placed against the corresponding wall without
/// creating runtime MRUK geometry or replacing live Space Setup data.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class AagRoom3ReferencePreview : MonoBehaviour
{
    public const string ExportSource =
        "aag_room_coordinates_20260721_024847_536.json";

    [SerializeField] private bool showFloor = true;
    [SerializeField] private bool showWalls = true;
    [SerializeField] private bool showDoor = true;

    private static readonly Vector3[] Floor =
    {
        new Vector3(-2.7471106f, -0.0166353f, 4.8266306f),
        new Vector3(2.9111092f, -0.0166353f, 4.974218f),
        new Vector3(2.9664996f, -0.0166351f, 2.4114347f),
        new Vector3(2.8012073f, -0.0166351f, 2.402764f),
        new Vector3(2.9966564f, -0.0166348f, -2.2418394f),
        new Vector3(-2.6031506f, -0.0166348f, -2.3682008f),
    };

    private static readonly Vector3[][] Walls =
    {
        Quad(
            new Vector3(2.9966557f, 2.688974f, -2.2418392f),
            new Vector3(-2.6031511f, 2.6889732f, -2.3682013f),
            new Vector3(-2.6031506f, -0.0166347f, -2.3682015f),
            new Vector3(2.9966562f, -0.016634f, -2.2418394f)),
        Quad(
            new Vector3(-2.6031516f, 2.6889722f, -2.3682008f),
            new Vector3(-2.7471094f, 2.6889718f, 4.8266311f),
            new Vector3(-2.7471094f, -0.016636f, 4.8266311f),
            new Vector3(-2.6031516f, -0.0166355f, -2.3682008f)),
        Quad(
            new Vector3(-2.7471104f, 2.688972f, 4.8266311f),
            new Vector3(2.9111087f, 2.6889725f, 4.9742188f),
            new Vector3(2.9111092f, -0.0166347f, 4.9742188f),
            new Vector3(-2.7471099f, -0.0166354f, 4.8266311f)),
        Quad(
            new Vector3(2.8012064f, 2.6889737f, 2.4027643f),
            new Vector3(2.9966569f, 2.688974f, -2.2418385f),
            new Vector3(2.9966569f, -0.0166337f, -2.2418385f),
            new Vector3(2.8012064f, -0.016634f, 2.4027643f)),
        Quad(
            new Vector3(2.9111094f, 2.688974f, 4.9742193f),
            new Vector3(2.9665003f, 2.6889744f, 2.4114366f),
            new Vector3(2.9665005f, -0.0166334f, 2.4114363f),
            new Vector3(2.9111099f, -0.0166336f, 4.9742193f)),
    };

    private static readonly Vector3[] Door = Quad(
        new Vector3(-1.5999818f, 2.6418481f, -2.3430834f),
        new Vector3(-2.5310006f, 2.6418478f, -2.3641562f),
        new Vector3(-2.5310006f, -0.0156815f, -2.3641567f),
        new Vector3(-1.5999815f, -0.0156814f, -2.3430839f));

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
        if (showDoor)
        {
            Gizmos.color = new Color(1f, 0.8f, 0.1f, 1f);
            DrawClosed(Door);
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
