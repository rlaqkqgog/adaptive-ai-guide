# FP1 fiducial-marker pilot

This pilot uses Meta XR 203's native MRUK QR tracker. It does not read raw
passthrough frames, does not move `OVRCameraRig/TrackingSpace`, and does not
modify Meta Space Setup data.

## QR payloads

Print one unique QR code for each exact payload:

- `AAG-FP1-ZONE:room1`
- `AAG-FP1-ZONE:room2`
- `AAG-FP1-ZONE:room3`
- `AAG-FP1-ZONE:hall1-1`
- `AAG-FP1-ZONE:hall1-2`
- `AAG-FP1-ZONE:hall1-3`
- `AAG-FP1-ZONE:hall2-1`
- `AAG-FP1-ZONE:hall2-2`

Use matte paper, a high-contrast black/white print, and the same physical size
for all eight markers. Mount each marker rigidly; moving a calibrated marker
invalidates that zone's coordinate reference.

## One-time calibration

Calibration is deliberately fail-closed. Do it only after independently
confirming that Meta Space Setup identifies every physical room correctly.

1. Open the app's idle selection screen.
2. Stand in the physical zone matching the marker payload.
3. Look at exactly one QR marker until the HUD says it is seen.
4. Hold the right controller thumbstick button for 1.2 seconds.
5. Confirm `QR CALIBRATED` and the expected room id.
6. Repeat for all eight zones.

The app rejects calibration when the marker is not inside or within 0.75 m of
the intended MRUK floor boundary. This prevents a Room3 marker from being
silently calibrated against Room2 during a false room match.

Calibration is stored at:

`Application.persistentDataPath/AagFiducialMarkers/fp1_fiducial_zone_calibrations.json`

If Meta Space Setup is deliberately re-scanned and its room/floor UUIDs change,
the old calibration is rejected and all eight markers must be calibrated again.

## Runtime behavior

- A session starts only after all eight calibrations are current and the Room3
  marker has been detected within the previous 12 seconds.
- Content for a zone stays inactive until that zone's marker is detected during
  the session.
- Detection moves only that zone's content root. It never moves the headset or
  any other room.
- Each content root is converted from its current MRUK-floor-local pose into
  the marker-corrected floor frame. Spatial-anchor pose updates are frozen
  before registration so they cannot overwrite the marker correction.
- If two zone markers are visible at once, content for both zones may align,
  but participant room identity does not switch arbitrarily; the previous
  unambiguous room is retained and the ambiguity is logged.
- A stone is detached permanently from its zone root on its first successful
  grab, so later re-alignment cannot move a participant-manipulated stone.
- Marker alignment, rejection, calibration, content registration, and detach
  actions are written as structured system events in the session log.

## Rollback

The recommended pre-marker APK is:

`Checkpoints/PRE_MARKER_FIDUCIAL_20260721_010457/APK/LAST_KNOWN_STABLE_PRE_SPACE_SHIFT_20260720_134226.apk`

SHA-256:

`F9146641D98855537E153EAC385D639D26B6E7C5204A088433B99FDD3F4ED5A0`

Install rollback APKs with `adb install -r`. Never uninstall or clear app data
as part of rollback.
