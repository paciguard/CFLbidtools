"""Extract pump curves from CAD PDF sheets using PDF path geometry."""

import argparse
import ctypes as ct
import csv
import hashlib
import json
import math
import re
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
import pypdfium2 as pdf
import pypdfium2.raw as raw
from PIL import Image, ImageDraw


def paths(page):
    height = page.get_height()
    for obj in page.get_objects(filter=[raw.FPDF_PAGEOBJ_PATH], max_depth=1):
        matrix = obj.get_matrix()
        fill, stroke = ct.c_int(), ct.c_int()
        raw.FPDFPath_GetDrawMode(obj, ct.byref(fill), ct.byref(stroke))
        parts, points = [], []
        for i in range(raw.FPDFPath_CountSegments(obj)):
            segment = raw.FPDFPath_GetPathSegment(obj, i)
            kind = raw.FPDFPathSegment_GetType(segment)
            if kind == raw.FPDF_SEGMENT_MOVETO and points:
                parts.append(points)
                points = []
            x, y = ct.c_float(), ct.c_float()
            raw.FPDFPathSegment_GetPoint(segment, ct.byref(x), ct.byref(y))
            x, y = matrix.on_point(x.value, y.value)
            points.append((x, height - y, kind))
        if points:
            parts.append(points)
        yield fill.value, bool(stroke.value), parts


def read_geometry(page):
    """Reduce strokes and outlined CAD strokes to trace centerlines."""
    for fill, stroke, parts in paths(page):
        if fill and len(parts) >= 2:
            # Some exporters tessellate each strip as two triangles sharing an edge.
            strips = []
            i = 0
            while i < len(parts):
                u = list(dict.fromkeys(p[:2] for p in parts[i]))
                if len(u) == 3 and i+1 < len(parts):
                    v = list(dict.fromkeys(p[:2] for p in parts[i+1]))
                    quad = list(dict.fromkeys(u+v))
                    if len(v) == 3 and len(quad) == 4:
                        mid = np.mean(quad, axis=0)
                        quad.sort(key=lambda p: math.atan2(p[1]-mid[1], p[0]-mid[0]))
                        strips.append([(a,b,0) for a,b in quad])
                        i += 2
                        continue
                strips.append(parts[i])
                i += 1
            parts = strips
        if fill and len(parts) >= 3 and all(len(p) in (4, 5) for p in parts):
            lines = []
            for part in parts:
                quad = np.array([p[:2] for p in part[:4]])
                sides = np.linalg.norm(quad - np.roll(quad, -1, axis=0), axis=1)
                i = int(np.argmin(sides))
                segment = np.array([(quad[i] + quad[(i+1)%4])/2,
                                    (quad[(i+2)%4] + quad[(i+3)%4])/2])
                if segment[1,0] < segment[0,0]:
                    segment = segment[::-1]
                lines.append(segment)
            lines.sort(key=lambda v:v[0,0])
            if all(p[1,0]-p[0,0] > .05 for p in lines):
                delta = np.array([b[0]-a[1] for a,b in zip(lines,lines[1:])])
                if np.min(delta[:,0]) > -1.8 and np.max(np.linalg.norm(delta,axis=1)) < 12:
                    ps = np.concatenate(lines)
                    if np.ptp(ps[:,0]) > 10:
                        keep = np.r_[True, np.diff(np.maximum.accumulate(ps[:,0])) > .025]
                        yield ps[keep]
                        continue
        for part in parts:
            ps = np.array([(x, y) for x, y, _ in part])
            if len(ps) < 2:
                continue
            if fill and not (len(parts) == 1 and len(ps) > 30 and
                             max(ps[:, 0]) - min(ps[:, 0]) > 45):
                # The CAD exporter uses filled quadrilaterals for some lines.
                if len(ps) not in (4, 5):
                    continue
                quad = ps[:4]
                sides = np.linalg.norm(quad - np.roll(quad, -1, axis=0), axis=1)
                if min(sides) > 2.6 or max(sides) < 1.5 * min(sides):
                    continue
                start = int(np.argmin(sides))
                ps = np.array([(quad[start] + quad[(start + 1) % 4]) / 2,
                               (quad[(start + 2) % 4] + quad[(start + 3) % 4]) / 2])
            elif fill:
                # Outline the open stroke with the median of each vertical slice.
                xmin, xmax = np.min(ps[:, 0]), np.max(ps[:, 0])
                xs = np.linspace(xmin + .15, xmax - .15, max(3, int((xmax - xmin) * 2)))
                slices = [[] for _ in xs]
                for a, b in zip(ps, np.roll(ps, -1, axis=0)):
                    if abs(a[0] - b[0]) < 1e-6:
                        continue
                    for i in range(*np.searchsorted(xs, sorted((a[0], b[0])))):
                        slices[i].append(a[1] + (b[1] - a[1]) * (xs[i] - a[0]) / (b[0] - a[0]))
                if sum(bool(s) and max(s) - min(s) < 5 for s in slices) < .9 * len(xs):
                    continue
                ps = np.array([(x, np.median(s)) for x, s in zip(xs, slices) if s])
            elif not stroke:
                continue
            if len(ps) > 2 and any(kind == raw.FPDF_SEGMENT_BEZIERTO for _, _, kind in part) and not fill:
                out = [ps[0]]
                i = 1
                while i < len(ps):
                    if part[i][2] == raw.FPDF_SEGMENT_BEZIERTO and i + 2 < len(ps):
                        a, b, c, d = ps[i - 1:i + 3]
                        for t in np.linspace(0, 1, 17)[1:]:
                            out.append((1 - t)**3 * a + 3 * t * (1 - t)**2 * b
                                       + 3 * t**2 * (1 - t) * c + t**3 * d)
                        i += 3
                    else:
                        out.append(ps[i])
                        i += 1
                ps = np.array(out)
            if len(ps) >= 2:
                yield ps


