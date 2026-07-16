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
- Color-to-prefab slots: `Assets/Script/SpatialAnchorManager.cs`
- Android build helper: `Assets/Editor/AagCoreAndroidBuild.cs`
