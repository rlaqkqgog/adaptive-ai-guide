#!/usr/bin/env python3
"""Generate print-ready AprilTag + QR hybrid fiducial cards.

The tool intentionally depends only on the Python standard library. AprilTag
source images are the official tagStandard41h12 images from
AprilRobotics/apriltag-imgs. The QR encoder is restricted to byte-mode,
Version 2, error-correction level Q, which is sufficient for the AAG zone
payload contract (up to 20 UTF-8 bytes).
"""

from __future__ import annotations

import argparse
import base64
import csv
import io
import json
import struct
import sys
import zlib
from pathlib import Path
from xml.sax.saxutils import escape


APRILTAG_PNG_BASE64 = {
    0: "iVBORw0KGgoAAAANSUhEUgAAAAkAAAAJCAYAAADgkQYQAAAAOElEQVR4XmP4DwUMDAwwJhyAxKAYzsCNYTqwAQyTsAGYOIoidA1YTcJJI3PQAYZJeDGyLmTdyGwADR65RztR+ssAAAAASUVORK5CYII=",
    1: "iVBORw0KGgoAAAANSUhEUgAAAAkAAAAJCAYAAADgkQYQAAAANklEQVR4XmP4DwUMDAwwJhzAxBhADIIYWQc6QFIIYWADWE1CVwwXR5dEZ5NuHS4MlcftHpgYAASlyzVXCPCDAAAAAElFTkSuQmCC",
}

APRILTAG_SOURCE_BLOB_SHA = {
    0: "cdbeebd32c17c99c2aa4791537379bf01dd994cd",
    1: "ae6045ff4ff6456dd685c70273cb166b51a51bb8",
}

QR_VERSION = 2
QR_SIZE = 25
QR_DATA_CODEWORDS_Q = 22
QR_ECC_CODEWORDS_Q = 22
QR_MAX_PAYLOAD_BYTES_Q = 20
QR_MASK = 0
APRILTAG_TOTAL_MODULES = 9
APRILTAG_WIDTH_AT_BORDER_MODULES = 5
APRILTAG_MODULE_SIZE_MM = 19.0
APRILTAG_PRINTED_EXTENT_METERS = (
    APRILTAG_TOTAL_MODULES * APRILTAG_MODULE_SIZE_MM / 1000.0
)
APRILTAG_TAG_SIZE_METERS = (
    APRILTAG_WIDTH_AT_BORDER_MODULES * APRILTAG_MODULE_SIZE_MM / 1000.0
)


def decode_rgba_png_matrix(encoded: str) -> list[list[bool]]:
    data = base64.b64decode(encoded)
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Invalid embedded PNG signature")
    offset = 8
    width = height = color_type = bit_depth = None
    compressed = bytearray()
    while offset < len(data):
        length = struct.unpack(">I", data[offset : offset + 4])[0]
        kind = data[offset + 4 : offset + 8]
        body = data[offset + 8 : offset + 8 + length]
        offset += 12 + length
        if kind == b"IHDR":
            width, height, bit_depth, color_type, _, _, _ = struct.unpack(
                ">IIBBBBB", body
            )
        elif kind == b"IDAT":
            compressed.extend(body)
        elif kind == b"IEND":
            break
    if bit_depth != 8 or color_type != 6 or width is None or height is None:
        raise ValueError("Embedded AprilTag PNG must be 8-bit RGBA")

    raw = zlib.decompress(bytes(compressed))
    stride = width * 4
    previous = bytearray(stride)
    rows: list[list[bool]] = []
    cursor = 0
    for _ in range(height):
        filter_type = raw[cursor]
        cursor += 1
        scanline = bytearray(raw[cursor : cursor + stride])
        cursor += stride
        reconstructed = bytearray(stride)
        for index, value in enumerate(scanline):
            left = reconstructed[index - 4] if index >= 4 else 0
            above = previous[index]
            upper_left = previous[index - 4] if index >= 4 else 0
            if filter_type == 0:
                predictor = 0
            elif filter_type == 1:
                predictor = left
            elif filter_type == 2:
                predictor = above
            elif filter_type == 3:
                predictor = (left + above) // 2
            elif filter_type == 4:
                p = left + above - upper_left
                pa, pb, pc = abs(p - left), abs(p - above), abs(p - upper_left)
                predictor = left if pa <= pb and pa <= pc else above if pb <= pc else upper_left
            else:
                raise ValueError(f"Unsupported PNG filter {filter_type}")
            reconstructed[index] = (value + predictor) & 0xFF
        rows.append(
            [sum(reconstructed[x * 4 : x * 4 + 3]) < 384 for x in range(width)]
        )
        previous = reconstructed
    return rows


