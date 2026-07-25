# AAG FP1 hotspot recovery and 100% manual placement

FP1 uses 12 approved manual hotspots and 0 procedural placements per set. The immutable inputs bundled in every APK are:

- `Assets/StreamingAssets/AAG/fp1_preferred_hotspot_catalog.json` — approved UUID/room/diagnostic catalog.
- `Assets/StreamingAssets/AAG/fp1_manual_hotspots_room_local_v1.json` — the same 47 approved hotspots converted from the matching `aag_room_coordinates_20260715_111627_964` room-anchor frame into room-local coordinates.

The source catalog and raw Quest Spatial Anchors are read-only. The room-local file stores original world poses for diagnostics only; the app never uses them as a runtime pose fallback.

## Recovery order

For each bundled UUID the app attempts one Quest local-store query after FP1 MRUK validation. It does not retry a failure loop.

1. `SPATIAL_ANCHOR_LOCALIZED` — use the current localized Spatial Anchor pose.
2. `MRUK_ROOM_LOCAL_RECOVERY` — when the anchor cannot localize, find the same MRUK room UUID and calculate `currentRoomTransform.TransformPoint(roomLocalPosition)`.
3. `UNAVAILABLE` — do not use the hotspot.

Room-local recovery requires the UUID, an approved room-local entry, the same current MRUK room UUID, an in-floor reconstructed position, and normal floor/wall/doorway/obstacle/discoverability checks. A missing or changed room UUID is reported as `ROOM_LOCAL_RECOVERY_BLOCKED`; AAG never falls back to the historical raw world coordinate.

Expected logs include:

```text
[AAG Hotspot Recovery] requested=47 found=0 loadSucceeded=0 localized=0 bundledCatalog=true playerPrefs=0
[AAG Hotspot Recovery] source=MRUK_ROOM_LOCAL restored=47 rejected=0
```

The first line also reports the bundled catalog path at source resolution, all store/localization failures are logged per UUID and stage, and the room-UUID report compares the catalog's rooms to current MRUK rooms.

## Storage lifecycle

| Data | Same package update | Uninstall/reinstall or Clear App Data | Package ID/signing change |
|---|---|---|---|
| Bundled catalog and room-local catalog | Replaced by the updated APK | Restored from the APK | Included in each matching build |
| PlayerPrefs and `persistentDataPath` candidate/LOCKED data | Retained | Deleted | Separate app storage; signing mismatch normally requires reinstall |
| Quest Spatial Anchor local store | Read-only by AAG | Not deleted or changed by AAG | Availability depends on platform/app identity policy |
| `adb logcat` | Volatile only | Volatile only | Not a persistence mechanism |

Runtime override catalog priority remains `runtime override > bundled > legacy PlayerPrefs migration`. An override must match the bundled room-local catalog's source hash; otherwise room-local recovery is blocked instead of mixing unrelated coordinate data. PlayerPrefs is migration/diagnostic input only and is not required to discover the bundled 47 UUIDs.

## Placement rules

- `manual=12`, `procedural=0`, with no same-set UUID duplicates.
- Same-room/same-zone hotspots within the Inspector `manualHotspotAdjacencyMeters` (default `0.75m`) are adjacent and may not be selected together or across consecutive sets.
- Room2 maximum is 5, Room3 maximum is 3, using the explicit UUID-to-zone mapping.
- Fixed seeds, candidate bank, room quota, clearance, diversity/equivalence, and `TRIPLET_READY` gates remain unchanged.
- If fewer than 12 restored and hard-valid hotspots are available, generation stops with the precise shortage/rejection reason; it never spawns a procedural or arbitrary fallback position.

After final confirmation, export candidate-bank and LOCKED data to a PC before uninstalling or clearing app data.
