# AAG hybrid fiducial tag generator

This tool creates the single startup reference board described by the spatial
alignment prototype. The board keeps two separate machine-readable patterns:

- `tagStandard41h12` AprilTag for high-precision pose estimation.
- QR Version 2-Q for the existing `AAG-FP1-ZONE:<room-id>` reference-board
  payload.

The AprilTag is ID 0 from the official `AprilRobotics/apriltag-imgs`
repository. `tagStandard41h12` replaces the earlier conversation's tentative
`tag36h11` choice because it is the only family currently supported by
`jp.keijiro.apriltag`. The QR payload preserves the project's existing native
MRUK payload-validation path for the FP1 start room (`room3`). MRUK does not
automatically associate that payload with a Room3 object; the app must validate
the payload and, when needed, test the QR pose against Room3 geometry.

Run from the project root:

```powershell
python Tools/FiducialTags/generate_hybrid_tags.py
```

The standard-library-only script generates:

- One A4-landscape hybrid board for the Room3 startup reference.
- Separate AprilTag-only and QR-only SVGs for detector isolation tests.
- JSON and CSV manifests describing the fixed board geometry and IDs.
- A runtime copy of the JSON manifest in `Assets/StreamingAssets/AAG`.

Print the hybrid A4 SVG in **landscape** at **100% / Actual Size** on matte
white stock. Disable "Fit to page". Do not crop either white quiet zone. Mount
the complete sheet flat and rigidly, with the `TOP` arrow pointing upward.
The SVG is the authoritative print file; do not use JPG screenshots or preview
renders for dimensional printing.

## AprilTag size definition

There are two different physical dimensions on this print, and they must not
be confused:

- Full 9-module AprilTag bitmap extent: **171 mm**.
- AprilTag detection-corner span (`width_at_border = 5` modules): **95 mm**.

The physical size passed to `AprilTag.TagDetector.ProcessImage` must therefore
be **`0.095f` meters**, matching `tagSizeMeters` in the manifest. The upstream
AprilTag pose API defines tag size as the distance between the detection
corners where the black and white borders meet, explicitly not the outside of
the full printed tag. Keijiro passes its `tagSize` argument directly to that
upstream estimator.

After printing, verify one module is 19 mm, the full bitmap extent is 171 mm,
and the detection-corner span is 95 mm. Then place the board at a measured
1.0 m distance and confirm the estimated Z distance is approximately 1.0 m
before using it for spatial correction.

## Hybrid roles

- AprilTag: authoritative pose for the translation correction.
- MRUK QR: validate the exact `AAG-FP1-ZONE:room3` payload and optionally
  confirm that the observed QR pose is consistent with Room3 and the fixed
  board geometry.
- Do not average the AprilTag and QR poses in the first prototype.
- QR-to-Room3 association is performed by application logic, not by MRUK.

AR Foundation 6.4/6.5 exposes marker APIs, but its own platform-support page
states that no provider plug-in currently implements the marker subsystem.
For this Quest prototype, native MRUK QR tracking and Keijiro AprilTag tracking
are therefore the two executable paths; AR Foundation is a future adapter, not
a detector dependency for this generation step.

## Validation order before automatic correction

1. Apply the expected translation manually and verify every room, collision,
   raycast, and Room UUID-owned object moves correctly.
2. Run the AprilTag-only file at an accurately measured 1 m distance and verify
   ID 0 and pose depth.
3. Run the QR-only file and verify the exact payload.
4. Run the hybrid A4 board with both detectors enabled and confirm each detects
   only its own marker without unstable pose changes.
5. Only after those checks, calculate correction from the AprilTag center and
   the MRUK Room3 reference point.
