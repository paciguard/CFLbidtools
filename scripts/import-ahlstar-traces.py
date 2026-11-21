"""Extract calibrated vector paths from reviewed AHLSTAR chart pages."""
import argparse
import ctypes as c
import json
from pathlib import Path

import pypdfium2 as pdfium
import pypdfium2.raw as pdf


# Grid calibrations are in PDF points (origin at bottom left); flow axis is l/s.
# Path indices are local to the traced curve layer, in the producer's draw order.
PROFILES = {
    1000: ((84.1, 396.8, 250), (366.1, 624.2, 50, 25), (116.5, 325.8, 100),
           {390: (0, 1, None), 435: (2, 3, 16), 485: (4, 5, None), 530: (6, 7, 17)}, 2238),
    1900: ((85.1, 397.4, 1000), (366.1, 624.2, 50, 50), (116.8, 325.8, 200),
           {420: (0, 1, None), 425: (2, 3, 15), 445: (4, 5, None), 477: (6, 7, 14)}, 2029),
    2100: ((79.3, 393.1, 125), (364.9, 624.0, 40, 40), (115.9, 325.8, 30),
           {210: (0, 1, 14), 240: (2, 3, None), 270: (4, 5, 13)}, 1927),
    2400: ((77.7, 395.6, 30), (365.7, 628.5, 15, 15), (112.9, 325.8, 5),
           {260: (16, 2, 0), 300: (17, 3, None), 330: (18, 4, 1)}, 3361),
}


def paths(page, segment_count):
    layers = [obj for obj in page.get_objects(filter=[pdf.FPDF_PAGEOBJ_PATH])
              if pdf.FPDFPath_CountSegments(obj) == segment_count]
    if len(layers) != 1:
        raise ValueError(f"expected one curve layer, found {len(layers)}")
    obj = layers[0]
    transform = obj.get_matrix()
    parts = []
    for index in range(segment_count):
        segment = pdf.FPDFPath_GetPathSegment(obj, index)
        kind = pdf.FPDFPathSegment_GetType(segment)
        if kind == pdf.FPDF_SEGMENT_MOVETO:
            parts.append([])
        x, y = c.c_float(), c.c_float()
        pdf.FPDFPathSegment_GetPoint(segment, c.byref(x), c.byref(y))
        parts[-1].append((*transform.on_point(x.value, y.value), kind))
    return parts


def trace(part, flow_axis, vertical_axis):
    x0, x1, q1 = flow_axis
    y0, y1, value1 = vertical_axis
    samples = [part[0][:2]]
    index = 1
    while index < len(part):
        if part[index][2] == pdf.FPDF_SEGMENT_BEZIERTO:
            controls = [samples[-1], *(point[:2] for point in part[index:index+3])]
            if len(controls) != 4 or any(p[2] != pdf.FPDF_SEGMENT_BEZIERTO for p in part[index:index+3]):
                raise ValueError("invalid cubic path")
            for step in range(1, 7):
                t = step / 6
                u = 1 - t
                weights = (u**3, 3*u*u*t, 3*u*t*t, t**3)
                samples.append(tuple(sum(v*w for v, w in zip(coords, weights)) for coords in zip(*controls)))
            index += 3
        elif part[index][2] == pdf.FPDF_SEGMENT_LINETO:
            samples.append(part[index][:2])
            index += 1
        else:
            raise ValueError("unexpected path segment")
    points = []
    for x, y in samples:
        flow = round(3.6 * q1 * (x - x0) / (x1 - x0), 4)
        value = round(value1 * (y - y0) / (y1 - y0), 4)
        if flow < 0 or value <= 0 or flow > 3.6 * q1 * 1.02 or value > value1 * 1.05:
            raise ValueError("trace leaves reviewed chart scale")
        if points and flow <= points[-1][0]:
            if abs(flow - points[-1][0]) < 0.001:
                continue
            raise ValueError("trace is not single-valued in flow")
        points.append([flow, value])
    if len(points) < 10:
        raise ValueError("curve path has insufficient samples")
    return points


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("pdf", type=Path)
    cli.add_argument("index", type=Path)
    cli.add_argument("output", type=Path)
    args = cli.parse_args()
    records = {row["page"]: row for row in json.loads(args.index.read_text(encoding="ascii"))["records"]}
    result = []
    with pdfium.PdfDocument(args.pdf) as document:
        for page, (flow, head, power, traces, count) in PROFILES.items():
            row = records[page]
            if not set(traces).issubset(row["trimDiametersMm"]) or row["kind"] != "fixed" or not row["rpm"]:
                raise ValueError(f"unexpected chart metadata on page {page}")
            parts = paths(document[page - 1], count)
            result.append({
                "sourceFile": args.pdf.name, "page": page, "curveNumber": row["curveNumber"], "rpm": row["rpm"],
                "impellers": [
                    {"diameterMm": diameter, "head": trace(parts[h], flow, head[:3]),
                     "power": trace(parts[p], flow, power),
                     "npshr": trace(parts[n], flow, (*head[:2], head[3])) if n is not None else []}
                    for diameter, (h, p, n) in sorted(traces.items(), reverse=True)
                ],
            })
    args.output.write_text(json.dumps(result, separators=(",", ":")) + "\n", encoding="ascii")
    print(f"Extracted {sum(len(row['impellers']) for row in result)} plotted diameters on {len(result)} pages.")


if __name__ == "__main__":
    main()
