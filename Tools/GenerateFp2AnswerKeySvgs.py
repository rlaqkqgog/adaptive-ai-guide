#!/usr/bin/env python3
"""Generate one operator answer-key SVG for each FP2 session set."""

from __future__ import annotations

import html
import json
import math
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "QuestAnchorBackup/FP2_BakedSpace_20260726/fp2_room_coordinates_source.json"
STONE_SOURCE = ROOT / "Assets/Resources/AAG/fp2_floor_anchor_local_placements.json"
TOWER_SOURCE = ROOT / "Assets/Resources/AAG/fp2_fixed_tower_floor_local.json"
INCIDENTAL_SOURCE = ROOT / "Assets/Resources/AAG/fp2_incidental_floor_local.json"
OUTPUT = ROOT / "Documentation/FP2AnswerKeys"

ROOM_ORDER = [
    ("0e4e8223-3c13-735b-a552-4acf2ba915a7", "이름없는룸"),
    ("d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b", "이름없는룸2"),
    ("28e81069-81b3-b60a-0166-50599f88ce42", "이름없는룸3"),
    ("133adc09-ce31-302f-1b53-788b59deeb4f", "이름없는룸4"),
    ("7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b", "이름없는룸5"),
    ("e2e79df2-facb-0150-67b4-a43dfaad9218", "이름없는룸6"),
    ("96a223f3-baf3-7044-2958-6f2468b35c72", "이름없는룸7"),
    ("2d4f4c7d-9189-0198-a0ff-ecd07d843c6a", "이름없는룸8"),
]

ROOM_COLORS = ["#dbeafe", "#fef3c7", "#ede9fe", "#dcfce7", "#ffedd5", "#cffafe", "#fce7f3", "#e2e8f0"]
STONE_COLORS = {
    "red": "#dc2626",
    "blue": "#2563eb",
    "green": "#16a34a",
    "yellow": "#eab308",
}


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def rotate_vector(q: dict[str, float], vector: tuple[float, float, float]) -> tuple[float, float, float]:
    x, y, z, w = (float(q[key]) for key in "xyzw")
    length = math.sqrt(x * x + y * y + z * z + w * w)
    x, y, z, w = x / length, y / length, z / length, w / length
    vx, vy, vz = vector
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (
        vx + w * tx + y * tz - z * ty,
        vy + w * ty + z * tx - x * tz,
        vz + w * tz + x * ty - y * tx,
    )


def world_pose(placement: dict, floors: dict[str, dict]) -> tuple[float, float, float]:
    floor = floors[placement["floorAnchorUuid"].lower()]
    local = (float(placement["localX"]), float(placement["localY"]), float(placement["localZ"]))
    rotated = rotate_vector(floor["worldRotation"], local)
    origin = floor["worldPosition"]
    return (
        float(origin["x"]) + rotated[0],
        float(origin["y"]) + rotated[1],
        float(origin["z"]) + rotated[2],
    )


def svg_text(x: float, y: float, value: str, css: str, anchor: str = "start") -> str:
    return f'<text x="{x:.1f}" y="{y:.1f}" class="{css}" text-anchor="{anchor}">{html.escape(value)}</text>'


