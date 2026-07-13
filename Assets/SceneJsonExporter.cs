using System.Text;
using System.Collections;
using System.IO;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class SceneJsonExporter : MonoBehaviour
{
    private StringBuilder log = new StringBuilder();

    private void Start() => StartCoroutine(WaitAndInspect());

    private IEnumerator WaitAndInspect()
    {
        float timeout = 15f, elapsed = 0f;
        while (MRUK.Instance == null && elapsed < timeout)
        { elapsed += Time.deltaTime; yield return null; }

        if (MRUK.Instance == null) { Write("[Scene] MRUK.Instance null"); yield break; }

        elapsed = 0f;
        while (MRUK.Instance.Rooms.Count == 0 && elapsed < timeout)
        { elapsed += Time.deltaTime; yield return null; }

        Inspect();
    }

    private void Inspect()
    {
        var rooms = MRUK.Instance.Rooms;
        L($"===== ROOM COUNT: {rooms.Count} =====");

        int idx = 0;
        foreach (var room in rooms)
        {
            L($"--- Room #{idx} name={room.name} ---");
            L($"    anchors: {room.Anchors.Count}");

            // FloorAnchor 접근 (버전 차이 대비)
            MRUKAnchor floor = null;
            try { floor = room.FloorAnchor; } catch { }

            if (floor == null)
            {
                // 대체: 앵커 순회하며 FLOOR 라벨 찾기
                foreach (var anc in room.Anchors)
                {
                    if (anc.Label.ToString().ToUpper().Contains("FLOOR")) { floor = anc; break; }
                }
            }

            if (floor != null)
            {
                Vector3 fp = floor.transform.position;
                L($"    FLOOR center: ({fp.x:F3}, {fp.y:F3}, {fp.z:F3})");

                // boundary 시도
                try
                {
                    var b = floor.PlaneBoundary2D;
                    if (b != null && b.Count > 0)
                    {
                        L($"    boundary verts: {b.Count}");
                        foreach (var v in b)
                        {
                            Vector3 w = floor.transform.TransformPoint(new Vector3(v.x, v.y, 0f));
                            L($"      corner: ({w.x:F3}, {w.z:F3})");
                        }
                    }
                    else L("    (no boundary2D)");
                }
                catch (System.Exception e) { L($"    boundary err: {e.Message}"); }
            }
            else
            {
                // FLOOR 못 찾으면 모든 앵커 위치라도 찍기
                L("    (no FloorAnchor) — dumping all anchor positions:");
                foreach (var anc in room.Anchors)
                {
                    Vector3 ap = anc.transform.position;
                    L($"      [{anc.Label}] ({ap.x:F3}, {ap.y:F3}, {ap.z:F3})");
                }
            }
            idx++;
        }

        // 파일 저장
        string path = Path.Combine(Application.persistentDataPath, "scene_log.txt");
        try { File.WriteAllText(path, log.ToString()); Debug.Log($"[Scene] saved: {path}"); }
        catch (System.Exception e) { Debug.LogError("[Scene] write fail: " + e.Message); }
    }

    private void L(string s) { log.AppendLine(s); Debug.Log("[Scene] " + s); }
    private void Write(string s) { L(s); }
}