def gf_tables() -> tuple[list[int], list[int]]:
    exp = [0] * 512
    log = [0] * 256
    value = 1
    for index in range(255):
        exp[index] = value
        log[value] = index
        value <<= 1
        if value & 0x100:
            value ^= 0x11D
    for index in range(255, 512):
        exp[index] = exp[index - 255]
    return exp, log


GF_EXP, GF_LOG = gf_tables()


def gf_multiply(left: int, right: int) -> int:
    if left == 0 or right == 0:
        return 0
    return GF_EXP[GF_LOG[left] + GF_LOG[right]]


def polynomial_multiply(left: list[int], right: list[int]) -> list[int]:
    result = [0] * (len(left) + len(right) - 1)
    for i, a in enumerate(left):
        for j, b in enumerate(right):
            result[i + j] ^= gf_multiply(a, b)
    return result


def reed_solomon_generator(degree: int) -> list[int]:
    result = [1]
    for index in range(degree):
        result = polynomial_multiply(result, [1, GF_EXP[index]])
    return result


def reed_solomon_remainder(data: list[int], degree: int) -> list[int]:
    generator = reed_solomon_generator(degree)
    work = data + [0] * degree
    for index in range(len(data)):
        factor = work[index]
        if factor == 0:
            continue
        for offset, coefficient in enumerate(generator):
            work[index + offset] ^= gf_multiply(coefficient, factor)
    return work[-degree:]


def append_bits(bits: list[bool], value: int, count: int) -> None:
    for shift in range(count - 1, -1, -1):
        bits.append(((value >> shift) & 1) != 0)


