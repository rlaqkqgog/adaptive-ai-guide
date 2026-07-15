# FP1 manual Spatial Anchor set authoring

This mode reuses the existing right-controller Spatial Anchor workflow and does not run candidate generation, random placement, floor/distance/visibility validation, or procedural spawning.

## Quest workflow

1. Select `FP1-S1` on the authoring panel.
2. Select `Red`.
3. Press the right index trigger to create an anchor, then press right `A` to save it. Only a successful Quest anchor-save callback appends the UUID to the set manifest.
4. Save exactly 3 Red, 3 Blue, 3 Green, and 3 Yellow anchors.
5. Press `LOCK SET` after the panel shows `12/12` and every color shows `3/3`.
6. Press `NEXT SET` or explicitly select `FP1-S2`, repeat, then repeat for `FP1-S3`.
7. Press `EXPORT SETS` after all three sets are complete.

The former Grip-based set switching and automatic candidate authoring are disabled in this mode. Right-controller anchor creation/save is never rebound to set switching. Right Grip erase-all is also disabled; `UNDO LAST` erases only the most recently managed anchor in the active unlocked set. The original legacy anchors are never assigned to S1/S2/S3 or deleted.

Authoring UI uses Meta Interaction SDK's official left-controller chain (`OVRCameraRigRef`, `TrackingToWorldTransformerOVR`, `FromOVRControllerDataSource`, `IController.TryGetPointerPose`) and Meta trigger state. There is no grip-pose or guessed-pose fallback. The current authoring build is configured as `ControllersOnly`; hand tracking is disabled to prevent controller/hand pose switching.

Controller-only configuration also disables Android OpenXR `Hand Interaction Profile` and the scene roots `[BuildingBlock] Real Hands`, `[BuildingBlock] HandGrabInstallationRoutine`, and `[BuildingBlock] Hand Tracking left/right`. The core `OVRCameraRig` tracking transforms (`LeftHandAnchor`/`RightHandAnchor`) remain intact because Meta's controller data source uses that tracking space. MRUK and Spatial Anchor objects are not changed.

## Manifest and export

Runtime manifest:

`Application.persistentDataPath/AagManualAnchorSets/fp1_manual_anchor_sets.json`

Exports:

`Application.persistentDataPath/AagManualAnchorSets/Exports/fp1_manual_anchor_sets_<UTC>.json`

`Application.persistentDataPath/AagManualAnchorSets/Exports/fp1_manual_anchor_sets_<UTC>.csv`

Each entry records `floor_plan_id`, `set_id`, `marker_id`, color, anchor UUID, diagnostic save-time world pose, and UTC save time. JSON/CSV export paths are printed with `[AAG Manual Sets Export]` in logcat.

> WARNING: The runtime manifest, candidate/LOCKED state, and export copies inside the app data directory are deleted by app uninstall or Clear App Data. Press `EXPORT SETS` and copy the exported JSON/CSV to the PC before uninstalling or clearing data.

## Locked references

When authoring S2/S3, `SHOW/HIDE LOCKED` displays prior locked-set poses as collider-free translucent gray spheres. These are authoring references only. They do not change or replace the Spatial Anchors and are not used as a world-pose fallback during experiment runtime.

## Experiment runtime

`PlacementSetManager` accepts a locked manifest set, requests its 12 UUIDs from `AnchorLoader`, and spawns the configured marker prefab only at successfully localized Spatial Anchor poses. It does not use the manifest's diagnostic world pose as placement data.

`GuideManager` exposes only `PlacementSetManager.CurrentSet` and its read-only localized marker-pose list. It does not generate, move, or save marker positions.
