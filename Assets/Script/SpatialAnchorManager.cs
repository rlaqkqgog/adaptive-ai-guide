using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;

public class SpatialAnchorManager : MonoBehaviour
{
    [Header("Default Anchor Prefab")]
    public OVRSpatialAnchor anchorPrefab;

    [Header("Marker Prefabs by Manifest Color")]
    [Tooltip("Prefab used by entries whose JSON color is Red. Falls back to Anchor Prefab when empty.")]
    public OVRSpatialAnchor redAnchorPrefab;
    [Tooltip("Prefab used by entries whose JSON color is Blue. Falls back to Anchor Prefab when empty.")]
    public OVRSpatialAnchor blueAnchorPrefab;
    [Tooltip("Prefab used by entries whose JSON color is Green. Falls back to Anchor Prefab when empty.")]
    public OVRSpatialAnchor greenAnchorPrefab;
    [Tooltip("Prefab used by entries whose JSON color is Yellow. Falls back to Anchor Prefab when empty.")]
    public OVRSpatialAnchor yellowAnchorPrefab;

    public static string NumUuidsPlayerPref => ExperimentSpaceRuntime.PlayerPrefsCountKey;
    [SerializeField] private bool enableManualSetAuthoring = true;

    private Canvas canvas;
    private TextMeshProUGUI uuidText;
    private TextMeshProUGUI savedStatusText;
    private List<OVRSpatialAnchor> anchors = new List<OVRSpatialAnchor>();
    private OVRSpatialAnchor lastCreatedAnchor;
    private AnchorLoader anchorLoader;
    private OVRCameraRig cameraRig;
    private AagFp1PlacementAuthoring fp1PlacementAuthoring;
    private AagExperimentSpaceValidator experimentSpaceValidator;
    private AagRoomCoordinateExporter roomCoordinateExporter;
    private AagManualAnchorSetAuthoring manualSetAuthoring;
    private bool anchorSaveInProgress;

    // ── A-1 추가: pose 로깅 ──
    private string currentType = "marker";   // 시작은 marker 모드
    private int markerIndex = 0;
    private int objectIndex = 0;
    private string sessionId;
    private List<AnchorRecord> records = new List<AnchorRecord>();
    private static string CurrentLogPath => Path.Combine(
        Application.persistentDataPath,
        ExperimentSpaceRuntime.UsesLegacyFp1Storage
            ? "anchor_log.json"
            : ExperimentSpaceRuntime.NamespacedFileName("anchor_log"));
    private string LogPath => CurrentLogPath;

    private void Awake()
    {
        var experimentMain = FindFirstObjectByType<ExperimentMain>();
        if (experimentMain != null)
            ExperimentSpaceRuntime.Configure(experimentMain.Configuration);
        anchorLoader = GetComponent<AnchorLoader>();
        cameraRig = FindFirstObjectByType<OVRCameraRig>();
        fp1PlacementAuthoring = GetComponent<AagFp1PlacementAuthoring>();
        experimentSpaceValidator = GetComponent<AagExperimentSpaceValidator>();
        roomCoordinateExporter = GetComponent<AagRoomCoordinateExporter>();
        if (isActiveAndEnabled && enableManualSetAuthoring)
        {
            if (fp1PlacementAuthoring != null) fp1PlacementAuthoring.enabled = false;
            if (experimentSpaceValidator != null) experimentSpaceValidator.enabled = false;
            if (roomCoordinateExporter != null) roomCoordinateExporter.enabled = false;
            manualSetAuthoring = GetComponent<AagManualAnchorSetAuthoring>() ?? gameObject.AddComponent<AagManualAnchorSetAuthoring>();
            Debug.Log("[AAG Manual Set] Automatic placement, candidate solver, floor validation, and room export-on-start are disabled.");
        }
        sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        LoadRecordsFromFile();
    }

