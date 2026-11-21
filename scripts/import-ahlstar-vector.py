"""Extract and audit fixed-speed vector charts in the AHLSTAR source PDF."""
import argparse
import bisect
import ctypes as c
import gzip
import json
import re
import runpy
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from statistics import median

import pypdfium2 as pdfium
import pypdfium2.raw as pdf


HERE = Path(__file__).resolve().parent
decode = runpy.run_path(str(HERE / "import-ahlstar-index.py"))["decode"]
REVIEWED = runpy.run_path(str(HERE / "import-ahlstar-traces.py"))["PROFILES"]
NUMBER = re.compile(r"(?:\d+(?:[.,]\d+)?|[.,]\d+)")


class Unmatched(ValueError):
    pass


@dataclass(frozen=True)
class Grid:
    left: float
    bottom: float
    right: float
    top: float
    xs: tuple
    ys: tuple

    @property
    def width(self):
        return self.right - self.left

    @property
    def height(self):
        return self.top - self.bottom


def strokes(page):
    for obj in page.get_objects(filter=[pdf.FPDF_PAGEOBJ_PATH]):
        l, b, r, t = obj.get_bounds()
        if l > 160 or r - l < 12 or r > 520 or t > 720 or b < 55:
            continue
        fill, stroke = c.c_int(), c.c_int()
        pdf.FPDFPath_GetDrawMode(obj, c.byref(fill), c.byref(stroke))
        if fill.value or not stroke.value:
            continue
        transform = obj.get_matrix()
        part = []
        for i in range(pdf.FPDFPath_CountSegments(obj)):
            segment = pdf.FPDFPath_GetPathSegment(obj, i)
            kind = pdf.FPDFPathSegment_GetType(segment)
            if kind == pdf.FPDF_SEGMENT_MOVETO and part:
                yield part
                part = []
            x, y = c.c_float(), c.c_float()
            pdf.FPDFPathSegment_GetPoint(segment, c.byref(x), c.byref(y))
            part.append((*transform.on_point(x.value, y.value), kind))
        if part:
            yield part


def distinct(values, tolerance=.35):
    result = []
    for value in sorted(values):
        if not result or value - result[-1] > tolerance:
            result.append(value)
    return tuple(result)


def grids(strokes):
    horizontal, vertical = [], []
    for path in strokes:
        if len(path) != 2:
            continue
        (ax, ay, _), (bx, by, _) = path
        if abs(ay-by) < .12 and abs(ax-bx) > 150:
            horizontal.append((min(ax,bx), max(ax,bx), (ay+by)/2))
        if abs(ax-bx) < .12 and abs(ay-by) > 45:
            vertical.append(((ax+bx)/2, min(ay,by), max(ay,by)))
    found = []
    for x, b, t in vertical:
        if any(abs(t-g.top)<.5 and abs(b-g.bottom)<.5 for g in found):
            continue
        xs = distinct(u for u, low, high in vertical if abs(low-b)<.5 and abs(high-t)<.5)
        if len(xs) < 5 or xs[-1]-xs[0] < 150:
            continue
        l, r = xs[0], xs[-1]
        ys = distinct(y for a, z, y in horizontal if abs(a-l)<1 and abs(z-r)<1 and b-.6<=y<=t+.6)
        if len(ys) >= 5 and abs(ys[0]-b)<1 and abs(ys[-1]-t)<1:
            found.append(Grid(l,b,r,t,xs,ys))
    return sorted(found, key=lambda grid: grid.bottom, reverse=True)


def numerals(text):
    text = decode(text).strip().replace("\u2212", "-")
    if not text or any(not NUMBER.fullmatch(word) for word in text.split()):
        return ()
    return tuple(set(float(word.replace(",", ".")) for word in text.split()))


