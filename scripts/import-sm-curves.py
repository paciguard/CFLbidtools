"""Build the XDC/SM catalog from verified raster traces and the PDF header index."""

import argparse
import csv
import hashlib
import json
import re
from pathlib import Path

import numpy as np
import pypdfium2 as pdf
from PIL import ImageDraw


def read_anchors(filename):
    panels = {}
    for line in filename.read_text(encoding="utf-8").splitlines():
        if not line.split("#", 1)[0].strip():
            continue
        fields = line.split()
        if fields[1] in {"H", "P", "N"}:
            page, panel, q, low, high, *corners = fields
            image_points = [tuple(map(float, pair.split(","))) for pair in corners]
            grid = [(0, 1), (1, 1), (0, 0), (1, 0)]
            matrix, values = [], []
            for (x, y), (u, v) in zip(image_points, grid, strict=True):
                matrix.extend([[x, y, 1, 0, 0, 0, -u*x, -u*y], [0, 0, 0, x, y, 1, -v*x, -v*y]])
                values.extend([u, v])
            transform = np.append(np.linalg.solve(matrix, values), 1).reshape((3, 3))
            spans = tuple(map(float, (q, low, high)))
            traces = {}
            pixels = {}
            panels[int(page), panel] = (traces, pixels)
        else:
            diameter = int(fields[0])
            if diameter in traces:
                raise ValueError(f"Duplicate {panel} trace for D{diameter}, page {page}")
            pixels[diameter] = [tuple(map(int, pair.split(","))) for pair in fields[1:]]
            q, low, high = spans
            metric = []
            for i, (x, y) in enumerate(pixels[diameter]):
                u, v, z = transform @ [x, y, 1]
                flow, value = q*u/z, low+(high-low)*v/z
                if i == 0 and panel in {"H", "P"} and abs(flow) <= q*.005:
                    flow = 0
                if not 0 <= flow <= q or not low-0.01 <= value <= high+0.01:
                    raise ValueError(f"Trace outside calibrated grid: {page} {panel} D{diameter}")
                metric.append([round(flow, 3), round(value, 3)])
            if len(metric) < 5 or any(b[0] <= a[0] for a, b in zip(metric, metric[1:])):
                raise ValueError(f"Insufficient/inconsistent points: {page} {panel} D{diameter}")
            traces[diameter] = metric
    return panels


def reviewed_impellers(panels, page):
    if (page, "H") not in panels:
        return []
    head, power, npshr = (panels[page, panel][0] for panel in ("H", "P", "N"))
    if sorted(head) != sorted(power) or sorted(npshr) != [min(head), max(head)]:
        raise ValueError(f"Unmatched diameters on page {page}")
    impellers = []
    for diameter in sorted(head):
        h, p = np.array(head[diameter]), np.array(power[diameter])
        q = np.linspace(max(h[0, 0], p[0, 0]), min(h[-1, 0], p[-1, 0]), 300)
        eff = 100 * 9.81 / 3600 * q * np.interp(q, *h.T) / np.interp(q, *p.T)
        if not 50 <= max(eff) <= 97 or p[0, 1] <= 0:
            raise ValueError(f"Implausible head/power pairing on page {page}, D{diameter}: {max(eff):.1f}%")
        impellers.append({"diameterMm": diameter, "head": head[diameter],
                          "power": power[diameter], "npshr": npshr.get(diameter, [])})
    return impellers


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("numbers", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--overlay-dir", type=Path)
    args = parser.parse_args()
    folder = Path(__file__).parent
    panels = read_anchors(folder / "sm-scan-anchors.txt")
    with (folder / "sm-sheets.csv").open(newline="", encoding="utf-8") as source:
        sheets = list(csv.DictReader(source))
    doc = pdf.PdfDocument(args.pdf)
    pages = [int(sheet["page"]) for sheet in sheets]
    if pages != list(range(2, len(doc)+1)) or len({s["original_curve"] for s in sheets}) != len(sheets):
        raise ValueError("Check the PDF sheet index before numbering the curves")
    assignments = json.loads(args.numbers.read_text(encoding="utf-8-sig"))
    serial = max(int(re.search(r"_(\d+)$", row["Number"])[1])
                 for row in assignments if row["Series"] == "XDC")
    checksum = hashlib.sha256(args.pdf.read_bytes()).hexdigest()
    source_file = "SM \u66f2\u7ebf.pdf"
    rows = []
    for i, (sheet, page) in enumerate(zip(sheets, pages, strict=True), start=1):
        impellers = reviewed_impellers(panels, page)
        size = sheet["size"]
        rows.append({"curveNumber": f"SM_{size}_{serial+i}", "size": size, "rpm": int(sheet["rpm"]),
                     "originalCurveNumber": sheet["original_curve"], "sourcePage": page,
                     "impellers": impellers})
        if impellers and args.overlay_dir:
            args.overlay_dir.mkdir(parents=True, exist_ok=True)
            img = doc[page-1].render(scale=1.75).to_pil().convert("RGB")
            draw = ImageDraw.Draw(img)
            for name, color in (("H", "red"), ("P", "blue"), ("N", "green")):
                for trace in panels[page, name][1].values():
                    draw.line(trace, fill=color, width=2)
            img.save(args.overlay_dir / f"sm-p{page:02}.png")
    def encode(value):
        return json.dumps(value, ensure_ascii=True, separators=(",", ":"))

    metadata = encode({"sourceFile": source_file, "sourceSha256": checksum})
    args.output.write_text(metadata[:-1] + ',\n"records":[\n' + ",\n".join(map(encode, rows)) + "\n]}\n",
                           encoding="utf-8")
    print(f"{len(rows)} sheets, {sum(bool(row['impellers']) for row in rows)} traced; "
          f"XDC serials {serial+1}-{serial+len(rows)}.")


if __name__ == "__main__":
    main()
