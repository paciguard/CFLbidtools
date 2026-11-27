"""Build the reviewed 1470 rpm A33-100 and APP33-100 Open1 source records."""

import argparse
import json
from pathlib import Path
import runpy
import tempfile
import subprocess

import numpy as np
from PIL import Image, ImageDraw
import pypdfium2 as pdfium


VECTOR = runpy.run_path(str(Path(__file__).with_name("import-ahlstar-vector.py")))


def grid(positions, units):
    return np.polynomial.Polynomial.fit(positions, units, 1)


def raster_trace(gray, bounds, anchors, x_axis, y_axis, degree):
    left, top, right, bottom = bounds
    bg = np.median(gray[:, left:right+1], axis=1)
    vertical = np.mean(gray[top:bottom, :] < 150, axis=0) > .58
    ax, ay = np.array(anchors).T
    fit = np.polynomial.Polynomial.fit(ax, ay, degree)
    for window in (4, 2.5, 1.8):
        ink = []
        for x in range(round(ax[0]), round(ax[-1])+1):
            if np.any(vertical[x-1:x+2]):
                continue
            expected = fit(x)
            ys = np.arange(max(top, round(expected-window)), min(bottom, round(expected+window)+1))
            if not len(ys):
                continue
            strength = bg[ys]-gray[ys,x]
            y = ys[np.argmax(strength - 45*np.abs(ys-expected))]
            if bg[y]-gray[y,x] > 50 and abs(y-expected) <= window:
                ink.append((x,y))
        xp, yp = np.array(ink).T
        fit = np.polynomial.Polynomial.fit(np.r_[xp, ax], np.r_[yp, ay], degree,
                                           w=np.r_[np.ones(len(xp)), np.full(len(ax), 3)])
    if len(xp) < (ax[-1]-ax[0])*.42 or np.quantile(np.abs(fit(xp)-yp), .95) > 1.6 or max(abs(fit(ax)-ay)) > 2:
        raise ValueError("Raster fit does not agree with the labeled trace")
    xs = np.linspace(ax[0], ax[-1], 61)
    ys = fit(xs)
    return [[round(float(q),4), round(float(v),3)] for q,v in zip(x_axis(xs),y_axis(ys),strict=True)], list(zip(xs,ys))


def overlay(image, lines, path):
    mark = Image.new("RGBA", image.size)
    draw = ImageDraw.Draw(mark)
    palette = [(0,95,226,210),(179,60,35,210),(0,130,91,210),(150,41,145,210),(189,117,0,210)]
    for i, line in enumerate(lines):
        draw.line(line, fill=palette[i % len(palette)], width=2)
    Image.alpha_composite(image.convert("RGBA"),mark).convert("RGB").save(path)


def a_suction(image, folder):
    gray = np.asarray(image.convert("L"),dtype=float)
    if gray.shape != (254,711):
        raise ValueError("NPSHr screenshot needs independent calibration")
    # The crop shares the flow grid of the full K18490 page, in 10 L/s steps.
    xx = grid([58,133,208,284,359,435,510,586,661], np.arange(9)*36)
    yy = grid([227,171.5,116,60.5], range(4))
    anchors = {
        0: [(195,168.5),(222,165.5),(250,162),(278,158),(310,153.5),(348,145.5),
            (385,139.5),(420,131),(450,124),(473,118.5),(490,113),(520,103),(540,95.5),
            (551,89.5),(570,81),(583,73.5),(606,62),(630,46),(646,35.5),(660,25.5)],
        360: [(537,95),(540,91.5),(551,77.5),(565,58),(570,49.5),(582,26.5)],
        320: [(423,130),(439,119),(450,108),(473,77.5),(490,46.5),(501,26.5)],
    }
    traces, lines = {}, []
    for d, coords in anchors.items():
        traces[d], line = raster_trace(gray, (58,16,691,227), coords, xx, yy, 2 if d else 5)
        lines.append(line)
    # Before each fork the printed curves share one visible line. At each fork,
    # keep that line only up to the independently digitized branch.
    shared = traces.pop(0)
    for d in (320,360):
        traces[d] = [p for p in shared if p[0] < traces[d][0][0]] + traces[d]
    traces[400] = shared
    overlay(image,lines,folder/"K18490-npshr-overlay.png")
    return traces


