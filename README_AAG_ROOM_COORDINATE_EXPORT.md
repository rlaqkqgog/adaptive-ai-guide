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

The authoring component uses only live MRUK floor polygons from allowed FP1 rooms. It builds a deterministic geometry-aware candidate pool; it does not retry 1,000 random points. Hard constraints reject candidates outside the floor/object bounds, inside wall or doorway clearances, inside MRUK volume-obstacle clearances, too close to another marker, or undiscoverable from every valid observation point. It never generates FP2 rooms, boundaries, or placements, and it never fabricates `plan_x` / `plan_y` values.

All values under these Inspector headers are initial preview values and are explicitly **PROVISIONAL**:

- `PROVISIONAL clearance settings`: marker-bounds-edge wall distance, marker-surface distance, floor gap/alignment tolerance, marker-edge MRUK volume-obstacle distance, generation safety margin, and validation epsilon.
- `PROVISIONAL peripheral / corner placement`: corner target range, valid-corner geometry, doorway hard clearance, and walking-path/angular soft targets.
- `PROVISIONAL geometry-aware candidate pool`: deterministic grid spacing, preferred wall band, narrow-Hall aspect threshold, and seed variation.
- `PROVISIONAL candidate scoring weights`: corner, wall band, doorway distance, walking-path distance, entrance visibility, co-visibility, object distance, same-wall, and zone weights.
- `PROVISIONAL room / zone distribution`: exact eight-room quota, local zone grid, automatic/overridden physical zone groups, and maximum markers per zone group.
- `PROVISIONAL placement diversity / post optimization`: placement-type diversity, easy-discoverability penalty, and deterministic candidate-replacement passes.
- `PROVISIONAL difficulty comparison`: which distance metric is highlighted when comparing sets.
- `PROVISIONAL visibility / discovery validation`: actual HMD-camera frustum limit, required discoverable observation count, eye height, entrance inset, and deterministic walking-position grid.
- `PROVISIONAL preview appearance`: temporary marker diameter.
- `PROVISIONAL candidate bank / triplet search`: per-set top-N bank size and the S3 quality-drop warning ratio.
- `PROVISIONAL simplified cross-set diversity gate`: cross-set position exclusion, corner/wall slot reuse cap, and same-color similar-position exclusion.
- `PROVISIONAL triplet equivalence tolerances`: navigable route, representative dispersion, room/zone distribution, placement-type ratio, entrance exposure, discoverability, and maximum-FOV differences.
- `Optional navigable route metric`: a calibrated experiment start transform and NavMesh sampling radius. Leave the transform unassigned until both the start and connected NavMesh are trustworthy.

Every generation logs the settings and the applied result with `[AAG Authoring]`, including room counts, minimum wall/object/obstacle distances, corner distance, discoverable observation count, maximum same-room objects in one view, mean nearest-neighbor distance, mean pairwise distance, and maximum pairwise distance.

### Hard constraints, soft preferences, and candidate pools

