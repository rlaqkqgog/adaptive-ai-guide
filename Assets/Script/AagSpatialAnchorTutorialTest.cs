using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

/// <summary>
/// Isolated three-anchor diagnostic that intentionally mirrors Meta's basic
/// create -> localize -> save -> UUID storage -> load -> localize -> bind flow.
/// It never reads or writes the manual S1/S2/S3 manifest or its PlayerPrefs.
/// </summary>
[DisallowMultipleComponent]
public sealed class AagSpatialAnchorTutorialTest : MonoBehaviour
{
    private const int MaximumAnchors = 3;
    private const string StorageFolderName = "AagSpatialAnchorTutorialTest";
    private const string StorageFileName = "test3_anchor_uuids.json";

    [Serializable]
    private sealed class TestAnchorEntry
    {
        public int slot;
        public string uuid;
        public string saved_at_utc;
    }

    [Serializable]
    private sealed class TestAnchorStore
    {
        public List<TestAnchorEntry> anchors = new List<TestAnchorEntry>();
    }

    private readonly List<GameObject> runtimeObjects = new List<GameObject>();
    private TestAnchorStore store;
    private OVRSpatialAnchor anchorPrefab;
    private OVRCameraRig cameraRig;
    private bool operationInProgress;
    private string statusMessage = "READY";

    public event Action<string> StatusChanged;

    public int StoredCount => store?.anchors?.Count ?? 0;
    public IEnumerable<Guid> StoredUuids => (store?.anchors ?? new List<TestAnchorEntry>())
        .Select(entry => Guid.TryParse(entry.uuid, out var uuid) ? uuid : Guid.Empty)
        .Where(uuid => uuid != Guid.Empty);
    public bool OperationInProgress => operationInProgress;
    public string StatusMessage => statusMessage;
    public string StoragePath => Path.Combine(Application.persistentDataPath, StorageFolderName, StorageFileName);

    private void Awake()
    {
        store = LoadStore();
        cameraRig = FindFirstObjectByType<OVRCameraRig>();
        Debug.Log($"[AAG TEST-3] Ready stored={StoredCount}/{MaximumAnchors} path={StoragePath}; "
            + "isolatedFromManualManifest=true isolatedFromPlayerPrefs=true automaticLoad=false");
    }

    public void Configure(OVRSpatialAnchor prefab)
    {
        anchorPrefab = prefab;
        SetStatus($"READY {StoredCount}/{MaximumAnchors}");
    }

    public async void CreateAndSaveNextAnchor()
    {
        if (!BeginOperation("SAVE")) return;
        OVRSpatialAnchor createdAnchor = null;

        try
        {
            if (anchorPrefab == null)
            {
                FailOperation("SAVE", "anchor prefab missing");
                return;
            }
            if (StoredCount >= MaximumAnchors)
            {
                FailOperation("SAVE", $"test list already contains {MaximumAnchors}/{MaximumAnchors}; press RESET TEST LIST first");
                return;
            }
            if (!TryGetRightControllerWorldPose(out var position, out var rotation, out var poseFailure))
            {
                FailOperation("SAVE", poseFailure);
                return;
            }

            var slot = StoredCount + 1;
            SetStatus($"SAVING TEST {slot}/{MaximumAnchors}...");
            Debug.Log($"[AAG TEST-3] Save start slot={slot} world=({position.x:F3},{position.y:F3},{position.z:F3})");

            createdAnchor = Instantiate(anchorPrefab, position, rotation);
            createdAnchor.gameObject.name = $"AAG TEST-3 Slot {slot} (Creating)";
            runtimeObjects.Add(createdAnchor.gameObject);

            var localized = await createdAnchor.WhenLocalizedAsync();
            if (createdAnchor == null)
            {
                FailOperation("SAVE", $"slot={slot} anchor object was destroyed while creating");
                return;
            }
            if (!localized)
            {
                Debug.LogError($"[AAG TEST-3] Failed slot={slot} uuid=unavailable reason=create/localize failed");
                RemoveRuntimeObject(createdAnchor.gameObject);
                FailOperation("SAVE", $"TEST {slot} create/localize failed");
                return;
            }

            var uuid = createdAnchor.Uuid;
            Debug.Log($"[AAG TEST-3] Created slot={slot} uuid={uuid} localized=true");
            var saveResult = await OVRSpatialAnchor.SaveAnchorsAsync(new[] { createdAnchor });
            if (createdAnchor == null)
            {
                FailOperation("SAVE", $"slot={slot} anchor object was destroyed while saving");
                return;
            }
            if (!saveResult.Success)
            {
                var reason = saveResult.Status == OVRAnchor.SaveResult.FailureInsufficientView
                    ? "insufficient view / scan the surrounding space before saving"
                    : saveResult.Status.ToString();
                Debug.LogError($"[AAG TEST-3] Failed slot={slot} uuid={uuid} status={saveResult.Status} reason={reason}");
                SetMarkerLabel(createdAnchor, slot, uuid, $"SAVE FAILED: {saveResult.Status}");
                FailOperation("SAVE", $"TEST {slot} FAILED: {saveResult.Status}");
                return;
            }

            var entry = new TestAnchorEntry
            {
                slot = slot,
                uuid = uuid.ToString(),
                saved_at_utc = DateTime.UtcNow.ToString("O"),
            };
            store.anchors.Add(entry);
            if (!TryWriteStore(out var writeFailure))
            {
                store.anchors.Remove(entry);
                Debug.LogError($"[AAG TEST-3] Failed slot={slot} uuid={uuid} status={saveResult.Status} "
                    + $"reason=test UUID file write failed: {writeFailure}");
                SetMarkerLabel(createdAnchor, slot, uuid, "UUID FILE WRITE FAILED");
                FailOperation("SAVE", $"TEST {slot} UUID FILE WRITE FAILED");
                return;
            }

            createdAnchor.gameObject.name = $"AAG TEST-3 Slot {slot} {uuid}";
            SetMarkerLabel(createdAnchor, slot, uuid, "SAVED");
            Debug.Log($"[AAG TEST-3] Saved slot={slot} uuid={uuid} status={saveResult.Status} "
                + $"stored={StoredCount}/{MaximumAnchors} path={StoragePath}");
            SetStatus($"SAVED TEST {slot}  {StoredCount}/{MaximumAnchors}");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG TEST-3] Failed operation=SAVE reason={exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            FailOperation("SAVE", exception.Message);
        }
        finally
        {
            operationInProgress = false;
        }
    }

