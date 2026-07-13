using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class SpatialAnchorManager : MonoBehaviour
{
    public OVRSpatialAnchor anchorPrefab;
    public const string NumUuidsPlayerPref = "numUuids";

    private Canvas canvas;
    private TextMeshProUGUI uuidText;
    private TextMeshProUGUI savedStatusText;
    private List<OVRSpatialAnchor> anchors = new List<OVRSpatialAnchor>();
    private OVRSpatialAnchor lastCreatedAnchor;
    private AnchorLoader anchorLoader;

    // ── A-1 추가: pose 로깅 ──
    private string currentType = "marker";   // 시작은 marker 모드
    private int markerIndex = 0;
    private int objectIndex = 0;
    private string sessionId;
    private List<AnchorRecord> records = new List<AnchorRecord>();
    private string LogPath => Path.Combine(Application.persistentDataPath, "anchor_log.json");

    private void Awake()
    {
        anchorLoader = GetComponent<AnchorLoader>();
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        LoadRecordsFromFile();
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            CreateSpatialAnchor();

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            SaveLastCreatedAnchor();

        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
            UnsaveLastCreatedAnchor();

        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
            UnsaveAllAnchors();

        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch))
            LoadSavedAnchors();

        // ── 왼손 X 버튼으로 marker/object 모드 토글 (RawButton 직결) ──
        if (OVRInput.GetDown(OVRInput.RawButton.X))
        {
            currentType = (currentType == "marker") ? "object" : "marker";
            ShowModeToast();
        }
    }

    private void ShowModeToast()
    {
        Debug.Log($"[Anchor] Mode → {currentType}");
        if (savedStatusText != null)
        savedStatusText.text = $"MODE: {currentType}";
}

    public void CreateSpatialAnchor()
    {
        OVRSpatialAnchor workingAnchor = Instantiate(anchorPrefab, OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch), OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch));

        canvas = workingAnchor.gameObject.GetComponentInChildren<Canvas>();
        uuidText = canvas.gameObject.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
        savedStatusText = canvas.gameObject.transform.GetChild(1).GetComponent<TextMeshProUGUI>();

        StartCoroutine(AnchorCreated(workingAnchor));
    }

    private IEnumerator AnchorCreated(OVRSpatialAnchor workingAnchor)
    {
        while (!workingAnchor.Created && !workingAnchor.Localized)
            yield return new WaitForEndOfFrame();

        Guid anchorGuid = workingAnchor.Uuid;
        anchors.Add(workingAnchor);
        lastCreatedAnchor = workingAnchor;

        uuidText.text = "UUID: " + anchorGuid.ToString();
        savedStatusText.text = "Not Saved (" + currentType + ")";
    }

    private void SaveLastCreatedAnchor()
    {
        var anchorToLog = lastCreatedAnchor;   // 콜백 내 참조 고정
        var typeAtSave = currentType;

        anchorToLog.Save((savedAnchor, success) =>
        {
            if (success)
            {
                print("Successful save");
                savedStatusText.text = "Saved";

                // ── A-1 추가: 저장 성공 시 pose를 JSON에 기록 ──
                LogAnchor(savedAnchor, typeAtSave);
            }
        });

        SaveUuidToPlayerPrefs(anchorToLog.Uuid);
    }

    // ── A-1 추가: pose 로깅 로직 ──
    private void LogAnchor(OVRSpatialAnchor anchor, string type)
    {
        Vector3 p = anchor.transform.position;   // Unity world (= OVR tracking space) 좌표
        Quaternion r = anchor.transform.rotation;

        string label = (type == "marker") ? $"marker_{markerIndex++}" : $"object_{objectIndex++}";

        records.Add(new AnchorRecord
        {
            sessionId = sessionId,
            uuid = anchor.Uuid.ToString(),
            type = type,
            label = label,
            px = p.x, py = p.y, pz = p.z,
            rx = r.x, ry = r.y, rz = r.z, rw = r.w,
            timestamp = DateTime.Now.ToString("HH:mm:ss")
        });

        WriteRecordsToFile();

        if (savedStatusText != null)
            savedStatusText.text = $"Saved [{label}]";

        Debug.Log($"[Anchor] Saved {label} @ ({p.x:F2},{p.y:F2},{p.z:F2})");
    }

    private void WriteRecordsToFile()
    {
        try
        {
            var wrapper = new AnchorRecordList { records = this.records };
            File.WriteAllText(LogPath, JsonUtility.ToJson(wrapper, true));
        }
        catch (Exception e) { Debug.LogError("[Anchor] Write failed: " + e.Message); }
    }

    private void LoadRecordsFromFile()
    {
        try
        {
            if (File.Exists(LogPath))
            {
                var wrapper = JsonUtility.FromJson<AnchorRecordList>(File.ReadAllText(LogPath));
                if (wrapper?.records != null) records = wrapper.records;
            }
        }
        catch (Exception e) { Debug.LogError("[Anchor] Load failed: " + e.Message); }
    }

    void SaveUuidToPlayerPrefs(Guid uuid)
    {
        if (!PlayerPrefs.HasKey(NumUuidsPlayerPref))
            PlayerPrefs.SetInt(NumUuidsPlayerPref, 0);

        int playerNumUuids = PlayerPrefs.GetInt(NumUuidsPlayerPref);
        PlayerPrefs.SetString("uuid" + playerNumUuids, uuid.ToString());
        PlayerPrefs.SetInt(NumUuidsPlayerPref, ++playerNumUuids);
    }

    private void UnsaveLastCreatedAnchor()
    {
        lastCreatedAnchor.Erase((lastCreatedAnchor, success) =>
        {
            if (success) savedStatusText.text = "Not Saved";
        });
    }

    private void UnsaveAllAnchors()
    {
        foreach (var anchor in anchors) UnsaveAnchor(anchor);
        anchors.Clear();
        ClearAllUuidsFromPlayerPrefs();
    }

    private void UnsaveAnchor(OVRSpatialAnchor anchor)
    {
        anchor.Erase((erasedAnchor, success) =>
        {
            if (success)
            {
                var textComponents = erasedAnchor.GetComponentsInChildren<TextMeshProUGUI>();
                if (textComponents.Length > 1)
                    textComponents[1].text = "Not Saved";
            }
        });
    }

    private void ClearAllUuidsFromPlayerPrefs()
    {
        if (PlayerPrefs.HasKey(NumUuidsPlayerPref))
        {
            int playerNumUuids = PlayerPrefs.GetInt(NumUuidsPlayerPref);
            for (int i = 0; i < playerNumUuids; i++)
                PlayerPrefs.DeleteKey("uuid" + i);
            PlayerPrefs.DeleteKey(NumUuidsPlayerPref);
            PlayerPrefs.Save();
        }
    }

    public void LoadSavedAnchors() => anchorLoader.LoadAnchorsByUuid();
}