def grid_candidates(geometry, page):
    w, h = page.get_size()
    vertical = Counter()
    horizontal = []
    for p in geometry:
        x, y = p.T
        if np.ptp(x) < .55 and np.ptp(y) > 35 and w*.09 < x[0] < w*.89 and min(y) > h*.12 and max(y) < h*.91:
            vertical[(round(min(y)), round(max(y)))]+=1
        if np.ptp(y) < .6 and np.ptp(x) > w*.28 and h*.12 < y[0] < h*.91:
            horizontal.append((round(min(x),2), round(max(x),2), round(float(np.mean(y)),2)))
    return vertical, horizontal


def grid_boxes(geometry, page):
    vertical, horizontal = grid_candidates(geometry, page)
    spans = [span for span, count in vertical.most_common(3) if count >= 10]
    if len(spans) != 3 or any(b > c for (_, b), (c, _) in zip(sorted(spans), sorted(spans)[1:])):
        raise ValueError("could not identify three plot panels")
    boxes = {}
    across = [(a,b) for a,b,y in horizontal if any(t-1 < y < d+1 for t,d in spans)]
    recurring = Counter((round(a,1),round(b,1)) for a,b in across)
    filtered = [(a,b) for (a,b),count in recurring.items() if count>=4]
    height_first = np.argmax([b - a for a,b in sorted(spans)]) == 0
    for name, (a, b) in zip(("H", "N", "P") if height_first else ("N", "H", "P"), sorted(spans)):
        matching = [ps for ps in geometry if np.ptp(ps[:,0]) < .55
                    and abs(min(ps[:,1]) - a) < .8 and abs(max(ps[:,1]) - b) < .8]
        # Exclude secondary scales: they have vertical ticks but no grid lines.
        left = min(x for x,_ in filtered)
        right = max(y for _,y in filtered)
        boxes[name] = [left, float(np.median([min(ps[:,1]) for ps in matching])),
                       right, float(np.median([max(ps[:,1]) for ps in matching]))]
        if name == "P":
            # Some drawings split all the vertical grid lines at a major tick.
            earlier = [(s,e) for (s,e),c in vertical.items() if e==a and c>=.75*vertical[(a,b)]
                       and s > max(box[3] for label,box in boxes.items() if label!="P")+3]
            if earlier:
                s,e=min(earlier)
                starts=[min(ps[:,1]) for ps in geometry if np.ptp(ps[:,0])<.55 and abs(min(ps[:,1])-s)<.8
                        and abs(max(ps[:,1])-e)<.8]
                if starts:
                    boxes[name][1]=float(np.median(starts))
    return boxes


