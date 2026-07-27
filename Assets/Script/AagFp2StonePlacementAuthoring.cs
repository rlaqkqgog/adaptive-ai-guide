using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Fast FP2 field authoring: after AprilTag alignment, place twelve stones at
/// the tracked right-controller pose. The completed set is persisted directly
/// in MRUK floor-local coordinates; no OVR spatial-anchor round trip is needed.
/// </summary>
[DisallowMultipleComponent]
public sealed class AagFp2StonePlacementAuthoring : MonoBehaviour
{
    private readonly struct Slot
    {
        public Slot(string id, string color)
        {
            Id = id;
            Color = color;
        }

        public string Id { get; }
        public string Color { get; }
    }

    private static readonly Slot[] Slots =
    {
        new Slot("red_1", "Red"),
        new Slot("red_2", "Red"),
        new Slot("red_3", "Red"),
        new Slot("blue_1", "Blue"),
        new Slot("blue_2", "Blue"),
        new Slot("blue_3", "Blue"),
        new Slot("green_1", "Green"),
        new Slot("green_2", "Green"),
        new Slot("green_3", "Green"),
        new Slot("yellow_1", "Yellow"),
        new Slot("yellow_2", "Yellow"),
        new Slot("yellow_3", "Yellow"),
    };

    private readonly List<MrukRoomLocalPlacementStore.PoseInput> poses =
        new List<MrukRoomLocalPlacementStore.PoseInput>();
    private readonly List<GameObject> previews = new List<GameObject>();
    private readonly List<AagFixedTowerRoomLocalStore.PoseInput> towerPoses =
        new List<AagFixedTowerRoomLocalStore.PoseInput>();
    private readonly List<GameObject> towerPreviews = new List<GameObject>();
    private readonly List<AagIncidentalRoomLocalStore.PoseInput> incidentalPoses =
        new List<AagIncidentalRoomLocalStore.PoseInput>();
    private readonly List<string> incidentalResourcePaths = new List<string>();
    private readonly List<GameObject> incidentalPreviews = new List<GameObject>();

    private ExperimentMain experimentMain;
    private SpatialAnchorManager prefabSource;
    private TextMeshProUGUI hudText;
    private string activeSetId = string.Empty;
    private string message = "ALIGN TAG FIRST";
    private float nextInputTime;
    private bool placingTowers;
    private bool placingIncidentals;

    private void Awake()
    {
        experimentMain = GetComponent<ExperimentMain>()
            ?? FindFirstObjectByType<ExperimentMain>();
        prefabSource = FindFirstObjectByType<SpatialAnchorManager>(FindObjectsInactive.Include);
    }

    private void Start()
    {
        if (IsAuthoringComplete())
        {
            enabled = false;
            Debug.Log(
                "[AAG FP2 Field Placement] complete seeded catalog detected; authoring HUD and input disabled");
            return;
        }

        CreateHud();
        RefreshHud();
    }

