#!/usr/bin/env python3
"""Builds catalog/places/municipalities.json: Sweden's municipalities as polygons in SWEREF 99 TM (#330).

Run by hand when the municipalities change. Standard library only.

Source: Statistics Sweden's digital boundaries, CC0, in SWEREF 99 TM.
https://scb.se/hitta-statistik/regional-statistik-och-kartor/regionala-indelningar/digitala-granser/

SCB simplifies the borders for maps of statistics and says they do not suit analysis. That is enough to answer "the
sites in Gävle" and not to settle a border; exact borders are Lantmäteriet's.
"""
import io
import json
import pathlib
import struct
import sys
import urllib.request
import zipfile

SOURCE = "https://scb.se/contentassets/3443fea3fa6640f7a57ea15d9a372d33/shape_svenska_260225.zip"
TARGET = pathlib.Path(__file__).resolve().parent.parent / "catalog" / "places" / "municipalities.json"


def records(dbf: bytes, encoding: str) -> list[dict[str, str]]:
    """The attribute table of a shapefile."""
    count, header, size = struct.unpack("<xxxxIHH", dbf[:12])
    fields, at = [], 32
    while dbf[at] != 0x0D:
        fields.append((dbf[at : at + 11].split(b"\0")[0].decode("ascii"), dbf[at + 16]))
        at += 32
    rows = []
    for i in range(count):
        at = header + i * size + 1  # the first byte marks a deleted row
        row = {}
        for name, length in fields:
            row[name.upper()] = dbf[at : at + length].decode(encoding).strip()
            at += length
        rows.append(row)
    return rows


def shapes(shp: bytes) -> list[list[list[tuple[float, float]]]]:
    """The rings of each polygon record in a shapefile."""
    result, at = [], 100
    while at < len(shp):
        length = struct.unpack(">i", shp[at + 4 : at + 8])[0] * 2
        body = shp[at + 8 : at + 8 + length]
        at += 8 + length
        if struct.unpack("<i", body[:4])[0] != 5:
            raise SystemExit("expected polygons")
        parts, points = struct.unpack("<ii", body[36:44])
        starts = list(struct.unpack(f"<{parts}i", body[44 : 44 + 4 * parts])) + [points]
        base = 44 + 4 * parts
        xy = struct.unpack(f"<{points * 2}d", body[base : base + 16 * points])
        result.append(
            [[(xy[2 * j], xy[2 * j + 1]) for j in range(starts[p], starts[p + 1])] for p in range(parts)]
        )
    return result


def signed_area(ring: list[tuple[float, float]]) -> float:
    return sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(ring, ring[1:])) / 2


def inside(point: tuple[float, float], ring: list[tuple[float, float]]) -> bool:
    x, y, hit = point[0], point[1], False
    for (x1, y1), (x2, y2) in zip(ring, ring[1:]):
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
            hit = not hit
    return hit


def ring_wkt(ring: list[tuple[float, float]]) -> str:
    return "(" + ",".join(f"{round(x)} {round(y)}" for x, y in ring) + ")"


def wkt(rings: list[list[tuple[float, float]]]) -> str:
    """A shapefile's outer rings run clockwise and its holes the other way; WKT wants each hole with its polygon."""
    outer = [[r] for r in rings if signed_area(r) < 0]
    for hole in (r for r in rings if signed_area(r) >= 0):
        home = next((p for p in outer if inside(hole[0], p[0])), None)
        if home is None:
            raise SystemExit("a hole outside every polygon")
        home.append(hole)
    return "MULTIPOLYGON(" + ",".join("(" + ",".join(ring_wkt(r) for r in p) + ")" for p in outer) + ")"


def main() -> None:
    request = urllib.request.Request(SOURCE, headers={"User-Agent": "cmdb-build-places"})
    with urllib.request.urlopen(request, timeout=90) as response:
        archive = zipfile.ZipFile(io.BytesIO(response.read()))
    inner = next(n for n in archive.namelist() if n.lower().startswith("kommun"))
    files = zipfile.ZipFile(io.BytesIO(archive.read(inner)))
    of = lambda ending: files.read(next(n for n in files.namelist() if n.lower().endswith(ending)))
    encoding = sys.argv[1] if len(sys.argv) > 1 else "cp1252"
    rows, polygons = records(of(".dbf"), encoding), shapes(of(".shp"))
    if len(rows) != 290 or len(polygons) != 290:
        raise SystemExit(f"expected Sweden's 290 municipalities, got {len(rows)} rows and {len(polygons)} shapes")
    code = next(k for k in rows[0] if rows[0][k].isdigit() and len(rows[0][k]) == 4)
    name = next(k for k in rows[0] if k != code and not rows[0][k].isdigit())
    places = sorted(
        ({"code": r[code], "name": r[name], "wkt": wkt(p)} for r, p in zip(rows, polygons)), key=lambda p: p["code"]
    )
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    TARGET.write_text(json.dumps(places, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8", newline="\n")
    print(f"{TARGET}: {len(places)} municipalities, {TARGET.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