def trace_candidates(geometry, box):
    l,t,r,b = box
    width = r-l
    parts = []
    keys = set()
    for ps in geometry:
        if min(ps[:,0]) < l-1.5 or max(ps[:,0]) > r+1.5 or min(ps[:,1]) < t-1.5 or max(ps[:,1]) > b+1.5:
            continue
        if ps[-1,0] < ps[0,0]:
            ps = ps[::-1]
        dx = np.diff(ps[:,0])
        if np.min(dx) < -.24 or ps[-1,0] - ps[0,0] < 1.1:
            continue
        if np.ptp(ps[:,1]) < .1 and ps[-1,0]-ps[0,0] > width*.22:
            continue
        key = tuple(np.round(ps[[0, -1]].ravel(), 1))
        if key not in keys:
            parts.append(ps)
            keys.add(key)
    if not parts:
        return []
    parts.sort(key=lambda p: p[0,0])
    ends = np.array([p[-1] for p in parts])
    starts = np.array([p[0] for p in parts])
    def tangent(ps):
        a = ps[np.searchsorted(ps[:,0], min(ps[0,0] + 1, ps[-1,0]-1e-7))]
        return math.atan2(a[1]-ps[0,1], a[0]-ps[0,0])
    entry = np.array([tangent(p) for p in parts])
    exit = np.array([-tangent(-p[::-1] * np.array([1,-1])) for p in parts])
    edges = []
    for i, (x, y) in enumerate(ends):
        for j in range(np.searchsorted(starts[:,0], x-1.8), np.searchsorted(starts[:,0], x+max(11, width*.039))):
            if j <= i or ends[j,0] < x+.25:
                continue
            u,v = starts[j]-ends[i]
            angle = (entry[j]+exit[i])*.5
            change = abs(entry[j]-exit[i])
            dist = math.hypot(u, v)
            offset = abs(v*math.cos(angle)-u*math.sin(angle))
            if dist < .45 and change < .74:
                cost = dist + change
            elif change < .31 and offset < .55 + .015*dist and (u > -.9 or dist < 2.1):
                cost = 1 + .1*dist + 3*offset + 5*change
            else:
                continue
            edges.append((cost,i,j))
    prev, next_ = {}, {}
    for _, i,j in sorted(edges):
        if i not in next_ and j not in prev:
            next_[i], prev[j] = j,i
    results = []
    for i in range(len(parts)):
        if i in prev:
            continue
        chain = [parts[i]]
        while i in next_:
            i = next_[i]
            chain.append(parts[i])
        merged = np.concatenate(chain)
        span = merged[-1,0] - merged[0,0]
        coverage = sum(p[-1,0]-p[0,0] for p in chain) / max(.1, span)
        if span < .13*width or np.ptp(merged[:,1]) < .85 or coverage < .39:
            continue
        # Do not count a join with reversed x as an additional digitized point.
        keep = np.r_[True, np.diff(np.maximum.accumulate(merged[:,0])) > .025]
        results.append((merged[keep], round(coverage, 3), len(chain)))
    return sorted(results, key=lambda v: -(v[0][-1,0]-v[0][0,0]))


COLORS = ["#ee2450", "#0063df", "#169c50", "#a020f0", "#e89400", "#01a8bb", "#a05814", "#d507ac"]


def scaled_crop(img, box, scale):
    return img.crop(tuple(max(0, round(n*scale)) for n in box))


def atlas(page, boxes, output, n):
    scale = 4
    im = page.render(scale=scale).to_pil().convert("RGB")
    crops = []
    def crop(key, rect):
        crops.append((key,rect,scaled_crop(im, rect, scale)))
    for k,(l,t,r,b) in boxes.items():
        crop(k, (max(l-53,0),t-5,l-1,b+7))
    l,t,r,b = boxes["H"]
    crop("E", (r-.65,t-7,min(r+19,page.get_width()),b+7))
    l,t,r,b = boxes["P"]
    crop("Q", (l-8,b+.55,r+20,b+14))
    crop("metadata", (page.get_width()*.24,7,page.get_width()*.95,min(box[1] for box in boxes.values())-40))
    offset = 0
    panel_width = sum(im.width+12 for key,_,im in crops if key in "HNPE")
    panel_height = max(im.height for key,_,im in crops if key in "HNPE")
    image = Image.new("RGB", (max(panel_width, max(im.width for _,_,im in crops)), panel_height + 32 +
                              sum(im.height+12 for key,_,im in crops if key not in "HNPE")), "white")
    zones = {}
    lower = panel_height+12
    for key,rect,img in crops:
        if key in "HNPE":
            x,y = offset,0
            offset += img.width+12
        else:
            x,y = 0,lower
            lower += img.height+12
        image.paste(img, (x,y))
        zones[key] = (x,y,img.width,img.height,round(rect[0]*scale),round(rect[1]*scale))
    image.save(output / f"axis-{n:02}.png")
    return zones