    private void Update()
    {
        if (!AagMrukSpaceCorrection.IsApplied)
        {
            message = "ALIGN TAG FIRST";
            RefreshHud();
            return;
        }

        var selectedSet = experimentMain?.SelectedSetIdReadOnly ?? string.Empty;
        if (!string.Equals(selectedSet, activeSetId, StringComparison.Ordinal))
        {
            if (HasWorkInProgress())
            {
                message = $"FINISH OR UNDO {activeSetId} ({poses.Count}/12)";
            }
            else
            {
                ClearWorkingSet();
                ClearIncidentalWorkingSet();
                activeSetId = selectedSet;
                message = HasSavedSet(activeSetId)
                    ? $"{activeSetId} SAVED · CHANGE SET OR REPLACE"
                    : $"{activeSetId} READY";
                ConfigurePlacementPhase();
            }
            RefreshHud();
        }

        if (Time.unscaledTime < nextInputTime) return;
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            if (placingIncidentals) PlaceNextIncidental();
            else if (placingTowers) PlaceNextTower();
            else if (!HasSavedSet(activeSetId)) PlaceNext();
            else
            {
                message = $"{activeSetId} COMPLETE";
                RefreshHud();
            }
            nextInputTime = Time.unscaledTime + 0.2f;
        }
        else if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch))
        {
            if (placingIncidentals) UndoLastIncidental();
            else if (placingTowers) UndoLastTower();
            else UndoLast();
            nextInputTime = Time.unscaledTime + 0.2f;
        }
    }

    private void PlaceNext()
    {
        if (string.IsNullOrWhiteSpace(activeSetId)
            || !activeSetId.StartsWith("FP2-S", StringComparison.Ordinal))
        {
            message = "SELECT FP2 SET WITH Y";
            RefreshHud();
            return;
        }
        if (poses.Count >= Slots.Length)
        {
            message = $"{activeSetId} ALREADY 12/12";
            RefreshHud();
            return;
        }
        if (!OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch)
            || !OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch))
        {
            message = "RIGHT CONTROLLER NOT TRACKED";
            RefreshHud();
            return;
        }

        var rig = FindFirstObjectByType<OVRCameraRig>();
        var trackingSpace = rig != null ? rig.trackingSpace : null;
        if (trackingSpace == null)
        {
            message = "TRACKING SPACE MISSING";
            RefreshHud();
            return;
        }

        var position = trackingSpace.TransformPoint(
            OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));
        var controllerRotation = trackingSpace.rotation
            * OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        var forward = Vector3.ProjectOnPlane(controllerRotation * Vector3.forward, Vector3.up);
        var rotation = forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward.normalized, Vector3.up)
            : Quaternion.identity;
        var slot = Slots[poses.Count];

        poses.Add(new MrukRoomLocalPlacementStore.PoseInput(
            slot.Id, slot.Color, position, rotation));
        previews.Add(CreatePreview(slot, position, rotation));
        message = $"PLACED {slot.Id} · {poses.Count}/12";
        RefreshHud();

        if (poses.Count == Slots.Length) SaveCompletedSet();
    }

    private GameObject CreatePreview(Slot slot, Vector3 position, Quaternion rotation)
    {
        var prefab = prefabSource != null
            ? prefabSource.GetAnchorPrefabForColor(slot.Color)
            : null;
        if (prefab != null)
        {
            var source = prefab.gameObject;
            var wasActive = source.activeSelf;
            try
            {
                source.SetActive(false);
                var instance = Instantiate(source, position, rotation);
                instance.name = $"FP2_FIELD_{slot.Id}";
                foreach (var anchor in instance.GetComponentsInChildren<OVRSpatialAnchor>(true))
                    DestroyImmediate(anchor);
                foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true))
                    canvas.gameObject.SetActive(false);
                instance.SetActive(true);
                return instance;
            }
            finally
            {
                source.SetActive(wasActive);
            }
        }

        var fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        fallback.name = $"FP2_FIELD_{slot.Id}_FALLBACK";
        fallback.transform.SetPositionAndRotation(position, rotation);
        fallback.transform.localScale = Vector3.one * 0.12f;
        var renderer = fallback.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = ParseColor(slot.Color);
        return fallback;
    }

    private void UndoLast()
    {
        if (poses.Count == 0)
        {
            message = "NOTHING TO UNDO";
            RefreshHud();
            return;
        }

        var removed = poses[poses.Count - 1].ObjectId;
        poses.RemoveAt(poses.Count - 1);
        var preview = previews[previews.Count - 1];
        previews.RemoveAt(previews.Count - 1);
        if (preview != null) Destroy(preview);
        message = $"UNDID {removed} · {poses.Count}/12";
        RefreshHud();
    }

    private void SaveCompletedSet()
    {
        if (!MrukRoomLocalPlacementStore.TryCaptureAndSave(
                activeSetId, poses, out var placementDetail, out var placementFailure))
        {
            message = $"SAVE FAILED: {placementFailure}";
            RefreshHud();
            Debug.LogError($"[AAG FP2 Field Placement] {message}");
            return;
        }

        var manifest = AagManualAnchorSetStore.LoadOrCreate();
        var set = manifest.sets.First(value =>
            string.Equals(value.set_id, activeSetId, StringComparison.Ordinal));
        set.anchors.Clear();
        var now = DateTime.UtcNow.ToString("O");
        foreach (var pose in poses)
        {
            set.anchors.Add(new AagManualAnchorEntry
            {
                floor_plan_id = AagExperimentSpaceCatalog.Fp2Id,
                set_id = activeSetId,
                marker_id = pose.ObjectId,
                color = pose.Color,
                anchor_uuid = Guid.NewGuid().ToString(),
                world_x = pose.Position.x,
                world_y = pose.Position.y,
                world_z = pose.Position.z,
                rotation_x = pose.Rotation.x,
                rotation_y = pose.Rotation.y,
                rotation_z = pose.Rotation.z,
                rotation_w = pose.Rotation.w,
                saved_at_utc = now,
                cap_x = pose.Position.x,
                cap_y = pose.Position.y,
                cap_z = pose.Position.z,
                cap_rot_x = pose.Rotation.x,
                cap_rot_y = pose.Rotation.y,
                cap_rot_z = pose.Rotation.z,
                cap_rot_w = pose.Rotation.w,
            });
        }
        set.locked = true;
        set.locked_at_utc = now;
        set.offsets_captured_utc = now;
        if (!AagManualAnchorSetStore.Save(manifest, out var manifestFailure))
        {
            message = $"MANIFEST FAILED: {manifestFailure}";
            RefreshHud();
            Debug.LogError($"[AAG FP2 Field Placement] {message}");
            return;
        }

        if (!AagFixedTowerRoomLocalStore.HasCompleteCatalog)
        {
            placingTowers = true;
            message = "STONES SAVED · PLACE TOWER-1 RED";
        }
        else
        {
            message = $"SAVED {activeSetId} 12/12 · USE Y FOR NEXT SET";
        }
        if (!placingTowers) BeginIncidentalPlacement();
        RefreshHud();
        Debug.Log(
            $"[AAG FP2 Field Placement] saved {activeSetId}; {placementDetail}; "
            + $"manifest={AagManualAnchorSetStore.ManifestPath}");
    }

    private void PlaceNextTower()
    {
        if (towerPoses.Count >= AagFixedTowerAnchorStore.RequiredTowerCount)
        {
            message = "TOWERS ALREADY 4/4";
            RefreshHud();
            return;
        }
        if (!TryGetRightControllerWorldPose(out var position, out var rotation))
        {
            message = "RIGHT CONTROLLER NOT TRACKED";
            RefreshHud();
            return;
        }

        var index = towerPoses.Count;
        var towerId = AagFixedTowerAnchorStore.TowerIds[index];
        var color = AagFixedTowerAnchorStore.TowerColors[index];
        towerPoses.Add(new AagFixedTowerRoomLocalStore.PoseInput(
            towerId, position, rotation));
        towerPreviews.Add(CreateTowerPreview(towerId, color, position, rotation));
        message = $"PLACED {towerId} {color.ToUpperInvariant()} · {towerPoses.Count}/4";
        RefreshHud();

        if (towerPoses.Count == AagFixedTowerAnchorStore.RequiredTowerCount)
            SaveCompletedTowers();
    }

    private bool TryGetRightControllerWorldPose(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        if (!OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch)
            || !OVRInput.GetControllerOrientationTracked(OVRInput.Controller.RTouch))
            return false;
        var rig = FindFirstObjectByType<OVRCameraRig>();
        var trackingSpace = rig != null ? rig.trackingSpace : null;
        if (trackingSpace == null) return false;
        position = trackingSpace.TransformPoint(
            OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch));
        var controllerRotation = trackingSpace.rotation
            * OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        var forward = Vector3.ProjectOnPlane(controllerRotation * Vector3.forward, Vector3.up);
        rotation = forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward.normalized, Vector3.up)
            : Quaternion.identity;
        return true;
    }

    private GameObject CreateTowerPreview(
        string towerId,
        string color,
        Vector3 position,
        Quaternion rotation)
    {
        var prefab = Resources.Load<GameObject>("Prefabs/StonepagodaTower");
        GameObject instance;
        if (prefab != null)
        {
            instance = Instantiate(prefab, position, rotation);
            instance.transform.localScale = Vector3.one * 0.1f;
        }
        else
        {
            instance = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            instance.transform.SetPositionAndRotation(position, rotation);
            instance.transform.localScale = new Vector3(0.22f, 0.6f, 0.22f);
        }
        instance.name = $"FP2_FIELD_{towerId}";

        var colorMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        colorMarker.name = $"{towerId}_{color}_Marker";
        colorMarker.transform.SetParent(instance.transform, false);
        colorMarker.transform.localPosition = new Vector3(0f, 1.2f, 0f);
        colorMarker.transform.localScale = Vector3.one * 0.25f;
        var renderer = colorMarker.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = ParseColor(color);
        return instance;
    }

    private void UndoLastTower()
    {
        if (towerPoses.Count == 0)
        {
            message = "NO TOWER TO UNDO";
            RefreshHud();
            return;
        }
        var removed = towerPoses[towerPoses.Count - 1].TowerId;
        towerPoses.RemoveAt(towerPoses.Count - 1);
        var preview = towerPreviews[towerPreviews.Count - 1];
        towerPreviews.RemoveAt(towerPreviews.Count - 1);
        if (preview != null) Destroy(preview);
        message = $"UNDID {removed} · {towerPoses.Count}/4";
        RefreshHud();
    }

    private void SaveCompletedTowers()
    {
        if (!AagFixedTowerRoomLocalStore.TryCaptureAndSave(
                towerPoses, out var localDetail, out var localFailure))
        {
            message = $"TOWER SAVE FAILED: {localFailure}";
            RefreshHud();
            return;
        }

        var manifest = AagFixedTowerAnchorStore.LoadOrCreate();
        manifest.towers.Clear();
        foreach (var pose in towerPoses)
        {
            manifest.towers.Add(new AagFixedTowerAnchorEntry
            {
                tower_id = pose.TowerId,
                anchor_uuid = Guid.NewGuid().ToString(),
                has_fallback_pose = true,
                fallback_x = pose.Position.x,
                fallback_y = pose.Position.y,
                fallback_z = pose.Position.z,
                fallback_rotation_x = pose.Rotation.x,
                fallback_rotation_y = pose.Rotation.y,
                fallback_rotation_z = pose.Rotation.z,
                fallback_rotation_w = pose.Rotation.w,
            });
        }
        if (!AagFixedTowerAnchorStore.Save(manifest, out var manifestFailure))
        {
            message = $"TOWER MANIFEST FAILED: {manifestFailure}";
            RefreshHud();
            return;
        }

        placingTowers = false;
        message = $"TOWERS SAVED 4/4 · {activeSetId} COMPLETE · USE Y";
        BeginIncidentalPlacement();
        RefreshHud();
        Debug.Log(
            $"[AAG FP2 Field Placement] tower save complete; {localDetail}; "
            + $"manifest={AagFixedTowerAnchorStore.ManifestPath}");
    }

    private void ConfigurePlacementPhase()
    {
        placingTowers = false;
        placingIncidentals = false;
        if (string.IsNullOrWhiteSpace(activeSetId)
            || !activeSetId.StartsWith("FP2-S", StringComparison.Ordinal))
        {
            message = "SELECT FP2 SET WITH Y";
            return;
        }
        if (!HasSavedSet(activeSetId))
        {
            message = $"{activeSetId} STONES READY";
            return;
        }
        if (!AagFixedTowerRoomLocalStore.HasCompleteCatalog)
        {
            placingTowers = true;
            message = "PLACE TOWER-1 RED";
            return;
        }
        if (!HasSavedIncidentalSet(activeSetId))
        {
            BeginIncidentalPlacement();
            return;
        }
        message = $"{activeSetId} COMPLETE";
    }

    private void BeginIncidentalPlacement()
    {
        placingTowers = false;
        placingIncidentals = !HasSavedIncidentalSet(activeSetId);
        message = placingIncidentals
            ? $"{activeSetId} PLACE INCIDENTAL 1/{AagIncidentalAnchorStore.RequiredObjectsPerSet}"
            : $"{activeSetId} COMPLETE";
    }

    private void PlaceNextIncidental()
    {
        var catalog = AagIncidentalAnchorStore.LoadPrefabCatalog(activeSetId);
        if (catalog.Length != AagIncidentalAnchorStore.RequiredObjectsPerSet)
        {
            message = $"INCIDENTAL PREFABS {catalog.Length}/{AagIncidentalAnchorStore.RequiredObjectsPerSet}";
            RefreshHud();
            return;
        }
        if (incidentalPoses.Count >= catalog.Length)
        {
            message = $"INCIDENTALS ALREADY {AagIncidentalAnchorStore.RequiredObjectsPerSet}/{AagIncidentalAnchorStore.RequiredObjectsPerSet}";
            RefreshHud();
            return;
        }
        if (!TryGetRightControllerWorldPose(out var position, out var rotation))
        {
            message = "RIGHT CONTROLLER NOT TRACKED";
            RefreshHud();
            return;
        }

        var index = incidentalPoses.Count;
        var prefab = catalog[index];
        var objectId = $"incidental_{index + 1:00}_{prefab.name}";
        var resourcePath = $"{AagIncidentalAnchorStore.ResourceFolderForSet(activeSetId)}/{prefab.name}";
        incidentalPoses.Add(new AagIncidentalRoomLocalStore.PoseInput(
            objectId, position, rotation));
        incidentalResourcePaths.Add(resourcePath);
        incidentalPreviews.Add(CreateIncidentalPreview(prefab, objectId, position, rotation));
        message = $"PLACED INCIDENTAL {index + 1}/{AagIncidentalAnchorStore.RequiredObjectsPerSet}";
        RefreshHud();

        if (incidentalPoses.Count == AagIncidentalAnchorStore.RequiredObjectsPerSet)
            SaveCompletedIncidentals();
    }

    private static GameObject CreateIncidentalPreview(
        GameObject prefab,
        string objectId,
        Vector3 position,
        Quaternion rotation)
    {
        var instance = Instantiate(prefab, position, rotation);
        instance.name = $"FP2_FIELD_{objectId}";
        foreach (var anchor in instance.GetComponentsInChildren<OVRSpatialAnchor>(true))
            DestroyImmediate(anchor);
        foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true))
            canvas.gameObject.SetActive(false);
        return instance;
    }

    private void UndoLastIncidental()
    {
        if (incidentalPoses.Count == 0)
        {
            message = "NO INCIDENTAL TO UNDO";
            RefreshHud();
            return;
        }
        incidentalPoses.RemoveAt(incidentalPoses.Count - 1);
        incidentalResourcePaths.RemoveAt(incidentalResourcePaths.Count - 1);
        var preview = incidentalPreviews[incidentalPreviews.Count - 1];
        incidentalPreviews.RemoveAt(incidentalPreviews.Count - 1);
        if (preview != null) Destroy(preview);
        message = $"UNDID INCIDENTAL {incidentalPoses.Count}/{AagIncidentalAnchorStore.RequiredObjectsPerSet}";
        RefreshHud();
    }

    private void SaveCompletedIncidentals()
    {
        if (!AagIncidentalRoomLocalStore.TryCaptureAndSave(
                activeSetId,
                incidentalPoses,
                out var localDetail,
                out var localFailure))
        {
            message = $"INCIDENTAL SAVE FAILED: {localFailure}";
            RefreshHud();
            return;
        }

        var manifest = AagIncidentalAnchorStore.LoadOrCreate();
        var set = AagIncidentalAnchorStore.GetSet(manifest, activeSetId);
        if (set == null)
        {
            message = $"INCIDENTAL SET MISSING: {activeSetId}";
            RefreshHud();
            return;
        }
        set.objects.Clear();
        var now = DateTime.UtcNow.ToString("O");
        for (var index = 0; index < incidentalPoses.Count; index++)
        {
            var pose = incidentalPoses[index];
            set.objects.Add(new AagIncidentalAnchorEntry
            {
                set_id = activeSetId,
                object_id = pose.ObjectId,
                prefab_resource_path = incidentalResourcePaths[index],
                anchor_uuid = Guid.NewGuid().ToString(),
                fallback_x = pose.Position.x,
                fallback_y = pose.Position.y,
                fallback_z = pose.Position.z,
                fallback_rotation_x = pose.Rotation.x,
                fallback_rotation_y = pose.Rotation.y,
                fallback_rotation_z = pose.Rotation.z,
                fallback_rotation_w = pose.Rotation.w,
                saved_at_utc = now,
            });
        }
        if (!AagIncidentalAnchorStore.Save(manifest, out var manifestFailure))
        {
            message = $"INCIDENTAL MANIFEST FAILED: {manifestFailure}";
            RefreshHud();
            return;
        }

        placingIncidentals = false;
        message = $"INCIDENTALS SAVED {AagIncidentalAnchorStore.RequiredObjectsPerSet}/{AagIncidentalAnchorStore.RequiredObjectsPerSet} {activeSetId} COMPLETE";
        RefreshHud();
        Debug.Log(
            $"[AAG FP2 Field Placement] incidental save complete; {localDetail}; "
            + $"manifest={AagIncidentalAnchorStore.ManifestPath}");
    }

    private bool HasWorkInProgress() =>
        (poses.Count > 0 && poses.Count < Slots.Length)
        || (towerPoses.Count > 0
            && towerPoses.Count < AagFixedTowerAnchorStore.RequiredTowerCount)
        || (incidentalPoses.Count > 0
            && incidentalPoses.Count < AagIncidentalAnchorStore.RequiredObjectsPerSet);

    private static bool HasSavedSet(string setId) =>
        !string.IsNullOrEmpty(setId)
        && MrukRoomLocalPlacementStore.TryGetSet(setId, out _, out _);

    private static bool HasSavedIncidentalSet(string setId)
    {
        if (!AagIncidentalRoomLocalStore.HasCompleteSet(setId)) return false;
        var manifest = AagIncidentalAnchorStore.LoadOrCreate();
        var set = AagIncidentalAnchorStore.GetSet(manifest, setId);
        return set?.objects?.Count == AagIncidentalAnchorStore.RequiredObjectsPerSet;
    }

    private static bool IsAuthoringComplete() =>
        AagFixedTowerRoomLocalStore.HasCompleteCatalog
        && ExperimentSpaceRuntime.SetIds.All(setId =>
            HasSavedSet(setId) && HasSavedIncidentalSet(setId));

    private void ClearWorkingSet()
    {
        poses.Clear();
        foreach (var preview in previews)
            if (preview != null) Destroy(preview);
        previews.Clear();
    }

    private void ClearIncidentalWorkingSet()
    {
        incidentalPoses.Clear();
        incidentalResourcePaths.Clear();
        foreach (var preview in incidentalPreviews)
            if (preview != null) Destroy(preview);
        incidentalPreviews.Clear();
    }

    private void CreateHud()
    {
        var eye = GameObject.Find("CenterEyeAnchor")?.transform;
        if (eye == null) return;
        var canvasObject = new GameObject("FP2_FieldPlacement_HUD", typeof(Canvas));
        canvasObject.transform.SetParent(eye, false);
        canvasObject.transform.localPosition = new Vector3(0.22f, -0.22f, 0.65f);
        canvasObject.transform.localRotation = Quaternion.identity;
        canvasObject.transform.localScale = Vector3.one * 0.0012f;
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasObject.transform, false);
        var backgroundRect = background.GetComponent<RectTransform>();
        backgroundRect.sizeDelta = new Vector2(570f, 135f);
        background.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textObject.transform.SetParent(background.transform, false);
        var textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(14f, 8f);
        textRect.offsetMax = new Vector2(-14f, -8f);
        hudText = textObject.GetComponent<TextMeshProUGUI>();
        hudText.fontSize = 25f;
        hudText.alignment = TextAlignmentOptions.Center;
        hudText.color = Color.white;
    }

    private void RefreshHud()
    {
        if (hudText == null) return;
        var next = placingIncidentals
            ? NextIncidentalLabel()
            : placingTowers
                ? towerPoses.Count < AagFixedTowerAnchorStore.RequiredTowerCount
                    ? $"{AagFixedTowerAnchorStore.TowerIds[towerPoses.Count]} "
                        + AagFixedTowerAnchorStore.TowerColors[towerPoses.Count].ToUpperInvariant()
                    : "TOWERS COMPLETE"
                : poses.Count < Slots.Length ? Slots[poses.Count].Id : "STONES COMPLETE";
        hudText.text = $"FP2 FIELD AUTHORING · {activeSetId}\n"
            + $"{message}\nNEXT {next} · R TRIGGER PLACE · L TRIGGER UNDO";
    }

    private string NextIncidentalLabel()
    {
        var catalog = AagIncidentalAnchorStore.LoadPrefabCatalog(activeSetId);
        return incidentalPoses.Count < catalog.Length
            ? $"INCIDENTAL {incidentalPoses.Count + 1}/{AagIncidentalAnchorStore.RequiredObjectsPerSet} {catalog[incidentalPoses.Count].name}"
            : "INCIDENTALS COMPLETE";
    }

    private static Color ParseColor(string value)
    {
        if (string.Equals(value, "Red", StringComparison.OrdinalIgnoreCase)) return Color.red;
        if (string.Equals(value, "Blue", StringComparison.OrdinalIgnoreCase)) return Color.blue;
        if (string.Equals(value, "Green", StringComparison.OrdinalIgnoreCase)) return Color.green;
        return Color.yellow;
    }

    private void OnDestroy()
    {
        ClearWorkingSet();
        ClearIncidentalWorkingSet();
        foreach (var preview in towerPreviews)
            if (preview != null) Destroy(preview);
        towerPreviews.Clear();
        towerPoses.Clear();
    }
}
