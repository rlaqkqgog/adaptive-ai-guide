using System;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class AagManualAnchorSetAuthoring : MonoBehaviour
{
    public readonly struct SaveContext
    {
        public SaveContext(string setId, string color) { SetId = setId; Color = color; }
        public string SetId { get; }
        public string Color { get; }
    }
    private const int AnchorsPerColor = 3;
    private const int AnchorsPerSet = 12;
    private static readonly HashSet<string> ControllerOnlyDisabledRoots = new HashSet<string>(StringComparer.Ordinal)
    {
        "[BuildingBlock] OVRComprehensiveInteractionRig",
        "[BuildingBlock] Real Hands",
        "[BuildingBlock] HandGrabInstallationRoutine",
        "[BuildingBlock] Hand Tracking left",
        "[BuildingBlock] Hand Tracking right",
        "OVRHands",
    };

    private SpatialAnchorManager anchorManager;
    private AagManualAnchorSetManifest manifest;
    private string activeSetId = "FP1-S1";
    private string activeColor = "Red";
    private string lastSavedUuid = "NONE";
    private string operationMessage = "Ready: right trigger creates, A saves";
    private TextMeshProUGUI statusText;
    private bool showLockedReferences = true;
    private readonly List<GameObject> lockedReferenceMarkers = new List<GameObject>();
    private readonly Dictionary<Collider, Button> uiButtonsByCollider = new Dictionary<Collider, Button>();
    private readonly Dictionary<Button, Color> uiButtonBaseColors = new Dictionary<Button, Color>();
    private Button hoveredButton;
    private LineRenderer leftControllerRay;
    private IController metaLeftController;
    private bool previousMetaTriggerPressed;
    private bool metaControllerUnavailableLogged;

    public string ActiveSetId => activeSetId;
    public string ActiveColor => activeColor;
    public string ManifestPath => AagManualAnchorSetStore.ManifestPath;

    private void Awake()
    {
        DisableHandTrackingRootsForControllerOnly();
        anchorManager = GetComponent<SpatialAnchorManager>();
        manifest = AagManualAnchorSetStore.LoadOrCreate();
        var firstUnlocked = AagManualAnchorSetStore.SetIds.FirstOrDefault(id => !GetSet(id).locked);
        if (!string.IsNullOrEmpty(firstUnlocked)) activeSetId = firstUnlocked;
        var existingLast = manifest.sets.SelectMany(set => set.anchors).LastOrDefault();
        if (existingLast != null) lastSavedUuid = existingLast.anchor_uuid;

        var legacyAuthoring = GetComponent<AagFp1PlacementAuthoring>();
        if (legacyAuthoring != null && legacyAuthoring.enabled)
        {
            legacyAuthoring.enabled = false;
            Debug.Log("[AAG Manual Sets] Disabled automatic candidate authoring and Grip set switching. Right-controller anchor create/save remains active.");
        }
    }

    private void Start()
    {
        InitializeMetaLeftController();
        CreateHudAndControls();
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log($"[AAG Manual Sets] authoring=true manifest={AagManualAnchorSetStore.ManifestPath}; legacyAnchors=REFERENCE_ONLY; automaticPlacement=false");
    }

    private void Update()
    {
        UpdateLeftControllerUiRay();
    }

    public bool CanAcceptAnchor(out string reason)
    {
        return CanAcceptAnchor(activeSetId, activeColor, out reason);
    }

    public bool BeginAnchorSave(out SaveContext context, out string reason)
    {
        context = new SaveContext(activeSetId, activeColor);
        return CanAcceptAnchor(context.SetId, context.Color, out reason);
    }

    private bool CanAcceptAnchor(string setId, string color, out string reason)
    {
        var set = GetSet(setId);
        if (set.locked)
        {
            reason = $"{setId} is LOCKED; select another set";
            return false;
        }
        var colorCount = CountColor(set, color);
        if (colorCount >= AnchorsPerColor)
        {
            reason = $"{color} is complete ({colorCount}/{AnchorsPerColor}); select another color";
            return false;
        }
        if (set.anchors.Count >= AnchorsPerSet)
        {
            reason = $"{setId} already has {set.anchors.Count}/{AnchorsPerSet}; LOCK SET";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public bool RecordSuccessfulAnchor(OVRSpatialAnchor anchor, SaveContext context, out string failure)
    {
        if (anchor == null)
        {
            failure = "saved anchor callback returned null";
            return false;
        }
        if (!CanAcceptAnchor(context.SetId, context.Color, out failure)) return false;
        var set = GetSet(context.SetId);
        if (manifest.sets.SelectMany(value => value.anchors).Any(value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in manual manifest: {anchor.Uuid}";
            return false;
        }

        var colorOrdinal = CountColor(set, context.Color) + 1;
        var position = anchor.transform.position;
        var rotation = anchor.transform.rotation;
        var entry = new AagManualAnchorEntry
        {
            floor_plan_id = "FP1",
            set_id = context.SetId,
            marker_id = $"{context.Color.ToLowerInvariant()}_{colorOrdinal}",
            color = context.Color,
            anchor_uuid = anchor.Uuid.ToString(),
            world_x = position.x,
            world_y = position.y,
            world_z = position.z,
            rotation_x = rotation.x,
            rotation_y = rotation.y,
            rotation_z = rotation.z,
            rotation_w = rotation.w,
            saved_at_utc = DateTime.UtcNow.ToString("O"),
        };
        set.anchors.Add(entry);
        if (!AagManualAnchorSetStore.Save(manifest, out failure))
        {
            set.anchors.Remove(entry);
            operationMessage = $"MANIFEST WRITE FAILED: {failure}";
            RefreshHud();
            Debug.LogError($"[AAG Manual Sets] append failed uuid={anchor.Uuid}; set={context.SetId}; color={context.Color}; reason={failure}");
            return false;
        }

        lastSavedUuid = entry.anchor_uuid;
        var colorCount = CountColor(set, context.Color);
        operationMessage = colorCount == AnchorsPerColor
            ? $"{context.Color} complete. Select the next color."
            : $"Saved {entry.marker_id}";
        RefreshHud();
        Debug.Log($"[AAG Manual Sets Append] floor_plan_id=FP1 set_id={entry.set_id} marker_id={entry.marker_id} color={entry.color} "
            + $"anchor_uuid={entry.anchor_uuid} world=({entry.world_x:F6},{entry.world_y:F6},{entry.world_z:F6}) saved_at={entry.saved_at_utc} "
            + $"colorCount={colorCount}/3 setCount={set.anchors.Count}/12");
        failure = string.Empty;
        return true;
    }

    public void ReportBlockedSave(string reason)
    {
        operationMessage = reason;
        RefreshHud();
        Debug.LogWarning($"[AAG Manual Sets] save blocked set={activeSetId}; color={activeColor}; reason={reason}");
    }

    public void SelectSet(string setId)
    {
        if (!AagManualAnchorSetStore.SetIds.Contains(setId, StringComparer.Ordinal)) return;
        activeSetId = setId;
        operationMessage = GetSet(setId).locked ? $"Viewing locked {setId}" : $"Active set: {setId}";
        RefreshLockedReferences();
        RefreshHud();
    }

    public void SelectColor(string color)
    {
        if (!AagManualAnchorSetStore.Colors.Contains(color, StringComparer.Ordinal)) return;
        activeColor = color;
        var count = CountColor(GetSet(activeSetId), color);
        operationMessage = count >= AnchorsPerColor ? $"{color} already complete; choose another color" : $"Active color: {color}";
        RefreshHud();
    }

    public void NextSet()
    {
        var index = Array.IndexOf(AagManualAnchorSetStore.SetIds, activeSetId);
        SelectSet(AagManualAnchorSetStore.SetIds[(index + 1) % AagManualAnchorSetStore.SetIds.Length]);
    }

    public void UndoLast()
    {
        var set = GetSet(activeSetId);
        if (set.locked)
        {
            ReportBlockedSave("UNDO blocked: set is locked");
            return;
        }
        var entry = set.anchors.LastOrDefault();
        if (entry == null)
        {
            ReportBlockedSave("UNDO blocked: active set is empty");
            return;
        }
        if (!Guid.TryParse(entry.anchor_uuid, out var uuid))
        {
            ReportBlockedSave("UNDO blocked: invalid manifest UUID");
            return;
        }
        operationMessage = $"Undoing {entry.marker_id}...";
        RefreshHud();
        anchorManager.EraseManagedAnchor(uuid, success =>
        {
            if (!success)
            {
                ReportBlockedSave($"UNDO failed: anchor erase failed {uuid}");
                return;
            }
            set.anchors.Remove(entry);
            if (!AagManualAnchorSetStore.Save(manifest, out var failure))
            {
                ReportBlockedSave($"UNDO manifest save failed: {failure}");
                return;
            }
            lastSavedUuid = set.anchors.LastOrDefault()?.anchor_uuid ?? "NONE";
            operationMessage = $"Undid {entry.marker_id}";
            RefreshLockedReferences();
            RefreshHud();
            Debug.Log($"[AAG Manual Sets Undo] set={activeSetId}; marker_id={entry.marker_id}; uuid={uuid}; erased=true; remaining={set.anchors.Count}/12");
        });
    }

    public void LockActiveSet()
    {
        var set = GetSet(activeSetId);
        if (set.locked)
        {
            ReportBlockedSave($"{activeSetId} is already locked");
            return;
        }
        var incomplete = AagManualAnchorSetStore.Colors.Where(color => CountColor(set, color) != AnchorsPerColor).ToList();
        if (set.anchors.Count != AnchorsPerSet || incomplete.Count > 0)
        {
            ReportBlockedSave($"LOCK blocked: total={set.anchors.Count}/12 incomplete=[{string.Join(",", incomplete)}]");
            return;
        }
        set.locked = true;
        set.locked_at_utc = DateTime.UtcNow.ToString("O");
        if (!AagManualAnchorSetStore.Save(manifest, out var failure))
        {
            set.locked = false;
            set.locked_at_utc = string.Empty;
            ReportBlockedSave($"LOCK save failed: {failure}");
            return;
        }
        operationMessage = $"LOCKED {activeSetId}. Select NEXT SET.";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log($"[AAG Manual Sets Lock] set={activeSetId}; total=12; Red=3; Blue=3; Green=3; Yellow=3; locked=true");
    }

    public void ExportSets()
    {
        var incompleteSets = manifest.sets.Where(set => AagManualAnchorSetStore.SetIds.Contains(set.set_id, StringComparer.Ordinal) && !set.locked)
            .Select(set => set.set_id).ToList();
        if (incompleteSets.Count > 0)
        {
            ReportBlockedSave($"EXPORT blocked: lock all sets first [{string.Join(",", incompleteSets)}]");
            return;
        }
        if (AagManualAnchorSetStore.ExportJsonAndCsv(manifest, out var jsonPath, out var csvPath, out var failure))
        {
            operationMessage = "EXPORT COMPLETE. Copy files to PC before uninstall.";
            RefreshHud();
            Debug.Log($"[AAG Manual Sets Export] json={jsonPath}; csv={csvPath}; lockedSets={manifest.sets.Count(set => set.locked)}/3");
        }
        else
        {
            ReportBlockedSave($"EXPORT failed: {failure}");
        }
    }

    public void ToggleLockedReferences()
    {
        showLockedReferences = !showLockedReferences;
        RefreshLockedReferences();
        operationMessage = $"Locked references: {(showLockedReferences ? "SHOWN" : "HIDDEN")}";
        RefreshHud();
    }

    private AagManualAnchorSetRecord GetSet(string setId)
    {
        return manifest.sets.First(value => string.Equals(value.set_id, setId, StringComparison.Ordinal));
    }

    private static int CountColor(AagManualAnchorSetRecord set, string color)
    {
        return set.anchors.Count(entry => string.Equals(entry.color, color, StringComparison.Ordinal));
    }

    private void RefreshHud()
    {
        if (statusText == null) return;
        var set = GetSet(activeSetId);
        statusText.text = $"MANUAL ANCHOR SET AUTHORING\nACTIVE SET: {activeSetId} {(set.locked ? "[LOCKED]" : "[OPEN]")}\n"
            + $"ACTIVE COLOR: {activeColor} {CountColor(set, activeColor)}/3\n"
            + $"Red {CountColor(set, "Red")}/3   Blue {CountColor(set, "Blue")}/3   Green {CountColor(set, "Green")}/3   Yellow {CountColor(set, "Yellow")}/3\n"
            + $"SET TOTAL: {set.anchors.Count}/12\nLAST UUID: {lastSavedUuid}\n{operationMessage}\n"
            + "RIGHT TRIGGER=create  |  A=save\nWARNING: EXPORT SETS BEFORE APP UNINSTALL / CLEAR APP DATA";
    }

    private void CreateHudAndControls()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            Debug.LogError("[AAG Manual Sets] HMD camera missing; authoring HUD not created");
            return;
        }
        var root = new GameObject("AAG Manual Anchor Set UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
        var rect = root.GetComponent<RectTransform>();
        rect.SetParent(camera.transform, false);
        rect.localPosition = new Vector3(0f, -0.02f, 0.9f);
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * 0.00085f;
        rect.sizeDelta = new Vector2(1000f, 720f);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = camera;
        canvas.sortingOrder = 200;
        root.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
        var background = root.AddComponent<Image>();
        background.color = new Color(0.02f, 0.03f, 0.05f, 0.9f);

        statusText = CreateText(rect, "Status", new Vector2(0.03f, 0.43f), new Vector2(0.97f, 0.98f), 28f);
        var setLabels = new[] { "FP1-S1", "FP1-S2", "FP1-S3", "NEXT SET" };
        for (var i = 0; i < setLabels.Length; i++)
        {
            var captured = setLabels[i];
            CreateButton(rect, captured, new Vector2(0.03f + i * 0.24f, 0.33f), new Vector2(0.25f + i * 0.24f, 0.41f),
                () => { if (captured == "NEXT SET") NextSet(); else SelectSet(captured); }, new Color(0.1f, 0.3f, 0.65f, 0.95f));
        }
        for (var i = 0; i < AagManualAnchorSetStore.Colors.Length; i++)
        {
            var captured = AagManualAnchorSetStore.Colors[i];
            var tint = captured == "Red" ? Color.red : captured == "Blue" ? Color.blue : captured == "Green" ? new Color(0f, 0.65f, 0.15f) : new Color(0.85f, 0.7f, 0f);
            CreateButton(rect, captured, new Vector2(0.03f + i * 0.24f, 0.23f), new Vector2(0.25f + i * 0.24f, 0.31f), () => SelectColor(captured), tint);
        }
        CreateButton(rect, "UNDO LAST", new Vector2(0.03f, 0.12f), new Vector2(0.25f, 0.20f), UndoLast, new Color(0.55f, 0.2f, 0.1f));
        CreateButton(rect, "LOCK SET", new Vector2(0.27f, 0.12f), new Vector2(0.49f, 0.20f), LockActiveSet, new Color(0.4f, 0.1f, 0.5f));
        CreateButton(rect, "EXPORT SETS", new Vector2(0.51f, 0.12f), new Vector2(0.73f, 0.20f), ExportSets, new Color(0.05f, 0.5f, 0.5f));
        CreateButton(rect, "SHOW/HIDE LOCKED", new Vector2(0.75f, 0.12f), new Vector2(0.97f, 0.20f), ToggleLockedReferences, new Color(0.3f, 0.3f, 0.3f));
    }

    private static TextMeshProUGUI CreateText(RectTransform parent, string name, Vector2 min, Vector2 max, float size)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        var rect = obj.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var text = obj.GetComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        return text;
    }

    private void CreateButton(RectTransform parent, string label, Vector2 min, Vector2 max, UnityEngine.Events.UnityAction action, Color tint)
    {
        var obj = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(BoxCollider));
        var rect = obj.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var image = obj.GetComponent<Image>();
        image.color = tint;
        var button = obj.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(action);
        var colors = button.colors;
        colors.highlightedColor = Color.green;
        colors.selectedColor = Color.green;
        button.colors = colors;
        var text = CreateText(rect, "Label", Vector2.zero, Vector2.one, 24f);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
        var collider = obj.GetComponent<BoxCollider>();
        collider.size = new Vector3(Mathf.Max(1f, rect.rect.width), Mathf.Max(1f, rect.rect.height), 2f);
        uiButtonsByCollider[collider] = button;
        uiButtonBaseColors[button] = tint;
    }

    private void UpdateLeftControllerUiRay()
    {
        if (metaLeftController == null)
            InitializeMetaLeftController();
        if (metaLeftController == null || !metaLeftController.IsConnected
            || !metaLeftController.TryGetPointerPose(out var pointerPose))
        {
            SetHoveredButton(null);
            if (leftControllerRay != null) leftControllerRay.enabled = false;
            previousMetaTriggerPressed = false;
            if (!metaControllerUnavailableLogged)
            {
                Debug.LogWarning("[AAG Manual Sets UI] Meta left controller pointer pose unavailable; ray hidden and no fallback pose used");
                metaControllerUnavailableLogged = true;
            }
            return;
        }
        if (metaControllerUnavailableLogged)
        {
            Debug.Log("[AAG Manual Sets UI] Meta left controller pointer pose restored");
            metaControllerUnavailableLogged = false;
        }
        var origin = pointerPose.position;
        var direction = pointerPose.rotation * Vector3.forward;
        var end = origin + direction * 5f;
        Button hitButton = null;
        foreach (var hit in Physics.RaycastAll(origin, direction, 5f).OrderBy(value => value.distance))
        {
            if (!uiButtonsByCollider.TryGetValue(hit.collider, out hitButton)) continue;
            end = hit.point;
            break;
        }
        SetHoveredButton(hitButton);
        EnsureLeftControllerRay();
        leftControllerRay.enabled = true;
        leftControllerRay.SetPosition(0, origin);
        leftControllerRay.SetPosition(1, end);
        var triggerPressed = metaLeftController.IsButtonUsageAnyActive(ControllerButtonUsage.TriggerButton);
        var triggerStarted = triggerPressed && !previousMetaTriggerPressed;
        previousMetaTriggerPressed = triggerPressed;
        if (hitButton != null && triggerStarted)
        {
            Debug.Log($"[AAG Manual Sets UI] click={hitButton.name}; input=MetaIController.TriggerButton; pointerPose=MetaTryGetPointerPose");
            hitButton.onClick.Invoke();
        }
    }

    private void InitializeMetaLeftController()
    {
        if (metaLeftController != null) return;

        // ControllerRef also implements IController, but inactive/unwired instances have a null
        // backing controller. Reading Handedness from one throws every frame. Only reuse the
        // concrete, active Controller data source; otherwise create a dedicated source below.
        foreach (var controller in FindObjectsByType<Oculus.Interaction.Input.Controller>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            try
            {
                if (controller.Handedness != Handedness.Left) continue;
                metaLeftController = controller;
                Debug.Log($"[AAG Manual Sets UI] Meta Controller reused component={controller.GetType().Name}#{controller.GetInstanceID()}");
                return;
            }
            catch (NullReferenceException)
            {
                Debug.LogWarning($"[AAG Manual Sets UI] Skipped uninitialized Meta Controller #{controller.GetInstanceID()}");
            }
        }

        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig == null) return;
        var sourceObject = new GameObject("AAG Meta Left Controller Data Source");
        sourceObject.transform.SetParent(transform, false);
        var cameraRigRef = sourceObject.AddComponent<OVRCameraRigRef>();
        cameraRigRef.InjectAllOVRCameraRigRef(cameraRig, false);
        var trackingTransformer = sourceObject.AddComponent<TrackingToWorldTransformerOVR>();
        trackingTransformer.InjectAllTrackingToWorldTransformerOVR(cameraRigRef);
        var source = sourceObject.AddComponent<FromOVRControllerDataSource>();
        source.InjectAllFromOVRControllerDataSource(
            DataSource<ControllerDataAsset>.UpdateModeFlags.UnityUpdate,
            null,
            Handedness.Left,
            cameraRigRef,
            trackingTransformer);
        var controllerComponent = sourceObject.AddComponent<Oculus.Interaction.Input.Controller>();
        controllerComponent.InjectAllController(
            DataSource<ControllerDataAsset>.UpdateModeFlags.AfterPreviousStep,
            source,
            source,
            false);
        metaLeftController = controllerComponent;
        Debug.Log("[AAG Manual Sets UI] Meta controller chain created: OVRCameraRigRef>TrackingToWorldTransformerOVR>FromOVRControllerDataSource>Controller; handPoseFallback=false");
    }

    private static void DisableHandTrackingRootsForControllerOnly()
    {
        var found = 0;
        var disabled = 0;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (!candidate.scene.IsValid() || !ControllerOnlyDisabledRoots.Contains(candidate.name)) continue;
            found++;
            if (!candidate.activeSelf) continue;
            candidate.SetActive(false);
            disabled++;
        }
        Debug.Log($"[AAG Manual Sets UI] inputMode=CONTROLLERS_ONLY; handTrackingRootsFound={found}; disabledNow={disabled}; "
            + "source=Meta IController pointer pose; smoothing=false; handFallback=false");
    }

    private void SetHoveredButton(Button next)
    {
        if (hoveredButton == next) return;
        if (hoveredButton != null && uiButtonBaseColors.TryGetValue(hoveredButton, out var previousColor))
            hoveredButton.targetGraphic.color = previousColor;
        hoveredButton = next;
        if (hoveredButton != null) hoveredButton.targetGraphic.color = Color.green;
    }

    private void EnsureLeftControllerRay()
    {
        if (leftControllerRay != null) return;
        var rayObject = new GameObject("AAG Manual Set Left Controller Ray");
        leftControllerRay = rayObject.AddComponent<LineRenderer>();
        leftControllerRay.positionCount = 2;
        leftControllerRay.useWorldSpace = true;
        leftControllerRay.startWidth = 0.004f;
        leftControllerRay.endWidth = 0.002f;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null) leftControllerRay.material = new Material(shader);
        leftControllerRay.startColor = Color.cyan;
        leftControllerRay.endColor = Color.green;
    }

    private void RefreshLockedReferences()
    {
        foreach (var marker in lockedReferenceMarkers)
            if (marker != null) Destroy(marker);
        lockedReferenceMarkers.Clear();
        if (!showLockedReferences) return;
        foreach (var set in manifest.sets.Where(value => value.locked && !string.Equals(value.set_id, activeSetId, StringComparison.Ordinal)))
        {
            foreach (var entry in set.anchors)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = $"AAG Locked Reference {set.set_id} {entry.marker_id} {entry.anchor_uuid}";
                marker.transform.SetPositionAndRotation(entry.WorldPosition, entry.WorldRotation);
                marker.transform.localScale = Vector3.one * 0.09f;
                var collider = marker.GetComponent<Collider>();
                if (collider != null) Destroy(collider);
                var renderer = marker.GetComponent<Renderer>();
                var shader = Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Lit");
                if (renderer != null && shader != null)
                {
                    var material = new Material(shader) { color = new Color(0.5f, 0.5f, 0.5f, 0.35f) };
                    renderer.material = material;
                }
                lockedReferenceMarkers.Add(marker);
            }
        }
    }
}