def catalog(source, output, indexes):
    output.mkdir(parents=True, exist_ok=True)
    data = []
    for n, path in enumerate(sorted(source.glob("*.pdf")), 1):
        if indexes and n not in indexes:
            continue
        with pdf.PdfDocument(path) as doc:
            page = doc[0]
            geometry = list(read_geometry(page))
            boxes = grid_boxes(geometry, page)
            zones = atlas(page,boxes,output,n)
            scale = 1.6
            image = page.render(scale=scale).to_pil().convert("RGB")
            draw = ImageDraw.Draw(image)
            series = {}
            for key, box in boxes.items():
                trace = trace_candidates(geometry,box)
                series[key] = [{"xy": np.round(p,4).tolist(), "coverage": a, "fragments": m}
                               for p,a,m in trace]
                for i, (p, _, _) in enumerate(trace):
                    color = COLORS[i%len(COLORS)]
                    draw.line([tuple(v*scale) for v in p], fill=color, width=2)
                    x,y=p[-1]*scale
                    draw.text((x+2,y-7), f"{key}{i+1}", fill=color, stroke_width=1, stroke_fill="white")
            image.save(output / f"review-{n:02}.png")
            ticks = {}
            for key,(l,t,r,b) in boxes.items():
                ticks[key] = sorted(set(round(float(np.mean(ps[:,1])),2) for ps in geometry
                                        if np.ptp(ps[:,0]) > (r-l)*.14 and np.ptp(ps[:,1])<.33
                                        and t-.6<np.mean(ps[:,1])<b+.6
                                        and l-1<min(ps[:,0])<r-4 and max(ps[:,0])<r+1))
            l,t,r,b=boxes["P"]
            ticks["Q"] = sorted(set(round(float(np.mean(ps[:,0])),2) for ps in geometry
                                    if np.ptp(ps[:,0]) < .35 and np.ptp(ps[:,1]) > (b-t)*.35
                                    and l-.8<np.mean(ps[:,0])<r+.8 and t-.8<min(ps[:,1])
                                    and max(ps[:,1])<b+.8))
            data.append({"index":n,"file":path.name,"page":1,"sha256":hashlib.sha256(path.read_bytes()).hexdigest(),
                         "boxes":boxes,"zones":zones,"traces":series,"ticks":ticks})
            print(n,path.name,{key:len(series[key]) for key in series},flush=True)
    (output / "vectors.json").write_text(json.dumps(data,separators=(",",":")), encoding="utf-8")


def axis_words(words, zone):
    x,y,w,h,x0,y0=zone
    found=[]
    for word in words:
        a,b=word["x"],word["y"]
        if not (x-.5 <= a < x+w and y-.5 <= b < y+h):
            continue
        token=word["t"].strip()
        token=token.translate(str.maketrans("IOol|", "10011"))
        if not re.fullmatch(r"[0-9]+(?:[.,][0-9]+)?",token):
            continue
        val=float(token.replace(",","."))
        found.append(((a-x+x0+word["w"]*.5)/4,(b-y+y0+word["h"]*.5)/4,val))
    return found


