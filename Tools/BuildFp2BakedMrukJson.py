#!/usr/bin/env python3
"""Convert an AAG room-coordinate capture into an MRUK Scene JSON asset.

The capture intentionally stores the exact Unity-world pose and plane boundary
of every MRUK surface.  This converter preserves the original room/floor UUIDs
so existing floor-local experiment placements can be restored without access
to the Quest Space Setup database.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path


def _uuid(value: str) -> str:
    return value.replace("-", "").upper()


def _quaternion_to_unity_euler(rotation: dict[str, float]) -> list[float]:
    """Return degrees for Unity's Quaternion.Euler Z-X-Y application order."""
    x = float(rotation["x"])
    y = float(rotation["y"])
    z = float(rotation["z"])
    w = float(rotation["w"])
    length = math.sqrt(x * x + y * y + z * z + w * w)
    if length <= 1e-12:
        return [0.0, 0.0, 0.0]
    x, y, z, w = x / length, y / length, z / length, w / length

    m02 = 2.0 * (x * z + y * w)
    m10 = 2.0 * (x * y + z * w)
    m11 = 1.0 - 2.0 * (x * x + z * z)
    m12 = 2.0 * (y * z - x * w)
    m22 = 1.0 - 2.0 * (x * x + y * y)

    rx = math.asin(max(-1.0, min(1.0, -m12)))
    cx = math.cos(rx)
    if abs(cx) > 1e-6:
        rz = math.atan2(m10, m11)
        ry = math.atan2(m02, m22)
    else:
        # At gimbal lock only the combined yaw/roll is observable.  Keeping
        # roll at zero is stable for horizontal floors and vertical walls.
        rz = 0.0
        m00 = 1.0 - 2.0 * (y * y + z * z)
        m20 = 2.0 * (x * z - y * w)
        ry = math.atan2(-m20, m00)

    return [math.degrees(rx), math.degrees(ry), math.degrees(rz)]


def _build_anchor(surface: dict) -> dict:
    position = surface["worldPosition"]
    anchor = {
        "UUID": _uuid(surface["anchorId"]),
        "SemanticClassifications": [surface["label"]],
        "Transform": {
            "Translation": [position["x"], position["y"], position["z"]],
            "Rotation": _quaternion_to_unity_euler(surface["worldRotation"]),
            "Scale": [1.0, 1.0, 1.0],
        },
    }
    if surface.get("hasPlaneRect"):
        x = float(surface["planeRectX"])
        y = float(surface["planeRectY"])
        width = float(surface["planeRectWidth"])
        height = float(surface["planeRectHeight"])
        anchor["PlaneBounds"] = {
            "Min": [x, y],
            "Max": [x + width, y + height],
        }
    boundary = surface.get("planeBoundaryLocal") or []
    if boundary:
        anchor["PlaneBoundary2D"] = [[point["x"], point["y"]] for point in boundary]
    return anchor


def convert(source: Path, destination: Path, expected_rooms: int | None = None) -> None:
    capture = json.loads(source.read_text(encoding="utf-8-sig"))
    rooms = []
    for room in capture.get("rooms", []):
        surfaces = [
            surface
            for surface in room.get("surfaces", [])
            if surface.get("anchorId")
            and (surface.get("hasPlaneRect") or surface.get("planeBoundaryLocal"))
        ]
        floors = [surface for surface in surfaces if surface.get("isFloor")]
        ceilings = [surface for surface in surfaces if "CEILING" in surface.get("label", "")]
        walls = [surface for surface in surfaces if "WALL_FACE" in surface.get("label", "")]
        if len(floors) != 1:
            raise ValueError(f"room {room.get('roomId')} requires exactly one floor; found {len(floors)}")

        room_layout = {
            "FloorUuid": _uuid(floors[0]["anchorId"]),
            "CeilingUuid": _uuid(ceilings[0]["anchorId"]) if ceilings else "00000000000000000000000000000000",
            "WallsUuid": [_uuid(surface["anchorId"]) for surface in walls],
        }
        rooms.append(
            {
                "UUID": _uuid(room["roomId"]),
                "RoomLayout": room_layout,
                "Anchors": [_build_anchor(surface) for surface in surfaces],
            }
        )

    if expected_rooms is not None and len(rooms) != expected_rooms:
        raise ValueError(
            f"baked scene requires {expected_rooms} rooms; found {len(rooms)}"
        )
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps({"CoordinateSystem": "Unity", "Rooms": rooms}, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"wrote {destination} rooms={len(rooms)}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--expected-rooms", type=int)
    args = parser.parse_args()
    convert(args.source, args.destination, args.expected_rooms)


if __name__ == "__main__":
    main()