def axis(text, grid, side, minimum_labels=3, expected=None, marks=()):
    if side == "bottom":
        zero, end = grid.left, grid.right
        labels = [(x, numerals(text.get_text_bounded(x-13, grid.bottom-17, x+13, grid.bottom-2)))
                  for x in grid.xs]
    elif side == "top":
        zero, end = grid.left, grid.right
        labels = [(x, numerals(text.get_text_bounded(x-13, grid.top+1, x+13, grid.top+12)))
                  for x in grid.xs]
    else:
        zero, end = grid.bottom, grid.top
        l, r = (grid.left-36, grid.left-.8) if side == "left" else (grid.right+.8, grid.right+27)
        ticks = {y: set() for y in grid.ys}
        for value,(left,bottom,right,top) in marks:
            if left<l or right>r:
                continue
            middle = (top+bottom)/2
            if side == "right" and middle > grid.bottom + grid.height*.68:
                continue
            y = min(grid.ys,key=lambda tick:abs(tick-middle))
            if abs(y-middle) <= 1.8:
                ticks[y].add(value)
        labels = list(ticks.items())
    if expected is None and (0 not in labels[0][1] or abs(labels[0][0]-zero)>1):
        raise Unmatched("missing printed axis zero: " + side)
    anchors = [(position-zero, value) for position, values in labels if position-zero > 2 for value in values if value>0]
    if expected is not None:
        matched = [(dx, value) for dx,value in anchors if abs(dx*expected-value) <= value*.012]
        if len(matched)<2 or max(dx for dx,_ in matched)<(end-zero)*.3:
            raise Unmatched("flow labels disagree between plots")
        return expected, []
    scores = []
    for distance, value in anchors:
        slope = value/distance
        fitted = [(dx, actual) for dx, actual in anchors if abs(slope*dx-actual) <= actual*.012]
        scores.append((len(fitted), slope, fitted))
    if not scores:
        raise Unmatched("missing numeric axis labels: " + side)
    hits, slope, fitted = max(scores, key=lambda item: item[0])
    span = .20 if side == "right" else .30
    if hits < minimum_labels-1 or max(dx for dx, _ in fitted) < (end-zero)*span:
        raise Unmatched("axis calibration not corroborated: " + side)
    if any(n == hits and abs(other/slope-1) > .025 for n, other, _ in scores):
        raise Unmatched("ambiguous axis scale: " + side)
    return median(actual/dx for dx, actual in fitted), [(round(zero+dx,2), actual) for dx, actual in fitted]


def flatten(part):
    if len(part)<8:
        return None
    samples = [part[0][:2]]
    i = 1
    while i < len(part):
        if part[i][2] == pdf.FPDF_SEGMENT_BEZIERTO:
            chunk = part[i:i+3]
            if len(chunk)!=3 or any(p[2]!=pdf.FPDF_SEGMENT_BEZIERTO for p in chunk):
                return None
            for step in range(1,5):
                t = step/4
                u = 1-t
                if step == 1:
                    controls = (samples[-1], *(p[:2] for p in chunk))
                weights = (u**3,3*u*u*t,3*u*t*t,t**3)
                samples.append(tuple(sum(a*b for a,b in zip(coords,weights)) for coords in zip(*controls)))
            i += 3
        elif part[i][2] == pdf.FPDF_SEGMENT_LINETO:
            samples.append(part[i][:2])
            i += 1
        else:
            return None
    if samples[0][0]>samples[-1][0]:
        samples.reverse()
    result = [samples[0]]
    for x,y in samples[1:]:
        if x < result[-1][0]-.18:
            return None
        if x > result[-1][0]+.015:
            result.append((x,y))
        elif abs(y-result[-1][1])>1.8:
            return None
    return result if len(result)>=8 else None


def curve_paths(parts, grid):
    for index, part in parts:
        x = [p[0] for p in part]
        y = [p[1] for p in part]
        if (max(x)-min(x) < grid.width*.16 or min(x)<grid.left-.6 or max(x)>grid.right+2 or
            min(y)<grid.bottom-1 or max(y)>grid.top+5 or max(y)-min(y)<2):
            continue
        path = flatten(part)
        if path:
            yield index, path


