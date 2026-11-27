"""Independent parser audit for two licensed CADCraft external DXF examples.

These upstream CADCraft-generated examples are NOT industrial production files.
Intentionally do not manufacture, repair or export a cutting program.
"""
import collections
import hashlib
import math
import pathlib

import ezdxf

ROOT = pathlib.Path(__file__).resolve().parent / "fixtures"
FIXTURES = {
    "cadcraft-sample-bracket.dxf": {
        "blob_sha1": "873791200e59035a0fc333072fe301495f7bcfd7",
        "types": {
            "LWPOLYLINE": 6, "CIRCLE": 7, "LINE": 16,
            "HATCH": 4, "DIMENSION": 6, "TEXT": 6, "MTEXT": 1,
        },
    },
    "cadcraft-sample-floorplan.dxf": {
        "blob_sha1": "9fcb53564f6b973eda34f9dd86ccd662489803bd",
        "types": {
            "LWPOLYLINE": 4, "LINE": 7, "ARC": 1,
            "TEXT": 2, "DIMENSION": 1, "CIRCLE": 1,
        },
    },
}


def verify_external_examples():
    for name, expected in FIXTURES.items():
        path = ROOT / name
        data = path.read_bytes()
        fingerprint = hashlib.sha1(
            b"blob " + str(len(data)).encode("ascii") + b"\0" + data
        ).hexdigest()
        if fingerprint != expected["blob_sha1"]:
            raise AssertionError(f"{name}: licensed source fixture changed")
        drawing = ezdxf.readfile(path)
        if drawing.dxfversion != "AC1032" or drawing.header.get("$INSUNITS") != 1:
            raise AssertionError(f"{name}: unexpected DXF version or inch units")
        model = list(drawing.modelspace())
        inventory = dict(collections.Counter(entity.dxftype() for entity in model))
        if inventory != expected["types"]:
            raise AssertionError(
                f"{name}: independent DXF entity inventory changed: {inventory}"
            )
        simple_count = 0
        for entity in model:
            t = entity.dxftype()
            if t == "LINE":
                coords = [*entity.dxf.start, *entity.dxf.end]
            elif t == "CIRCLE":
                coords = [*entity.dxf.center, entity.dxf.radius]
                if entity.dxf.radius <= 0:
                    raise AssertionError(f"{name}: invalid circle radius")
            elif t == "ARC":
                coords = [
                    *entity.dxf.center, entity.dxf.radius,
                    entity.dxf.start_angle, entity.dxf.end_angle,
                ]
                if entity.dxf.radius <= 0:
                    raise AssertionError(f"{name}: invalid arc radius")
            else:
                continue  # Complex/annotation entities are NOT cut geometry.
            if not all(math.isfinite(v) for v in coords):
                raise AssertionError(f"{name}: nonfinite {t} geometry")
            simple_count += 1
        print(
            f"{name}: external ezdxf read PASS; {len(model)} entities; "
            f"{simple_count} elementary shapes; R2018/inch units; "
            "unsupported CAD/CAM entities remain BLOCKED"
        )


def verify_manufacturing_examples():
    from ezdxf import bbox
    examples = {
        "Bottom_Plate_240mm_240mm.dxf": ("adbf15cc76aeef0c4705751a1624ed7e1e05cad4", {"LINE":178,"ARC":56,"CIRCLE":132}, 240, 240),
        "PC_Plate_117mm_122mm.dxf": ("d760aad15a1b0a794500dc936bec353ed20b8fc1", {"LINE":60,"CIRCLE":72}, 117, 122),
        "Top_Plate_240mm_240mm.dxf": ("0742978c71c30349faa752b62d7452102cffc969", {"LINE":96,"ARC":32,"CIRCLE":64}, 240, 240),
    }
    for name, (sha, inventory, width, height) in examples.items():
        path = ROOT / "lgdxrobot2" / name
        data = path.read_bytes()
        assert hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data).hexdigest() == sha, name
        drawing = ezdxf.readfile(path)
        assert drawing.dxfversion == "AC1014" and drawing.header.get("$INSUNITS") == 4, name
        model = list(drawing.modelspace())
        assert dict(collections.Counter(e.dxftype() for e in model)) == inventory, name
        # Independently classify the original 3D export, not the core preview.
        expected_xy = {"Bottom_Plate_240mm_240mm.dxf": 122,
                       "PC_Plate_117mm_122mm.dxf": 44,
                       "Top_Plate_240mm_240mm.dxf": 64}[name]
        def in_xy(entity):
            points = ([entity.dxf.start, entity.dxf.end]
                      if entity.dxftype() == "LINE" else [entity.dxf.center])
            return all(abs(point.z) <= 1e-12 for point in points)
        xy = [entity for entity in model if in_xy(entity)]
        assert len(xy) == expected_xy, (name, len(xy))
        print(f"{name}: independent XY classification: {len(xy)} planar, {len(model)-len(xy)} nonplanar")
        bounds = bbox.extents(model, fast=False)
        dimensions = sorted([bounds.size.x, bounds.size.y])
        assert all(math.isfinite(v) and v > 0 for v in dimensions), name
        # Nominal plate dimensions are documented in the upstream filenames;
        # report measured extents without assuming exact nominal equality.
        print(f"{name}: independent manufacturing DXF audit PASS; "
              f"{len(model)} entities; bounds {dimensions}; nominal {width} x {height} mm")
        for entity in model:
            if entity.dxftype() in ("ARC", "CIRCLE"):
                assert math.isfinite(entity.dxf.radius) and entity.dxf.radius > 0, name


if __name__ == "__main__":
    verify_external_examples()
    verify_manufacturing_examples()