    public async void LoadStoredAnchors()
    {
        if (!BeginOperation("LOAD")) return;

        try
        {
            if (anchorPrefab == null)
            {
                FailOperation("LOAD", "anchor prefab missing");
                return;
            }
            if (StoredCount == 0)
            {
                FailOperation("LOAD", "test UUID list is empty; save up to three test anchors first");
                return;
            }

            var entriesByUuid = new Dictionary<Guid, TestAnchorEntry>();
            foreach (var entry in store.anchors.OrderBy(value => value.slot))
            {
                if (!Guid.TryParse(entry.uuid, out var uuid) || uuid == Guid.Empty)
                {
                    Debug.LogError($"[AAG TEST-3] Failed slot={entry.slot} uuid={entry.uuid} reason=invalid UUID in test file");
                    continue;
                }
                entriesByUuid[uuid] = entry;
            }
            if (entriesByUuid.Count == 0)
            {
                FailOperation("LOAD", "test UUID file contains no valid UUIDs");
                return;
            }

            DestroyRuntimeObjectsOnly();
            await Task.Yield();

            var requested = entriesByUuid.Keys.ToList();
            var returnedAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            SetStatus($"LOADING TEST-3 0/{requested.Count}...");
            Debug.Log($"[AAG TEST-3] Load start requested={requested.Count} uuids=[{string.Join(",", requested)}] "
                + "source=test3_anchor_uuids.json");

            var loadResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(requested, returnedAnchors);
            Debug.Log($"[AAG TEST-3] Load query complete status={loadResult.Status} success={loadResult.Success} "
                + $"requested={requested.Count} returned={returnedAnchors.Count}");
            foreach (var returnedAnchor in returnedAnchors)
                Debug.Log($"[AAG TEST-3] Returned raw uuid={returnedAnchor.Uuid} localized={returnedAnchor.Localized}");

            var failures = requested.ToDictionary(uuid => uuid, _ => "not returned");
            var loaded = 0;
            foreach (var unboundAnchor in returnedAnchors)
            {
                var uuid = unboundAnchor.Uuid;
                if (!entriesByUuid.TryGetValue(uuid, out var entry))
                {
                    Debug.LogWarning($"[AAG TEST-3] Returned unexpected uuid={uuid}; ignored");
                    continue;
                }

                if (!unboundAnchor.Localized && !await unboundAnchor.LocalizeAsync())
                {
                    failures[uuid] = "localize failed";
                    continue;
                }
                if (!unboundAnchor.TryGetPose(out var pose))
                {
                    failures[uuid] = "pose unavailable";
                    continue;
                }

                var spatialAnchor = Instantiate(anchorPrefab, pose.position, pose.rotation);
                spatialAnchor.gameObject.name = $"AAG TEST-3 Slot {entry.slot} {uuid} (Loaded)";
                unboundAnchor.BindTo(spatialAnchor);
                runtimeObjects.Add(spatialAnchor.gameObject);
                SetMarkerLabel(spatialAnchor, entry.slot, uuid, "LOADED");
                failures.Remove(uuid);
                loaded++;
                Debug.Log($"[AAG TEST-3] Loaded slot={entry.slot} uuid={uuid} "
                    + $"world=({pose.position.x:F3},{pose.position.y:F3},{pose.position.z:F3})");
            }

            foreach (var uuid in requested)
            {
                if (!failures.TryGetValue(uuid, out var reason)) continue;
                var entry = entriesByUuid[uuid];
                Debug.LogError($"[AAG TEST-3] Failed slot={entry.slot} uuid={uuid} reason={reason}");
            }

            Debug.Log($"[AAG TEST-3] Load complete loaded={loaded}/{requested.Count} failed={requested.Count - loaded} "
                + $"queryStatus={loadResult.Status}");
            SetStatus($"LOADED {loaded}/{requested.Count}" + (loaded == requested.Count ? " PASS" : " FAILED"));
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG TEST-3] Failed operation=LOAD reason={exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            FailOperation("LOAD", exception.Message);
        }
        finally
        {
            operationInProgress = false;
        }
    }