def fit_axis(record, name, words):
    v = axis_words(words,record["zones"][name])
    dim = 0 if name=="Q" else 1
    k = "H" if name=="E" else name
    l,t,r,b = record["boxes"]["P" if name=="Q" else k]
    extent = r-l if name=="Q" else b-t
    anchor = l if name=="Q" else b
    low,high = (l-5,r+12) if name=="Q" else (t-4,b+5)
    labels = [(v_[dim], a) for v_ in v for a in [v_[2]]
              if low <= v_[dim] <= high and (name!="E" or a<=100)]
    if name=="Q":
        labels=[(p,v) for p,v in labels if v==0 or (v>=5 and abs(v-round(v))<.01)]
    labels.sort()
    best = None
    for i,(a,m) in enumerate(labels):
        for q,n in labels[i+1:]:
            if abs(q-a)<extent*.12 or (n-m)*(1 if name=="Q" else -1)<=0:
                continue
            slope=(n-m)/(q-a)
            origin=m+slope*(anchor-a)
            if (name=="Q" and abs(origin/slope)>9
                    or name=="E" and not (-6<origin<85 and origin - extent*slope < 165)
                    or name not in ("Q","E") and origin < -abs(slope)*4):
                continue
            found=[(p,v) for p,v in labels if abs((v-m)/slope+a-p)<1.75]
            if len(found)<2:
                continue
            length=found[-1][0]-found[0][0]
            if length<extent*.27:
                continue
            score=(len(found), length/extent, -abs(origin/slope) if name=="Q" else 0)
            if best is None or score>best[0]:
                best=score,found
    if not best:
        raise ValueError(f"{name}: insufficient printed ticks ({labels})")
    _,found=best
    xs,ys=np.array(found).T
    slope,offset = np.polyfit(xs,ys,1)
    ticks = record["ticks"]["H" if name=="E" else name]
    if name=="Q":
        bias = -offset/slope-anchor
    else:
        bias = 0
    actual = np.array([min(ticks,key=lambda z: abs(z-(x-bias))) for x in xs]) if ticks else xs
    if len(set(actual))==len(xs) and max(abs(actual-xs+bias)) < 1.9:
        blended_slope,blended_off=np.polyfit(actual,ys,1)
        if max(abs((blended_slope*actual+blended_off-ys)/slope)) < .46:
            xs=actual
            slope,offset=blended_slope,blended_off
    if name=="Q":
        offset=-slope*anchor
    return {"slope":float(slope),"intercept":float(offset),
            "ticks": [[round(float(x),3),float(y)] for x,y in zip(xs,ys)],
            "source":"printed metric tick labels"}


def calibrations(directory):
    source=json.loads((directory/"vectors.json").read_text(encoding="utf-8"))
    ocr=json.loads((directory/"ocr.json").read_text(encoding="utf-8"))
    results={}
    for rec in source:
        n=rec["index"]
        results[n]={}
        for k in "QHNEP":
            try:
                results[n][k]=fit_axis(rec,k,ocr[f"axis-{n:02}"])
            except ValueError as error:
                results[n][k]={"error":str(error)}
        def summary(k,m):
            if "error" in m:
                return m["error"]
            pos=(rec["boxes"]["P"][2] if k=="Q" else
                 rec["boxes"]["H" if k=="E" else k][3])
            return f"{k}({len(m['ticks'])})={m['slope']*pos+m['intercept']:.2f}"
        print(n,rec["file"]," | ".join(summary(k,v) for k,v in results[n].items()),flush=True)
    (directory/"calibrations.json").write_text(json.dumps(results,indent=2),encoding="utf-8")


def contact_sheets(directory):
    data=json.loads((directory/"vectors.json").read_text(encoding="utf-8"))
    scale=1.6
    for group in range(0,len(data),6):
        canvas=Image.new("RGB",(1600,3*590),"white")
        draw=ImageDraw.Draw(canvas)
        for j,rec in enumerate(data[group:group+6]):
            n=rec["index"]
            src=Image.open(directory/f"review-{n:02}.png")
            x,y=j%2*800,j//2*590
            draw.line([(x,y),(x+798,y)],fill="#336b9a",width=2)
            draw.text((x+4,y+4),f"{n:02}  {rec['file']}",fill="#002644")
            for k,dx,right in [("H",4,False),("H",81,True),("N",153,False),("P",231,False)]:
                l,t,r,b=rec["boxes"][k]
                box=(r-1,t-5,r+38,b+8) if right else (l-38,t-5,l+1,b+8)
                img=scaled_crop(src,box,scale)
                canvas.paste(img,(x+dx,y+24))
                if not right:
                    for z in rec["ticks"][k]:
                        draw.line([(x+dx+62,y+24+(z-t+5)*scale),(x+dx+67,y+24+(z-t+5)*scale)],fill="#31a03a")
            l,t,r,b=rec["boxes"]["P"]
            canvas.paste(scaled_crop(src,(l-9,b-.8,r+13,b+16),scale),(x+4,y+552))
            l,t,r,b=rec["boxes"]["H"]
            head=scaled_crop(src,(l-2,t-1,l+84,b+.2),scale)
            canvas.paste(head,(x+311,y+24))
            first=min(p[1] for p in rec["boxes"].values())
            header=(140,10,565,first-47)
            im=scaled_crop(src,header,scale)
            im.thumbnail((344,150))
            canvas.paste(im,(x+453,y+24))
            pw=scaled_crop(src,(l+(.45*(r-l)),rec["boxes"]["P"][1]-1,
                                r+10,rec["boxes"]["P"][3]+1),scale)
            pw.thumbnail((340,184))
            canvas.paste(pw,(x+453,y+195))
        canvas.save(directory/f"check-{group+1:02}.png")