def a_power(image, folder):
    gray = np.asarray(image.convert("L"),dtype=float)
    if gray.shape != (1696,1199):
        raise ValueError("K18490 page expected at 145 dpi")
    xx = grid([186,265,345,424,504,584,663,743,822],np.arange(9)*36)
    yy = grid([1286,1224.5,1163,1101.5,1039.5,978], range(0,51,10))
    anchors = {
        400: [(230,1153),(315,1129.5),(405,1102.5),(488,1074),(567,1047),(654,1017.5),(729,992.5),(811,964)],
        380: [(227,1176),(315,1154.5),(405,1129.5),(488,1104),(567,1079.5),(654,1053.5),(729,1029.5),(779,1014.5)],
        360: [(224,1195),(315,1176),(405,1152),(488,1130),(567,1108),(654,1084),(733,1063)],
        340: [(221,1212),(315,1193.5),(405,1173),(488,1152),(567,1133),(629,1117.5),(674,1107.5)],
        320: [(217,1226),(315,1209),(405,1190.5),(488,1172.5),(567,1156),(629,1142.5)],
    }
    traces, lines = {}, []
    for d, coords in anchors.items():
        traces[d], line = raster_trace(gray, (186,952,854,1286), coords, xx, yy, 4)
        lines.append(line)
    overlay(image,lines,folder/"K18490-power-overlay.png")
    return traces


def app_traces(page, image, folder):
    text = page.get_textpage()
    decoded = VECTOR["decode"](text.get_text_range())
    if not all(s in decoded for s in ("APP33-100","K13847","1470","Q (l/s)","H (m)","P (kW)")):
        raise ValueError("Not the expected APP33-100 metric source sheet")
    parts = list(enumerate(VECTOR["strokes"](page)))
    h,p = VECTOR["grids"]([part for _,part in parts])
    marks = list(VECTOR["number_marks"](page,text))
    h_scale,_ = VECTOR["axis"](text,h,"left",marks=marks)
    p_scale,_ = VECTOR["axis"](text,p,"left",marks=marks)
    x_scale,_ = VECTOR["axis"](text,p,"bottom")
    VECTOR["axis"](text,h,"top",expected=x_scale)
    zeros = [box for n,box in marks if n == 0 and h.right < box[0] < h.right+20 and abs((box[1]+box[3])/2-h.bottom)<1.8]
    fives = [box for n,box in marks if n == 5 and h.right < box[0] < h.right+20 and abs((box[1]+box[3])/2-h.bottom-10/h_scale)<1.8]
    if len(zeros)!=1 or len(fives)!=1:
        raise ValueError("Could not corroborate the APP sheet's right-hand 0 and 5 m ticks")
    # The source's 5 m NPSHr tick is aligned with the 10 m head tick.
    n_scale = h_scale/2
    x_scale *= 3.6
    labels = [(n,box) for n,box in marks if n in (330,340,370,400)]
    hpaths = [line for _,line in VECTOR["curve_paths"](parts,h)]
    powers = [line for _,line in VECTOR["curve_paths"](parts,p)]
    pairs, npsh, lines = {}, {}, []
    for hp in hpaths:
        d = VECTOR["labeled"](hp,labels,start=True)
        if d is None or hp[0][0] > h.left + h.width*.1:
            continue
        matches = [pp for pp in powers if abs(hp[0][0]-pp[0][0]) < .1 and abs(hp[-1][0]-pp[-1][0]) < .1]
        if d in pairs or len(matches) != 1:
            raise ValueError("Head and power do not have a unique pair by start and end flow")
        pp = matches[0]
        pairs[d] = VECTOR["samples"](hp,h,x_scale,h_scale),VECTOR["samples"](pp,p,x_scale,p_scale)
        lines.extend([hp,pp])
        nn = [line for line in hpaths if line[0][1] < h.bottom+20 and line[-1][1] < h.bottom+60
              and VECTOR["labeled"](line,labels) == d and abs(line[-1][0]-hp[-1][0]) < 1.5]
        if len(nn) == 1:
            npsh[d] = VECTOR["samples"](nn[0],h,x_scale,n_scale)
            lines.append(nn[0])
        elif nn:
            raise ValueError("Multiple APP NPSHr traces for the same impeller")
    if set(pairs)!={330,340,370,400} or set(npsh)!={340,370,400}:
        raise ValueError("APP diameter coverage does not match the printed curves")
    length_x,length_y=page.get_size()
    overlay(image, [[(x*image.width/length_x,image.height-y*image.height/length_y)
                     for x,y in line] for line in lines],folder/"K13847-overlay.png")
    return pairs,npsh


