import hashlib
import importlib.util
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


SCRIPT = Path(__file__).with_name("generate_hybrid_tags.py")
SPEC = importlib.util.spec_from_file_location("generate_hybrid_tags", SCRIPT)
generator = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(generator)


def matrix_digest(matrix):
    bits = "".join("1" if value else "0" for row in matrix for value in row)
    return hashlib.sha256(bits.encode("ascii")).hexdigest()


class HybridTagGeneratorTests(unittest.TestCase):
    def test_official_apriltag_id_zero_matrix_is_stable(self):
        matrix = generator.decode_rgba_png_matrix(
            generator.APRILTAG_PNG_BASE64[0]
        )
        self.assertEqual((len(matrix), len(matrix[0])), (9, 9))
        self.assertEqual(
            matrix_digest(matrix),
            "9bb9e2bd33c9a27e9103e05b029cb2529ab0808e3afd92af4a4abaad82fa6e8c",
        )

    def test_official_apriltag_id_one_matrix_is_stable(self):
        matrix = generator.decode_rgba_png_matrix(
            generator.APRILTAG_PNG_BASE64[1]
        )
        self.assertEqual((len(matrix), len(matrix[0])), (9, 9))
        self.assertEqual(
            matrix_digest(matrix),
            "935571ba41b0c3aad923bad31ef13e92679425c1b2a9c3dd56f5bdbd18404904",
        )

    def test_room3_qr_matrix_matches_reference_vector(self):
        matrix = generator.qr_matrix("AAG-FP1-ZONE:room3")
        self.assertEqual((len(matrix), len(matrix[0])), (25, 25))
        self.assertEqual(
            matrix_digest(matrix),
            "ef84fcf61cbc4103ee0fbe0bea1fa8a5191b9b324351bf346bee1538b2c0bd9d",
        )

    def test_generation_contract_and_svg_dimensions(self):
        config = SCRIPT.with_name("fp1_hybrid_tags.json")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "generated"
            runtime = root / "runtime.json"
            generator.write_outputs(config, output, runtime)

            manifest = json.loads(runtime.read_text(encoding="utf-8"))
            reference = manifest["referenceTag"]
            self.assertEqual(reference["roomId"], "room3")
            self.assertEqual(reference["aprilTagFamily"], "tagStandard41h12")
            self.assertEqual(reference["aprilTagId"], 0)
            self.assertEqual(reference["aprilTagTotalModuleCount"], 9)
            self.assertEqual(reference["aprilTagWidthAtBorderModules"], 5)
            self.assertEqual(reference["tagSizeMeters"], 0.095)
            self.assertEqual(reference["printedAprilTagBitmapExtentMeters"], 0.171)
            self.assertEqual(reference["qrPayload"], "AAG-FP1-ZONE:room3")
            self.assertEqual(reference["poseFusionPolicy"], "useAprilTagPoseOnly")

            svg = ET.parse(output / reference["hybridBoardFile"]).getroot()
            self.assertEqual(svg.attrib["width"], "297mm")
            self.assertEqual(svg.attrib["height"], "210mm")
            self.assertEqual(svg.attrib["viewBox"], "0 0 297 210")

    def test_fp2_room8_generation_is_namespaced(self):
        config = SCRIPT.with_name("fp2_hybrid_tags.json")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "generated"
            runtime = root / "fp2_hybrid_tag_manifest.json"
            generator.write_outputs(config, output, runtime)

            manifest = json.loads(runtime.read_text(encoding="utf-8"))
            reference = manifest["referenceTag"]
            self.assertEqual(manifest["spaceId"], "FP2")
            self.assertEqual(reference["roomId"], "room8")
            self.assertEqual(
                reference["roomUuid"],
                "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a",
            )
            self.assertEqual(reference["aprilTagId"], 1)
            self.assertEqual(reference["qrPayload"], "AAG-FP2-ZONE:room8")
            self.assertEqual(
                reference["hybridBoardFile"],
                "aag_fp2_room8_hybrid_reference_a4.svg",
            )
            self.assertTrue((output / reference["hybridBoardFile"]).is_file())
            self.assertTrue((output / "fp2_hybrid_tag_manifest.json").is_file())
            self.assertFalse((output / "fp1_hybrid_tag_manifest.json").exists())


if __name__ == "__main__":
    unittest.main()
