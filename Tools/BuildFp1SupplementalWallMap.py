#!/usr/bin/env python3
"""Build the FP1 new-scan wall veto mapped onto the original baked rooms.

The original baked scan remains the placement and AprilTag source of truth.  This
tool only records the corresponding room boundary/wall geometry from the
2026-08-12 rescan so runtime placement can reject points in changed walls,
pillars, or outside a rescanned room.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path


# Logical room name, original room UUID, rescanned room name, rescanned room UUID.
# The logical/name correspondence was supplied after comparing the two plans.
ROOM_MAP = (
    ("Room1", "98332012-20ba-e0ba-78ec-8440113270cd", "이름없는 룸", "3bb04ce0-d25b-0bc7-9b21-d6385158a0ac"),
    ("Room2-1", "5faa1907-d2e2-7605-7b01-5149a34a4c6d", "이름없는 룸 3", "0239a778-ea89-55d5-8cf1-26b7cdc58490"),
    ("Room2-2", "ad306342-a794-cffd-be87-d9aea02c5823", "이름없는 룸 4", "ad2e0299-6afb-aff2-1acc-7eb24a4d6513"),
    ("Room3", "0d537c33-3e47-2606-3ea9-897c2bc9f1ce", "이름없는 룸 6", "e5da934c-8d63-05d5-ca0b-48f6ca01ab94"),
    ("Hall 1-1", "7e4d3e1f-3247-602b-3822-c21df384e947", "이름없는 룸 2", "602041c1-5031-cec0-5b52-fb36da9748d2"),
    ("Hall 1-2", "b4131884-79b1-7db8-5529-4875ea40bdd5", "이름없는 룸 5", "241fca56-f1c0-7905-8f73-105a18c300bd"),
    ("Hall 1-3", "7423d1c6-d1e7-3316-6714-6a9b282a396e", "거실", "f7977287-8ef5-ad3e-6413-a48998cef936"),
    ("Hall 2-1", "880b5e63-6438-a8d5-c156-2ddffc48a6d4", "이름없는 룸 7", "3e654fbe-c458-dac1-c25d-c2fc0e5ffaa1"),
    ("Hall 2-2", "ce00a9e3-c3dc-48cd-a503-e928584055f2", "이름없는 룸 8", "6343e8d6-2e59-e74f-7eb8-ad1347750774"),
)

WALL_LABELS = {"WALL_FACE", "INNER_WALL_FACE", "INVISIBLE_WALL_FACE"}


def vec3(value: dict) -> tuple[float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"])


def quat(value: dict) -> tuple[float, float, float, float]:
    return float(value["x"]), float(value["y"]), float(value["z"]), float(value["w"])


def qrot(q: tuple[float, float, float, float], v: tuple[float, float, float]) -> tuple[float, float, float]:
    qx, qy, qz, qw = q
    vx, vy, vz = v
    tx = 2.0 * (qy * vz - qz * vy)
    ty = 2.0 * (qz * vx - qx * vz)
    tz = 2.0 * (qx * vy - qy * vx)
    return (
        vx + qw * tx + (qy * tz - qz * ty),
        vy + qw * ty + (qz * tx - qx * tz),
        vz + qw * tz + (qx * ty - qy * tx),
    )


def qinv(q: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    x, y, z, w = q
    norm = x * x + y * y + z * z + w * w
    return -x / norm, -y / norm, -z / norm, w / norm


def add3(a, b):
    return a[0] + b[0], a[1] + b[1], a[2] + b[2]


def sub3(a, b):
    return a[0] - b[0], a[1] - b[1], a[2] - b[2]


def fit_rigid_2d(source, target):
    sx = sum(p[0] for p in source) / len(source)
    sy = sum(p[1] for p in source) / len(source)
    tx = sum(p[0] for p in target) / len(target)
    ty = sum(p[1] for p in target) / len(target)
    dot = 0.0
    cross = 0.0
    for a, b in zip(source, target):
        ax, ay = a[0] - sx, a[1] - sy
        bx, by = b[0] - tx, b[1] - ty
        dot += ax * bx + ay * by
        cross += ax * by - ay * bx
    angle = math.atan2(cross, dot)
    c, s = math.cos(angle), math.sin(angle)
    return c, s, tx - (c * sx - s * sy), ty - (s * sx + c * sy)


def apply2(transform, point):
    c, s, tx, ty = transform
    x, y = point
    return c * x - s * y + tx, s * x + c * y + ty


def sample_boundary(poly, spacing=0.08):
    result = []
    for index, start in enumerate(poly):
        end = poly[(index + 1) % len(poly)]
        length = math.dist(start, end)
        count = max(1, int(math.ceil(length / spacing)))
        for step in range(count):
            amount = step / count
            result.append((start[0] + (end[0] - start[0]) * amount,
                           start[1] + (end[1] - start[1]) * amount))
    return result


def nearest(point, candidates):
    best = candidates[0]
    best_d2 = float("inf")
    for candidate in candidates:
        dx, dy = point[0] - candidate[0], point[1] - candidate[1]
        d2 = dx * dx + dy * dy
        if d2 < best_d2:
            best, best_d2 = candidate, d2
    return best, math.sqrt(best_d2)


def refine_boundary_transform(old_poly, new_poly, initial):
    old_samples = sample_boundary(old_poly)
    new_samples = sample_boundary(new_poly)
    transform = initial
    for _ in range(12):
        pairs = []
        for source in old_samples:
            mapped = apply2(transform, source)
            target, distance = nearest(mapped, new_samples)
            if distance <= 0.80:
                pairs.append((distance, source, target))
        if len(pairs) < 8:
            break
        pairs.sort(key=lambda value: value[0])
        pairs = pairs[:max(8, int(len(pairs) * 0.75))]
        fitted = fit_rigid_2d([value[1] for value in pairs], [value[2] for value in pairs])
        transform = fitted
    distances = [nearest(apply2(transform, point), new_samples)[1] for point in old_samples]
    distances.sort()
    rms = math.sqrt(sum(value * value for value in distances) / len(distances))
    p95 = distances[min(len(distances) - 1, int(len(distances) * 0.95))]
    return transform, rms, p95


def floor_surface(room):
    floor_ids = {value.lower() for value in room.get("floorAnchorIds", [])}
    for surface in room["surfaces"]:
        if surface.get("isFloor") or surface["anchorId"].lower() in floor_ids:
            return surface
    raise ValueError(f"Floor missing for room {room['roomId']}")


def floor_poly(surface):
    return [(float(point["x"]), float(point["y"])) for point in surface["planeBoundaryLocal"]]


def local_to_world(surface, point):
    local = (point[0], point[1], 0.0)
    return add3(vec3(surface["worldPosition"]), qrot(quat(surface["worldRotation"]), local))


def world_to_local(surface, point):
    return qrot(qinv(quat(surface["worldRotation"])), sub3(point, vec3(surface["worldPosition"])))


def build_initial_local_transform(old_floor, new_floor, world_transform, y_offset):
    wc, ws, wtx, wtz = world_transform

    def convert(point):
        old_world = local_to_world(old_floor, point)
        new_world = (
            wc * old_world[0] - ws * old_world[2] + wtx,
            old_world[1] + y_offset,
            ws * old_world[0] + wc * old_world[2] + wtz,
        )
        local = world_to_local(new_floor, new_world)
        return local[0], local[1]

    p0, px = convert((0.0, 0.0)), convert((1.0, 0.0))
    angle = math.atan2(px[1] - p0[1], px[0] - p0[0])
    return math.cos(angle), math.sin(angle), p0[0], p0[1]


def wall_segments(room, floor):
    segments = []
    for surface in room["surfaces"]:
        if surface.get("label") not in WALL_LABELS:
            continue
        local_points = []
        for point in surface.get("planeBoundaryWorld") or []:
            local = world_to_local(floor, vec3(point))
            local_points.append((local[0], local[1]))
        if len(local_points) < 2:
            continue
        best = None
        best_length = 0.0
        for a_index, start in enumerate(local_points):
            for end in local_points[a_index + 1:]:
                length = math.dist(start, end)
                if length > best_length:
                    best, best_length = (start, end), length
        if best is None or best_length < 0.05:
            continue
        segments.append({
            "anchorId": surface["anchorId"],
            "label": surface["label"],
            "start": {"x": round(best[0][0], 6), "y": round(best[0][1], 6)},
            "end": {"x": round(best[1][0], 6), "y": round(best[1][1], 6)},
        })
    return segments


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--old", required=True, type=Path)
    parser.add_argument("--new", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    old_data = json.loads(args.old.read_text(encoding="utf-8-sig"))
    new_data = json.loads(args.new.read_text(encoding="utf-8-sig"))
    old_rooms = {room["roomId"].lower(): room for room in old_data["rooms"]}
    new_rooms = {room["roomId"].lower(): room for room in new_data["rooms"]}

    old_centers, new_centers = [], []
    for _, old_id, _, new_id in ROOM_MAP:
        old_center = vec3(old_rooms[old_id]["centerWorld"])
        new_center = vec3(new_rooms[new_id]["centerWorld"])
        old_centers.append((old_center[0], old_center[2]))
        new_centers.append((new_center[0], new_center[2]))
    world_transform = fit_rigid_2d(old_centers, new_centers)
    world_errors = [math.dist(apply2(world_transform, old), new)
                    for old, new in zip(old_centers, new_centers)]

    old_floor_heights, new_floor_heights = [], []
    for _, old_id, _, new_id in ROOM_MAP:
        old_floor_heights.append(
            vec3(floor_surface(old_rooms[old_id])["worldPosition"])[1])
        new_floor_heights.append(vec3(floor_surface(new_rooms[new_id])["worldPosition"])[1])
    y_offset = sum(n - o for o, n in zip(old_floor_heights, new_floor_heights)) / len(ROOM_MAP)

    output_rooms = []
    for logical, old_id, new_name, new_id in ROOM_MAP:
        old_room, new_room = old_rooms[old_id], new_rooms[new_id]
        old_floor, new_floor = floor_surface(old_room), floor_surface(new_room)
        old_poly, new_poly = floor_poly(old_floor), floor_poly(new_floor)
        initial = build_initial_local_transform(old_floor, new_floor, world_transform, y_offset)
        transform, rms, p95 = refine_boundary_transform(old_poly, new_poly, initial)
        initial_angle = math.atan2(initial[1], initial[0])
        final_angle = math.atan2(transform[1], transform[0])
        angle_delta = abs(math.degrees(math.atan2(
            math.sin(final_angle - initial_angle), math.cos(final_angle - initial_angle))))
        translation_delta = math.dist((initial[2], initial[3]), (transform[2], transform[3]))
        # A large boundary residual is expected where the rescan corrected a
        # pillar or hallway outline.  Reject only a divergent pose alignment;
        # retaining that changed boundary is the purpose of this artifact.
        if angle_delta > 12.0 or translation_delta > 1.0:
            raise ValueError(
                f"Unsafe alignment for {logical}: angleDelta={angle_delta:.2f}, "
                f"translationDelta={translation_delta:.3f}, p95={p95:.3f}")
        c, s, tx, ty = transform
        output_rooms.append({
            "logicalName": logical,
            "oldRoomId": old_id,
            "newRoomName": new_name,
            "newRoomId": new_id,
            "newFloorAnchorId": new_floor["anchorId"],
            "oldToNew": {"cos": round(c, 9), "sin": round(s, 9),
                         "tx": round(tx, 6), "ty": round(ty, 6)},
            "alignmentRmsMeters": round(rms, 4),
            "alignmentP95Meters": round(p95, 4),
            "newFloorBoundary": [{"x": round(x, 6), "y": round(y, 6)} for x, y in new_poly],
            "newWallSegments": wall_segments(new_room, new_floor),
        })
        print(f"{logical:8s} -> {new_name:10s} rms={rms:.3f} p95={p95:.3f} walls={len(output_rooms[-1]['newWallSegments'])}")

    result = {
        "schemaVersion": 1,
        "purpose": "FP1 baked placement supplemental rescan wall veto",
        "oldExportedAtUtc": old_data.get("exportedAtUtc", ""),
        "newExportedAtUtc": new_data.get("exportedAtUtc", ""),
        "worldAlignmentRmsMeters": round(math.sqrt(sum(v * v for v in world_errors) / len(world_errors)), 4),
        "rooms": output_rooms,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # ASCII escapes keep the generated Unity TextAsset and Windows PowerShell
    # tooling unambiguous while JsonUtility restores the Korean display names.
    args.output.write_text(json.dumps(result, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {args.output} ({len(output_rooms)} rooms)")


if __name__ == "__main__":
    main()