UNITS={"Q":"m3/h","H":"m","E":"%","N":"m","P":"kW"}
QUANTITIES={"H":"head","E":"efficiency","N":"npshr","P":"shaft_power"}
STEPS={"Q":"q_step","H":"h_step","E":"eff_step","N":"npshr_step","P":"power_step"}
PARTIAL = {
    1: {"E": [[4], [3]]},
    6: {"E": [[1], [4,5]]},
    8: {"E": [[2], [4,5]]},
    13: {"E": [[2], [5,4]]},
    14: {"E": [[1], [4]]},
    25: {"E": [[4], [5,3]]},
    39: {"E": [[4], [3]]},
    46: {"H": [[4,5],[2]]},
}
TRUNCATED_BRANCHES = {(1,"E",1),(14,"E",2),(25,"E",1),(39,"E",1)}


def spacing(values):
    gaps=np.diff(values)
    med=np.median(gaps[gaps>.5])
    idx=np.round((np.array(values)-values[0])/med)
    d,offset=np.polyfit(idx,values,1)
    if len(set(idx)) < 3 or np.max(np.abs((np.array(values)-offset)/d-idx))>.35:
        raise ValueError(f"not a regular grid: {values}")
    return d


def metric_axes(rec, profile):
    models={}
    for k,col in STEPS.items():
        grid="H" if k=="E" else k
        bounds=rec["boxes"]["P" if k=="Q" else grid]
        d=spacing(rec["ticks"][grid])
        step=float(profile[col])
        base=float(profile.get({"H":"h_bottom","N":"npshr_bottom"}.get(k,""),0))
        origin=bounds[0] if k=="Q" else bounds[3]
        # Take the position from the vector gridline rather than its bounding box.
        origin=min(rec["ticks"][grid],key=lambda z:abs(z-origin))
        models[k]={"unit":UNITS[k],"units_per_grid_step":step,"grid_step_pdf_points":round(d,6),
                   "origin_pdf_points":origin,"origin_value":base,
                   "slope":(1 if k=="Q" else -1)*step/d}
    return models


def coordinate(model,p):
    return model["origin_value"]+(p-model["origin_pdf_points"])*model["slope"]


def restrict_series(record,n_diameters):
    left,top,right,base=record["boxes"]["H"]
    traces=record["traces"]
    n=record["index"]
    groups={}
    ys=lambda p:p["xy"][0][1]
    ends=lambda p:p["xy"][-1][0]
    height=base-top
    special=PARTIAL.get(n,{})
    for k,idx in special.items():
        groups[k]=[[traces["H"][i-1] for i in branch] for branch in idx]
    if "H" not in groups:
        head=[p for p in traces["H"] if ys(p)<base-height*.17 and abs(p["xy"][0][0]-left)<3
              and p["xy"][-1][1] > ys(p)+2]
        groups["H"]=[[p] for p in sorted(head,key=ys)[:n_diameters]]
    if len(groups["H"])!=n_diameters:
        raise ValueError(f"sheet {n}: unexpected head traces ({len(groups['H'])})")
    if "E" not in groups:
        eff=[p for p in traces["H"]
             if ys(p)>base-height*.085 and p["xy"][0][0]<left+4 and p["xy"][-1][1]<ys(p)-12]
        groups["E"]=[[p] for p in sorted(eff,key=ends,reverse=True)[:2]]
    power=[p for p in traces["P"] if p["xy"][-1][1] < ys(p)-1.4]
    groups["P"]=[[p] for p in sorted(power,key=ys)[:2]]
    npshr=[]
    for p in sorted(traces["N"],key=lambda p:-np.ptp(np.array(p["xy"])[:,0])):
        b=np.array(p["xy"])
        for i,(a,) in enumerate(npshr):
            a=np.array(a["xy"])
            l,r=max(a[0,0],b[0,0]), min(a[-1,0],b[-1,0])
            x=np.linspace(l,r,33)
            if r-l>15 and max(abs(np.interp(x,a[:,0],a[:,1])-np.interp(x,b[:,0],b[:,1])))<.7:
                # Preserve the full plotted range when two CAD strokes coincide.
                full=np.vstack((b[b[:,0]<a[0,0]-.02],a,b[b[:,0]>a[-1,0]+.02]))
                npshr[i]=[{"xy":full.tolist(),"source_overlay_count":2}]
                break
        else:
            npshr.append([p])
    groups["N"]=npshr
    return groups


