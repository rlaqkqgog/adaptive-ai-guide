using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;

public class SpatialAnchorManager : MonoBehaviour
{
    public OVRSpatialAnchor anchorPrefab;
    public const string NumUuidsPlayerPref = "numUuids";
    [SerializeField] private bool enableManualSetAuthoring = true;

    private Canvas canvas;
    private TextMeshProUGUI uuidText;
    private TextMeshProUGUI savedStatusText;
    private List<OVRSpatialAnchor> anchors = new List<OVRSpatialAnchor>();
    private OVRSpatialAnchor lastCreatedAnchor;
    private AnchorLoader anchorLoader;
    private OVRCameraRig cameraRig;
    private AagFp1PlacementAuthoring fp1PlacementAuthoring;
    private AagManualAnchorSetAuthoring manualSetAuthoring;
    private bool anchorSaveInProgress;

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
        cameraRig = FindFirstObjectByType<OVRCameraRig>();
        fp1PlacementAuthoring = GetComponent<AagFp1PlacementAuthoring>();
        if (enableManualSetAuthoring)
        {
            if (fp1PlacementAuthoring != null) fp1PlacementAuthoring.enabled = false;
            manualSetAuthoring = GetComponent<AagManualAnchorSetAuthoring>() ?? gameObject.AddComponent<AagManualAnchorSetAuthoring>();
        }
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
        {
            if (manualSetAuthoring != null && manualSetAuthoring.enabled)
                Debug.Log("[Anchor] Right B legacy erase disabled during manual set authoring. Use UNDO LAST so anchor and manifest stay consistent.");
            else
                UnsaveLastCreatedAnchor();
        }

        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
        {
            if (manualSetAuthoring != null && manualSetAuthoring.enabled)
            {
                Debug.Log("[Anchor] Right Grip erase-all disabled during manual set authoring. Use UNDO LAST for managed anchors.");
            }
            else if (fp1PlacementAuthoring != null && fp1PlacementAuthoring.ReservesRightGripForSetSwitch)
            {
                Debug.Log("[Anchor] Right Grip erase-all skipped because FP1 set switching owns this input while authoring is ready.");
            }
            else
            {
                UnsaveAllAnchors();
            }
        }

        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch))
            LoadSavedAnchors();

        // ── 왼손 X 버튼으로 marker/object 모드 토글 (RawButton 직결) ──
        if (manualSetAuthoring == null && OVRInput.GetDown(OVRInput.RawButton.X))
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
        if (!OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch)
            || !OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch))
        {
            Debug.LogWarning("[Anchor] Create blocked: right controller pose is not fully tracked");
            manualSetAuthoring?.ReportBlockedSave("CREATE blocked: right controller tracking unavailable");
            return;
        }

        cameraRig ??= FindFirstObjectByType<OVRCameraRig>();
        var trackingSpace = cameraRig != null ? cameraRig.trackingSpace : null;
        if (trackingSpace == null)
        {
            Debug.LogError("[Anchor] Create blocked: OVRCameraRig TrackingSpace is missing");
            manualSetAuthoring?.ReportBlockedSave("CREATE blocked: Camera Rig TrackingSpace missing");
            return;
        }

        var localPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        var localRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        var worldPosition = trackingSpace.TransformPoint(localPosition);
        var worldRotation = trackingSpace.rotation * localRotation;
        OVRSpatialAnchor workingAnchor = Instantiate(anchorPrefab, worldPosition, worldRotation);
        Debug.Log($"[Anchor] Create requested local=({localPosition.x:F3},{localPosition.y:F3},{localPosition.z:F3}) "
            + $"world=({worldPosition.x:F3},{worldPosition.y:F3},{worldPosition.z:F3}) trackingOrigin={OVRManager.instance?.trackingOriginType}");

        canvas = workingAnchor.gameObject.GetComponentInChildren<Canvas>();
        if (canvas != null && canvas.transform.childCount >= 2)
        {
            uuidText = canvas.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
            savedStatusText = canvas.transform.GetChild(1).GetComponent<TextMeshProUGUI>();
        }

        StartCoroutine(AnchorCreated(workingAnchor));
    }

    private IEnumerator AnchorCreated(OVRSpatialAnchor workingAnchor)
    {
        while (!workingAnchor.Created && !workingAnchor.Localized)
            yield return new WaitForEndOfFrame();

        Guid anchorGuid = workingAnchor.Uuid;
        anchors.Add(workingAnchor);
        lastCreatedAnchor = workingAnchor;

        if (uuidText != null) uuidText.text = "UUID: " + anchorGuid.ToString();
        if (savedStatusText != null) savedStatusText.text = "Not Saved (" + currentType + ")";
        Debug.Log($"[Anchor] Created UUID={anchorGuid}; world=({workingAnchor.transform.position.x:F3},{workingAnchor.transform.position.y:F3},{workingAnchor.transform.position.z:F3})");
    }

    private void SaveLastCreatedAnchor()
    {
        var anchorToLog = lastCreatedAnchor;   // 콜백 내 참조 고정
        var typeAtSave = currentType;

        if (anchorToLog == null)
        {
            Debug.LogWarning("[Anchor] Save ignored: create an anchor with the right index trigger first.");
            manualSetAuthoring?.ReportBlockedSave("SAVE blocked: create an anchor first");
            return;
        }
        if (anchorSaveInProgress)
        {
            manualSetAuthoring?.ReportBlockedSave("SAVE blocked: previous anchor save is still in progress");
            return;
        }
        var saveContext = default(AagManualAnchorSetAuthoring.SaveContext);
        if (manualSetAuthoring != null && !manualSetAuthoring.BeginAnchorSave(out saveContext, out var blockedReason))
        {
            manualSetAuthoring.ReportBlockedSave(blockedReason);
            return;
        }

        anchorSaveInProgress = true;
        anchorToLog.Save((savedAnchor, success) =>
        {
            anchorSaveInProgress = false;
            if (success)
            {
                print("Successful save");
                savedStatusText.text = "Saved";

                // ── A-1 추가: 저장 성공 시 pose를 JSON에 기록 ──
                LogAnchor(savedAnchor, typeAtSave);
                SaveUuidToPlayerPrefs(savedAnchor.Uuid);
                PlayerPrefs.Save();
                if (manualSetAuthoring != null && !manualSetAuthoring.RecordSuccessfulAnchor(savedAnchor, saveContext, out var manifestFailure))
                    Debug.LogError($"[AAG Manual Sets] anchor saved but manifest append failed uuid={savedAnchor.Uuid}; reason={manifestFailure}");
            }
            else
            {
                Debug.LogError($"[Anchor] Save failed for UUID={anchorToLog.Uuid}; not recorded to PlayerPrefs.");
            }
        });
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

    public List<Guid> GetSavedAnchorUuidsReadOnly()
    {
        var result = new List<Guid>();
        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        for (var index = 0; index < count; index++)
        {
            if (Guid.TryParse(PlayerPrefs.GetString("uuid" + index, string.Empty), out var uuid))
            {
                result.Add(uuid);
            }
        }
        return result;
    }

    public void EraseManagedAnchor(Guid uuid, Action<bool> completed)
    {
        var anchor = anchors.FirstOrDefault(value => value != null && SafeUuid(value) == uuid);
        if (anchor != null)
        {
            EraseResolvedAnchor(uuid, anchor, completed);
            return;
        }

        StartCoroutine(LoadPreviousBuildAnchorAndErase(uuid, completed));
    }

    private IEnumerator LoadPreviousBuildAnchorAndErase(Guid uuid, Action<bool> completed)
    {
        if (anchorLoader == null)
        {
            Debug.LogError($"[AAG Manual Sets Undo] UUID={uuid}; previous-build erase failed: AnchorLoader missing");
            completed?.Invoke(false);
            yield break;
        }

        Debug.Log($"[AAG Manual Sets Undo] UUID={uuid}; not in current session, loading from Quest local anchor storage");
        anchorLoader.LoadAnchorsByUuid(new[] { uuid }, "MANUAL_UNDO_PREVIOUS_BUILD");
        var deadline = Time.realtimeSinceStartup + 15f;
        while (anchorLoader.IsReadOnlyLoadInProgress && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (anchorLoader.IsReadOnlyLoadInProgress)
            anchorLoader.FinalizePendingAsTimedOut();

        if (!anchorLoader.TryGetLocalizedAnchor(uuid, out var localizedAnchor))
        {
            var reason = anchorLoader.LocalizationFailuresReadOnly.TryGetValue(uuid, out var failure)
                ? failure
                : "UUID_NOT_LOCALIZED_WITHIN_TIMEOUT";
            Debug.LogError($"[AAG Manual Sets Undo] UUID={uuid}; previous-build erase failed: {reason}");
            completed?.Invoke(false);
            yield break;
        }

        EraseResolvedAnchor(uuid, localizedAnchor, completed);
    }

    private void EraseResolvedAnchor(Guid uuid, OVRSpatialAnchor anchor, Action<bool> completed)
    {
        anchor.Erase((erasedAnchor, success) =>
        {
            if (success)
            {
                anchors.Remove(anchor);
                if (lastCreatedAnchor == anchor) lastCreatedAnchor = anchors.LastOrDefault();
                RemoveUuidFromPlayerPrefs(uuid);
                anchorLoader?.ForgetLocalizedAnchor(uuid);
                Destroy(anchor.gameObject);
            }
            else
            {
                Debug.LogError($"[AAG Manual Sets Undo] UUID={uuid}; Quest raw anchor erase callback returned false");
            }
            completed?.Invoke(success);
        });
    }

    private static Guid SafeUuid(OVRSpatialAnchor anchor)
    {
        try { return anchor.Uuid; }
        catch (Exception) { return Guid.Empty; }
    }

    private static void RemoveUuidFromPlayerPrefs(Guid uuid)
    {
        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        var remaining = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var value = PlayerPrefs.GetString("uuid" + index, string.Empty);
            if (!string.Equals(value, uuid.ToString(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
                remaining.Add(value);
            PlayerPrefs.DeleteKey("uuid" + index);
        }
        for (var index = 0; index < remaining.Count; index++) PlayerPrefs.SetString("uuid" + index, remaining[index]);
        PlayerPrefs.SetInt(NumUuidsPlayerPref, remaining.Count);
        PlayerPrefs.Save();
    }

    public List<AnchorRecord> GetAnchorRecordsReadOnly()
    {
        return records.ConvertAll(record => new AnchorRecord
        {
            sessionId = record.sessionId,
            uuid = record.uuid,
            type = record.type,
            label = record.label,
            px = record.px,
            py = record.py,
            pz = record.pz,
            rx = record.rx,
            ry = record.ry,
            rz = record.rz,
            rw = record.rw,
            timestamp = record.timestamp,
        });
    }
}
