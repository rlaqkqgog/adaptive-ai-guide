#!/usr/bin/env python3
"""Build deterministic FP2 incidental anchors at each MRUK floor center."""

from __future__ import annotations

import json
import math
import uuid
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "QuestAnchorBackup/FP2_BakedSpace_20260726/fp2_room_coordinates_source.json"
RESOURCE_ROOT = ROOT / "Assets/Resources/IncidentalObjects"
OUTPUT_ROOT = ROOT / "Assets/Resources/AAG"

ROOM_UUIDS = [
    "0e4e8223-3c13-735b-a552-4acf2ba915a7",  # 이름없는룸
    "d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b",  # 이름없는룸2
    "28e81069-81b3-b60a-0166-50599f88ce42",  # 이름없는룸3
    "133adc09-ce31-302f-1b53-788b59deeb4f",  # 이름없는룸4
    "7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b",  # 이름없는룸5
    "e2e79df2-facb-0150-67b4-a43dfaad9218",  # 이름없는룸6
    "96a223f3-baf3-7044-2958-6f2468b35c72",  # 이름없는룸7
    "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a",  # 이름없는룸8
]


def point_in_polygon(point: tuple[float, float], polygon: list[tuple[float, float]]) -> bool:
    x, y = point
    inside = False
    j = len(polygon) - 1
    for i, (xi, yi) in enumerate(polygon):
        xj, yj = polygon[j]
        if (yi > y) != (yj > y):
            crossing_x = (xj - xi) * (y - yi) / (yj - yi) + xi
            if x < crossing_x:
                inside = not inside
        j = i
    return inside


def distance_to_segments(point: tuple[float, float], polygon: list[tuple[float, float]]) -> float:
    px, py = point
    best = math.inf
    for index, (ax, ay) in enumerate(polygon):
        bx, by = polygon[(index + 1) % len(polygon)]
        dx, dy = bx - ax, by - ay
        length_squared = dx * dx + dy * dy
        t = 0.0 if length_squared == 0 else max(
            0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length_squared)
        )
        qx, qy = ax + t * dx, ay + t * dy
        best = min(best, math.hypot(px - qx, py - qy))
    return best


def safest_floor_center(polygon: list[tuple[float, float]]) -> tuple[float, float]:
    """Return a grid-refined interior center, favoring clearance from walls."""
    min_x = min(point[0] for point in polygon)
    max_x = max(point[0] for point in polygon)
    min_y = min(point[1] for point in polygon)
    max_y = max(point[1] for point in polygon)
    candidates: list[tuple[float, float]] = [
        ((min_x + max_x) * 0.5, (min_y + max_y) * 0.5)
    ]
    steps = 80
    for ix in range(steps + 1):
        x = min_x + (max_x - min_x) * ix / steps
        for iy in range(steps + 1):
            y = min_y + (max_y - min_y) * iy / steps
            candidates.append((x, y))
    interior = [point for point in candidates if point_in_polygon(point, polygon)]
    if not interior:
        raise ValueError("floor polygon contains no sampled interior point")
    return max(interior, key=lambda point: distance_to_segments(point, polygon))


def rotate_vector(q: dict[str, float], vector: tuple[float, float, float]) -> tuple[float, float, float]:
    x, y, z, w = (q[key] for key in "xyzw")
    vx, vy, vz = vector
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (
        vx + w * tx + y * tz - z * ty,
        vy + w * ty + z * tx - x * tz,
        vz + w * tz + x * ty - y * tx,
    )


def normalized_conjugate(q: dict[str, float]) -> tuple[float, float, float, float]:
    magnitude = math.sqrt(sum(q[key] * q[key] for key in "xyzw"))
    return (-q["x"] / magnitude, -q["y"] / magnitude, -q["z"] / magnitude, q["w"] / magnitude)


def prefab_names(asset_set_id: str) -> list[str]:
    names = sorted(path.stem for path in (RESOURCE_ROOT / asset_set_id).glob("*.prefab"))
    if len(names) != 5:
        raise ValueError(f"{asset_set_id} requires exactly five prefab wrappers; actual={len(names)}")
    return names + names[:3]