def check_efficiency(series):
    by_key={(s["quantity"],tuple(s["impeller_diameters_mm"] or [])):s for s in series}
    mismatches=[]
    factor=100*1000*9.80665/(3600*1000)
    for s in series:
        if s["quantity"]!="efficiency" or not s["impeller_diameters_mm"]:
            continue
        d=tuple(s["impeller_diameters_mm"])
        h=by_key.get(("head",d))
        p=by_key.get(("shaft_power",d))
        if not h or not p:
            continue
        tests=[]
        for segment in s["segments"]:
            for hs in h["segments"]:
                for ps in p["segments"]:
                    q,eff=np.array(segment["points"]).T
                    x,y=np.array(hs["points"]).T
                    u,v=np.array(ps["points"]).T
                    mask=(q>=max(x[0],u[0])) & (q<=min(x[-1],u[-1])) & (eff>45)
                    for qi,ei in zip(q[mask],eff[mask]):
                        pi=np.interp(qi,u,v)
                        if pi>0:
                            tests.append((ei, factor*qi*np.interp(qi,x,y)/pi))
        if tests:
            shown,check=max(tests,key=lambda pair:pair[0])
            if abs(shown-check)>6:
                mismatches.append(f"{s['id']}: near the plotted efficiency peak, "
                                  f"plotted efficiency is {shown:.1f}% versus {check:.1f}% "
                                  "from Q/H/shaft power at water density. Verify before use.")
    return mismatches