- The temporary marker is a sphere, so its renderer-bottom offset is exactly half of `previewMarkerDiameterMeters`. Its center Y is `floorY + markerBottomOffset + floorGapMeters`; validation rejects floating or embedded markers outside `floorAlignmentToleranceMeters`.
- Every horizontal clearance uses one definition and one shared measurement function: **marker bounds edge to the MRUK floor boundary, doorway footprint, or obstacle bounds in horizontal XZ meters**. It is not center-to-wall distance. `Infinity` for an obstacle means no nearby MRUK volume obstacle and passes normally.
- Candidate generation targets `minimumWallDistanceMeters + generationSafetyMarginMeters`. Final validation does not lower the requirement; it fails only when `measuredClearance + validationEpsilonMeters < requiredClearance`. The epsilon is only for floating-point calculation error.
- Each floor gets a deterministic grid plus analytically inward-offset corner and long-wall candidates. Convex corner offsets include marker radius and hard wall clearance; long walls receive inward-offset wall-band candidates. Short/concave doorway notches are not promoted to corners.
- Hard constraints are floor/object bounds, minimum wall clearance, doorway exclusion, MRUK volume-obstacle clearance, object-to-object distance, allowed FP1 UUID membership, and discovery from at least one configured valid observation point.
- Every set has an exact room quota: all eight allowed UUIDs receive one marker, and four rotating rooms receive one additional marker. Therefore every set has exactly four rooms with two markers and four rooms with one. A zero-candidate room now makes the set infeasible; it is never silently skipped.
- Floor interiors that overlap across UUIDs are assigned deterministic `ZG-xx` IDs. Optional Inspector `zoneGroupOverrides` handle a field-confirmed physical grouping. A zone group contains at most two markers, so rooms already sharing one physical group are not selected for an additional marker. `[AAG Physical Zone Diagnosis]` distinguishes floor overlap from merely adjacent UUIDs that share a camera view.
- Corner proximity/range, preferred wall band, the floor polygon's central walking axis, entrance visibility, angular separation, same-wall reuse, local zone reuse, placement-type diversity, and extra object distance are score terms. At most one marker per room may use the `Corner` type; other markers favor long-wall `WallBand` and `PeripheralInterior` candidates.
- Rooms with length/width at or above `narrowHallAspectRatioThreshold` use the narrow-Hall profile: corner weight is reduced and long-wall-band weight is increased. The central walking axis remains a penalty only.
- Observation positions are built deterministically from room centers, points immediately inside `DOOR_FRAME` anchors, and valid points in a walking-position grid. Every marker must pass MRUK line-of-sight from at least `minimumDiscoverableObservationCount` positions.
- The component reads the runtime HMD Camera projection matrix. It audits all room, entrance, center, and walking observations across UUID boundaries. After the initial draft it deterministically replaces candidates to reduce simultaneous visibility, entrance visibility, excessive discoverability, and placement-type concentration. A draft above `maxVisibleObjectsPerView` may still be previewed, but `ready=false` prevents confirmation and logs the exact reason.
- Before selecting a set, `[AAG Candidate Pool]` logs each room's initial, floor, wall, doorway, obstacle, discoverable, and object-distance-precheck valid counts, plus width, aspect ratio, wall-band area, and room strategy. Feasibility verifies both the global 12-marker packing and the required one/two-marker capacity of every room. No hard setting is silently relaxed.
- `[AAG Distribution]` logs UUID and zone-group counts plus corner/wall-band/peripheral counts. `[AAG Visibility Audit]` logs entrance-visible markers, maximum entrance/FOV counts, and cross-UUID co-visible pairs. `[AAG Marker Visibility]` reports every marker's discoverable and entrance observation counts.
- `[AAG Clearance]` logs every final marker with at least six decimal places, required wall clearance, generation margin, validation epsilon, marker radius/diameter, and the distance definition. If one marker fails hard revalidation, `[AAG Marker Repair]` replaces only that marker from the existing hard-valid pool and revalidates the full set. `[AAG Clearance Report]` reports each set and all available FP1 sets with minimum actual wall clearance and failure count.
- With the component selected during Play mode, Unity Scene Gizmos show valid candidates in green, floor/wall failures in red, doorway failures in orange, obstacle failures in purple, and walking-path-penalized hard-valid candidates in yellow. Floor boundaries, doorway buffers, obstacle bounds, and walking axes are also drawn.

Each set has a distinct serialized seed. With the same MRUK scan and Inspector settings, explicit regeneration is deterministic. A confirmed set cannot be regenerated or overwritten in the app; it is loaded and spawned from the saved coordinates on subsequent launches.

### Candidate lifecycle, fingerprints, and triplet readiness

Each generated placement is stored in a bounded per-set candidate bank as `DRAFT`, `READY`, `LOCKED`, `STALE`, or `INVALIDATED`. The bank compares the top-N candidates for S1, S2, and S3 jointly instead of accepting only the first greedy locking sequence. A recommendation must pass each set's existing hard validation, the simplified diversity gate, and the equivalence gate. Among feasible combinations, the lowest quality-score spread is recommended.

`NEXT CANDIDATE` advances through the current set's bank in deterministic seed order. If the displayed candidate is already the bank's last entry, it derives a new unused seed, generates a candidate, retains that new entry in the bounded bank, removes the 12 old preview markers, and spawns the new 12 markers. It is blocked while the current candidate is `LOCKED`; unlock first. Every result logs input/controller/button, set ID, before/after candidate IDs, source (`BANK` or `GENERATED`), removed/spawned counts, and success/failure reason.

