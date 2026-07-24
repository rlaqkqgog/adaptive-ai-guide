using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class TrackLogger : MonoBehaviour
{
    [Tooltip("비우면 자동으로 CenterEyeAnchor를 찾음")]
    public Transform headTransform;              // CenterEyeAnchor
    public TextMeshProUGUI statusText;           // (선택) 상태 표시용, 없어도 됨

    public float interval = 0.1f;                // 기록 간격(초)

    private bool logging = false;
    private float timer = 0f;
    private float sessionStart = 0f;
    private string sessionId;
    private List<TrackPoint> points = new List<TrackPoint>();
    private string LogPath => Path.Combine(
        Application.persistentDataPath,
        ExperimentSpaceRuntime.UsesLegacyFp1Storage
            ? "track_log.json"
            : ExperimentSpaceRuntime.NamespacedFileName("track_log"));

    private void Awake()
    {
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (headTransform == null)
        {
            var rig = FindObjectOfType<OVRCameraRig>();
            if (rig != null) headTransform = rig.centerEyeAnchor;
        }
        if (headTransform == null)
            Debug.LogError("[Track] headTransform(CenterEyeAnchor) 미할당 — 인스펙터에서 지정하세요.");
    }

    private void Update()
    {
        // 왼손 Y로 기록 시작/정지 토글
        if (OVRInput.GetDown(OVRInput.RawButton.Y))
            ToggleLogging();

        if (!logging || headTransform == null) return;

        timer += Time.deltaTime;
        if (timer >= interval)
        {
            timer = 0f;
            RecordPoint();
        }
    }

    private void ToggleLogging()
    {
        logging = !logging;

        if (logging)
        {
            sessionStart = Time.time;
            timer = interval;   // 시작 즉시 첫 점 기록
            Debug.Log("[Track] Logging START");
            SetStatus("TRACK: REC ●");
        }
        else
        {
            WriteToFile();
            Debug.Log($"[Track] Logging STOP — {points.Count} points saved");
            SetStatus($"TRACK: STOP ({points.Count} pts)");
        }
    }

    private void RecordPoint()
    {
        Vector3 p = headTransform.position;
        float yaw = headTransform.eulerAngles.y;

        points.Add(new TrackPoint
        {
            t = Time.time - sessionStart,
            px = p.x, py = p.y, pz = p.z,
            yaw = yaw
        });

        // 너무 자주 파일 쓰면 부하 → 100점마다 중간 저장
        if (points.Count % 100 == 0) WriteToFile();
    }

    private void WriteToFile()
    {
        try
        {
            var wrapper = new TrackLog { sessionId = sessionId, interval = interval, points = points };
            File.WriteAllText(LogPath, JsonUtility.ToJson(wrapper, true));
        }
        catch (Exception e) { Debug.LogError("[Track] Write failed: " + e.Message); }
    }

    private void SetStatus(string s)
    {
        if (statusText != null) statusText.text = s;
    }

    // 앱이 백그라운드/종료될 때 안전 저장
    private void OnApplicationPause(bool paused)
    {
        if (paused && logging) WriteToFile();
    }
    private void OnApplicationQuit()
    {
        if (logging) WriteToFile();
    }
}