def main() -> None:
    source = json.loads(SOURCE.read_text(encoding="utf-8-sig"))
    rooms_by_uuid = {room["roomId"].lower(): room for room in source["rooms"]}
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")

    room_slots = []
    for room_index, room_uuid in enumerate(ROOM_UUIDS, start=1):
        room = rooms_by_uuid[room_uuid]
        floor = next(surface for surface in room["surfaces"] if surface["isFloor"])
        polygon = [(point["x"], point["y"]) for point in floor["planeBoundaryLocal"]]
        local_x, local_y = safest_floor_center(polygon)
        local_z = 0.02
        rotated = rotate_vector(floor["worldRotation"], (local_x, local_y, local_z))
        origin = floor["worldPosition"]
        world = (origin["x"] + rotated[0], origin["y"] + rotated[1], origin["z"] + rotated[2])
        local_rotation = normalized_conjugate(floor["worldRotation"])
        room_slots.append(
            {
                "roomNumber": room_index,
                "roomUuid": room_uuid,
                "floorAnchorUuid": floor["anchorId"],
                "localPosition": (local_x, local_y, local_z),
                "localRotation": local_rotation,
                "worldPosition": world,
                "wallClearance": distance_to_segments((local_x, local_y), polygon),
            }
        )

    local_catalog = {
        "schemaVersion": "aag-incidental-floor-local/v1",
        "floorPlanId": "FP2",
        "updatedAtUtc": now,
        "sets": [],
    }
    anchor_manifest = {
        "schema_version": "aag-fp2-incidental-anchor-sets/v1",
        "floor_plan_id": "FP2",
        "updated_at_utc": now,
        "persistence_warning": "BUNDLED DETERMINISTIC MRUK FLOOR-CENTER SEED",
        "sets": [],
    }

    for set_number in range(1, 4):
        set_id = f"FP2-S{set_number}"
        asset_set_id = f"FP1-S{set_number}"
        prefabs = prefab_names(asset_set_id)
        placements = []
        objects = []
        for index, (slot, prefab_name) in enumerate(zip(room_slots, prefabs), start=1):
            object_id = f"incidental_{index:02}_{prefab_name}"
            lx, ly, lz = slot["localPosition"]
            lrx, lry, lrz, lrw = slot["localRotation"]
            wx, wy, wz = slot["worldPosition"]
            anchor_uuid = str(uuid.uuid5(uuid.NAMESPACE_URL, f"aag:fp2:{set_id}:room{index}:incidental"))
            placements.append(
                {
                    "objectId": object_id,
                    "roomUuid": slot["roomUuid"],
                    "floorAnchorUuid": slot["floorAnchorUuid"],
                    "localX": lx,
                    "localY": ly,
                    "localZ": lz,
                    "localRotationX": lrx,
                    "localRotationY": lry,
                    "localRotationZ": lrz,
                    "localRotationW": lrw,
                }
            )
            objects.append(
                {
                    "set_id": set_id,
                    "object_id": object_id,
                    "prefab_resource_path": f"IncidentalObjects/{asset_set_id}/{prefab_name}",
                    "anchor_uuid": anchor_uuid,
                    "fallback_x": wx,
                    "fallback_y": wy,
                    "fallback_z": wz,
                    "fallback_rotation_x": 0.0,
                    "fallback_rotation_y": 0.0,
                    "fallback_rotation_z": 0.0,
                    "fallback_rotation_w": 1.0,
                    "saved_at_utc": now,
                }
            )
        local_catalog["sets"].append({"setId": set_id, "placements": placements})
        anchor_manifest["sets"].append({"set_id": set_id, "objects": objects})

    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    (OUTPUT_ROOT / "fp2_incidental_floor_local.json").write_text(
        json.dumps(local_catalog, ensure_ascii=False, indent=4) + "\n", encoding="utf-8"
    )
    (OUTPUT_ROOT / "fp2_incidental_anchor_sets.json").write_text(
        json.dumps(anchor_manifest, ensure_ascii=False, indent=4) + "\n", encoding="utf-8"
    )

    for slot in room_slots:
        print(
            f"room{slot['roomNumber']}: room={slot['roomUuid']} floor={slot['floorAnchorUuid']} "
            f"local={tuple(round(value, 3) for value in slot['localPosition'])} "
            f"wallClearance={slot['wallClearance']:.3f}m"
        )


if __name__ == "__main__":
    main()