def render_set(
    set_id: str,
    rooms: list[dict],
    stones: list[dict],
    towers: list[dict],
    incidentals: list[dict],
    floors: dict[str, dict],
) -> str:
    width, height = 1600, 1000
    map_x, map_y, map_w, map_h = 35, 110, 1030, 845
    panel_x = 1090

    room_polygons: list[tuple[str, str, list[tuple[float, float]]]] = []
    room_lookup = {uuid: (index + 1, name) for index, (uuid, name) in enumerate(ROOM_ORDER)}
    for room in rooms:
        room_uuid = room["roomId"].lower()
        if room_uuid not in room_lookup:
            continue
        floor = next(surface for surface in room["surfaces"] if surface.get("isFloor"))
        polygon = [(float(point["x"]), float(point["z"])) for point in floor["planeBoundaryWorld"]]
        room_polygons.append((room_uuid, room_lookup[room_uuid][1], polygon))

    stone_points = [(item, world_pose(item, floors)) for item in stones]
    tower_points = [(item, world_pose(item, floors)) for item in towers]
    incidental_points = [(item, world_pose(item, floors)) for item in incidentals]
    all_x = [point[0] for _, _, polygon in room_polygons for point in polygon]
    all_z = [point[1] for _, _, polygon in room_polygons for point in polygon]
    for _, point in stone_points + tower_points + incidental_points:
        all_x.append(point[0])
        all_z.append(point[2])
    min_x, max_x = min(all_x) - 0.8, max(all_x) + 0.8
    min_z, max_z = min(all_z) - 0.8, max(all_z) + 0.8
    scale = min(map_w / (max_x - min_x), map_h / (max_z - min_z))
    used_w, used_h = (max_x - min_x) * scale, (max_z - min_z) * scale
    origin_x = map_x + (map_w - used_w) * 0.5
    origin_y = map_y + (map_h - used_h) * 0.5

    def project(x: float, z: float) -> tuple[float, float]:
        return origin_x + (x - min_x) * scale, origin_y + (max_z - z) * scale

    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img">',
        f'<title>FP2 {set_id} 정답지 지도</title>',
        '<rect width="100%" height="100%" fill="#f8fafc"/>',
        '<style>',
        'text{font-family:"Malgun Gothic","Segoe UI",Arial,sans-serif;fill:#0f172a}',
        '.title{font-size:28px;font-weight:700}.subtitle{font-size:14px;fill:#475569}',
        '.room{stroke:#334155;stroke-width:2;fill-opacity:.72}.roomLabel{font-size:14px;font-weight:700}',
        '.pointLabel{font-size:11px;font-weight:700;paint-order:stroke;stroke:#fff;stroke-width:3px;stroke-linejoin:round}',
        '.panelTitle{font-size:17px;font-weight:700}.row{font-size:12px}.small{font-size:11px;fill:#64748b}',
        '</style>',
        svg_text(35, 42, f"{set_id} 정답지 지도", "title"),
        svg_text(35, 68, "방 8 · 돌 12 · 돌탑 4 · 우연객체 8 | 위에서 본 X-Z 배치", "subtitle"),
        svg_text(35, 90, "AprilTag 보정은 전체에 동일 평행이동으로 적용되므로 상대 배치는 동일합니다.", "subtitle"),
        f'<rect x="{map_x}" y="{map_y}" width="{map_w}" height="{map_h}" rx="16" fill="#ffffff" stroke="#cbd5e1"/>',
    ]

    for index, (room_uuid, room_name, polygon) in enumerate(room_polygons):
        points = " ".join(f"{px:.1f},{py:.1f}" for px, py in (project(x, z) for x, z in polygon))
        lines.append(f'<polygon points="{points}" class="room" fill="{ROOM_COLORS[index % len(ROOM_COLORS)]}"><title>{room_name} {room_uuid}</title></polygon>')
        center_x = sum(point[0] for point in polygon) / len(polygon)
        center_z = sum(point[1] for point in polygon) / len(polygon)
        px, py = project(center_x, center_z)
        lines.append(svg_text(px, py, room_name, "roomLabel", "middle"))

    for item, (x, y, z) in tower_points:
        px, py = project(x, z)
        points = f"{px:.1f},{py-12:.1f} {px+12:.1f},{py:.1f} {px:.1f},{py+12:.1f} {px-12:.1f},{py:.1f}"
        lines.append(f'<polygon points="{points}" fill="#111827" stroke="#fff" stroke-width="2"><title>{item["towerId"]} ({x:.3f}, {y:.3f}, {z:.3f})</title></polygon>')
        lines.append(svg_text(px + 15, py - 8, item["towerId"].replace("Tower-", "T"), "pointLabel"))

    for index, (item, (x, y, z)) in enumerate(incidental_points, start=1):
        px, py = project(x, z)
        lines.append(f'<rect x="{px-6:.1f}" y="{py-6:.1f}" width="12" height="12" rx="2" fill="#64748b" stroke="#fff" stroke-width="2"><title>{item["objectId"]} ({x:.3f}, {y:.3f}, {z:.3f})</title></rect>')
        lines.append(svg_text(px + 9, py + 4, f"I{index}", "pointLabel"))

    for item, (x, y, z) in stone_points:
        px, py = project(x, z)
        color = STONE_COLORS[item["color"].lower()]
        lines.append(f'<circle cx="{px:.1f}" cy="{py:.1f}" r="9" fill="{color}" stroke="#fff" stroke-width="2"><title>{item["objectId"]} ({x:.3f}, {y:.3f}, {z:.3f})</title></circle>')
        lines.append(svg_text(px + 12, py - 10, item["objectId"], "pointLabel"))

    lines.extend([
        svg_text(map_x + 16, map_y + map_h - 18, f"저장 MRUK 좌표 범위 X {min_x + .8:.1f}…{max_x - .8:.1f} m / Z {min_z + .8:.1f}…{max_z - .8:.1f} m", "small"),
        svg_text(panel_x, 130, "기호", "panelTitle"),
        '<circle cx="1110" cy="155" r="8" fill="#dc2626"/><text x="1128" y="160" class="row">돌 (색상 = 정답 색)</text>',
        '<polygon points="1110,174 1120,184 1110,194 1100,184" fill="#111827"/><text x="1128" y="189" class="row">돌탑 T1–T4</text>',
        '<rect x="1104" y="207" width="12" height="12" rx="2" fill="#64748b"/><text x="1128" y="218" class="row">우연객체 I1–I8</text>',
        svg_text(panel_x, 255, "돌 12개 (XYZ m)", "panelTitle"),
    ])

    row_y = 278
    for item, (x, y, z) in sorted(stone_points, key=lambda value: (value[0]["color"], value[0]["objectId"])):
        lines.append(svg_text(panel_x, row_y, f'{item["objectId"]:<9} {item["color"]:<6} ({x:6.2f}, {y:5.2f}, {z:6.2f})', "row"))
        row_y += 18

    row_y += 10
    lines.append(svg_text(panel_x, row_y, "돌탑 4개 (XYZ m)", "panelTitle"))
    row_y += 23
    for item, (x, y, z) in tower_points:
        lines.append(svg_text(panel_x, row_y, f'{item["towerId"]:<8} ({x:6.2f}, {y:5.2f}, {z:6.2f})', "row"))
        row_y += 18

    row_y += 10
    lines.append(svg_text(panel_x, row_y, "우연객체 8개 (XYZ m)", "panelTitle"))
    row_y += 23
    for index, (item, (x, y, z)) in enumerate(incidental_points, start=1):
        short_name = item["objectId"].split("_", 3)[-1]
        lines.append(svg_text(panel_x, row_y, f'I{index} {short_name[:22]:<22} ({x:5.1f}, {y:4.1f}, {z:5.1f})', "row"))
        row_y += 18

    lines.append('</svg>')
    return "\n".join(lines) + "\n"


def main() -> None:
    source = load(SOURCE)
    rooms = source["rooms"]
    floors = {}
    for room in rooms:
        for surface in room["surfaces"]:
            if surface.get("isFloor"):
                floors[surface["anchorId"].lower()] = surface

    stone_sets = {item["setId"]: item["placements"] for item in load(STONE_SOURCE)["sets"]}
    tower_placements = load(TOWER_SOURCE)["placements"]
    incidental_sets = {item["setId"]: item["placements"] for item in load(INCIDENTAL_SOURCE)["sets"]}
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for set_id in ("FP2-S1", "FP2-S2", "FP2-S3"):
        stones = stone_sets[set_id]
        incidentals = incidental_sets[set_id]
        if len(stones) != 12 or len(tower_placements) != 4 or len(incidentals) != 8:
            raise ValueError(f"{set_id} count mismatch: stones={len(stones)} towers={len(tower_placements)} incidentals={len(incidentals)}")
        destination = OUTPUT / f"{set_id}_answer_key.svg"
        destination.write_text(
            render_set(set_id, rooms, stones, tower_placements, incidentals, floors),
            encoding="utf-8",
        )
        print(f"wrote {destination}")


if __name__ == "__main__":
    main()