def export(directory, output):
    data=json.loads((directory/"vectors.json").read_text(encoding="utf-8"))
    with Path(__file__).with_name("bb2-axis-profiles.csv").open(newline="",encoding="utf-8") as f:
        profiles={int(c["sheet"]):c for c in csv.DictReader(f)}
    output.mkdir(parents=True,exist_ok=True)
    fields=["source_sheet","source_file","source_page","printed_model","original_curve_number",
            "rpm","stages_as_printed","series","quantity","impeller_diameters_mm","segment",
            "flow_m3h","value","ordinate_unit","status","pdf_x_pt","pdf_y_from_top_pt"]
    sheets, rows, audit = [], [], []
    for rec in data:
        n=rec["index"]
        p=profiles[n]
        diam=[d for d in p["diameters_mm"].split(";")]
        axes=metric_axes(rec,p)
        groups=restrict_series(rec,len(diam))
        report=[]
        if not p["curve_number"]:
            report.append("No distinct curve-number field on the drawing; retain the source filename/reference.")
        if n==41:
            report.append("Header lists one stage, while its model designation contains *2. Confirm before selection.")
        if n in (13,):
            report.append("Source note says the pump does not follow the impeller trim law.")
        if len(diam)>2:
            report.append("Intermediate diameter head curves have no matching efficiency or power trace on this sheet.")
        record={"sheet":n,"source_file":rec["file"],"source_page":1,"sha256":rec["sha256"],
                "printed_model":p["model"],"original_curve_number":p["curve_number"] or None,
                "drawing_filename_reference":(re.search(r"\b[56]-\d+-\d+\b",rec["file"]) or [None])[0],
                "rpm":int(p["rpm"]),"stages_as_printed":int(p["stages"]),"metric_axes":axes,
                "secondary_axes":{"flow":["US gal/min"] if list(rec["boxes"])[0]=="H" else
                                  ["Imperial gal/min","US gal/min","L/s"],
                                  "head":"ft","npshr":"ft",
                                  "shaft_power":"hp" if list(rec["boxes"])[0]=="H" else "PS"},
                "series":[],"notes":report}
        for k in "HEPN":
            branches=groups[k]
            ds=diam if k=="H" else ([diam[0],diam[-1]] if k in "EP" else [None]*len(branches))
            if k in "EP" and len(branches)!=2:
                report.append(f"Only {len(branches)} {QUANTITIES[k]} trace(s) extracted; verify assignment.")
                ds=[None]*len(branches)
            for index,(branch,dia) in enumerate(zip(branches,ds),1):
                status=("unassigned_diameter" if k=="N" else
                        "partial_trace" if len(branch)>1 or (n,k,index) in TRUNCATED_BRANCHES
                        else "digitized")
                if status=="partial_trace":
                    report.append(f"{k}{index}: trace coverage incomplete; keep segments separate and review against the PDF.")
                series={"id":f"{n:02}-{k}{index}","quantity":QUANTITIES[k],"unit":UNITS[k],
                        "impeller_diameters_mm":[float(v) for v in dia.split("/")] if dia else None,
                        "status":status,"segments":[]}
                for j,seg in enumerate(branch,1):
                    points=[]
                    last=-math.inf
                    for x,y in seg["xy"]:
                        q=round(max(0,coordinate(axes["Q"],x)),4)
                        value=round(coordinate(axes[k],y),4)
                        if q<=last:
                            continue
                        if value < 0 and k!="H" and abs(value)<float(p[STEPS[k]])*.18:
                            value=0.0
                        if value<0 or k=="E" and value>100.1:
                            raise ValueError(f"sheet {n}: {k} coordinate {value} outside physical bounds")
                        last=q
                        points.append([q,value])
                        rows.append([n,rec["file"],1,p["model"],p["curve_number"],p["rpm"],p["stages"],
                                     series["id"],QUANTITIES[k],dia or "",j,q,value,UNITS[k],status,x,y])
                    if len(points)<2:
                        raise ValueError(f"sheet {n}: {k} series with fewer than two points")
                    series["segments"].append({"index":j,"points":points})
                record["series"].append(series)
        report.extend(check_efficiency(record["series"]))
        if n in (10,39,49,50,51):
            record["plot_geometry_also_in_sheet"]={10:9,39:1,49:26,50:40,51:23}[n]
        sheets.append(record)
        audit.append([n,rec["file"],p["model"],p["curve_number"],p["rpm"],p["stages"],
                      *(len(groups[k]) for k in "HEPN"),
                      record.get("plot_geometry_also_in_sheet","")," | ".join(dict.fromkeys(report))])
    with (output/"BB2_curve_points.csv").open("w",newline="",encoding="utf-8-sig") as f:
        writer=csv.writer(f)
        writer.writerow(fields)
        writer.writerows(rows)
    with (output/"BB2_sheet_index.csv").open("w",newline="",encoding="utf-8-sig") as f:
        writer=csv.writer(f)
        writer.writerow(["source_sheet","source_file","printed_model","original_curve_number","rpm","stages_as_printed",
                         "head_traces","efficiency_traces","power_traces","npshr_traces","plot_geometry_also_in_sheet","notes"])
        writer.writerows(audit)
    (output/"BB2_curves.json").write_text(json.dumps({"schema":"bb2-digitized-curves-v1","flow_unit":"m3/h",
                                                     "selection_enabled":False,"sheets":sheets},
                                                     ensure_ascii=True,indent=2),encoding="utf-8")
    counts=Counter(v["quantity"] for s in sheets for v in s["series"])
    print(len(sheets),"sheets;",sum(counts.values()),"series;",len(rows),"points;",dict(counts))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("indexes", type=int, nargs="*")
    parser.add_argument("--calibrate", action="store_true")
    parser.add_argument("--contact", action="store_true")
    parser.add_argument("--export", type=Path)
    args = parser.parse_args()
    if args.export:
        export(args.output,args.export)
    elif args.calibrate:
        calibrations(args.output)
    elif args.contact:
        contact_sheets(args.output)
    else:
        catalog(args.source, args.output, args.indexes)
