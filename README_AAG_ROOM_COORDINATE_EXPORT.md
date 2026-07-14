# AAG room-coordinate export (Quest / MRUK)

This Unity-only addition exports the room data that MRUK loads from the Quest's existing Space Setup scan. It does **not** create object placements, zones, minimum distances, wall clearances, web-plan coordinates, scoring, or any participant data.

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
4. Build an APK with **File > Build Settings > Android > Build And Run**. The build must include `AnchorSpawn` (the scene already contains MRUK and the exporter).
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

## Optional room mapping

MRUK may expose a technical room object name rather than a researcher-friendly name. After the first real-device export, copy each real `roomId` into the `manualRoomMappings` list in the exporter Inspector and add a display name such as `FP1` or `FP2`. This is only a label; it never replaces the MRUK UUID, transforms, or vertices.

No room ID, mapping label, coordinate, boundary, or floor-plan relationship is pre-filled by this project.

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

- Which physical room corresponds to FP1 and FP2, and the optional room mapping labels.
- The valid floor region, excluded areas, zones, wall clearance, and minimum object-to-object distance.
- The 72 actual object coordinates and the six placement-set designs.
- The transformation from MRUK/Unity world coordinates to future web `plan_x` / `plan_y` coordinates.
- Acceptance criteria for a rescan, changed room geometry, or missing/changed room and floor UUIDs.

## Coordinate-origin safety

The export intentionally does not align, transform, or merge coordinate systems. A room UUID and floor-anchor UUID/pose are recorded as references, but a fresh Space Setup, changed scan, another device, or a different localization can produce a different world origin or different anchors.

If existing `anchor_log.json` marker/object coordinates or `track_log.json` trajectory coordinates do not line up with a new room export, do **not** offset them by eye. First collect both datasets in one localization session, compare the exported room/floor IDs and poses, and create a documented transform only after placing and re-localizing one or more deliberate shared reference anchors. Keep the raw exports unchanged alongside any later derived transform.