def number_marks(page, text):
    chunks = []
    for text_obj in page.get_objects(filter=[pdf.FPDF_PAGEOBJ_TEXT], textpage=text):
        l,b,r,t = text_obj.get_bounds()
        if l < 35 or l > 510 or b < 80 or t > 665:
            continue
        raw = re.sub(r"\s*(?:mm|in)\s*$", "", decode(text_obj.extract()).strip(), flags=re.I)
        if raw and NUMBER.fullmatch(raw):
            if raw.isdigit() and len(raw)==1:
                chunks.append((raw,l,b,r,t))
            else:
                yield float(raw.replace(",",".")),(l,b,r,t)
        elif raw in (".",","):
            chunks.append((raw,l,b,r,t))
    digits = []
    for char,l,b,r,t in sorted(chunks,key=lambda item:item[1]):
        options = [g for g in digits if -.4 <= l-g[-1][3] <= 3.2 and abs(b-g[-1][2])<1.35
                   and (char in (".",",") or g[-1][0] in (".",",") or abs(t-g[-1][4])<1.5)]
        if len(options)==1:
            options[0].append((char,l,b,r,t))
        else:
            digits.append([(char,l,b,r,t)])
    for group in digits:
        value = "".join(piece[0] for piece in group)
        if NUMBER.fullmatch(value):
            yield float(value.replace(",",".")),(group[0][1],min(p[2] for p in group),group[-1][3],max(p[4] for p in group))


def labeled(path, labels, start=False):
    x, y = path[0 if start else -1]
    options = {}
    for value, (left, bottom, right, top) in labels:
        dx = max(left-x, x-right, 0)
        dy = max(bottom-y, y-top, 0)
        if dx>9 or dy>9 or (start and (left-x>9 or bottom<y-1.5)):
            continue
        score = dx + 1.2*dy + .12*abs(x-left) + .04*abs(y-bottom)
        options[value] = min(options.get(value, float("inf")), score)
    ranked = sorted((v,k) for k,v in options.items())
    if not ranked or len(ranked)>1 and ranked[1][0]-ranked[0][0]<1.25:
        return None
    return ranked[0][1]


def at(points, x):
    pos = bisect.bisect_left(points, (x,-float("inf")))
    if pos == 0:
        return points[0][1]
    if pos == len(points):
        return points[-1][1]
    a,b = points[pos-1:pos+1]
    return a[1]+(b[1]-a[1])*(x-a[0])/(b[0]-a[0])


def samples(path, grid, q_slope, slope):
    # Retain PDF path breaks needed to reproduce the plotted shape to about one pixel.
    points = [(max(0, (x-grid.left)*q_slope), (y-grid.bottom)*slope) for x,y in path if x >= grid.left-.25]
    if len(points)<8 or any(y<=0 for _,y in points):
        raise Unmatched("curve outside calibrated domain")
    a,b = points[0][0],points[-1][0]
    knots = [(a+(b-a)*i/60) for i in range(61)]
    for function in (min,max):
        knots.append(function(points, key=lambda point: point[1])[0])
    result = []
    for q in sorted(knots):
        flow = max(0, round(q,5))
        if not result or flow > result[-1][0]:
            result.append([flow,round(at(points,q),5)])
    return result


def verify_pair(head, power):
    h = [tuple(pair) for pair in head]
    p = [tuple(pair) for pair in power]
    lo, hi = max(h[0][0],p[0][0]), min(h[-1][0],p[-1][0])
    if hi <= lo:
        return False
    efficiencies = []
    for i in range(21):
        q = lo+(hi-lo)*i/20
        efficiencies.append(9.81*q*at(h,q)/(3600*at(p,q)))
    return .19 <= max(efficiencies) <= .985