    public OVRSpatialAnchor GetAnchorPrefabForColor(string color)
    {
        OVRSpatialAnchor selected = null;
        if (string.Equals(color, "Red", StringComparison.OrdinalIgnoreCase)) selected = redAnchorPrefab;
        else if (string.Equals(color, "Blue", StringComparison.OrdinalIgnoreCase)) selected = blueAnchorPrefab;
        else if (string.Equals(color, "Green", StringComparison.OrdinalIgnoreCase)) selected = greenAnchorPrefab;
        else if (string.Equals(color, "Yellow", StringComparison.OrdinalIgnoreCase)) selected = yellowAnchorPrefab;

        return selected != null ? selected : anchorPrefab;
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            if (manualSetAuthoring != null && manualSetAuthoring.IsTutorialTestBusy)
                Debug.Log("[Anchor] Manual create blocked while TEST-3 operation is running.");
            else
                CreateSpatialAnchor();
        }

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            if (manualSetAuthoring != null && manualSetAuthoring.IsTutorialTestBusy)
                Debug.Log("[Anchor] Manual save blocked while TEST-3 operation is running.");
            else
                SaveLastCreatedAnchor();
        }

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
        {
            if (manualSetAuthoring != null && manualSetAuthoring.enabled)
                Debug.Log("[Anchor] Right Thumbstick PlayerPrefs load disabled during manual set authoring. Use LOAD ACTIVE SET.");
            else
                LoadSavedAnchors();
        }

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
        if (manualSetAuthoring != null && manualSetAuthoring.IsFixedTowerMode)
        {
            var levelForward = Vector3.ProjectOnPlane(worldRotation * Vector3.forward, Vector3.up);
            worldRotation = levelForward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(levelForward.normalized, Vector3.up)
                : Quaternion.identity;
        }
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
        while (workingAnchor != null && !workingAnchor.Created)
            yield return new WaitForEndOfFrame();

        if (workingAnchor == null) yield break;

        Guid anchorGuid = workingAnchor.Uuid;
        anchors.Add(workingAnchor);
        lastCreatedAnchor = workingAnchor;
        manualSetAuthoring?.NotifyAnchorCreated(workingAnchor);

        if (uuidText != null) uuidText.text = "UUID: " + anchorGuid.ToString();
        if (savedStatusText != null) savedStatusText.text = "Not Saved (" + currentType + ")";
        Debug.Log($"[Anchor] Created UUID={anchorGuid}; world=({workingAnchor.transform.position.x:F3},{workingAnchor.transform.position.y:F3},{workingAnchor.transform.position.z:F3})");
    }

    private async void SaveLastCreatedAnchor()
    {
        var anchorToLog = lastCreatedAnchor;
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
        if (manualSetAuthoring != null && !manualSetAuthoring.BeginAnchorSave(anchorToLog, out saveContext, out var blockedReason))
        {
            manualSetAuthoring.ReportBlockedSave(blockedReason);
            return;
        }

        anchorSaveInProgress = true;
        try
        {
            Debug.Log($"[AAG Anchor Save] Save start uuid={anchorToLog.Uuid} "
                + $"mode={(saveContext.IsReplacement ? "REPAIR" : saveContext.IsFixedTower ? "FIXED_TOWER" : saveContext.IsIncidental ? "INCIDENTAL" : "NORMAL")} "
                + $"marker={saveContext.MarkerId}");
            var localized = anchorToLog.Localized || await anchorToLog.WhenLocalizedAsync();
            if (anchorToLog == null)
            {
                Debug.LogError("[AAG Anchor Save] Failed reason=anchor object destroyed while localizing");
                manualSetAuthoring?.ReportBlockedSave("SAVE failed: anchor object destroyed");
                return;
            }
            if (!localized)
            {
                Debug.LogError($"[AAG Anchor Save] Failed uuid={anchorToLog.Uuid} reason=create/localize failed");
                manualSetAuthoring?.ReportBlockedSave("SAVE failed: anchor could not localize");
                return;
            }

            var saveResult = await OVRSpatialAnchor.SaveAnchorsAsync(new[] { anchorToLog });
            if (anchorToLog == null)
            {
                Debug.LogError("[AAG Anchor Save] Failed reason=anchor object destroyed while saving");
                manualSetAuthoring?.ReportBlockedSave("SAVE failed: anchor object destroyed");
                return;
            }
            if (!saveResult.Success)
            {
                var reason = saveResult.Status == OVRAnchor.SaveResult.FailureInsufficientView
                    ? "insufficient view; scan the surrounding space and create again"
                    : saveResult.Status.ToString();
                Debug.LogError($"[AAG Anchor Save] Failed uuid={anchorToLog.Uuid} status={saveResult.Status} reason={reason}");
                if (savedStatusText != null) savedStatusText.text = $"Save Failed: {saveResult.Status}";
                manualSetAuthoring?.ReportBlockedSave($"SAVE failed: {saveResult.Status}");
                return;
            }

            // Towers and incidental objects each have their own authoritative JSON.
            // Neither is allowed into the stone-object PlayerPrefs list.
            if (!saveContext.UsesSeparateStore)
            {
                SaveUuidToPlayerPrefs(anchorToLog.Uuid);
                PlayerPrefs.Save();
            }
            if (manualSetAuthoring != null)
            {
                if (!manualSetAuthoring.RecordSuccessfulAnchor(anchorToLog, saveContext, out var manifestFailure))
                {
                    if (!saveContext.UsesSeparateStore) RemoveUuidFromPlayerPrefs(anchorToLog.Uuid);
                    Debug.LogError($"[AAG Manual Set] Failed uuid={anchorToLog.Uuid} reason=manifest update failed: {manifestFailure}; "
                        + "newUuidRemovedFromPlayerPrefs=true rawQuestAnchorOrphaned=true");
                    manualSetAuthoring.ReportBlockedSave($"MANIFEST update failed: {manifestFailure}");
                    return;
                }

                if (saveContext.IsReplacement)
                    ReplaceUuidInPlayerPrefs(saveContext.ReplacedUuid, anchorToLog.Uuid);
            }
            else
            {
                LogAnchor(anchorToLog, typeAtSave);
            }

            if (savedStatusText != null) savedStatusText.text = saveContext.IsReplacement ? "Repaired" : "Saved";
            Debug.Log($"[AAG Anchor Save] Saved uuid={anchorToLog.Uuid} status={saveResult.Status} "
                + $"mode={(saveContext.IsReplacement ? "REPAIR" : saveContext.IsFixedTower ? "FIXED_TOWER" : saveContext.IsIncidental ? "INCIDENTAL" : "NORMAL")} "
                + $"marker={saveContext.MarkerId}");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Anchor Save] Failed uuid={SafeUuid(anchorToLog)} reason={exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            manualSetAuthoring?.ReportBlockedSave($"SAVE exception: {exception.Message}");
        }
        finally
        {
            anchorSaveInProgress = false;
        }
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
        PlayerPrefs.SetString(ExperimentSpaceRuntime.PlayerPrefsUuidKey(playerNumUuids), uuid.ToString());
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
                PlayerPrefs.DeleteKey(ExperimentSpaceRuntime.PlayerPrefsUuidKey(i));
            PlayerPrefs.DeleteKey(NumUuidsPlayerPref);
            PlayerPrefs.Save();
        }
    }

    public void LoadSavedAnchors()
    {
        Debug.LogWarning(
            "[Anchor] Legacy PlayerPrefs load is disabled. Use LOAD ACTIVE SET; " +
            $"the only set source is {AagManualAnchorSetStore.ManifestPath}.");
    }

    public List<Guid> GetSavedAnchorUuidsReadOnly()
    {
        var result = new List<Guid>();
        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        for (var index = 0; index < count; index++)
        {
            if (Guid.TryParse(PlayerPrefs.GetString(
                    ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), string.Empty), out var uuid))
            {
                result.Add(uuid);
            }
        }
        return result;
    }

    public void DetachRuntimeAnchorsForFreshLoad(IEnumerable<Guid> requestedUuids)
    {
        var requested = requestedUuids == null
            ? new HashSet<Guid>()
            : new HashSet<Guid>(requestedUuids.Where(uuid => uuid != Guid.Empty));
        var detached = 0;

        foreach (var anchor in anchors.ToArray())
        {
            if (anchor == null)
            {
                anchors.Remove(anchor);
                continue;
            }

            if (!requested.Contains(SafeUuid(anchor))) continue;
            anchors.Remove(anchor);
            detached++;
        }

        if (lastCreatedAnchor == null || requested.Contains(SafeUuid(lastCreatedAnchor)))
            lastCreatedAnchor = anchors.LastOrDefault(anchor => anchor != null);

        Debug.Log(
            $"[AAG Anchor Fresh Load] SpatialAnchorManager detachedRuntime={detached} " +
            $"requested={requested.Count} persistedAnchorErased=false");
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

    public bool TryEraseRuntimeAnchor(Guid uuid, Action<bool> completed)
    {
        var anchor = anchors.FirstOrDefault(value => value != null && SafeUuid(value) == uuid);
        if (anchor == null) return false;
        EraseResolvedAnchor(uuid, anchor, completed);
        return true;
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

    public bool JunkEraseInProgress { get; private set; }

    /// <summary>
    /// Erases every anchor UUID this app has ever recorded (PlayerPrefs, legacy
    /// anchor_log.json, manifest backups/exports/old runtime files) except the
    /// UUIDs in <paramref name="keepUuids"/>. Stale test anchors compete with the
    /// final 36 in the Quest's limited anchor-discovery budget, so removing them
    /// is required for reliable cold-start localization.
    /// </summary>
    public async void EraseJunkAnchorsAsync(HashSet<Guid> keepUuids, Action<string> onStatus)
    {
        if (JunkEraseInProgress)
        {
            onStatus?.Invoke("ERASE JUNK already running");
            return;
        }
        if (keepUuids == null || keepUuids.Count == 0)
        {
            onStatus?.Invoke("ERASE JUNK blocked: keep list is empty");
            return;
        }

        JunkEraseInProgress = true;
        try
        {
            var candidates = CollectHistoricalUuids();
            var junk = candidates.Where(uuid => !keepUuids.Contains(uuid)).OrderBy(uuid => uuid).ToList();
            Debug.Log(
                $"[AAG Junk Erase] Begin candidates={candidates.Count} keep={keepUuids.Count} junk={junk.Count} utc={DateTime.UtcNow:O}");
            if (junk.Count == 0)
            {
                onStatus?.Invoke($"ERASE JUNK: nothing to erase (keep={keepUuids.Count})");
                return;
            }

            WriteJunkAuditFile(junk, keepUuids.Count);

            var erased = 0;
            var failedBatches = 0;
            const int batchSize = 32;
            for (var offset = 0; offset < junk.Count; offset += batchSize)
            {
                var batch = junk.Skip(offset).Take(batchSize).ToArray();
                onStatus?.Invoke($"ERASE JUNK {offset}/{junk.Count}...");
                var result = await OVRSpatialAnchor.EraseAnchorsAsync(
                    Enumerable.Empty<OVRSpatialAnchor>(), batch);
                if (result.Success)
                {
                    erased += batch.Length;
                    continue;
                }

                // A batch fails as a whole when it contains UUIDs that no longer
                // exist in storage. Retry one-by-one so valid junk still gets erased.
                Debug.LogWarning(
                    $"[AAG Junk Erase] Batch failed offset={offset} size={batch.Length} status={result.Status}; retrying individually");
                var batchRecovered = 0;
                foreach (var uuid in batch)
                {
                    var single = await OVRSpatialAnchor.EraseAnchorsAsync(
                        Enumerable.Empty<OVRSpatialAnchor>(), new[] { uuid });
                    if (single.Success) batchRecovered++;
                }
                erased += batchRecovered;
                if (batchRecovered < batch.Length) failedBatches++;
            }

            RewritePlayerPrefsToKeepList(keepUuids);
            Debug.Log(
                $"[AAG Junk Erase] Complete erasedRequested={erased}/{junk.Count} failedBatches={failedBatches} " +
                $"keep={keepUuids.Count} playerPrefsRewritten=true utc={DateTime.UtcNow:O}");
            onStatus?.Invoke(failedBatches == 0
                ? $"ERASE JUNK DONE {erased} erased, {keepUuids.Count} kept"
                : $"ERASE JUNK PARTIAL {erased}/{junk.Count} ({failedBatches} batches failed)");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG Junk Erase] Exception {exception.GetType().Name}: {exception.Message}");
            onStatus?.Invoke($"ERASE JUNK ERROR: {exception.Message}");
        }
        finally
        {
            JunkEraseInProgress = false;
        }
    }

    private static HashSet<Guid> CollectHistoricalUuids()
    {
        var uuids = new HashSet<Guid>();

        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        for (var index = 0; index < count; index++)
        {
            if (Guid.TryParse(PlayerPrefs.GetString(
                    ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), string.Empty), out var uuid) && uuid != Guid.Empty)
                uuids.Add(uuid);
        }
        Debug.Log($"[AAG Junk Erase] PlayerPrefs uuids={uuids.Count}");

        var textSources = new List<string>();
        var legacyLog = CurrentLogPath;
        if (File.Exists(legacyLog)) textSources.Add(legacyLog);
        foreach (var folder in new[]
        {
            AagManualAnchorSetStore.FolderPath,
            AagManualAnchorSetStore.BackupFolderPath,
            AagManualAnchorSetStore.ExportFolderPath,
        })
        {
            if (!Directory.Exists(folder)) continue;
            textSources.AddRange(Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly));
        }

        var guidPattern = new System.Text.RegularExpressions.Regex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        foreach (var path in textSources)
        {
            try
            {
                foreach (System.Text.RegularExpressions.Match match in guidPattern.Matches(File.ReadAllText(path)))
                {
                    if (Guid.TryParse(match.Value, out var uuid) && uuid != Guid.Empty)
                        uuids.Add(uuid);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[AAG Junk Erase] Skipped unreadable source path={path} reason={exception.Message}");
            }
        }

        Debug.Log($"[AAG Junk Erase] Historical uuids total={uuids.Count} sources={textSources.Count + 1}");
        return uuids;
    }

    private static void WriteJunkAuditFile(List<Guid> junk, int keepCount)
    {
        try
        {
            var folder = Path.Combine(AagManualAnchorSetStore.FolderPath, "CleanupLogs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"erased_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
            File.WriteAllLines(path, junk.Select(uuid => uuid.ToString()));
            Debug.Log($"[AAG Junk Erase] Audit file written path={path} junk={junk.Count} keep={keepCount}");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[AAG Junk Erase] Audit file write failed: {exception.Message}");
        }
    }

    private static void RewritePlayerPrefsToKeepList(HashSet<Guid> keepUuids)
    {
        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        var remaining = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var value = PlayerPrefs.GetString(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), string.Empty);
            if (Guid.TryParse(value, out var uuid) && keepUuids.Contains(uuid)) remaining.Add(value);
            PlayerPrefs.DeleteKey(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index));
        }
        for (var index = 0; index < remaining.Count; index++)
            PlayerPrefs.SetString(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), remaining[index]);
        PlayerPrefs.SetInt(NumUuidsPlayerPref, remaining.Count);
        PlayerPrefs.Save();
        Debug.Log($"[AAG Junk Erase] PlayerPrefs rewritten remaining={remaining.Count}");
    }

    private static void RemoveUuidFromPlayerPrefs(Guid uuid)
    {
        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        var remaining = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var value = PlayerPrefs.GetString(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), string.Empty);
            if (!string.Equals(value, uuid.ToString(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
                remaining.Add(value);
            PlayerPrefs.DeleteKey(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index));
        }
        for (var index = 0; index < remaining.Count; index++)
            PlayerPrefs.SetString(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), remaining[index]);
        PlayerPrefs.SetInt(NumUuidsPlayerPref, remaining.Count);
        PlayerPrefs.Save();
    }

    private static void ReplaceUuidInPlayerPrefs(Guid replacedUuid, Guid newUuid)
    {
        var count = Mathf.Max(0, PlayerPrefs.GetInt(NumUuidsPlayerPref, 0));
        var rewritten = new List<string>();
        var newUuidWritten = false;
        for (var index = 0; index < count; index++)
        {
            var key = ExperimentSpaceRuntime.PlayerPrefsUuidKey(index);
            var value = PlayerPrefs.GetString(key, string.Empty);
            if (Guid.TryParse(value, out var parsed) && (parsed == replacedUuid || parsed == newUuid))
            {
                if (!newUuidWritten)
                {
                    rewritten.Add(newUuid.ToString());
                    newUuidWritten = true;
                }
            }
            else if (!string.IsNullOrEmpty(value))
            {
                rewritten.Add(value);
            }
            PlayerPrefs.DeleteKey(key);
        }

        if (!newUuidWritten) rewritten.Add(newUuid.ToString());
        for (var index = 0; index < rewritten.Count; index++)
            PlayerPrefs.SetString(ExperimentSpaceRuntime.PlayerPrefsUuidKey(index), rewritten[index]);
        PlayerPrefs.SetInt(NumUuidsPlayerPref, rewritten.Count);
        PlayerPrefs.Save();
        Debug.Log($"[AAG Repair] PlayerPrefs UUID replaced oldUuid={replacedUuid} newUuid={newUuid} total={rewritten.Count}");
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
