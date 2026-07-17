using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class AagManualAnchorSetAuthoring : MonoBehaviour
{
    // Runtime-only repair workflow; no scene serialization is required.
    public readonly struct SaveContext
    {
        public SaveContext(string setId, string color)
        {
            SetId = setId;
            Color = color;
            MarkerId = string.Empty;
            ReplacedUuid = Guid.Empty;
            IsFixedTower = false;
            IsIncidental = false;
            PrefabResourcePath = string.Empty;
        }

        public SaveContext(string setId, string color, string markerId, Guid replacedUuid)
        {
            SetId = setId;
            Color = color;
            MarkerId = markerId;
            ReplacedUuid = replacedUuid;
            IsFixedTower = false;
            IsIncidental = false;
            PrefabResourcePath = string.Empty;
        }

        private SaveContext(string towerId)
        {
            SetId = string.Empty;
            Color = string.Empty;
            MarkerId = towerId;
            ReplacedUuid = Guid.Empty;
            IsFixedTower = true;
            IsIncidental = false;
            PrefabResourcePath = string.Empty;
        }

        private SaveContext(string setId, string objectId, string prefabResourcePath, bool incidental)
        {
            SetId = setId;
            Color = string.Empty;
            MarkerId = objectId;
            ReplacedUuid = Guid.Empty;
            IsFixedTower = false;
            IsIncidental = incidental;
            PrefabResourcePath = prefabResourcePath;
        }

        public static SaveContext ForFixedTower(string towerId) => new SaveContext(towerId);
        public static SaveContext ForIncidental(string setId, string objectId, string prefabResourcePath) =>
            new SaveContext(setId, objectId, prefabResourcePath, true);

        public string SetId { get; }
        public string Color { get; }
        public string MarkerId { get; }
        public Guid ReplacedUuid { get; }
        public bool IsFixedTower { get; }
        public bool IsIncidental { get; }
        public string PrefabResourcePath { get; }
        public bool UsesSeparateStore => IsFixedTower || IsIncidental;
        public bool IsReplacement => ReplacedUuid != Guid.Empty && !string.IsNullOrEmpty(MarkerId);
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
    private AnchorLoader anchorLoader;
    private FixedTowerAnchorLoader fixedTowerAnchorLoader;
    private AagSpatialAnchorTutorialTest tutorialTest;
    private AagManualAnchorSetManifest manifest;
    private AagFixedTowerAnchorManifest fixedTowerManifest;
    private AagIncidentalAnchorManifest incidentalManifest;
    private FixedTowerManager fixedTowerManager;
    private IncidentalAnchorLoader incidentalAnchorLoader;
    private IncidentalObjectManager incidentalObjectManager;
    private bool fixedTowerMode;
    private bool incidentalMode;
    private string activeSetId = "FP1-S1";
    private string activeColor = "Red";
    private string lastSavedUuid = "NONE";
    private string operationMessage = "READY - PRESS LOAD ACTIVE SET";
    private TextMeshProUGUI statusText;
    private readonly List<GameObject> lockedReferenceMarkers = new List<GameObject>();
    private readonly Dictionary<Collider, Button> uiButtonsByCollider = new Dictionary<Collider, Button>();
    private readonly Dictionary<Button, Color> uiButtonBaseColors = new Dictionary<Button, Color>();
    private Button hoveredButton;
    private LineRenderer leftControllerRay;
    private IController metaLeftController;
    private bool previousMetaTriggerPressed;
    private bool metaControllerUnavailableLogged;
    private int setLoadVersion;
    private readonly List<GameObject> approximatedMarkers = new List<GameObject>();
    private readonly List<AagManualAnchorEntry> missingEntries = new List<AagManualAnchorEntry>();

    public string ActiveSetId => activeSetId;
    public string ActiveColor => activeColor;
    public string ManifestPath => AagManualAnchorSetStore.ManifestPath;
    public bool IsTutorialTestBusy => tutorialTest != null && tutorialTest.OperationInProgress;
    public bool IsFixedTowerMode => fixedTowerMode;
    public bool IsIncidentalMode => incidentalMode;
    private string CurrentModeLabel => fixedTowerMode ? "FIXED_TOWER" : incidentalMode ? "INCIDENTAL" : "OBJECT_SET";
    private int FixedTowerCount => fixedTowerManifest?.towers?.Count ?? 0;
    private string NextTowerId => FixedTowerCount < AagFixedTowerAnchorStore.RequiredTowerCount
        ? AagFixedTowerAnchorStore.TowerIds[FixedTowerCount]
        : "COMPLETE";
    private string NextTowerColor => FixedTowerCount < AagFixedTowerAnchorStore.RequiredTowerCount
        ? AagFixedTowerAnchorStore.TowerColors[FixedTowerCount]
        : "COMPLETE";

    private void Awake()
    {
        DisableHandTrackingRootsForControllerOnly();
        anchorManager = GetComponent<SpatialAnchorManager>();
        anchorLoader = GetComponent<AnchorLoader>();
        fixedTowerAnchorLoader = GetComponent<FixedTowerAnchorLoader>() ?? gameObject.AddComponent<FixedTowerAnchorLoader>();
        fixedTowerAnchorLoader.Initialize(anchorManager != null ? anchorManager.anchorPrefab : null);
        incidentalAnchorLoader = GetComponent<IncidentalAnchorLoader>() ?? gameObject.AddComponent<IncidentalAnchorLoader>();
        incidentalAnchorLoader.Initialize(anchorManager != null ? anchorManager.anchorPrefab : null);
        incidentalObjectManager = GetComponent<IncidentalObjectManager>() ?? gameObject.AddComponent<IncidentalObjectManager>();
        tutorialTest = GetComponent<AagSpatialAnchorTutorialTest>() ?? gameObject.AddComponent<AagSpatialAnchorTutorialTest>();
        tutorialTest.Configure(anchorManager != null ? anchorManager.anchorPrefab : null);
        tutorialTest.StatusChanged += OnTutorialTestStatusChanged;
        manifest = AagManualAnchorSetStore.LoadOrCreate();
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
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
        Debug.Log($"[AAG Manual Sets] authoring=true manifest={AagManualAnchorSetStore.ManifestPath}; legacyAnchors=REFERENCE_ONLY; automaticPlacement=false; automaticAnchorLoad=false; waitingForManualLoad=true");
    }

    private void Update()
    {
        UpdateLeftControllerUiRay();
    }

    private void OnDestroy()
    {
        if (tutorialTest != null) tutorialTest.StatusChanged -= OnTutorialTestStatusChanged;
    }

    public bool CanAcceptAnchor(out string reason)
    {
        if (fixedTowerMode) return CanAcceptFixedTower(out reason);
        if (incidentalMode) return CanAcceptIncidental(activeSetId, out _, out _, out reason);
        return CanAcceptAnchor(activeSetId, activeColor, out reason);
    }

    public bool BeginAnchorSave(OVRSpatialAnchor candidateAnchor, out SaveContext context, out string reason)
    {
        if (fixedTowerMode)
        {
            var nextTowerId = NextTowerId;
            context = SaveContext.ForFixedTower(nextTowerId);
            return CanAcceptFixedTower(out reason);
        }
        if (incidentalMode)
        {
            if (!CanAcceptIncidental(activeSetId, out var nextPrefab, out var objectId, out reason))
            {
                context = default;
                return false;
            }
            var resourcePath = $"{AagIncidentalAnchorStore.ResourceFolderForSet(activeSetId)}/{nextPrefab.name}";
            context = SaveContext.ForIncidental(activeSetId, objectId, resourcePath);
            return true;
        }
        context = new SaveContext(activeSetId, activeColor);
        return CanAcceptAnchor(context.SetId, context.Color, out reason);
    }

    private bool CanAcceptIncidental(
        string setId,
        out GameObject nextPrefab,
        out string objectId,
        out string reason)
    {
        nextPrefab = null;
        objectId = string.Empty;
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        var set = AagIncidentalAnchorStore.GetSet(incidentalManifest, setId);
        var folder = AagIncidentalAnchorStore.ResourceFolderForSet(setId);
        var catalog = AagIncidentalAnchorStore.LoadPrefabCatalog(setId);
        if (catalog.Length == 0)
        {
            reason = $"No incidental prefabs in Resources/{folder}";
            return false;
        }
        if (catalog.Length != AagIncidentalAnchorStore.RequiredObjectsPerSet)
        {
            reason = $"{setId} requires {AagIncidentalAnchorStore.RequiredObjectsPerSet} incidental wrappers; found {catalog.Length}";
            return false;
        }
        if (catalog.GroupBy(value => value.name, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            reason = $"Duplicate incidental prefab names in Resources/{folder}";
            return false;
        }
        var usedPaths = new HashSet<string>(
            set?.objects?.Select(value => value.prefab_resource_path) ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        nextPrefab = catalog.FirstOrDefault(value => !usedPaths.Contains($"{folder}/{value.name}"));
        if (nextPrefab == null)
        {
            reason = $"{setId} incidental objects complete ({set?.objects?.Count ?? 0}/{catalog.Length})";
            return false;
        }
        objectId = $"incidental_{(set?.objects?.Count ?? 0) + 1:00}_{nextPrefab.name}";
        reason = string.Empty;
        return true;
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

    private bool CanAcceptFixedTower(out string reason)
    {
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        if (FixedTowerCount >= AagFixedTowerAnchorStore.RequiredTowerCount)
        {
            reason = "Fixed towers complete (4/4); use UNDO LAST to replace Tower-4";
            return false;
        }
        if (fixedTowerManifest.towers.Any(value =>
                !AagFixedTowerAnchorStore.TowerIds.Contains(value.tower_id, StringComparer.Ordinal)
                || !Guid.TryParse(value.anchor_uuid, out var uuid)
                || uuid == Guid.Empty))
        {
            reason = "Fixed tower store contains an invalid entry; use UNDO LAST or restore JSON";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private bool RecordSuccessfulFixedTower(
        OVRSpatialAnchor anchor,
        SaveContext context,
        out string failure)
    {
        if (anchor == null)
        {
            failure = "saved tower anchor callback returned null";
            return false;
        }

        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        if (!CanAcceptFixedTower(out failure)) return false;
        if (!string.Equals(context.MarkerId, NextTowerId, StringComparison.Ordinal))
        {
            failure = $"tower save order changed expected={NextTowerId} actual={context.MarkerId}";
            return false;
        }
        if (manifest.sets.SelectMany(value => value.anchors).Any(
            value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in S1/S2/S3 manifest: {anchor.Uuid}";
            return false;
        }
        if (fixedTowerManifest.towers.Any(
            value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in fixed tower store: {anchor.Uuid}";
            return false;
        }
        if (AagIncidentalAnchorStore.LoadOrCreate().sets.SelectMany(value => value.objects).Any(
            value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in incidental object store: {anchor.Uuid}";
            return false;
        }

        var position = anchor.transform.position;
        var rotation = anchor.transform.rotation;
        var entry = new AagFixedTowerAnchorEntry
        {
            tower_id = context.MarkerId,
            anchor_uuid = anchor.Uuid.ToString(),
            has_fallback_pose = true,
            fallback_x = position.x,
            fallback_y = position.y,
            fallback_z = position.z,
            fallback_rotation_x = rotation.x,
            fallback_rotation_y = rotation.y,
            fallback_rotation_z = rotation.z,
            fallback_rotation_w = rotation.w,
        };
        fixedTowerManifest.towers.Add(entry);
        if (!AagFixedTowerAnchorStore.Save(fixedTowerManifest, out failure))
        {
            fixedTowerManifest.towers.Remove(entry);
            operationMessage = $"TOWER STORE WRITE FAILED: {failure}";
            RefreshHud();
            return false;
        }

        lastSavedUuid = entry.anchor_uuid;
        operationMessage = FixedTowerCount == AagFixedTowerAnchorStore.RequiredTowerCount
            ? "TOWERS COMPLETE 4/4 - press LOAD/VERIFY"
            : $"Saved {entry.tower_id}. Next: {NextTowerId} ({NextTowerColor.ToUpperInvariant()})";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log(
            $"[FixedTower Authoring] Saved tower={entry.tower_id} uuid={entry.anchor_uuid} " +
            $"count={FixedTowerCount}/{AagFixedTowerAnchorStore.RequiredTowerCount} store={AagFixedTowerAnchorStore.ManifestPath}");
        failure = string.Empty;
        return true;
    }

    public bool RecordSuccessfulAnchor(OVRSpatialAnchor anchor, SaveContext context, out string failure)
    {
        if (context.IsFixedTower)
            return RecordSuccessfulFixedTower(anchor, context, out failure);
        if (context.IsIncidental)
            return RecordSuccessfulIncidental(anchor, context, out failure);

        if (anchor == null)
        {
            failure = "saved anchor callback returned null";
            return false;
        }
        if (manifest.sets.SelectMany(value => value.anchors).Any(value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in manual manifest: {anchor.Uuid}";
            return false;
        }
        if (AagFixedTowerAnchorStore.LoadOrCreate().towers.Any(
            value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in fixed tower store: {anchor.Uuid}";
            return false;
        }
        if (AagIncidentalAnchorStore.LoadOrCreate().sets.SelectMany(value => value.objects).Any(
            value => string.Equals(value.anchor_uuid, anchor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in incidental object store: {anchor.Uuid}";
            return false;
        }

        if (!CanAcceptAnchor(context.SetId, context.Color, out failure)) return false;
        var set = GetSet(context.SetId);

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
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log($"[AAG Manual Set] Saved marker={entry.marker_id} uuid={entry.anchor_uuid} set={entry.set_id} "
            + $"color={entry.color} setCount={set.anchors.Count}/12");
        failure = string.Empty;
        return true;
    }

    private bool RecordSuccessfulIncidental(OVRSpatialAnchor anchor, SaveContext context, out string failure)
    {
        if (anchor == null)
        {
            failure = "saved incidental anchor callback returned null";
            return false;
        }
        if (!CanAcceptIncidental(context.SetId, out var expectedPrefab, out var expectedObjectId, out failure))
            return false;
        var expectedPath = $"{AagIncidentalAnchorStore.ResourceFolderForSet(context.SetId)}/{expectedPrefab.name}";
        if (!string.Equals(context.MarkerId, expectedObjectId, StringComparison.Ordinal)
            || !string.Equals(context.PrefabResourcePath, expectedPath, StringComparison.Ordinal))
        {
            failure = $"incidental save order changed expected={expectedObjectId} actual={context.MarkerId}";
            return false;
        }

        var uuidText = anchor.Uuid.ToString();
        if (manifest.sets.SelectMany(value => value.anchors).Any(value =>
                string.Equals(value.anchor_uuid, uuidText, StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in S1/S2/S3 stone manifest: {anchor.Uuid}";
            return false;
        }
        if (AagFixedTowerAnchorStore.LoadOrCreate().towers.Any(value =>
                string.Equals(value.anchor_uuid, uuidText, StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in fixed tower store: {anchor.Uuid}";
            return false;
        }
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        if (incidentalManifest.sets.SelectMany(value => value.objects).Any(value =>
                string.Equals(value.anchor_uuid, uuidText, StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"anchor UUID already exists in incidental object store: {anchor.Uuid}";
            return false;
        }

        var set = AagIncidentalAnchorStore.GetSet(incidentalManifest, context.SetId);
        var position = anchor.transform.position;
        var rotation = anchor.transform.rotation;
        var entry = new AagIncidentalAnchorEntry
        {
            set_id = context.SetId,
            object_id = context.MarkerId,
            prefab_resource_path = context.PrefabResourcePath,
            anchor_uuid = uuidText,
            fallback_x = position.x,
            fallback_y = position.y,
            fallback_z = position.z,
            fallback_rotation_x = rotation.x,
            fallback_rotation_y = rotation.y,
            fallback_rotation_z = rotation.z,
            fallback_rotation_w = rotation.w,
            saved_at_utc = DateTime.UtcNow.ToString("O"),
        };
        set.objects.Add(entry);
        if (!AagIncidentalAnchorStore.Save(incidentalManifest, out failure))
        {
            set.objects.Remove(entry);
            operationMessage = $"INCIDENTAL STORE WRITE FAILED: {failure}";
            RefreshHud();
            return false;
        }

        lastSavedUuid = entry.anchor_uuid;
        var remaining = AagIncidentalAnchorStore.LoadPrefabCatalog(context.SetId).Length
            - set.objects.Count;
        operationMessage = remaining <= 0
            ? $"{context.SetId} INCIDENTAL COMPLETE - press LOAD/VERIFY"
            : $"Saved {entry.object_id}. {remaining} remaining";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log($"[Incidental Authoring] Saved set={entry.set_id} object={entry.object_id} "
            + $"prefab={entry.prefab_resource_path} uuid={entry.anchor_uuid} store={AagIncidentalAnchorStore.ManifestPath}");
        failure = string.Empty;
        return true;
    }

    public void NotifyAnchorCreated(OVRSpatialAnchor anchor)
    {
        // Runtime load mode does not replace UUIDs. Kept as a no-op because the
        // anchor creation component also supports the original authoring flow.
    }

    public void ReportBlockedSave(string reason)
    {
        operationMessage = reason;
        RefreshHud();
        Debug.LogWarning(
            $"[AAG Manual Sets] save blocked mode={CurrentModeLabel}; " +
            $"set={activeSetId}; color={activeColor}; reason={reason}");
    }

    public void ToggleObjectTowerMode()
    {
        setLoadVersion++;
        missingEntries.Clear();
        ClearApproximatedMarkers();
        fixedTowerManager?.ClearRuntimeContents();
        fixedTowerAnchorLoader?.ClearLoadedAnchors();
        incidentalObjectManager?.ClearRuntimeContents();
        incidentalAnchorLoader?.ClearLoadedAnchors();
        anchorLoader?.ClearLoadedAnchors();
        if (!fixedTowerMode && !incidentalMode)
        {
            fixedTowerMode = true;
        }
        else if (fixedTowerMode)
        {
            fixedTowerMode = false;
            incidentalMode = true;
        }
        else
        {
            incidentalMode = false;
        }
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        operationMessage = fixedTowerMode
            ? $"TOWER MODE - next {NextTowerId} ({NextTowerColor.ToUpperInvariant()}); Right Trigger CREATE, Right A SAVE"
            : incidentalMode
                ? $"INCIDENTAL MODE - active {activeSetId}; Right Trigger CREATE, Right A SAVE"
                : $"OBJECT MODE - active {activeSetId}/{activeColor}";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log(
            $"[AAG Authoring Mode] mode={CurrentModeLabel}; " +
            $"towerCount={FixedTowerCount}/{AagFixedTowerAnchorStore.RequiredTowerCount}");
    }

    public void SelectSet(string setId)
    {
        if (!AagManualAnchorSetStore.SetIds.Contains(setId, StringComparer.Ordinal)) return;
        setLoadVersion++;
        missingEntries.Clear();
        ClearApproximatedMarkers();
        tutorialTest?.HideRuntimeAnchors();
        fixedTowerManager?.ClearRuntimeContents();
        fixedTowerAnchorLoader?.ClearLoadedAnchors();
        incidentalObjectManager?.ClearRuntimeContents();
        incidentalAnchorLoader?.ClearLoadedAnchors();
        anchorLoader?.ClearLoadedAnchors();
        activeSetId = setId;
        operationMessage = $"READY - PRESS LOAD ACTIVE SET ({setId})";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log($"[AAG Manual Set] Selected set={setId}; automaticLoad=false; waitingForManualLoad=true");
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
        if (fixedTowerMode)
        {
            UndoLastFixedTower();
            return;
        }
        if (incidentalMode)
        {
            UndoLastIncidental();
            return;
        }

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

    private void UndoLastFixedTower()
    {
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        var entry = fixedTowerManifest.towers.LastOrDefault();
        if (entry == null)
        {
            ReportBlockedSave("UNDO blocked: fixed tower list is empty");
            return;
        }
        if (!Guid.TryParse(entry.anchor_uuid, out var uuid) || uuid == Guid.Empty)
        {
            ReportBlockedSave("UNDO blocked: invalid fixed tower UUID");
            return;
        }

        operationMessage = $"Undoing {entry.tower_id}...";
        RefreshHud();
        // The UUID list is authoritative for towers. A stale previous-build UUID
        // is removed immediately without routing through the object AnchorLoader.
        fixedTowerManifest.towers.Remove(entry);
        if (!AagFixedTowerAnchorStore.Save(fixedTowerManifest, out var failure))
        {
            fixedTowerManifest.towers.Add(entry);
            ReportBlockedSave($"UNDO tower store save failed: {failure}");
            return;
        }
        var questEraseStarted = anchorManager.TryEraseRuntimeAnchor(uuid, success =>
        {
            Debug.Log($"[FixedTower Authoring Undo] runtime quest erase uuid={uuid} success={success}");
        });
        fixedTowerManager?.ClearRuntimeContents();
        fixedTowerAnchorLoader?.ClearLoadedAnchors();
        lastSavedUuid = fixedTowerManifest.towers.LastOrDefault()?.anchor_uuid ?? "NONE";
        operationMessage = $"Undid {entry.tower_id}. Next: {NextTowerId}";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log(
            $"[FixedTower Authoring Undo] tower={entry.tower_id}; uuid={uuid}; " +
            $"runtimeQuestEraseStarted={questEraseStarted}; remaining={FixedTowerCount}/{AagFixedTowerAnchorStore.RequiredTowerCount}");
    }

    private void UndoLastIncidental()
    {
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        var set = AagIncidentalAnchorStore.GetSet(incidentalManifest, activeSetId);
        var entry = set?.objects?.LastOrDefault();
        if (entry == null)
        {
            ReportBlockedSave($"UNDO blocked: {activeSetId} incidental list is empty");
            return;
        }
        if (!Guid.TryParse(entry.anchor_uuid, out var uuid) || uuid == Guid.Empty)
        {
            ReportBlockedSave("UNDO blocked: invalid incidental UUID");
            return;
        }

        set.objects.Remove(entry);
        if (!AagIncidentalAnchorStore.Save(incidentalManifest, out var failure))
        {
            set.objects.Add(entry);
            ReportBlockedSave($"UNDO incidental store save failed: {failure}");
            return;
        }
        var questEraseStarted = anchorManager.TryEraseRuntimeAnchor(uuid, success =>
            Debug.Log($"[Incidental Authoring Undo] runtime quest erase uuid={uuid} success={success}"));
        incidentalObjectManager?.ClearRuntimeContents();
        incidentalAnchorLoader?.ClearLoadedAnchors();
        lastSavedUuid = set.objects.LastOrDefault()?.anchor_uuid ?? "NONE";
        operationMessage = $"Undid {entry.object_id}. Next incidental object is ready.";
        RefreshLockedReferences();
        RefreshHud();
        Debug.Log($"[Incidental Authoring Undo] set={activeSetId}; object={entry.object_id}; uuid={uuid}; "
            + $"runtimeQuestEraseStarted={questEraseStarted}; remaining={set.objects.Count}");
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
        if (incidentalMode)
        {
            incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
            if (AagIncidentalAnchorStore.Export(incidentalManifest, out var incidentalPath, out var incidentalFailure))
            {
                var count = incidentalManifest.sets.Sum(value => value.objects?.Count ?? 0);
                operationMessage = "INCIDENTAL EXPORT COMPLETE. Copy JSON to PC before uninstall.";
                RefreshHud();
                Debug.Log($"[Incidental Authoring Export] json={incidentalPath}; objects={count}");
            }
            else
            {
                ReportBlockedSave($"INCIDENTAL EXPORT failed: {incidentalFailure}");
            }
            return;
        }
        if (fixedTowerMode)
        {
            fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
            if (FixedTowerCount != AagFixedTowerAnchorStore.RequiredTowerCount)
            {
                ReportBlockedSave(
                    $"TOWER EXPORT blocked: saved={FixedTowerCount}/{AagFixedTowerAnchorStore.RequiredTowerCount}");
                return;
            }
            if (AagFixedTowerAnchorStore.ExportJsonAndCsv(
                    fixedTowerManifest,
                    out var towerJsonPath,
                    out var towerCsvPath,
                    out var towerFailure))
            {
                operationMessage = "TOWER EXPORT COMPLETE. Copy files to PC before uninstall.";
                RefreshHud();
                Debug.Log(
                    $"[FixedTower Authoring Export] json={towerJsonPath}; csv={towerCsvPath}; towers={FixedTowerCount}/4");
            }
            else
            {
                ReportBlockedSave($"TOWER EXPORT failed: {towerFailure}");
            }
            return;
        }

        var incompleteSets = manifest.sets.Where(set => AagManualAnchorSetStore.SetIds.Contains(set.set_id, StringComparer.Ordinal) && !set.locked)
            .Select(set => set.set_id).ToList();
        if (incompleteSets.Count > 0)
        {
            ReportBlockedSave($"EXPORT blocked: lock all sets first [{string.Join(",", incompleteSets)}]");
            return;
        }
        if (AagManualAnchorSetStore.ExportJsonAndCsv(manifest, out var jsonPath, out var csvPath, out var failure))
        {
            fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
            var towerExport = "tower export skipped (not 4/4)";
            if (FixedTowerCount == AagFixedTowerAnchorStore.RequiredTowerCount)
            {
                if (!AagFixedTowerAnchorStore.ExportJsonAndCsv(
                        fixedTowerManifest,
                        out var towerJsonPath,
                        out var towerCsvPath,
                        out var towerFailure))
                {
                    ReportBlockedSave($"TOWER EXPORT failed: {towerFailure}");
                    return;
                }
                towerExport = $"towerJson={towerJsonPath}; towerCsv={towerCsvPath}";
            }
            operationMessage = "EXPORT COMPLETE. Copy object and tower files to PC before uninstall.";
            RefreshHud();
            Debug.Log(
                $"[AAG Manual Sets Export] json={jsonPath}; csv={csvPath}; " +
                $"lockedSets={manifest.sets.Count(set => set.locked)}/3; {towerExport}");
        }
        else
        {
            ReportBlockedSave($"EXPORT failed: {failure}");
        }
    }

    public void ReloadActiveSelection()
    {
        if (fixedTowerMode) ReloadFixedTowers();
        else if (incidentalMode) ReloadIncidentalObjects();
        else ReloadActiveSet();
    }

    private void ReloadIncidentalObjects()
    {
        if (incidentalAnchorLoader == null || incidentalObjectManager == null) return;
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        var set = AagIncidentalAnchorStore.GetSet(incidentalManifest, activeSetId);
        if (set?.objects == null || set.objects.Count == 0)
        {
            operationMessage = $"{activeSetId} has no incidental UUIDs";
            RefreshHud();
            return;
        }
        if (!incidentalObjectManager.TryBuildMap(activeSetId, out var entries, out var failure, false))
        {
            ReportBlockedSave($"LOAD INCIDENTAL blocked: {failure}");
            return;
        }

        setLoadVersion++;
        missingEntries.Clear();
        ClearApproximatedMarkers();
        tutorialTest?.HideRuntimeAnchors();
        incidentalObjectManager.ClearRuntimeContents();
        incidentalAnchorLoader.ClearLoadedAnchors();
        operationMessage = $"Loading {activeSetId} incidental objects 0/{entries.Count}...";
        RefreshHud();
        incidentalAnchorLoader.Load(entries);
        StartCoroutine(ReloadIncidentalObjectsCoroutine(activeSetId, entries));
    }

    private IEnumerator ReloadIncidentalObjectsCoroutine(
        string setId,
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries)
    {
        while (incidentalAnchorLoader.IsLoading)
        {
            operationMessage = $"Loading {setId} incidental objects {incidentalAnchorLoader.LoadedCount}/{entries.Count}...";
            RefreshHud();
            yield return null;
        }
        if (!incidentalObjectManager.TrySpawn(incidentalAnchorLoader, entries, out var failure))
        {
            ReportBlockedSave($"LOAD INCIDENTAL failed: {failure}");
            yield break;
        }
        operationMessage = $"INCIDENTAL READY {setId}: REAL {incidentalAnchorLoader.RealLoadedCount} + "
            + $"APPROX {incidentalAnchorLoader.ApproximateCount} = {incidentalAnchorLoader.LoadedCount}/{entries.Count}";
        RefreshHud();
        foreach (var pair in entries)
        {
            var placement = incidentalAnchorLoader.ApproximateReasons.ContainsKey(pair.Key) ? "APPROX" : "REAL";
            Debug.Log($"[Incidental Authoring Load] set={setId} object={pair.Value.object_id} uuid={pair.Key} placement={placement}");
        }
    }

    private void ReloadFixedTowers()
    {
        if (fixedTowerAnchorLoader == null || anchorManager == null) return;
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        if (FixedTowerCount != AagFixedTowerAnchorStore.RequiredTowerCount)
        {
            ReportBlockedSave(
                $"LOAD TOWERS blocked: saved={FixedTowerCount}/{AagFixedTowerAnchorStore.RequiredTowerCount}");
            return;
        }

        setLoadVersion++;
        missingEntries.Clear();
        ClearApproximatedMarkers();
        tutorialTest?.HideRuntimeAnchors();
        fixedTowerManager = FindFirstObjectByType<FixedTowerManager>()
            ?? gameObject.AddComponent<FixedTowerManager>();
        var experimentMain = FindFirstObjectByType<ExperimentMain>();
        fixedTowerManager.Initialize(experimentMain?.Configuration, experimentMain);
        if (!fixedTowerManager.TryBuildAnchorMap(out var towersByUuid, out var configurationFailure))
        {
            ReportBlockedSave($"LOAD TOWERS blocked: {configurationFailure}");
            return;
        }

        fixedTowerManager.ClearRuntimeContents();
        fixedTowerAnchorLoader.ClearLoadedAnchors();
        operationMessage = $"Loading fixed towers 0/{towersByUuid.Count}...";
        RefreshHud();
        fixedTowerAnchorLoader.Load(towersByUuid);
        StartCoroutine(ReloadFixedTowersCoroutine(towersByUuid));
    }

    private IEnumerator ReloadFixedTowersCoroutine(
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid)
    {
        while (fixedTowerAnchorLoader.IsLoading)
        {
            operationMessage =
                $"Loading fixed towers {fixedTowerAnchorLoader.LoadedCount}/{towersByUuid.Count}...";
            RefreshHud();
            yield return null;
        }

        if (!fixedTowerManager.TrySpawnLocalizedTowers(fixedTowerAnchorLoader, towersByUuid, out var failure))
        {
            foreach (var pair in towersByUuid)
            {
                if (!fixedTowerAnchorLoader.Failures.TryGetValue(pair.Key, out var reason)) continue;
                Debug.LogError(
                    $"[FixedTower Authoring Load] failed tower={pair.Value.towerId} uuid={pair.Key} reason={reason}");
            }
            ReportBlockedSave($"LOAD TOWERS failed: {failure}");
            yield break;
        }
        operationMessage = fixedTowerAnchorLoader.ApproximateCount > 0
            ? $"TOWERS READY {towersByUuid.Count}/4 (REAL {fixedTowerAnchorLoader.RealLoadedCount}, APPROX {fixedTowerAnchorLoader.ApproximateCount})"
            : $"TOWERS VERIFIED {towersByUuid.Count}/{AagFixedTowerAnchorStore.RequiredTowerCount}";
        RefreshHud();
        Debug.Log(
            $"[FixedTower Authoring Load] real={fixedTowerAnchorLoader.RealLoadedCount}; " +
            $"approx={fixedTowerAnchorLoader.ApproximateCount}; " +
            $"store={AagFixedTowerAnchorStore.ManifestPath}");
    }

    public void ReloadActiveSet()
    {
        if (anchorLoader == null) return;
        var loadVersion = ++setLoadVersion;
        // Reload exactly one authoritative file. Exports, repair backups,
        // PlayerPrefs, and anchor_log.json are never set-load sources.
        manifest = AagManualAnchorSetStore.LoadOrCreate();
        var set = GetSet(activeSetId);
        missingEntries.Clear();
        ClearApproximatedMarkers();
        tutorialTest?.HideRuntimeAnchors();
        anchorLoader.ClearLoadedAnchors();
        RefreshLockedReferences();
        if (set.anchors.Count == 0)
        {
            operationMessage = $"{activeSetId} has no manifest UUIDs";
            RefreshHud();
            return;
        }
        Debug.Log(
            $"[AAG Manual Set] Manual load trigger set={activeSetId}; clearingRuntimeAnchors=true; " +
            $"forceQuestStoreQuery=true; existingAnchorReuse=false; requested={set.anchors.Count}; " +
            $"manifestSource=FIXED_RUNTIME_FILE; manifestPath={AagManualAnchorSetStore.ManifestPath}");
        StartCoroutine(ReloadActiveSetCoroutine(activeSetId, loadVersion));
    }

    private IEnumerator ReloadActiveSetCoroutine(string setId, int loadVersion)
    {
        var set = GetSet(setId);
        var uuids = new List<Guid>();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid) || uuid == Guid.Empty)
            {
                operationMessage = $"{setId} load failed: invalid UUID for {entry.marker_id}";
                RefreshHud();
                yield break;
            }
            uuids.Add(uuid);
        }

        operationMessage = $"Fresh Quest load {setId}: 0 / {uuids.Count}...";
        RefreshLockedReferences();
        RefreshHud();
        anchorLoader.ClearPrefabOverrides();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid) || uuid == Guid.Empty) continue;
            anchorLoader.SetPrefabForUuid(uuid, anchorManager != null
                ? anchorManager.GetAnchorPrefabForColor(entry.color)
                : null);
        }
        anchorLoader.LoadAnchorsByUuid(uuids, $"MANUAL_ACTIVE_SET:{setId}", true);
        var lastHudRefresh = -1f;
        var lastLocalizedCount = -1;
        while (anchorLoader.IsReadOnlyLoadInProgress)
        {
            if (loadVersion != setLoadVersion) yield break;
            RefreshManagedAnchorVisibility();
            var localizedNow = anchorLoader.LocalizedRequestedCount;
            if (localizedNow != lastLocalizedCount || Time.realtimeSinceStartup - lastHudRefresh >= 1f)
            {
                lastLocalizedCount = localizedNow;
                lastHudRefresh = Time.realtimeSinceStartup;
                operationMessage = $"Loading {setId}: {localizedNow} / {uuids.Count}...";
                RefreshHud();
            }
            yield return null;
        }
        if (loadVersion != setLoadVersion) yield break;

        var localized = uuids.Count(uuid => anchorLoader.TryGetLocalizedAnchor(uuid, out _)
            && !anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid));
        missingEntries.Clear();
        foreach (var entry in set.anchors)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
            if (anchorLoader.TryGetLocalizedAnchor(uuid, out _)
                && !anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid))
            {
                Debug.Log($"[AAG Manual Set] Loaded marker={entry.marker_id} uuid={uuid}");
            }
            else
            {
                missingEntries.Add(entry);
                var reason = anchorLoader.LocalizationFailuresReadOnly.TryGetValue(uuid, out var failure)
                    ? failure
                    : "not returned";
                Debug.LogError($"[AAG Manual Set] Failed marker={entry.marker_id} uuid={uuid} reason={reason}");
            }
        }
        operationMessage = missingEntries.Count == 0
            ? $"Loaded {localized} / {uuids.Count} REAL"
            : $"Loaded {localized} / {uuids.Count}; aligning {missingEntries.Count} missing markers...";
        RefreshLockedReferences();
        if (missingEntries.Count > 0 && set.HasOffsetCapture)
        {
            var approximated = SpawnApproximatedMarkers(set);
            if (approximated > 0)
            {
                operationMessage =
                    $"READY {setId}: {localized} REAL + {approximated} APPROX = {localized + approximated}/{uuids.Count}";
            }
            else
            {
                operationMessage = $"LOAD FAILED: 0 localized reference anchors for {setId}";
            }
        }
        else if (missingEntries.Count > 0)
        {
            operationMessage = $"LOAD FAILED: no reference layout in fixed manifest";
        }
        RefreshHud();
        Debug.Log(
            $"[AAG Manual Set] Load complete set={setId} loaded={localized}/{uuids.Count} " +
            $"approximated={approximatedMarkers.Count} offsetCapture={set.HasOffsetCapture}");
    }

    /// <summary>
    /// Spawns non-anchored marker objects for entries that failed to localize,
    /// positioned relative to the nearest successfully localized anchor using
    /// the offsets captured while the set was last 12/12 in one session.
    /// </summary>
    private int SpawnApproximatedMarkers(AagManualAnchorSetRecord set)
    {
        var references = new List<(AagManualAnchorEntry entry, Transform transform)>();
        foreach (var entry in set.anchors)
        {
            if (Guid.TryParse(entry.anchor_uuid, out var uuid)
                && anchorLoader.TryGetLocalizedAnchor(uuid, out var anchor)
                && !anchorLoader.LocalizationFailuresReadOnly.ContainsKey(uuid))
                references.Add((entry, anchor.transform));
        }
        if (references.Count == 0)
        {
            Debug.LogWarning($"[AAG Approx] No localized reference anchors set={set.set_id}; approximation skipped");
            return 0;
        }

        var spawned = 0;
        foreach (var failedEntry in missingEntries)
        {
            var capturedPosition = failedEntry.CapturedPosition;
            var reference = references
                .OrderBy(candidate => (candidate.entry.CapturedPosition - capturedPosition).sqrMagnitude)
                .First();

            // Transform the captured pose from the capture-session frame into the
            // current session frame via the reference anchor's pose delta.
            var deltaRotation = reference.transform.rotation * Quaternion.Inverse(reference.entry.CapturedRotation);
            var position = reference.transform.position
                + deltaRotation * (capturedPosition - reference.entry.CapturedPosition);
            var rotation = deltaRotation * failedEntry.CapturedRotation;

            var marker = InstantiateMarkerWithoutAnchor(
                position,
                rotation,
                $"AAG Approx {failedEntry.marker_id}",
                failedEntry.color);
            if (marker == null) continue;
            approximatedMarkers.Add(marker);
            spawned++;
            Debug.Log(
                $"[AAG Approx] Spawned marker={failedEntry.marker_id} uuid={failedEntry.anchor_uuid} " +
                $"ref={reference.entry.marker_id} refDistance={(reference.entry.CapturedPosition - capturedPosition).magnitude:F2}m " +
                $"captureUtc={set.offsets_captured_utc}");
        }
        return spawned;
    }

    private GameObject InstantiateMarkerWithoutAnchor(
        Vector3 position,
        Quaternion rotation,
        string name,
        string color)
    {
        var prefab = anchorManager != null ? anchorManager.GetAnchorPrefabForColor(color) : null;
        if (prefab == null) return null;

        // Instantiate inactive so the OVRSpatialAnchor component can be removed
        // before Awake/OnEnable would create a brand-new spatial anchor.
        var prefabObject = prefab.gameObject;
        var prefabWasActive = prefabObject.activeSelf;
        GameObject instance;
        try
        {
            prefabObject.SetActive(false);
            instance = Instantiate(prefabObject, position, rotation);
        }
        finally
        {
            prefabObject.SetActive(prefabWasActive);
        }

        var anchorComponent = instance.GetComponent<OVRSpatialAnchor>();
        if (anchorComponent != null) DestroyImmediate(anchorComponent);
        instance.name = name;
        instance.SetActive(true);
        return instance;
    }

    private void ClearApproximatedMarkers()
    {
        foreach (var marker in approximatedMarkers)
            if (marker != null) Destroy(marker);
        approximatedMarkers.Clear();
    }

    private AagManualAnchorSetRecord GetSet(string setId)
    {
        return manifest.sets.First(value => string.Equals(value.set_id, setId, StringComparison.Ordinal));
    }

    private static int CountColor(AagManualAnchorSetRecord set, string color)
    {
        return set.anchors.Count(entry => string.Equals(entry.color, color, StringComparison.Ordinal));
    }

    private static Guid SafeUuid(OVRSpatialAnchor anchor)
    {
        try { return anchor != null ? anchor.Uuid : Guid.Empty; }
        catch (Exception) { return Guid.Empty; }
    }

    private void RefreshHud()
    {
        if (statusText == null) return;
        if (incidentalMode)
        {
            incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
            var incidentalSet = AagIncidentalAnchorStore.GetSet(incidentalManifest, activeSetId);
            var catalogCount = AagIncidentalAnchorStore.LoadPrefabCatalog(activeSetId).Length;
            var next = CanAcceptIncidental(activeSetId, out var nextPrefab, out _, out var nextReason)
                ? nextPrefab.name
                : catalogCount == 0 ? "NO PREFABS" : "COMPLETE";
            statusText.text = "FP1 INCIDENTAL OBJECT AUTHORING\n"
                + $"SET: {activeSetId}   SAVED: {incidentalSet?.objects?.Count ?? 0}/{catalogCount}   NEXT: {next}\n"
                + $"{operationMessage}\n"
                + "Right Trigger = CREATE at controller   Right A = SAVE\n"
                + (catalogCount == 0
                    ? $"Add prefabs to Resources/{AagIncidentalAnchorStore.ResourceFolderForSet(activeSetId)}"
                    : $"UUID store is separate. Missing UUID = APPROX. {nextReason}");
            return;
        }
        if (fixedTowerMode)
        {
            fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
            statusText.text = "FP1 FIXED TOWER AUTHORING\n"
                + $"TOWERS: {FixedTowerCount}/{AagFixedTowerAnchorStore.RequiredTowerCount}   NEXT: {NextTowerId} ({NextTowerColor.ToUpperInvariant()})\n"
                + $"{operationMessage}\n"
                + "Right Trigger = CREATE at controller   Right A = SAVE\n"
                + "Four UUIDs are shared by S1, S2 and S3.";
            return;
        }
        var set = GetSet(activeSetId);
        statusText.text = $"FP1 OBJECT LOADER\nACTIVE SET: {activeSetId}\n"
            + $"OBJECTS: {set.anchors.Count}/12\n{operationMessage}\n"
            + "REAL = Quest anchor   APPROX = 013758 reference layout\n"
            + "Select a set, then press LOAD ACTIVE SET.";
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

        statusText = CreateText(rect, "Status", new Vector2(0.03f, 0.42f), new Vector2(0.97f, 0.98f), 32f);
        var setLabels = new[] { "FP1-S1", "FP1-S2", "FP1-S3", "NEXT SET" };
        for (var i = 0; i < setLabels.Length; i++)
        {
            var captured = setLabels[i];
            CreateButton(rect, captured, new Vector2(0.03f + i * 0.24f, 0.30f), new Vector2(0.25f + i * 0.24f, 0.40f),
                () => { if (captured == "NEXT SET") NextSet(); else SelectSet(captured); }, new Color(0.1f, 0.3f, 0.65f, 0.95f));
        }
        CreateButton(rect, "OBJECT / TOWER / INCIDENTAL", new Vector2(0.03f, 0.17f), new Vector2(0.34f, 0.27f),
            ToggleObjectTowerMode, new Color(0.55f, 0.25f, 0.05f));
        CreateButton(rect, "UNDO LAST", new Vector2(0.36f, 0.17f), new Vector2(0.65f, 0.27f),
            UndoLast, new Color(0.6f, 0.12f, 0.12f));
        CreateButton(rect, "LOAD / VERIFY", new Vector2(0.67f, 0.17f), new Vector2(0.97f, 0.27f),
            ReloadActiveSelection, new Color(0.1f, 0.5f, 0.25f));
        CreateButton(rect, "LOAD ACTIVE", new Vector2(0.03f, 0.04f), new Vector2(0.72f, 0.15f), ReloadActiveSelection, new Color(0.1f, 0.5f, 0.25f));
        CreateButton(rect, "EXPORT JSON", new Vector2(0.75f, 0.04f), new Vector2(0.97f, 0.15f), ExportSets, new Color(0.05f, 0.5f, 0.5f));
    }

    /// <summary>
    /// Erases every historical anchor UUID recorded by this app except the 36
    /// object anchors, four fixed tower anchors, and TEST-3 anchors. Days of testing left hundreds of
    /// dead anchors in Quest local storage; they compete with the final 36 in the
    /// device's limited anchor-discovery budget and cause random load failures.
    /// </summary>
    private void EraseJunkAnchors()
    {
        if (anchorManager == null)
        {
            operationMessage = "ERASE JUNK unavailable";
            RefreshHud();
            return;
        }
        if (anchorLoader != null && anchorLoader.IsReadOnlyLoadInProgress)
        {
            operationMessage = "ERASE JUNK blocked: load in progress";
            RefreshHud();
            return;
        }
        if (incidentalAnchorLoader != null && incidentalAnchorLoader.IsLoading)
        {
            operationMessage = "ERASE JUNK blocked: incidental load in progress";
            RefreshHud();
            return;
        }
        manifest = AagManualAnchorSetStore.LoadOrCreate();
        var keep = new HashSet<Guid>();
        foreach (var entry in manifest.sets.SelectMany(set => set.anchors))
        {
            if (Guid.TryParse(entry.anchor_uuid, out var uuid) && uuid != Guid.Empty) keep.Add(uuid);
        }
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        foreach (var entry in fixedTowerManifest.towers)
        {
            if (Guid.TryParse(entry.anchor_uuid, out var uuid) && uuid != Guid.Empty) keep.Add(uuid);
        }
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        foreach (var entry in incidentalManifest.sets.SelectMany(set => set.objects))
        {
            if (Guid.TryParse(entry.anchor_uuid, out var uuid) && uuid != Guid.Empty) keep.Add(uuid);
        }
        if (tutorialTest != null)
        {
            foreach (var uuid in tutorialTest.StoredUuids) keep.Add(uuid);
        }

        operationMessage = $"ERASE JUNK starting (keeping {keep.Count})...";
        RefreshHud();
        anchorManager.EraseJunkAnchorsAsync(keep, status =>
        {
            operationMessage = status;
            RefreshHud();
        });
    }

    private void SaveTutorialTestAnchor()
    {
        if (tutorialTest == null)
        {
            operationMessage = "TEST-3 unavailable";
            RefreshHud();
            return;
        }
        setLoadVersion++;
        missingEntries.Clear();
        ClearApproximatedMarkers();
        anchorLoader?.ClearLoadedAnchors();
        tutorialTest.CreateAndSaveNextAnchor();
    }

    private void LoadTutorialTestAnchors()
    {
        if (tutorialTest == null)
        {
            operationMessage = "TEST-3 unavailable";
            RefreshHud();
            return;
        }
        setLoadVersion++;
        missingEntries.Clear();
        ClearApproximatedMarkers();
        anchorLoader?.ClearLoadedAnchors();
        tutorialTest.LoadStoredAnchors();
    }

    private void ResetTutorialTestList()
    {
        tutorialTest?.ResetTestList();
    }

    private void OnTutorialTestStatusChanged(string _)
    {
        RefreshHud();
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
        foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (candidate == null || !candidate.scene.IsValid()
                || !candidate.name.StartsWith("AAG Locked Reference ", StringComparison.Ordinal)) continue;
            Destroy(candidate);
        }
        RefreshManagedAnchorVisibility();
    }

    private void RefreshManagedAnchorVisibility()
    {
        if (manifest == null) return;
        var managedUuids = new HashSet<Guid>();
        var activeUuids = new HashSet<Guid>();
        foreach (var set in manifest.sets)
        {
            foreach (var entry in set.anchors)
            {
                if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
                managedUuids.Add(uuid);
                if (!fixedTowerMode && !incidentalMode
                    && string.Equals(set.set_id, activeSetId, StringComparison.Ordinal))
                    activeUuids.Add(uuid);
            }
        }
        fixedTowerManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        foreach (var entry in fixedTowerManifest.towers)
        {
            if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
            managedUuids.Add(uuid);
            if (fixedTowerMode) activeUuids.Add(uuid);
        }
        incidentalManifest = AagIncidentalAnchorStore.LoadOrCreate();
        foreach (var set in incidentalManifest.sets)
        {
            foreach (var entry in set.objects)
            {
                if (!Guid.TryParse(entry.anchor_uuid, out var uuid)) continue;
                managedUuids.Add(uuid);
                if (incidentalMode && string.Equals(set.set_id, activeSetId, StringComparison.Ordinal))
                    activeUuids.Add(uuid);
            }
        }

        foreach (var anchor in Resources.FindObjectsOfTypeAll<OVRSpatialAnchor>())
        {
            if (anchor == null || !anchor.gameObject.scene.IsValid()) continue;
            Guid uuid;
            try { uuid = anchor.Uuid; }
            catch (Exception) { continue; }
            if (!managedUuids.Contains(uuid)) continue;
            var visible = activeUuids.Contains(uuid);
            foreach (var renderer in anchor.GetComponentsInChildren<Renderer>(true)) renderer.enabled = visible;
            foreach (var canvas in anchor.GetComponentsInChildren<Canvas>(true)) canvas.enabled = visible;
            foreach (var collider in anchor.GetComponentsInChildren<Collider>(true)) collider.enabled = visible;
        }
    }
}