def selection_witness(impellers):
    # A chart must contain a common, nonempty H/P/NPSHr range near its BEP.
    all_traces = {i["diameterMm"]:{key:[tuple(p) for p in i[key]] for key in ("head","power","npshr")}
                  for i in impellers}
    diameters = sorted(all_traces)
    suction_trims = [diameter for diameter in diameters if all_traces[diameter]["npshr"]]
    for lo,hi in zip(diameters,diameters[1:]):
        n1 = max((d for d in suction_trims if d<=lo),default=None)
        n2 = min((d for d in suction_trims if d>=hi),default=None)
        if n1 is None or n2 is None:
            continue
        h1,p1,h2,p2 = (all_traces[d][trace] for d in (lo,hi) for trace in ("head","power"))
        npsh = all_traces[n1]["npshr"], all_traces[n2]["npshr"]
        hyd = (h1,p1,h2,p2)
        q0,q1 = max(p[0][0] for p in hyd),min(p[-1][0] for p in hyd)
        if q1<=q0:
            continue
        flows = [q0+(q1-q0)*i/60 for i in range(61)]
        best = max(flows,key=lambda q: q*(at(h1,q)+at(h2,q))/(at(p1,q)+at(p2,q)))
        start = max(q0,*(c[0][0] for c in npsh),best*.4)
        end = min(q1,*(c[-1][0] for c in npsh),best*1.3)
        for f in (.55,.25,.8):
            if end<=start:
                break
            q = start+(end-start)*f
            low,high = at(h1,q),at(h2,q)
            if high > low and 0 < 9.81*q*(low+high)/(3600*(at(p1,q)+at(p2,q))) < 1:
                return [round(q,7),round((low+high)/2,7)]
    raise Unmatched("no common head/power/NPSHr selection window")


def pack(record, source_file, pairs, npsh, calibration):
    impellers = [{"diameterMm":d,"head":pair[0],"power":pair[1],"npshr":npsh.get(d,[])}
                 for d,pair in sorted(pairs.items(),reverse=True)]
    calibration["selectionDuty"] = selection_witness(impellers)
    return ({"page":record["page"],"curveNumber":record["curveNumber"],"sourceFile":source_file,"rpm":record["rpm"],
             "impellers":impellers}, calibration)


