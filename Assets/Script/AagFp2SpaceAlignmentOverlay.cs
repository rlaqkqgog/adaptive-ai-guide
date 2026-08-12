using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

/// <summary>
/// Runtime-only FP2 alignment overlay. After the Room8 AprilTag translation is
/// applied, it draws every loaded MRUK floor outline and vertical corner posts
/// in the corrected physical frame. This is diagnostic geometry only.
/// </summary>
[DisallowMultipleComponent]
public sealed class AagFp2SpaceAlignmentOverlay : MonoBehaviour
{
    private const string Room8Uuid = "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a";
    private const float RefreshSeconds = 0.5f;

    private static readonly Dictionary<string, string> RoomLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0e4e8223-3c13-735b-a552-4acf2ba915a7"] = "ROOM 1",
            ["d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b"] = "ROOM 2",
            ["28e81069-81b3-b60a-0166-50599f88ce42"] = "ROOM 3",
            ["133adc09-ce31-302f-1b53-788b59deeb4f"] = "ROOM 4",
            ["7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b"] = "ROOM 5",
            ["e2e79df2-facb-0150-67b4-a43dfaad9218"] = "ROOM 6",
            ["96a223f3-baf3-7044-2958-6f2468b35c72"] = "ROOM 7",
            [Room8Uuid] = "ROOM 8 · TAG MASTER",
        };

    [SerializeField, Min(0.005f)] private float lineWidthMeters = 0.025f;
    [SerializeField, Min(0.5f)] private float cornerPostHeightMeters = 2.2f;
    [SerializeField] private Color roomColor = new Color(0.05f, 0.9f, 1f, 0.95f);
    [SerializeField] private Color masterRoomColor = new Color(1f, 0.45f, 0.05f, 1f);

    private Transform overlayRoot;
    private Material roomMaterial;
    private Material masterMaterial;
    private float nextRefreshTime;
    private Vector3 lastTranslation = new Vector3(float.NaN, float.NaN, float.NaN);
    private float lastYaw = float.NaN;
    private int lastRoomCount = -1;

    private void Update()
    {
        if (!AagMrukSpaceCorrection.IsApplied || MRUK.Instance == null)
        {
            if (overlayRoot != null) overlayRoot.gameObject.SetActive(false);
            return;
        }

        if (Time.unscaledTime < nextRefreshTime) return;
        nextRefreshTime = Time.unscaledTime + RefreshSeconds;

        var translation = AagMrukSpaceCorrection.TranslationMeters;
        var yaw = AagMrukSpaceCorrection.YawDegrees;
        var roomCount = MRUK.Instance.Rooms?.Count ?? 0;
        if (overlayRoot != null
            && overlayRoot.gameObject.activeSelf
            && roomCount == lastRoomCount
            && Vector3.Distance(translation, lastTranslation) < 0.001f
            && Mathf.Abs(Mathf.DeltaAngle(yaw, lastYaw)) < 0.05f)
            return;

        Rebuild(translation, roomCount);
    }

    private void Rebuild(Vector3 translation, int roomCount)
    {
        EnsureRootAndMaterials();
        for (var index = overlayRoot.childCount - 1; index >= 0; index--)
            Destroy(overlayRoot.GetChild(index).gameObject);

        foreach (var room in MRUK.Instance.Rooms)
        {
            if (room == null || room.Anchor == null || room.Anchor.Uuid == Guid.Empty) continue;
            var uuid = room.Anchor.Uuid.ToString();
            var isMaster = string.Equals(uuid, Room8Uuid, StringComparison.OrdinalIgnoreCase);
            var material = isMaster ? masterMaterial : roomMaterial;
            var color = isMaster ? masterRoomColor : roomColor;

            foreach (var floor in room.FloorAnchors)
            {
                if (floor == null || floor.PlaneBoundary2D == null
                    || floor.PlaneBoundary2D.Count < 3) continue;

                var points = new Vector3[floor.PlaneBoundary2D.Count];
                var center = Vector3.zero;
                for (var index = 0; index < points.Length; index++)
                {
                    var local = floor.PlaneBoundary2D[index];
                    points[index] = AagMrukSpaceCorrection.MrukToObservedPosition(
                        floor.transform.TransformPoint(new Vector3(local.x, local.y, 0f)))
                        + Vector3.up * 0.025f;
                    center += points[index];
                }
                center /= points.Length;

                CreateLoop($"{uuid}_floor", points, material, color);
                var top = new Vector3[points.Length];
                for (var index = 0; index < points.Length; index++)
                {
                    top[index] = points[index] + Vector3.up * cornerPostHeightMeters;
                    CreateSegment($"{uuid}_corner_{index}", points[index], top[index], material, color);
                }
                CreateLoop($"{uuid}_top", top, material, color);
                CreateRoomLabel(uuid, center + Vector3.up * 0.04f, color);
            }
        }

        overlayRoot.gameObject.SetActive(true);
        lastTranslation = translation;
        lastYaw = AagMrukSpaceCorrection.YawDegrees;
        lastRoomCount = roomCount;
        Debug.Log(
            $"[AAG FP2 Space Overlay] rebuilt rooms={roomCount}; translation={translation:F3}; "
            + $"yaw={lastYaw:F1}deg");
    }

    private void EnsureRootAndMaterials()
    {
        if (overlayRoot == null)
        {
            var root = new GameObject("FP2_SpaceAlignmentOverlay_Runtime");
            overlayRoot = root.transform;
        }

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color");
        if (shader == null) throw new MissingReferenceException("No unlit overlay shader found.");
        if (roomMaterial == null) roomMaterial = CreateMaterial(shader, roomColor, "FP2RoomOverlay");
        if (masterMaterial == null) masterMaterial = CreateMaterial(shader, masterRoomColor, "FP2Room8Overlay");
    }

    private static Material CreateMaterial(Shader shader, Color color, string name)
    {
        var material = new Material(shader) { name = name, color = color };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        return material;
    }

    private void CreateLoop(string name, IReadOnlyList<Vector3> points, Material material, Color color)
    {
        var line = CreateLine(name, points.Count + 1, material, color);
        for (var index = 0; index < points.Count; index++) line.SetPosition(index, points[index]);
        line.SetPosition(points.Count, points[0]);
    }

    private void CreateSegment(string name, Vector3 start, Vector3 end, Material material, Color color)
    {
        var line = CreateLine(name, 2, material, color);
        line.SetPosition(0, start);
        line.SetPosition(1, end);
    }

    private LineRenderer CreateLine(string name, int count, Material material, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(overlayRoot, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;
        line.positionCount = count;
        line.startWidth = lineWidthMeters;
        line.endWidth = lineWidthMeters;
        line.sharedMaterial = material;
        line.startColor = color;
        line.endColor = color;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        return line;
    }

    private void CreateRoomLabel(string uuid, Vector3 position, Color color)
    {
        var go = new GameObject($"{uuid}_label");
        go.transform.SetParent(overlayRoot, false);
        go.transform.SetPositionAndRotation(position, Quaternion.Euler(90f, 0f, 0f));
        var text = go.AddComponent<TextMeshPro>();
        text.text = RoomLabels.TryGetValue(uuid, out var label)
            ? label
            : $"ROOM {uuid.Substring(0, 8)}";
        text.alignment = TextAlignmentOptions.Center;
        text.color = color;
        text.fontSize = 2.2f;
        text.rectTransform.sizeDelta = new Vector2(4f, 0.8f);
    }

    private void OnDestroy()
    {
        if (overlayRoot != null) Destroy(overlayRoot.gameObject);
        if (roomMaterial != null) Destroy(roomMaterial);
        if (masterMaterial != null) Destroy(masterMaterial);
    }
}
