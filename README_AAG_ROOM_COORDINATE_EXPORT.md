# AAG room-coordinate export (Quest / MRUK)

This Unity-only workflow exports the room data that MRUK loads from the Quest's existing Space Setup scan. The separate FP1 authoring component can create and confirm FP1-S1 through FP1-S3 placements from those live MRUK polygons. It does **not** create FP2 data, web-plan coordinates, scoring, or participant data.

## What is exported

`AagRoomCoordinateExporter` waits for `MRUK.Instance.Rooms` on the device, then writes one JSON file and one CSV file. It records:

- MRUK room UUID (`MRUKRoom.Anchor.Uuid`), Unity room object name, and an optional researcher mapping name.
- Room-anchor world pose and a room `centerWorld`. The center is the AABB center of all floor-boundary vertices when they exist; otherwise it is the room-anchor position. `centerMethod` states which value was used.
- Every MRUK anchor's UUID, semantic label, world pose, plane rectangle (when supplied), and plane boundary vertices.
- `planeBoundaryLocal` (the MRUK anchor's local XY vertices) and `planeBoundaryWorld` (`TransformPoint(localX, localY, 0)` in the Unity world frame).
- Floor-anchor IDs and validation warnings for missing room IDs, floor anchors, or floor boundaries.
- Whether the existing `anchor_log.json`, `track_log.json`, and `scene_log.txt` files exist. Those files are read only for their presence and are never overwritten.

The JSON includes the coordinate convention used by this export:

- Unity world frame at the instant MRUK was read.
- `x` is right, `y` is up, and `z` is forward in Unity's convention.
- Quest/MRUK scene geometry is meter-scale; this exporter records Unity units and treats one Unity unit as one meter.

## Add the exporter to the scene

This project already attaches `AagRoomCoordinateExporter` to the `SpatialAnchorManager` GameObject in `Assets/Scenes/AnchorSpawn.unity`. It runs once automatically after MRUK reports at least one room. Existing `SceneJsonExporter`, `SpatialAnchorManager`, `AnchorLoader`, and `TrackLogger` remain separate and unchanged.

To re-export while the app is running, invoke the component's public `ExportNow()` from an Inspector UnityEvent or restart the app. It deliberately does not add a controller-button shortcut because the existing scene already uses Quest buttons for anchor and trajectory controls.

## Startup diagnostics

The `SpatialAnchorManager` GameObject is active in `AnchorSpawn`, and its attached exporter now reports every startup stage both on screen and to Android logcat with the `[AAG Room Export]` prefix:

- `Exporter Awake` and `Exporter started` confirm the APK launched the expected scene and enabled component.
- `Waiting for MRUK` reports whether `MRUK.Instance` is null and the current room count, then repeats at most every two seconds while waiting.
- `No MRUK instance` or `No rooms` records the exact timeout condition; no output files are expected in these states.
- `Export succeeded` or `Export failed` reports the terminal result.

For a focused device log after launching the app:

```powershell
adb logcat -s Unity | Select-String "AAG Room Export"
```

## Quest build and run

1. In Meta Quest Developer Hub, enable Developer Mode on the Quest 3 and connect it by USB or Wi-Fi.
2. On the headset, complete **Space Setup** for the physical room. Do not use an Editor simulation as a substitute for this capture.
3. Open `Assets/Scenes/AnchorSpawn.unity` in Unity 6.0.0.77f1. Confirm the Android/Quest target and the existing Meta XR project configuration are selected.
4. Enable **Development Build**, then build an APK with **File > Build Settings > Android > Build And Run**. The build must include `AnchorSpawn` (the scene already contains MRUK, the exporter, FP1 validation, and FP1 authoring).
5. Grant the app's requested spatial/scene permission on the headset. Start the app in the scanned room and wait up to 20 seconds. Successful export paths are printed in the Unity/Android log with the `[AAG Room Export]` prefix.

The new files are written to:

```text
<Application.persistentDataPath>/AagRoomExports/
  aag_room_coordinates_YYYYMMDD_HHMMSS_mmm.json
  aag_room_coordinates_YYYYMMDD_HHMMSS_mmm.csv
```

For a standard Android build, copy the files with ADB (the exact package ID can be confirmed in Android Player Settings):

```powershell
adb shell run-as <your.application.identifier> ls files/AagRoomExports
adb exec-out run-as <your.application.identifier> cat files/AagRoomExports/aag_room_coordinates_YYYYMMDD_HHMMSS_mmm.json > room-export.json
adb exec-out run-as <your.application.identifier> cat files/AagRoomExports/aag_room_coordinates_YYYYMMDD_HHMMSS_mmm.csv > room-export.csv
```

If `run-as` is unavailable for the deployed build, use Meta Quest Developer Hub's device file browser or `adb pull` from the app's externally accessible persistent-data location. Do not assume a fixed Android path: inspect the `[AAG Room Export]` log line first.

## FP1 placement authoring (PROVISIONAL)

`AagFp1PlacementAuthoring` is attached to the `SpatialAnchorManager` GameObject in `AnchorSpawn`. It stays disabled unless `AagExperimentSpaceValidator` first validates all eight allowed FP1 room UUIDs and the one excluded test-room UUID.

The authoring component uses only live MRUK floor polygons from allowed FP1 rooms. It builds a deterministic geometry-aware candidate pool; it does not retry 1,000 random points. Hard constraints reject candidates outside the floor/object bounds, inside wall or doorway clearances, inside MRUK volume-obstacle clearances, too close to another marker, or undiscoverable from every valid observation point. It never generates FP2 rooms, boundaries, placements, `plan_x`, or `plan_y`.

All values under these Inspector headers are initial preview values and are explicitly **PROVISIONAL**:

- `PROVISIONAL clearance settings`: marker-bounds-edge wall distance, marker-surface distance, floor gap/alignment tolerance, marker-edge MRUK volume-obstacle distance, generation safety margin, and validation epsilon.
- `PROVISIONAL peripheral / corner placement`: corner target range, valid-corner geometry, doorway hard clearance, and walking-path/angular soft targets.
- `PROVISIONAL geometry-aware candidate pool`: deterministic grid spacing, preferred wall band, narrow-Hall aspect threshold, and seed variation.
- `PROVISIONAL candidate scoring weights`: corner, wall band, doorway distance, walking-path distance, entrance visibility, co-visibility, object distance, same-wall, and zone weights.
- `PROVISIONAL room / zone distribution`: balanced or area-weighted room allocation, minimum rooms, zone grid, and maximum markers per zone.
- `PROVISIONAL difficulty comparison`: which distance metric is highlighted when comparing sets.
- `PROVISIONAL visibility / discovery validation`: actual HMD-camera frustum limit, required discoverable observation count, eye height, entrance inset, and deterministic walking-position grid.
- `PROVISIONAL preview appearance`: temporary marker diameter.

Every generation logs the settings and the applied result with `[AAG Authoring]`, including room counts, minimum wall/object/obstacle distances, corner distance, discoverable observation count, maximum same-room objects in one view, mean nearest-neighbor distance, mean pairwise distance, and maximum pairwise distance.

### Hard constraints, soft preferences, and candidate pools

- The temporary marker is a sphere, so its renderer-bottom offset is exactly half of `previewMarkerDiameterMeters`. Its center Y is `floorY + markerBottomOffset + floorGapMeters`; validation rejects floating or embedded markers outside `floorAlignmentToleranceMeters`.
- Every horizontal clearance uses one definition and one shared measurement function: **marker bounds edge to the MRUK floor boundary, doorway footprint, or obstacle bounds in horizontal XZ meters**. It is not center-to-wall distance. `Infinity` for an obstacle means no nearby MRUK volume obstacle and passes normally.
- Candidate generation targets `minimumWallDistanceMeters + generationSafetyMarginMeters`. Final validation does not lower the requirement; it fails only when `measuredClearance + validationEpsilonMeters < requiredClearance`. The epsilon is only for floating-point calculation error.
- Each floor gets a deterministic grid plus analytically inward-offset corner and long-wall candidates. Convex corner offsets include marker radius and hard wall clearance; long walls receive inward-offset wall-band candidates. Short/concave doorway notches are not promoted to corners.
- Hard constraints are floor/object bounds, minimum wall clearance, doorway exclusion, MRUK volume-obstacle clearance, object-to-object distance, allowed FP1 UUID membership, and discovery from at least one configured valid observation point.
- Corner proximity/range, preferred wall band, the floor polygon's central walking axis, `maxVisibleObjectsPerView`, entrance visibility, angular separation, same-wall reuse, room/zone distribution, and extra object distance are soft score terms. Missing a soft target produces a warning but does not suppress an otherwise hard-valid 12-marker draft. `BalancedAcrossAllRooms` prefers all eight rooms; a room with zero safe candidates is reported and skipped rather than blocking the whole draft.
- Rooms with length/width at or above `narrowHallAspectRatioThreshold` use the narrow-Hall profile: corner weight is reduced and long-wall-band weight is increased. The central walking axis remains a penalty only.
- Observation positions are built deterministically from room centers, points immediately inside `DOOR_FRAME` anchors, and valid points in a walking-position grid. Every marker must pass MRUK line-of-sight from at least `minimumDiscoverableObservationCount` positions.
- The component reads the runtime HMD Camera projection matrix. Co-visibility above `maxVisibleObjectsPerView` lowers the score and is reported as a soft warning; it is not a hard rejection.
- Before selecting a set, `[AAG Candidate Pool]` logs each room's initial, floor, wall, doorway, obstacle, discoverable, and object-distance-precheck valid counts, plus width, aspect ratio, wall-band area, and room strategy. A zero room pool is reported once with a concrete adjustment and removed from soft room distribution; generation stops without retry only when the combined hard-valid feasibility scan cannot find a 12-marker packing. No hard setting is silently relaxed.
- `[AAG Clearance]` logs every final marker with at least six decimal places, required wall clearance, generation margin, validation epsilon, marker radius/diameter, and the distance definition. If one marker fails hard revalidation, `[AAG Marker Repair]` replaces only that marker from the existing hard-valid pool and revalidates the full set. `[AAG Clearance Report]` reports each set and all available FP1 sets with minimum actual wall clearance and failure count.
- With the component selected during Play mode, Unity Scene Gizmos show valid candidates in green, floor/wall failures in red, doorway failures in orange, obstacle failures in purple, and walking-path-penalized hard-valid candidates in yellow. Floor boundaries, doorway buffers, obstacle bounds, and walking axes are also drawn.

Each set has a distinct serialized seed. With the same MRUK scan and Inspector settings, explicit regeneration is deterministic. A confirmed set cannot be regenerated or overwritten in the app; it is loaded and spawned from the saved coordinates on subsequent launches.

### Quest controls

Only one set is visible at a time. Nothing is generated automatically.

| Action | Quest left controller | Editor keyboard |
|---|---|---|
| Select next set | Index trigger | `F1` |
| Deterministically regenerate selected, unconfirmed set | Grip | `F2` |
| Confirm and save the displayed preview | Thumbstick click | `F3` |

Temporary sphere markers are colored red, blue, green, or yellow and carry a world-space label containing the set ID, color, and color-specific number.
When Grip/F2 is detected, the enlarged outlined HUD first shows `LEFT GRIP DETECTED` and `BUILDING ... CANDIDATE POOLS`. Candidate pools are built once, feasibility is checked once, and the highest scoring hard-valid draft is shown. There is no 1,000-attempt rejection loop. Full pool counts and feasibility details remain in `[AAG Candidate Pool]`, `[AAG Feasibility]`, and `[AAG Authoring]` logcat lines.

To capture the six-decimal clearance audit and the FP1-S1 through FP1-S3 minimum/failure report:

```powershell
& $adb logcat -s Unity | Select-String "AAG Clearance|AAG Marker Repair|AAG Clearance Report"
```

### Verify FP1-S1, FP1-S2, and FP1-S3 on Quest

1. Build and run `AnchorSpawn` as an Android Development Build in the current FP1 Space Setup.
2. Wait for `[AAG Space] FP1 validation passed`. Authoring remains disabled if this line is not produced.
3. Confirm the headset HUD shows `PROVISIONAL FP1-S1 [EMPTY]` or the already-confirmed state. Existing confirmed coordinates spawn without regeneration.
4. For an empty set, press the left grip once. Walk through the full space and visually inspect all 12 markers. Confirm each sphere bottom is at the configured floor gap, no marker blocks a doorway/walking path, and same-room markers occupy separated corners/directions.
5. Check the HUD and `[AAG Authoring]` log for room counts, minimum wall/object/obstacle/corner distances, discoverable observation counts, and `maxVisibleInOneView`. Physically check from the room entrance, room center, and normal walking positions that exploration reveals every marker without exposing all same-room markers in one view.
6. If accepted, click the left thumbstick once to confirm. The set becomes immutable in-app and is saved immediately.
7. Press the left index trigger to select FP1-S2 and repeat steps 4-6, then repeat for FP1-S3.
8. After the third confirmation, require the log `FINAL FP1 export verified: JSON/CSV rows=36`. Do not accept a partial row count as the final authoring result.
9. Restart the app. Cycle through all three sets and verify that each confirmed set spawns without regeneration. At each confirmation, require the `JSON/CSV save-and-reload verification passed with identical coordinates/content` log; after restart, require `Loaded confirmed placements without regeneration`.

The aggregate files are written separately from the raw MRUK exports:

```text
<Application.persistentDataPath>/AagFp1Placements/
  fp1_confirmed_placements.json
  fp1_confirmed_placements.csv
```

Both formats use the fields below. No plan-coordinate fields are emitted.

```text
floor_plan_id,set_id,answer_marker_id,color,room_uuid,world_x,world_y,world_z,seed,corner_distance,wall_distance,nearest_object_distance,discoverable_observation_count,set_max_visible_objects_per_view
```

The last five columns are validation measurements. Distances are horizontal meters; wall and nearest-object values are clear distances from the temporary marker surface. `set_max_visible_objects_per_view` is repeated on each row so the flat CSV still remains exactly 36 data rows.

### Copy confirmed placements to the PC

For this project's current Android application identifier:

```powershell
$adb = "C:\Program Files\Unity\Hub\Editor\6000.0.77f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
& $adb devices -l
& $adb pull "/sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/AagFp1Placements" ".\AagFp1Placements"
```

Verify the final CSV row count after pulling it. The result must be 36 data rows:

```powershell
$rows = Import-Csv ".\AagFp1Placements\fp1_confirmed_placements.csv"
$rows.Count
$rows | Group-Object set_id | Select-Object Name,Count
$rows | Group-Object color | Select-Object Name,Count
```

Expected totals are 12 rows per set and 9 rows per color across all three sets. Keep these confirmed files and the original `AagRoomExports` files unchanged as separate source artifacts.

## Optional exporter room mapping

MRUK may expose a technical room object name rather than a researcher-friendly name. The exporter retains its optional `manualRoomMappings` field for compatibility, but FP1 authoring does not use it. FP1 membership is determined only from the UUID catalog.

No room-name mapping is required or pre-filled for FP1.

## Example shape (illustrative field names only)

The following is a schema sketch, not a real room or coordinate sample:

```json
{
  "schemaVersion": "aag-room-coordinate-export/v1",
  "coordinateSystem": {
    "frame": "Unity world coordinates read from MRUK transforms at export time",
    "automaticAlignment": "not performed"
  },
  "rooms": [
    {
      "roomId": "<MRUK room UUID>",
      "manualMapping": "<optional researcher label>",
      "centerWorld": { "x": "<float>", "y": "<float>", "z": "<float>" },
      "surfaces": [
        {
          "label": "FLOOR",
          "planeBoundaryWorld": [{ "x": "<float>", "y": "<float>", "z": "<float>" }]
        }
      ]
    }
  ]
}
```

The CSV has one `room_center` row per room and one `plane_boundary_vertex` row for every plane boundary vertex. Its columns are:

```text
record_type,room_id,room_name,manual_mapping,center_method,anchor_id,anchor_label,vertex_index,world_x,world_y,world_z,local_x,local_y
```

## What still needs researcher decisions

This export makes the following data available automatically: current MRUK room/floor IDs, semantic labels, room-anchor pose, floor and other plane boundaries, world-space vertices, and diagnostic warnings.

The research team must still determine all of the following after reviewing a real export:

- The final research values for FP1 wall, object, obstacle, height, distribution, zone, and difficulty settings. Current Inspector defaults are provisional.
- The FP2 scan, allowed/excluded UUIDs, and its three placement sets. No FP2 values are fabricated by this project.
- The final 36 FP1 coordinates after reviewing and explicitly confirming FP1-S1 through FP1-S3 on Quest.
- The transformation from MRUK/Unity world coordinates to future web `plan_x` / `plan_y` coordinates.
- Acceptance criteria for a rescan, changed room geometry, or missing/changed room and floor UUIDs.

## Coordinate-origin safety

The export intentionally does not align, transform, or merge coordinate systems. A room UUID and floor-anchor UUID/pose are recorded as references, but a fresh Space Setup, changed scan, another device, or a different localization can produce a different world origin or different anchors.

If existing `anchor_log.json` marker/object coordinates or `track_log.json` trajectory coordinates do not line up with a new room export, do **not** offset them by eye. First collect both datasets in one localization session, compare the exported room/floor IDs and poses, and create a documented transform only after placing and re-localizing one or more deliberate shared reference anchors. Keep the raw exports unchanged alongside any later derived transform.