def qr_codewords(payload: str) -> list[int]:
    encoded = payload.encode("utf-8")
    if len(encoded) > QR_MAX_PAYLOAD_BYTES_Q:
        raise ValueError(
            f"QR payload is {len(encoded)} bytes; Version 2-Q limit is "
            f"{QR_MAX_PAYLOAD_BYTES_Q}: {payload}"
        )
    bits: list[bool] = []
    append_bits(bits, 0b0100, 4)  # Byte mode
    append_bits(bits, len(encoded), 8)
    for value in encoded:
        append_bits(bits, value, 8)
    capacity = QR_DATA_CODEWORDS_Q * 8
    bits.extend([False] * min(4, capacity - len(bits)))
    bits.extend([False] * ((-len(bits)) % 8))
    data = [
        sum((1 << (7 - bit)) for bit in range(8) if bits[index + bit])
        for index in range(0, len(bits), 8)
    ]
    pads = (0xEC, 0x11)
    while len(data) < QR_DATA_CODEWORDS_Q:
        data.append(pads[(len(data) - ((len(bits) + 7) // 8)) & 1])
    return data + reed_solomon_remainder(data, QR_ECC_CODEWORDS_Q)


def qr_format_bits(mask: int) -> int:
    data = (0b11 << 3) | mask  # Error correction Q is format value 3.
    remainder = data << 10
    while remainder.bit_length() >= 11:
        remainder ^= 0x537 << (remainder.bit_length() - 11)
    return ((data << 10) | remainder) ^ 0x5412


def qr_matrix(payload: str) -> list[list[bool]]:
    modules: list[list[bool | None]] = [
        [None for _ in range(QR_SIZE)] for _ in range(QR_SIZE)
    ]
    function = [[False for _ in range(QR_SIZE)] for _ in range(QR_SIZE)]

    def set_function(x: int, y: int, black: bool) -> None:
        if 0 <= x < QR_SIZE and 0 <= y < QR_SIZE:
            modules[y][x] = black
            function[y][x] = True

    def draw_finder(center_x: int, center_y: int) -> None:
        for dy in range(-4, 5):
            for dx in range(-4, 5):
                distance = max(abs(dx), abs(dy))
                set_function(
                    center_x + dx,
                    center_y + dy,
                    distance != 2 and distance != 4,
                )

    for center in ((3, 3), (QR_SIZE - 4, 3), (3, QR_SIZE - 4)):
        draw_finder(*center)
    for index in range(QR_SIZE):
        if not function[6][index]:
            set_function(index, 6, index % 2 == 0)
        if not function[index][6]:
            set_function(6, index, index % 2 == 0)

    # Version 2 has one non-finder alignment pattern centered at (18, 18).
    for dy in range(-2, 3):
        for dx in range(-2, 3):
            set_function(18 + dx, 18 + dy, max(abs(dx), abs(dy)) != 1)

    format_value = qr_format_bits(QR_MASK)
    for index in range(6):
        set_function(8, index, ((format_value >> index) & 1) != 0)
    set_function(8, 7, ((format_value >> 6) & 1) != 0)
    set_function(8, 8, ((format_value >> 7) & 1) != 0)
    set_function(7, 8, ((format_value >> 8) & 1) != 0)
    for index in range(9, 15):
        set_function(14 - index, 8, ((format_value >> index) & 1) != 0)
    for index in range(8):
        set_function(QR_SIZE - 1 - index, 8, ((format_value >> index) & 1) != 0)
    for index in range(8, 15):
        set_function(8, QR_SIZE - 15 + index, ((format_value >> index) & 1) != 0)
    set_function(8, QR_SIZE - 8, True)

    codewords = qr_codewords(payload)
    data_bits: list[bool] = []
    for value in codewords:
        append_bits(data_bits, value, 8)
    bit_index = 0
    right = QR_SIZE - 1
    while right >= 1:
        if right == 6:
            right -= 1
        upward = ((right + 1) & 2) == 0
        for vertical in range(QR_SIZE):
            y = QR_SIZE - 1 - vertical if upward else vertical
            for column in range(2):
                x = right - column
                if function[y][x]:
                    continue
                black = data_bits[bit_index] if bit_index < len(data_bits) else False
                bit_index += 1
                if (x + y) % 2 == 0:  # Mask pattern 0.
                    black = not black
                modules[y][x] = black
        right -= 2
    if any(value is None for row in modules for value in row):
        raise AssertionError("QR matrix contains unset modules")
    return [[bool(value) for value in row] for row in modules]


def matrix_rects(
    matrix: list[list[bool]], x: float, y: float, module_size: float, quiet: int
) -> str:
    lines: list[str] = []
    for row_index, row in enumerate(matrix):
        run_start = None
        for column in range(len(row) + 1):
            black = column < len(row) and row[column]
            if black and run_start is None:
                run_start = column
            elif not black and run_start is not None:
                lines.append(
                    f'<rect x="{x + (quiet + run_start) * module_size:g}" '
                    f'y="{y + (quiet + row_index) * module_size:g}" '
                    f'width="{(column - run_start) * module_size:g}" '
                    f'height="{module_size:g}"/>'
                )
                run_start = None
    return "\n".join(lines)


def hybrid_board(zone: dict[str, object]) -> str:
    tag_id = int(zone["aprilTagId"])
    room_id = str(zone["roomId"])
    payload = str(zone["qrPayload"])
    april = decode_rgba_png_matrix(APRILTAG_PNG_BASE64[tag_id])
    qr = qr_matrix(payload)

    # A4 landscape. The 9x9 AprilTag black square is 171 mm, leaving a full
    # 19 mm (one-module) white quiet zone above and below within 210 mm.
    april_module = APRILTAG_MODULE_SIZE_MM
    april_quiet = 1
    april_x, april_y = 0.0, 0.0
    qr_module = 2.0
    qr_quiet = 4
    qr_x, qr_y = 222.0, 71.5
    group = ['<rect width="297" height="210" fill="white"/>']
    group.append('<g fill="black" shape-rendering="crispEdges">')
    group.append(matrix_rects(april, april_x, april_y, april_module, april_quiet))
    group.append(matrix_rects(qr, qr_x, qr_y, qr_module, qr_quiet))
    group.append('</g>')
    group.append(f'<text x="255" y="151" text-anchor="middle" font-family="Arial,sans-serif" font-size="7" font-weight="bold">{escape(room_id.upper())} REFERENCE</text>')
    group.append(f'<text x="255" y="162" text-anchor="middle" font-family="Arial,sans-serif" font-size="4.2">tagStandard41h12 / ID {tag_id}</text>')
    group.append(f'<text x="255" y="171" text-anchor="middle" font-family="Arial,sans-serif" font-size="3.2">{escape(payload)}</text>')
    group.append('<text x="255" y="181" text-anchor="middle" font-family="Arial,sans-serif" font-size="2.9">POSE TAG SIZE 95 mm / FULL BITMAP 171 mm</text>')
    group.append('<text x="255" y="187" text-anchor="middle" font-family="Arial,sans-serif" font-size="3.1">PRINT 100% / ACTUAL SIZE</text>')
    group.append('<path d="M251 196 L255 190 L259 196 Z" fill="black"/>')
    group.append('<text x="255" y="203" text-anchor="middle" font-family="Arial,sans-serif" font-size="2.8">TOP</text>')
    return "\n".join(group)


def april_tag_only(tag_id: int) -> str:
    matrix = decode_rgba_png_matrix(APRILTAG_PNG_BASE64[tag_id])
    return (
        '<rect width="209" height="209" fill="white"/>\n'
        '<g fill="black" shape-rendering="crispEdges">\n'
        + matrix_rects(matrix, 0.0, 0.0, APRILTAG_MODULE_SIZE_MM, 1)
        + '\n</g>'
    )


def qr_only(payload: str) -> str:
    return (
        '<rect width="66" height="66" fill="white"/>\n'
        '<g fill="black" shape-rendering="crispEdges">\n'
        + matrix_rects(qr_matrix(payload), 0.0, 0.0, 2.0, 4)
        + '\n</g>'
    )


def svg_document(width_mm: float, height_mm: float, content: str) -> str:
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width_mm:g}mm" '
        f'height="{height_mm:g}mm" viewBox="0 0 {width_mm:g} {height_mm:g}">\n'
        f'{content}\n</svg>\n'
    )


def manifest(config: dict[str, object]) -> dict[str, object]:
    zone = config["referenceTag"]
    space_id = str(config["spaceId"]).strip()
    space_slug = space_id.lower()
    tag_id = int(zone["aprilTagId"])
    room_id = str(zone["roomId"])
    room_uuid = str(zone.get("roomUuid", ""))
    payload = str(zone["qrPayload"])
    qr_codewords(payload)
    if tag_id not in APRILTAG_PNG_BASE64:
        raise ValueError(f"No embedded tagStandard41h12 image for ID {tag_id}")
    reference = {
        "role": "startupGlobalTranslationReference",
        "roomId": room_id,
        "roomUuid": room_uuid,
        "aprilTagFamily": "tagStandard41h12",
        "aprilTagId": tag_id,
        "aprilTagSource": (
            "https://github.com/AprilRobotics/apriltag-imgs/blob/master/"
            f"tagStandard41h12/tag41_12_{tag_id:05d}.png"
        ),
        "aprilTagSourceBlobSha": APRILTAG_SOURCE_BLOB_SHA[tag_id],
        "aprilTagFamilyDefinition": "https://github.com/AprilRobotics/apriltag/blob/master/tagStandard41h12.c",
        "aprilTagTotalModuleCount": APRILTAG_TOTAL_MODULES,
        "aprilTagWidthAtBorderModules": APRILTAG_WIDTH_AT_BORDER_MODULES,
        "aprilTagModuleSizeMeters": APRILTAG_MODULE_SIZE_MM / 1000.0,
        "tagSizeMeters": APRILTAG_TAG_SIZE_METERS,
        "tagSizeDefinition": "distance between AprilTag detection corners at the black-white border boundary",
        "printedAprilTagBitmapExtentMeters": APRILTAG_PRINTED_EXTENT_METERS,
        "aprilTagQuietZoneModules": 1,
        "qrPayload": payload,
        "qrRole": "referencePayloadValidation",
        "qrRoomAssociation": "applicationValidatedNotAutomatic",
        "qrVersion": QR_VERSION,
        "qrErrorCorrection": "Q",
        "qrCoreSizeMeters": 0.05,
        "qrQuietZoneModules": 4,
        "qrCenterOffsetFromAprilTagCenterOnPrintMeters": {
            "right": 0.1505,
            "up": 0.0,
        },
        "poseFusionPolicy": "useAprilTagPoseOnly",
        "qrPosePolicy": "crossValidationOnlyDoNotAverage",
        "hybridBoardFile": (
            f"aag_{space_slug}_{room_id}_hybrid_reference_a4.svg"
        ),
        "aprilTagOnlyFile": f"tagStandard41h12_id_{tag_id}.svg",
        "qrOnlyFile": f"aag_{space_slug}_{room_id}_qr.svg",
    }
    return {
        "schemaVersion": "aag-fiducial-hybrid-tags/v1",
        "spaceId": space_id,
        "printScalePercent": 100,
        "pageSizeMillimeters": {"width": 297.0, "height": 210.0},
        "referenceTag": reference,
    }


def write_outputs(config_path: Path, output_dir: Path, runtime_manifest: Path) -> None:
    config = json.loads(config_path.read_text(encoding="utf-8"))
    result = manifest(config)
    output_dir.mkdir(parents=True, exist_ok=True)
    runtime_manifest.parent.mkdir(parents=True, exist_ok=True)

    zone = config["referenceTag"]
    tag_id = int(zone["aprilTagId"])
    payload = str(zone["qrPayload"])
    reference = result["referenceTag"]
    (output_dir / reference["hybridBoardFile"]).write_text(
        svg_document(297.0, 210.0, hybrid_board(zone)),
        encoding="utf-8",
        newline="\n",
    )
    (output_dir / f"tagStandard41h12_id_{tag_id}.svg").write_text(
        svg_document(209.0, 209.0, april_tag_only(tag_id)),
        encoding="utf-8",
        newline="\n",
    )
    (output_dir / reference["qrOnlyFile"]).write_text(
        svg_document(66.0, 66.0, qr_only(payload)),
        encoding="utf-8",
        newline="\n",
    )

    manifest_text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    manifest_stem = f"{str(result['spaceId']).lower()}_hybrid_tag_manifest"
    (output_dir / f"{manifest_stem}.json").write_text(
        manifest_text, encoding="utf-8", newline="\n"
    )
    runtime_manifest.write_text(manifest_text, encoding="utf-8", newline="\n")
    with (output_dir / f"{manifest_stem}.csv").open(
        "w", encoding="utf-8", newline=""
    ) as stream:
        writer = csv.writer(stream, lineterminator="\n")
        writer.writerow(
            [
                "room_id",
                "april_tag_family",
                "april_tag_id",
                "tag_size_m",
                "printed_bitmap_extent_m",
                "qr_payload",
                "qr_core_size_m",
                "card_file",
            ]
        )
        zone = result["referenceTag"]
        writer.writerow(
            [
                zone["roomId"],
                zone["aprilTagFamily"],
                zone["aprilTagId"],
                zone["tagSizeMeters"],
                zone["printedAprilTagBitmapExtentMeters"],
                zone["qrPayload"],
                zone["qrCoreSizeMeters"],
                zone["hybridBoardFile"],
            ]
        )


def parse_args(argv: list[str]) -> argparse.Namespace:
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--config",
        type=Path,
        default=root / "Tools/FiducialTags/fp1_hybrid_tags.json",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=root / "Documentation/FiducialTags/Generated",
    )
    parser.add_argument(
        "--runtime-manifest",
        type=Path,
        default=root / "Assets/StreamingAssets/AAG/fp1_hybrid_tag_manifest.json",
    )
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    write_outputs(args.config.resolve(), args.output_dir.resolve(), args.runtime_manifest.resolve())
    print(f"Generated hybrid tags in {args.output_dir.resolve()}")
    print(f"Runtime manifest: {args.runtime_manifest.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