def three_panel(text, record, source_file, panels, parts, marks):
    head, npsh_grid, power = panels
    if any(abs(head.left-panel.left)>.8 or abs(head.right-panel.right)>.8 for panel in panels[1:]):
        raise Unmatched("three-panel grids do not share a flow scale")
    x_title = text.get_text_bounded(power.left,power.bottom-36,power.right,power.bottom-6)
    flow = re.search(r"flow\s*\[\s*(m3/h|l/s|USGPM)\s*\]", x_title, re.I)
    if not flow:
        raise Unmatched("three-panel flow unit not confirmed")
    q, qt = axis(text,power,"bottom")
    q *= {"m3/h":1, "l/s":3.6, "usgpm":0.22712470704}[flow[1].lower()]
    scales, ticks = [], [qt]
    for grid, quantity, converters in ((head,"head",{"m":1,"ft":.3048}),
        (npsh_grid,"npsh",{"m":1,"ft":.3048}), (power,"power",{"kw":1,"bhp":.74569987158})):
        title = text.get_text_bounded(grid.left-43,grid.bottom,grid.left-1,grid.top)
        unit = re.search(rf"{quantity}\s*\[\s*([a-z]+)\s*\]", title, re.I)
        if not unit or unit[1].lower() not in converters:
            raise Unmatched("three-panel vertical units not confirmed")
        slope, measured = axis(text,grid,"left",minimum_labels=2 if quantity=="npsh" else 3,marks=marks)
        scales.append(slope*converters[unit[1].lower()])
        ticks.append(measured)
    h,n,p = scales
    sg = re.search(r"specific gravity\s*=\s*(\d+[.,]\d+)", text.get_text_range(), re.I)
    if sg and .95 < float(sg[1].replace(",",".")) < 1.06:
        p /= float(sg[1].replace(",","."))

    diameters = set(record.get("plottedDiametersMm") or ())
    labels = [(d,box) for d,box in marks if d in diameters]
    if not labels:
        raise Unmatched("missing printed three-panel diameters")
    heads = {}
    for _, path in curve_paths(parts,head):
        d = labeled(path,labels,start=True)
        if d is None or path[0][0]>head.left+head.width*.04:
            continue
        if d in heads:
            raise Unmatched("ambiguous three-panel head diameters")
        heads[d] = path
    powers = [path for _,path in curve_paths(parts,power)]
    suction = [path for _,path in curve_paths(parts,npsh_grid)]
    pairs, npsh_data, end_paths = {}, {}, {}
    for d, path in heads.items():
        pp = [part for part in powers if abs(part[0][0]-path[0][0])<1.4 and abs(part[-1][0]-path[-1][0])<1.4]
        if len(pp)!=1:
            continue
        hp_out, pp_out = samples(path,head,q,h), samples(pp[0],power,q,p)
        if verify_pair(hp_out,pp_out):
            pairs[d] = (hp_out,pp_out)
            end_paths[d] = path[-1][0]
    if len(pairs)<2 or pairs.keys()!=diameters:
        raise Unmatched("missing uniquely paired three-panel head/power traces")
    for path in suction:
        ends = [(abs(x-path[-1][0]),d) for d,x in end_paths.items() if abs(x-path[-1][0])<=3.2]
        if len(ends)!=1:
            continue
        _, d = ends[0]
        if d in npsh_data:
            raise Unmatched("ambiguous three-panel NPSHr endpoints")
        npsh_data[d] = samples(path,npsh_grid,q,n)
    if len(npsh_data)<2:
        raise Unmatched("fewer than two matching three-panel NPSHr traces")
    return pack(record,source_file,pairs,npsh_data,
                {"ticks":ticks,"grids":[panel.__dict__ for panel in panels],"scales":[q,h,p,n]})


