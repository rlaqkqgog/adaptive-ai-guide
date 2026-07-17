# FP1 Core Object Loader

The runtime path is intentionally small:

1. Read one fixed manifest:
   `Application.persistentDataPath/AagManualAnchorSets/fp1_manual_anchor_sets.json`
2. Query the 12 UUIDs for the selected set once.
3. Spawn every successfully localized anchor as `REAL`.
4. Align the 013758 reference layout through the nearest `REAL` anchor and spawn every missing marker as `APPROX`.

There is no progressive retry window and no required repair workflow.

## Authoritative data

- UUID source: `fp1_manual_anchor_sets_20260716_092839.json`
- Reference pose source: `fp1_manual_anchor_sets_20260716_013758.json`
- Combined runtime file: `QuestAnchorBackup/fp1_manual_anchor_sets_CORE.json`

The 013758 poses are never used as absolute Unity world coordinates. For a missing marker, the loader applies the pose delta between a localized reference anchor's captured pose and its current pose.

## Quest controls

1. Select `FP1-S1`, `FP1-S2`, or `FP1-S3`.
2. Press `LOAD ACTIVE SET` once.
3. Read the result:
   - `12 REAL`: all Quest anchors localized.
   - `n REAL + m APPROX = 12/12`: missing anchors were restored from the reference layout.
   - `LOAD FAILED: 0 localized reference anchors`: the set cannot be aligned because no UUID localized.
4. Use `EXPORT JSON` to back up the combined manifest.

Do not uninstall the app or clear app data before exporting the JSON. The CSV export does not contain the captured reference-pose fields.

## Saving the four shared stone pagodas

Build the `AnchorSpawn` scene, then use the in-headset panel:

1. Press `OBJECT / TOWER / INCIDENTAL` and confirm `FP1 FIXED TOWER AUTHORING` is shown.
2. At the base position of Tower-1, aim the right controller in the desired forward direction.
3. Press the right index trigger to create the anchor, then right A to save it.
4. Repeat in color order: Tower-1 RED, Tower-2 BLUE, Tower-3 YELLOW, Tower-4 GREEN.
5. At `TOWERS: 4/4`, press `LOAD / VERIFY`. It uses the dedicated tower-only loader to query exactly those four UUIDs from Quest storage and show `Stonepagoda.fbx` at all four positions.
6. Press `EXPORT JSON` while still in tower mode to back up the tower JSON and CSV.

Tower rotation is automatically leveled to world-up; controller yaw sets the tower's forward direction.
The authoritative shared file is
`Application.persistentDataPath/AagManualAnchorSets/fp1_fixed_tower_anchors.json`.
It is independent from S1/S2/S3, and the experiment loads these same four UUIDs for every set.
Each pagoda shows a billboard label such as `red 0/3`. Only matching-color stones increment the label; a wrong-color drop is rejected and logged as `wrong_tower`.

The tower file stores each Tower ID/UUID and its capture pose. If Quest cannot return a UUID, the participant build places that tower with this pose as `APPROX`, just like a missing ordinary stone. Use `UNDO LAST` in tower mode to remove the latest entry. If an old UUID is no longer queryable by Quest, it is still removed so the tower can be created again.
Install later builds as an update. Uninstalling the app or clearing app data removes both the JSON and Quest-local anchor ownership.

## Saving session-specific incidental objects

Incidental objects use a third, independent UUID store and loader. They never enter
the stone PlayerPrefs list or the fixed-tower JSON.

1. Put each session's prefabs in `Assets/Resources/IncidentalObjects/FP1-S1`,
   `FP1-S2`, or `FP1-S3`. Prefix names with `01_`, `02_`, etc. to fix the
   placement order. Use one uniquely named prefab asset per placed instance.
2. In `AnchorSpawn`, press the mode button until `FP1 INCIDENTAL OBJECT AUTHORING`
   appears, then select S1, S2, or S3.
3. For the displayed `NEXT` prefab, point the right controller at its intended
   origin, press Right Trigger to create, then Right A to save immediately.
4. `LOAD / VERIFY` reloads only that session's incidental UUIDs. A UUID not
   returned by Quest is spawned as `APPROX` from its stored fallback layout.
5. `EXPORT JSON` makes a PC-backup copy. Saving does not depend on Export.

Runtime incidental objects are visual-only: colliders, grabbing, gravity, and
experiment-object interaction are disabled. Each participant session now requires
exactly five ordered wrappers and five saved incidental UUIDs; an incomplete set is
blocked with an `incidental_set_requires_5...` startup message.

## Changing marker prefabs later

The `SpatialAnchorManager` object in `AnchorSpawn` has four independent Inspector slots under
`Marker Prefabs by Manifest Color`: Red, Blue, Green, and Yellow. Replace a slot with any prefab
that contains an `OVRSpatialAnchor`; no loader code or JSON edit is required. Both `REAL` and
`APPROX` markers use the same color slot. An empty slot safely falls back to `Anchor Prefab`.

Current editable prefabs:

- `Assets/Prefab/AnchorPrefabAnchor_Red.prefab`
- `Assets/Prefab/AnchorPrefabAnchor_Blue.prefab`
- `Assets/Prefab/AnchorPrefabAnchor_Green.prefab`
- `Assets/Prefab/AnchorPrefabAnchor_Yellow.prefab`

## Files

- Loader: `Assets/Script/AnchorLoader.cs`
- Set UI and REAL/APPROX alignment: `Assets/Script/AagManualAnchorSetAuthoring.cs`
- Manifest schema and fixed path: `Assets/Script/AagManualAnchorSetManifest.cs`
- Shared tower store: `Assets/Script/AagFixedTowerAnchorStore.cs`
- Dedicated tower-only Quest loader: `Assets/Script/FixedTowerAnchorLoader.cs`
- Editable tower wrapper prefab: `Assets/Resources/Prefabs/StonepagodaTower.prefab`
- Fixed tower runtime spawner: `Assets/Script/FixedTowerManager.cs`
- Incidental UUID store: `Assets/Script/AagIncidentalAnchorStore.cs`
- Dedicated incidental loader: `Assets/Script/IncidentalAnchorLoader.cs`
- Visual-only incidental spawner: `Assets/Script/IncidentalObjectManager.cs`
- Color-to-prefab slots: `Assets/Script/SpatialAnchorManager.cs`
- Android build helper: `Assets/Editor/AagCoreAndroidBuild.cs`
