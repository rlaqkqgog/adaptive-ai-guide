#!/usr/bin/env python3
"""Generate audited FP1 floor-local walkable waypoints from completed sessions.

The input folder is intentionally kept outside Assets.  Each session is paired
with the MRUK room export captured immediately before that run (or before the
continuous P16 block).  Only sessions ending with all_targets_delivered are
accepted.  Output is a C# dictionary body suitable for AagFp1WalkablePath.
"""

from __future__ import annotations

import argparse
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path


SESSION_ROOM_EXPORTS = {
    "P15_FP1-S3_NG_20260722_170813": "aag_room_coordinates_20260722_080757_034.json",
    "P15_FP1-S1_AAG_20260722_172402": "aag_room_coordinates_20260722_082347_125.json",
    "P15_FP1-S2_VG_20260722_173947": "aag_room_coordinates_20260722_084833_081.json",
    "P16_FP1-S1_NG_20260723_193347": "aag_room_coordinates_20260723_103337_604.json",
    "P16_FP1-S2_VG_20260723_194540": "aag_room_coordinates_20260723_103337_604.json",
    "P16_FP1-S3_AAG_20260723_195323": "aag_room_coordinates_20260723_103337_604.json",
    "P18_FP1-S1_VG_20260725_152437": "aag_room_coordinates_20260725_062355_659.json",
    "P19_FP1-S2_AAG_20260725_165452": "aag_room_coordinates_20260725_075416_751.json",
    "P19_FP1-S1_VG_20260725_171756": "aag_room_coordinates_20260725_081739_495.json",
    "P20_FP1-S1_NG_20260725_183316": "aag_room_coordinates_20260725_093257_736.json",
    "P20_FP1-S2_AAG_20260725_184412": "aag_room_coordinates_20260725_094353_547.json",
}


def conjugate(q: tuple[float, float, float, float]):
    x, y, z, w = q
    return -x, -y, -z, w


def rotate(q: tuple[float, float, float, float], v: tuple[float, float, float]):
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (
        vx + w * tx + (y * tz - z * ty),
        vy + w * ty + (z * tx - x * tz),
        vz + w * tz + (x * ty - y * tx),
    )


def floor_frames(room_export: Path):
    data = json.loads(room_export.read_text(encoding="utf-8-sig"))
    frames = {}
    for room in data["rooms"]:
        floor = next(surface for surface in room["surfaces"] if surface.get("isFloor"))
        p = floor["worldPosition"]
        q = floor["worldRotation"]
        frames[room["roomId"]] = (
            (p["x"], p["y"], p["z"]),
            (q["x"], q["y"], q["z"], q["w"]),
        )
    return frames


def load_tracks(session_file: Path, frames):
    events = [json.loads(line) for line in session_file.read_text(encoding="utf-8-sig").splitlines()]
    endings = [event for event in events if event.get("type") == "session_end"]
    if len(endings) != 1 or endings[0].get("reason") != "all_targets_delivered":
        raise RuntimeError(f"incomplete session rejected: {session_file.name}")

    result = defaultdict(list)
    for event in events:
        if event.get("type") != "track" or not event.get("roomUuid"):
            continue
        room_id = event.get("roomUuid")
        if room_id not in frames:
            continue
        origin, rotation = frames[room_id]
        delta = (
            event["x"] - origin[0],
            event["y"] - origin[1],
            event["z"] - origin[2],
        )
        local = rotate(conjugate(rotation), delta)
        # The floor anchor's XY plane is the walkable plane; Z is head height.
        if 0.7 <= abs(local[2]) <= 2.2:
            result[event["roomUuid"]].append((local[0], local[1]))
    return result


def aggregate(source: Path, grid_size: float, spacing: float):
    cells = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    for session_id, export_name in SESSION_ROOM_EXPORTS.items():
        frames = floor_frames(source / "rooms" / export_name)
        tracks = load_tracks(source / "sessions" / f"{session_id}.jsonl", frames)
        for room_uuid, points in tracks.items():
            for x, y in points:
                cell = (round(x / grid_size), round(y / grid_size))
                cells[room_uuid][cell][session_id].append((x, y))

    result = {}
    for room_uuid, room_cells in sorted(cells.items()):
        candidates = []
        for per_session in room_cells.values():
            if len(per_session) < 2:
                continue
            # Equal session weighting prevents a slow participant dominating.
            session_centers = []
            for points in per_session.values():
                session_centers.append((
                    statistics.median(point[0] for point in points),
                    statistics.median(point[1] for point in points),
                ))
            candidates.append((
                statistics.median(point[0] for point in session_centers),
                statistics.median(point[1] for point in session_centers),
                len(per_session),
            ))

        # Prefer the cells supported by most sessions, then retain spatially
        # distinct samples.  The final sort keeps generated source deterministic.
        selected = []
        for x, y, support in sorted(candidates, key=lambda value: (-value[2], value[0], value[1])):
            if all(math.hypot(x - sx, y - sy) >= spacing for sx, sy, _ in selected):
                selected.append((x, y, support))
        result[room_uuid] = sorted(selected, key=lambda value: (value[0], value[1]))
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("--grid", type=float, default=0.35)
    parser.add_argument("--spacing", type=float, default=0.55)
    args = parser.parse_args()
    result = aggregate(args.source, args.grid, args.spacing)
    for room_uuid, points in result.items():
        print(f'[Guid.Parse("{room_uuid}")] = new[]')
        print("{")
        for index in range(0, len(points), 3):
            row = points[index:index + 3]
            print("    " + " ".join(
                f"new Vector2({x:.2f}f, {y:.2f}f)," for x, y, _ in row))
        print("},")
        print(f"// {room_uuid}: {len(points)} waypoints; support >= 2 completed sessions")


if __name__ == "__main__":
    main()