def affinity_npshr(reference, source_diameter, target_diameter, head, power):
    ratio = target_diameter / source_diameter
    flow, required = np.array(reference).T
    # Stay inside all three source ranges and never reduce the published
    # suction requirement at the same flow for this preliminary estimate.
    lo = max(flow[0], flow[0]*ratio, head[0][0], power[0][0])
    hi = min(flow[-1], flow[-1]*ratio, head[-1][0], power[-1][0])
    if lo >= hi:
        raise ValueError("No overlapping flow range for the estimated NPSHr trace")
    grid = sorted({lo, hi} | {float(q) for q in np.r_[flow,flow*ratio] if lo < q < hi})
    return [[q, float(max(np.interp(q,flow,required), ratio**2*np.interp(q/ratio,flow,required)))]
            for q in grid]


def make_record(page, code, filename, pairs, npsh, estimated_sources=None):
    if not all(VECTOR["verify_pair"](*pair) for pair in pairs.values()):
        raise ValueError("Digitized head and power are hydraulically inconsistent")
    impellers = [{"diameterMm":d,"head":pair[0],"power":pair[1],"npshr":npsh.get(d,[])}
                 for d,pair in sorted(pairs.items(),reverse=True)]
    for impeller in impellers:
        source = (estimated_sources or {}).get(impeller["diameterMm"])
        if source is not None:
            impeller["npshrAffinitySourceDiameterMm"] = source
    print(code,"selection witness:",VECTOR["selection_witness"](impellers))
    return {"page":page,"sourceFile":filename,"curveNumber":code,"rpm":1470,"impellers":impellers}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    for name in ("catalog","a_head_points","a_npshr_image","output"):
        cli.add_argument(name,type=Path)
    args = cli.parse_args()
    folder=args.output.parent
    head=json.loads(args.a_head_points.read_text(encoding="utf-8"))
    if set(head)!={"320","340","360","380","400"} or any(
        abs(q*3.6-q_metric)>1e-6 or h<=0 for trace in head.values() for q,q_metric,h in trace
    ):
        raise ValueError("A head data must be the checked K18490 digitization")
    doc=pdfium.PdfDocument(str(args.catalog))
    page=doc[55].get_textpage().get_text_range()
    if not all(s in page for s in ("A33-100","K18490","1470","Open1")):
        raise ValueError("Not the expected K18490 source page")
    with tempfile.TemporaryDirectory(dir=folder) as temp:
        images=[]
        for number in (56,1066):
            prefix=Path(temp)/str(number)
            subprocess.run(["pdftoppm","-f",str(number),"-l",str(number),"-r","145","-singlefile","-png",
                            str(args.catalog),str(prefix)],check=True)
            with Image.open(prefix.with_suffix(".png")) as image:
                images.append(image.copy())
    with Image.open(args.a_npshr_image) as suction_image:
        npsh=a_suction(suction_image,folder)
    power=a_power(images[0],folder)
    pairs={int(d):([[q,h] for _,q,h in trace],power[int(d)]) for d,trace in head.items()}
    app_hp,app_n=app_traces(doc[1065],images[1],folder)
    app_n[330]=affinity_npshr(app_n[340],340,330,*app_hp[330])
    records=[make_record(56,"K18490",args.catalog.name,pairs,npsh),
             make_record(1066,"K13847",args.catalog.name,app_hp,app_n,{330:340})]
    args.output.write_text("[\n"+",\n".join(json.dumps(row,separators=(",",":")) for row in records)+"\n]\n",
                           encoding="utf-8",newline="\n")


if __name__ == "__main__":
    main()