def extract(page, record, source_file):
    text = page.get_textpage()
    normalized = decode(text.get_text_range())
    parts = list(enumerate(strokes(page)))
    gs = grids([part for _,part in parts])
    if len(gs)==3 and record.get("plottedDiametersMm"):
        return three_panel(text,record,source_file,gs,parts,list(number_marks(page,text)))
    if len(gs)!=2:
        raise Unmatched("not a two-grid vector layout")
    head, power = gs
    if abs(head.left-power.left)>1 or abs(head.right-power.right)>1 or power.top>=head.bottom:
        raise Unmatched("head and power grids do not share a flow scale")
    if not all(re.search(pattern,normalized,re.I) for pattern in
               (r"\bQ\s*\(\s*l\s*/\s*s\s*\)",r"\bH\s*\(\s*m\s*\)",r"\bP\s*\(\s*k\s*w\s*\)")):
        raise Unmatched("not the metric l/s chart layout")
    q, q_ticks = axis(text,power,"bottom")
    axis(text,head,"top",expected=q)
    marks = list(number_marks(page,text))
    h, h_ticks = axis(text,head,"left",marks=marks)
    p, p_ticks = axis(text,power,"left",marks=marks)
    right_title = decode(text.get_text_bounded(head.right+.8,head.bottom,head.right+27,head.top+3))
    if not re.search(r"\(\s*m\s*\)", right_title, re.I):
        raise Unmatched("NPSHr axis unit not confirmed")
    n, n_ticks = axis(text,head,"right",minimum_labels=2,marks=marks)
    q *= 3.6

    diameters = set(record["trimDiametersMm"])
    labels = [(d,box) for d,box in marks if d in diameters]
    left_labels = {d for d,(l,b,r,t) in labels if head.left-3 <= l < head.left+head.width*.14
                   and head.bottom+5 < b < head.top and r < head.left+head.width*.22}
    heads = list(curve_paths(parts,head))
    powers = list(curve_paths(parts,power))
    pairs = {}
    used_heads = {}
    for hid, hp in heads:
        if hp[0][0]-head.left>head.width*.1:
            continue
        diameter = labeled(hp,labels,start=True)
        if diameter is None:
            continue
        valid = [pp for _, pp in powers if abs(pp[0][0]-hp[0][0])<2.5 and abs(pp[-1][0]-hp[-1][0])<3.5
                 and labeled(pp,labels)==diameter]
        if len(valid)!=1 or diameter in pairs:
            continue
        hp_out = samples(hp,head,q,h)
        pp_out = samples(valid[0],power,q,p)
        if not verify_pair(hp_out,pp_out):
            continue
        used_heads[hid] = hp
        pairs[diameter] = (hp_out,pp_out)
    if len(pairs)<2 or max(pairs)!=max(diameters) or not left_labels.issubset(pairs):
        raise Unmatched("no independently labeled head/power envelope")

    npsh = {}
    for ident, candidate in heads:
        x,y = candidate[0]
        if ident in used_heads or x-head.left<head.width*.025 or y-head.bottom>head.height*.45:
            continue
        levels = [at(path,x) for path in used_heads.values() if path[0][0]<=x<=path[-1][0]]
        if not levels or y >= min(levels)-2:
            continue
        diameter = labeled(candidate,labels)
        if diameter not in pairs:
            continue
        if diameter in npsh:
            raise Unmatched("multiple NPSHr paths for one diameter")
        curve = samples(candidate,head,q,n)
        if curve[-1][0]>pairs[diameter][0][-1][0]*1.1:
            continue
        npsh[diameter] = curve
    if len(npsh)<2:
        raise Unmatched("not enough independently labeled NPSHr traces")

    return pack(record,source_file,pairs,npsh,
            {"ticks":[q_ticks,h_ticks,p_ticks,n_ticks],"grids":[head.__dict__,power.__dict__],"scales":[q,h,p,n]})


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("pdf",type=Path)
    cli.add_argument("index",type=Path)
    cli.add_argument("output",type=Path)
    cli.add_argument("--audit",type=Path,required=True)
    cli.add_argument("--pages",nargs="+",type=int)
    args = cli.parse_args()
    index = json.loads(args.index.read_text(encoding="ascii"))
    results, audit = [], {}
    with pdfium.PdfDocument(args.pdf) as document:
        for record in index["records"]:
            number = record["page"]
            if args.pages and number not in args.pages:
                continue
            if number in REVIEWED or number==21:
                audit[number] = "separately reviewed"
                continue
            if record["kind"]!="fixed" or not record["rpm"]:
                audit[number] = "variable or unknown speed"
                continue
            if len(record["trimDiametersMm"])<2 and len(record.get("plottedDiametersMm") or ())<2:
                audit[number] = "no matching trim table"
                continue
            page = document[number-1]
            try:
                curve, calibration = extract(page,record,index["sourceFile"])
                results.append(curve)
                audit[number] = calibration
            except Unmatched as error:
                audit[number] = str(error)
            finally:
                page.close()
    payload = (json.dumps(results,separators=(",",":"))+"\n").encode("ascii")
    if args.output.suffix==".gz":
        args.output.write_bytes(gzip.compress(payload,mtime=0))
    else:
        args.output.write_bytes(payload)
    args.audit.write_text(json.dumps(audit,separators=(",",":"))+"\n",encoding="ascii")
    print(f"{len(results)} charts; {sum(len(row['impellers']) for row in results)} diameter traces")
    print(Counter(value for value in audit.values() if isinstance(value,str)))


if __name__ == "__main__":
    main()