`TRIPLET_READY` is separate from individual `READY`: it becomes true only when all three recommended candidates pass the joint gates and all three are explicitly `LOCKED`. A stale, invalidated, draft, or merely ready candidate may be previewed, but it cannot be locked for runtime use. Runtime consumers must require `IsTripletReadyForRuntime == true`.

The only cross-set diversity hard gates are `crossSetExclusionRadiusMeters`, `maximumCrossSetSlotReuse`, and `sameColorCrossSetExclusionRadiusMeters`. Mean nearest-neighbor distance, spatial-cell overlap, overall location similarity, exact minimum assignment (Hungarian-equivalent) distance, and placement-type difference are logged as report-only and never block `READY`.

The equivalence gate uses one Inspector-selected dispersion metric plus room/zone and visibility differences. It also uses minimum navigable route length only when a calibrated `experimentStartPoint` and complete NavMesh paths are available. The current `AnchorSpawn` scene deliberately has no experiment start transform and no baked NavMesh data, so route output is `ROUTE_METRIC_UNAVAILABLE` and report-only; no fake start or straight-line-through-walls value is generated.

Every candidate fingerprint includes its ID/seed, prior locked candidate IDs, prior locked world-coordinate hash, placement-settings hash, FP1 UUID list, latest raw MRUK export identifier, live MRUK geometry hash, and generator version. A mismatch marks it `STALE` and logs what changed. Explicit revalidation (`F5`) recomputes hard validation, metrics, and the fingerprint.

Unlock is a two-step explicit action. On a selected `LOCKED` set, left-thumbstick click (or `F4`) first displays, for example, `Unlocking FP1-S1 will invalidate FP1-S2, FP1-S3 and the current triplet. Continue?`; repeat the same action within the Inspector confirmation window to continue. S1 changes invalidate S2/S3, S2 changes invalidate S3, and every change immediately clears `TRIPLET_READY`. Coordinates and candidate files are retained; only their states are demoted until revalidation. On a `STALE`/`INVALIDATED` set, left-thumbstick click performs revalidation only; click again after it becomes `READY` to lock it.

### Quest controls

Only one set is visible at a time. A set switch loads confirmed coordinates when present, otherwise it creates that target set's reproducible fixed-seed draft. It never changes the placement algorithm or clearance settings during a switch.

| Action | Quest controller / headset UI | Editor keyboard |
|---|---|---|
| Next set (`S1 → S2 → S3 → S1`) | Left or right Grip press start; left index trigger remains a secondary shortcut; `NEXT SET` UI | `F1` |
| Next candidate in current set; generate one at bank end | Head-locked `NEXT CANDIDATE` button, UI ray or hand pointer + index pinch | `F6` |
| Deterministically regenerate selected, unconfirmed set | — | `F2` |
| Lock and save displayed `READY`; revalidate displayed `STALE/INVALIDATED` | Left thumbstick click | `F3` (same context-sensitive action), or `F5` for revalidation only |
| Unlock selected `LOCKED` set (two presses required) | Left thumbstick twice within confirmation window | `F4`, then `F4` again within the confirmation window |

Temporary sphere markers are colored red, blue, green, or yellow and carry a world-space label containing the set ID, color, and color-specific number.
Grip is read directly through Meta `OVRInput` (`LHandTrigger` / `RHandTrigger`) on the press-start edge with an Inspector-configurable debounce, so holding it cannot repeatedly advance sets. The active `SpatialAnchorManager` previously used right Grip to erase all anchors; while FP1 authoring is ready, that erase action is suppressed so right Grip belongs only to set switching. Other anchor controls are unchanged.

Right Thumbstick click was checked first, but it is already assigned to `SpatialAnchorManager.LoadSavedAnchors()`. That binding is preserved. Therefore Quest `NEXT CANDIDATE` uses the separate head-locked button and Editor diagnosis uses `F6`; startup logs `RightThumbstickConflict=SpatialAnchorManager.LoadSavedAnchors (preserved)` and `RightThumbstickRebound=false`.

The headset HUD always displays set ID, full candidate ID, seed, lifecycle state, and `TRIPLET_READY`. Every set or candidate switch resolves the target data, removes the old markers, and spawns the target positions. A failed generation keeps the current candidate visible and logs `success=false reason="..."` instead of failing silently.

