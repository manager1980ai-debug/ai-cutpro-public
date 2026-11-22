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


if __name__ == "__main__":
    verify_external_examples()