    public void ResetTestList()
    {
        if (operationInProgress)
        {
            SetStatus("RESET BLOCKED: TEST OPERATION RUNNING");
            return;
        }

        DestroyRuntimeObjectsOnly();
        store = new TestAnchorStore();
        try
        {
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
            Debug.Log($"[AAG TEST-3] Test list reset path={StoragePath}; rawQuestAnchorsErased=false; "
                + "manualManifestChanged=false playerPrefsChanged=false");
            SetStatus("TEST LIST RESET 0/3");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG TEST-3] Reset failed path={StoragePath} reason={exception.Message}");
            SetStatus($"RESET FAILED: {exception.Message}");
        }
    }

    public void HideRuntimeAnchors()
    {
        if (operationInProgress) return;
        DestroyRuntimeObjectsOnly();
    }

    private bool BeginOperation(string operation)
    {
        if (operationInProgress)
        {
            SetStatus($"{operation} BLOCKED: TEST OPERATION RUNNING");
            return false;
        }
        operationInProgress = true;
        return true;
    }

    private void FailOperation(string operation, string reason)
    {
        Debug.LogError($"[AAG TEST-3] {operation} failed reason={reason}");
        SetStatus($"{operation} FAILED: {reason}");
    }

    private bool TryGetRightControllerWorldPose(out Vector3 position, out Quaternion rotation, out string failure)
    {
        position = default;
        rotation = default;
        failure = string.Empty;
        if (!OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch)
            || !OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch))
        {
            failure = "right controller tracking unavailable";
            return false;
        }

        cameraRig ??= FindFirstObjectByType<OVRCameraRig>();
        var trackingSpace = cameraRig != null ? cameraRig.trackingSpace : null;
        if (trackingSpace == null)
        {
            failure = "OVRCameraRig TrackingSpace missing";
            return false;
        }

        var localPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        var localRotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        position = trackingSpace.TransformPoint(localPosition);
        rotation = trackingSpace.rotation * localRotation;
        return true;
    }

    private TestAnchorStore LoadStore()
    {
        try
        {
            if (!File.Exists(StoragePath)) return new TestAnchorStore();
            var loaded = JsonUtility.FromJson<TestAnchorStore>(File.ReadAllText(StoragePath)) ?? new TestAnchorStore();
            loaded.anchors ??= new List<TestAnchorEntry>();
            loaded.anchors = loaded.anchors
                .Where(entry => entry != null && Guid.TryParse(entry.uuid, out var uuid) && uuid != Guid.Empty)
                .GroupBy(entry => entry.uuid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.slot)
                .Take(MaximumAnchors)
                .ToList();
            return loaded;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AAG TEST-3] Test UUID file read failed path={StoragePath} reason={exception.Message}");
            return new TestAnchorStore();
        }
    }

    private bool TryWriteStore(out string failure)
    {
        try
        {
            var folder = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var temporaryPath = StoragePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(store, true));
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
            File.Move(temporaryPath, StoragePath);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    private void DestroyRuntimeObjectsOnly()
    {
        foreach (var runtimeObject in runtimeObjects)
            if (runtimeObject != null) Destroy(runtimeObject);
        runtimeObjects.Clear();
    }

    private void RemoveRuntimeObject(GameObject runtimeObject)
    {
        runtimeObjects.Remove(runtimeObject);
        if (runtimeObject != null) Destroy(runtimeObject);
    }

    private static void SetMarkerLabel(OVRSpatialAnchor anchor, int slot, Guid uuid, string state)
    {
        if (anchor == null) return;
        var textComponents = anchor.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (textComponents.Length > 0) textComponents[0].text = $"TEST-{slot}\n{uuid.ToString().Substring(0, 8)}";
        if (textComponents.Length > 1) textComponents[1].text = state;
    }

    private void SetStatus(string message)
    {
        statusMessage = message;
        StatusChanged?.Invoke(message);
    }
}