For controller-free diagnosis, point either tracked hand at the headset-fixed `NEXT SET` or `NEXT CANDIDATE` button and index-pinch. Each button highlights while targeted and remains a normal Unity UI `Button`, so configured UI rays can click it. There is no 1,000-attempt rejection loop. Full pool counts and feasibility details remain in `[AAG Candidate Pool]`, `[AAG Feasibility]`, and `[AAG Authoring]` logcat lines.

To capture the six-decimal clearance audit and the FP1-S1 through FP1-S3 minimum/failure report:

```powershell
& $adb logcat -s Unity | Select-String "AAG Clearance|AAG Marker Repair|AAG Clearance Report"
```

### Verify FP1-S1, FP1-S2, and FP1-S3 on Quest

1. Build and run `AnchorSpawn` as an Android Development Build in the current FP1 Space Setup.
2. Wait for `[AAG Space] FP1 validation passed`. Authoring remains disabled if this line is not produced.
3. Confirm the headset HUD continuously shows set ID, candidate ID, seed, lifecycle state, and `TRIPLET_READY=true/false`. Existing locked coordinates spawn without regeneration; legacy coordinates without fingerprints appear as `STALE` until explicitly revalidated.
4. If FP1-S1 is empty, use `F2` in an Editor test to explicitly generate it; on Quest, one Grip switch resolves the next set automatically. Walk through the full space and visually inspect all 12 displayed markers. Confirm each sphere bottom is at the configured floor gap, no marker blocks a doorway/walking path, and same-room markers occupy separated corners/directions.
5. Before locking, press `NEXT CANDIDATE` to inspect bank alternatives. Confirm the log reports different before/after candidate IDs and `removed=12 spawned=12`. At the bank end, confirm `source=GENERATED` and a new seed. Check the HUD and `[AAG Authoring]` log for room counts, clearances, discoverability, and `maxVisibleInOneView`.
6. If accepted and the HUD state is `READY`, click the left thumbstick once to lock it. `STALE`, `INVALIDATED`, or `DRAFT` is rejected with an explicit reason.
7. Press either Grip once to select FP1-S2 and repeat steps 4-6, then press once for FP1-S3. Verify the actual marker positions change, not only their labels/colors. Holding Grip must not advance again. The expected log shape is `[AAG Authoring] Switch input=RightGrip ... from=FP1-S1 to=FP1-S2 ... removed=12 spawned=12 positionsDiffer=true success=true`.
8. After the third lock, require `[AAG Equivalence] ... tripletReady=true`, 36 JSON/CSV rows, and three recommended locked candidate IDs. If individual READY candidates exist but no feasible joint combination exists, generate/revalidate alternatives rather than accepting the greedy sequence.
9. Restart the app. Cycle through all three sets and verify that each confirmed set spawns without regeneration. At each confirmation, require the `JSON/CSV save-and-reload verification passed with identical coordinates/content` log; after restart, require `Loaded confirmed placements without regeneration`.

The aggregate files are written separately from the raw MRUK exports:

```text
<Application.persistentDataPath>/AagFp1Placements/
  fp1_confirmed_placements.json
  fp1_confirmed_placements.csv
  fp1_candidate_bank.json
```

The confirmed JSON and CSV retain world coordinates and explicitly state that plan coordinates are unavailable:

```text
floor_plan_id,set_id,answer_marker_id,color,room_uuid,world_x,world_y,world_z,seed,corner_distance,wall_distance,nearest_object_distance,discoverable_observation_count,set_max_visible_objects_per_view,plan_x,plan_y,planCoordinateStatus,worldToPlanTransformVersion,candidate_id,candidate_state,triplet_ready,export_status
```

`plan_x=null`, `plan_y=null`, `planCoordinateStatus=UNAVAILABLE`, and `worldToPlanTransformVersion=null` are deliberate. Even a runtime-eligible triplet is exported as `TRIPLET_READY_RUNTIME_ONLY_PLAN_COORDINATES_UNAVAILABLE`, not as a `FINAL` website answer key. A future calibrated transform must add its version/hash, control points, residual/error, normalized-range checks, and round-trip verification before that status can change. No website or Supabase path consumes this authoring export automatically.

The validation columns remain horizontal-meter measurements. `set_max_visible_objects_per_view` is repeated on each row so the flat CSV still has exactly 36 data rows when all sets are locked. The candidate bank preserves alternative coordinates, lifecycle reasons, dependency fingerprints, metrics, and the recommended triplet without modifying the raw MRUK exports.

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
