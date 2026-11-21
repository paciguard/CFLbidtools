"""Read labels and digitize calibrated raster curves from the AHLSTAR PDF."""
import argparse
import dataclasses
import gzip
import json
import re
import runpy
import subprocess
from collections import Counter
from contextlib import contextmanager
from pathlib import Path
from statistics import median

import numpy as np
import pypdfium2 as pdfium
from PIL import Image, ImageFilter


HERE = Path(__file__).resolve().parent
SCALE = 2.5
NUMBER = re.compile(r"^\d+(?:[.,]\d+)?$")
checks = runpy.run_path(str(HERE/"import-ahlstar-vector.py"))
selection_witness, verify_pair = checks["selection_witness"],checks["verify_pair"]


class Unmatched(ValueError):
    pass


@dataclasses.dataclass(frozen=True)
class Grid:
    left: int
    top: int
    right: int
    bottom: int
    rows: tuple
    cols: tuple

    @property
    def width(self):
        return self.right-self.left

    @property
    def height(self):
        return self.bottom-self.top


def peaks(values, threshold):
    positions = np.flatnonzero(values >= threshold)
    if not len(positions):
        return ()
    return tuple(int(chunk[len(chunk)//2]) for chunk in np.split(positions,np.where(np.diff(positions)>1)[0]+1))


def grid_positions(gray):
    height,width = gray.shape
    panels = []
    for baseline, top_window, bottom_window in (
        ((.265,.491),(.222,.265),(.493,.539)),
        ((.581,.737),(.545,.59),(.739,.777)),
        ((.796,.886),(.758,.813),(.89,.935)),
    ):
        y0,y1 = (round(height*y) for y in baseline)
        threshold = np.mean(gray[y0:y1,:]<190, axis=0)
        cols = peaks(threshold,.65)
        primary = [x for x in cols if .133*width <= x <= .73*width]
        if len(primary)<8 or primary[-1]-primary[0]<width*.51:
            raise Unmatched("could not find regular raster grid columns")
        left,right = primary[0],primary[-1]
        # Include pale boundary lines when the chart ends between bold grid divisions.
        allcols = peaks(np.mean(gray[y0:y1,left-14:right+31]<234,axis=0),.70)
        xs = tuple(x+left-14 for x in allcols)
        xs = tuple(x for x in xs if .132*width<=x<=.735*width)
        left,right = xs[0],xs[-1]
        section = gray[:,left+3:right-2]
        rows = peaks(np.mean(section<233,axis=1),.76)
        def row_in(window, top):
            possibilities = [y for y in rows if window[0]*height <= y <= window[1]*height]
            for y in possibilities if top else reversed(possibilities):
                interior = y+5 if top else y-5
                if np.mean(gray[interior,np.array(xs)] < 226)>.50:
                    return y
            raise Unmatched("raster grid edge not found")
        top,bottom = row_in(top_window,True),row_in(bottom_window,False)
        ys = tuple(y for y in rows if top<=y<=bottom)
        if len(ys)<7 or bottom-top<height*.105:
            raise Unmatched("not three calibrated raster plots")
        panels.append(Grid(left,top,right,bottom,ys,xs))
    if max(g.left for g in panels)-min(g.left for g in panels)>5 or max(g.right for g in panels)-min(g.right for g in panels)>8:
        raise Unmatched("raster panels use different flow edges")
    return panels


def remove_grid(gray, grid):
    clean = gray[grid.top:grid.bottom+1,grid.left:grid.right+1].copy()
    # Subtract line darkness instead of erasing a whole band through labels/traces.
    values = clean.astype("int16")
    row_median = np.median(values, axis=1).astype("int16")
    values = np.minimum(255, values + (255-row_median[:,None]))
    col_median = np.median(values,axis=0).astype("int16")
    values = np.minimum(255, values + (255-col_median[None,:]))
    return values.astype("uint8")


def recognition_atlas(gray, panels):
    head,power,suction = panels
    atlas = Image.new("L",(max(1850,head.width+230),1940),255)
    zones = []
    def add(name, arr, x, y, offset, scale=1):
        tile=Image.fromarray(arr)
        if scale!=1:
            tile=tile.resize((round(tile.width*scale),round(tile.height*scale)),Image.Resampling.BICUBIC)
        atlas.paste(tile,offset)
        zones.append((name,x,y,*offset,tile.width,tile.height,scale))

    for i,g in enumerate(panels):
        x,y = g.left-37,g.top-13
        strip = gray[y:g.bottom+16,x:g.left-4]
        occupied=np.flatnonzero(np.sum(strip<190,axis=1)>=2)
        runs=np.split(occupied,np.flatnonzero(np.diff(occupied)>3)+1)
        j=0
        for run in runs:
            if not 5<=len(run)<=27 or j>=21:
                continue
            lo,hi=max(run[0]-4,0),run[-1]+5
            add(f"y{i}",strip[lo:hi],x,y+lo,(12+j*87,195+i*68),1.8)
            j+=1
    xax = gray[head.bottom+5:head.bottom+35,head.left-21:head.right+24]
    add("flow",xax,head.left-21,head.bottom+5,(10,4),1.3)
    ox = round((head.left+head.right)/2)-138
    add("flow_unit",gray[head.bottom+38:power.top-5,ox:ox+276],ox,head.bottom+38,(1300,5),1.3)
    aux = gray[head.top-68:head.top-25,head.left-22:head.right+22]
    add("aux",aux,head.left-22,head.top-68,(10,76),1.3)
    add("aux_unit",gray[head.top-124:head.top-70,ox:ox+276],ox,head.top-124,(1300,80),1.3)
    for i,g in enumerate(panels):
        layer = remove_grid(gray,g)
        if i==0:
            layer = layer[:,:round(g.width*.13)]
        else:
            layer = np.hstack((layer,gray[g.top:g.bottom+1,g.right+1:g.right+36]))
        # Bold annotations survive this opening; thin trace ink is suppressed.
        filtered = np.asarray(Image.fromarray(layer).filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3)))
        if i==0:
            add("label0",layer,g.left,g.top,(10,436))
            add("label0",filtered,g.left,g.top,(150,436))
        elif i==1:
            add("label1",filtered,g.left,g.top,(315,436))
            add("label1",layer,g.left,g.top,(315,910))
        else:
            add("label2",filtered,g.left,g.top,(315,1380))
            add("label2",layer,g.left,g.top,(315,1655))
    return atlas,zones


def assign_words(words,zones):
    output = {zone[0]:[] for zone in zones}
    for word in words:
        for name,x,y,ox,oy,w,h,s in zones:
            px,py = word["x"]+word["w"]/2,word["y"]+word["h"]/2
            if ox<=px<ox+w and oy<=py<oy+h:
                output[name].append({**word,"x":x+(word["x"]-ox)/s,"y":y+(word["y"]-oy)/s,
                                     "w":word["w"]/s,"h":word["h"]/s})
                break
    return output


@contextmanager
def windows_ocr():
    executable = str(Path.home().anchor / Path("Windows/System32/WindowsPowerShell/v1.0/powershell.exe"))
    worker = subprocess.Popen([executable, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned",
                               "-File", str(HERE/"ahlstar-windows-ocr.ps1")],
                              stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,encoding="utf-8")
    def recognize(path):
        worker.stdin.write(str(path)+"\n")
        worker.stdin.flush()
        reply=worker.stdout.readline()
        if not reply:
            raise RuntimeError("Local Windows OCR worker exited; check the English language pack.")
        result=json.loads(reply)
        if isinstance(result,dict):
            raise RuntimeError(result.get("error","Windows OCR rejected the input image."))
        return result
    try:
        yield recognize
    finally:
        worker.stdin.close()
        worker.wait()


def ocr_records(source, rows, targets, cache_dir, cache, pages):
    temp_images = (cache_dir/"ahlstar-ocr-images").resolve()
    temp_images.mkdir(exist_ok=True)
    outcomes = Counter()
    with windows_ocr() as recognize:
        with pdfium.PdfDocument(source) as doc, cache.open("a",encoding="utf-8") as items:
            for page_num in targets:
                if pages and page_num not in pages or page_num in rows:
                    continue
                page = doc[page_num-1]
                try:
                    gray = np.asarray(page.render(scale=SCALE).to_pil().convert("L"))
                    panels = grid_positions(gray)
                    atlas,zones = recognition_atlas(gray,panels)
                    atlas_path = temp_images/f"page-{page_num}.png"
                    atlas.save(atlas_path)
                    result=recognize(atlas_path)
                    words=assign_words(result,zones)
                    record = {"page":page_num,"grids":[dataclasses.asdict(g) for g in panels],"words":words}
                    outcomes["recognized"]+=1
                except Unmatched as error:
                    record={"page":page_num,"error":str(error)}
                    outcomes[str(error)]+=1
                finally:
                    page.close()
                items.write(json.dumps(record,ensure_ascii=True,separators=(",",":"))+"\n")
                items.flush()
                if sum(outcomes.values()) % 100 == 0:
                    print("OCR:",sum(outcomes.values()),"pages",flush=True)
    print(dict(outcomes),flush=True)


def image_pages(source, audit):
    with pdfium.PdfDocument(source) as doc:
        for number,reason in audit.items():
            if reason!="not a two-grid vector layout":
                continue
            page=doc[int(number)-1]
            try:
                bounds=(obj.get_bounds() for obj in page.get_objects(filter=[3]))
                if any(left<115 and right-left>180 and top-bottom>55 for left,bottom,right,top in bounds):
                    yield int(number)
            finally:
                page.close()


def numeric(text):
    cleaned=re.sub(r"[^0-9.,=OoCcUuIl|Ss]", "", text).strip(".,=")
    cleaned=cleaned.translate(str.maketrans("OoCcUuIl|Ss=,", "00000011155.."))
    return float(cleaned) if NUMBER.fullmatch(cleaned) and cleaned.count(".")<=1 else None


def word_box(word):
    return word["x"],word["y"],word["x"]+word["w"],word["y"]+word["h"]


def axis(words, origin, coordinates, horizontal=False):
    anchors=[]
    for word in words:
        value=numeric(word["t"])
        if value is None or value<=0:
            continue
        mid=word["x"]+word["w"]/2 if horizontal else word["y"]+word["h"]/2
        tick=min(coordinates,key=lambda c:abs(c-mid))
        if abs(tick-mid) > (6 if horizontal else 5):
            continue
        run=tick-origin if horizontal else origin-tick
        if run>4:
            anchors.append((run,value))
    modes=[]
    for distance,value in anchors:
        ratio=value/distance
        matches={(x,y) for x,y in anchors if abs(x*ratio-y)/y < .023}
        modes.append((len(matches),ratio,matches))
    if not modes:
        raise Unmatched("OCR did not recover axis values")
    hits,ratio,matched=max(modes,key=lambda m:m[0])
    if hits<2 or max(x for x,y in matched)-min(x for x,y in matched)<15 or any(
        count==hits and abs(slope/ratio-1)>.035 for count,slope,_ in modes
    ):
        raise Unmatched("OCR axis values are ambiguous")
    return median(y/x for x,y in matched)


def units_and_scales(words, panels):
    head,power,suction = panels
    try:
        flow = axis(words["flow"],head.left,head.cols,True)
    except Unmatched as error:
        raise Unmatched("primary flow axis: "+str(error)) from error
    # The thin upper ruler is deliberately outside the rectangular plot grid.
    auxiliary=[]
    for word in words.get("aux",()):
        value=numeric(word["t"])
        x=word["x"]+word["w"]/2-head.left
        if value is not None and value>0 and x>15 and x<head.width+8:
            auxiliary.append((x,value))
    for convert,metric in ((3.6,True),(.22712470704,False)):
        qscale=flow*convert
        matches=[(x,value) for x,value in auxiliary if abs(x*qscale-value)/value<.033]
        if len(matches)<2 or max(x for x,v in matches)-min(x for x,v in matches)<head.width*.17:
            continue
        flow_text=" ".join(w["t"] for w in words.get("flow_unit",())).lower()
        if metric and re.search(r"us[gcs]?pm",flow_text) or not metric and "/s" in flow_text:
            continue
        size_unit="metric" if metric else "US"
        ys=[]
        for i,(name,grid) in enumerate(zip(("head","power","NPSHr"),panels)):
            try:
                ys.append(axis(words[f"y{i}"],grid.bottom,grid.rows))
            except Unmatched as error:
                raise Unmatched(f"{name} axis: {error}") from error
        if not metric:
            ys=[a*b for a,b in zip(ys,(.3048,.74569987158,.3048))]
        return qscale,*ys,size_unit
    raise Unmatched("primary and secondary flow scales could not be reconciled")


def label_marks(items, diameters):
    marks=[]
    cached=list(items)
    for i,item in enumerate(cached):
        t=item["t"]
        value=numeric(t)
        box=word_box(item)
        options=[(value,box)]
        if value not in diameters:
            x,y,r,b=box
            for other in cached:
                a,o,j,p=word_box(other)
                if .5<=a-r<=7 and abs(y-o)<=3 and abs(b-p)<=4:
                    options.append((numeric(t+other["t"]),(x,min(y,o),j,max(b,p))))
        for d,position in options:
            if d not in diameters:
                continue
            if any(old==d and abs(pos[0]-position[0])<4 and abs(pos[1]-position[1])<5 for old,pos in marks):
                continue
            marks.append((d,position))
    return marks


def darkness(gray, grid, labels):
    clean=remove_grid(gray,grid)
    trace=255-clean
    for _,(l,t,r,b) in labels:
        x0,x1=max(0,round(l-grid.left-1)),min(trace.shape[1],round(r-grid.left+2))
        y0,y1=max(0,round(t-grid.top-1)),min(trace.shape[0],round(b-grid.top+1))
        if x0<x1 and y0<y1:
            trace[y0:y1,x0:x1]=0
    return trace


def bands(image, x, radius=3, threshold=65):
    available=image[:,max(0,x-radius):x+radius+1]
    return peaks(np.quantile(available,.55,axis=1),threshold)


def follow(ink, x, y, sign, steep=False):
    height,width=ink.shape
    found=[(x,float(y))]
    gaps=0
    delta=0.
    observed=1
    for xx in range(x+2*sign,width-1 if sign>0 else 1,2*sign):
        future=found[-1][1]+delta
        radius=(11 if steep else 5) + min(gaps,4)
        a=max(1,round(future)-radius)
        b=min(height-1,round(future)+radius+1)
        if a>=b:
            break
        line=np.max(ink[a:b,xx-1:xx+2],axis=1).astype("float64")
        indices=np.arange(a,b)
        merit=line*.065-abs(indices-future)*1.05
        index=int(np.argmax(merit))
        if line[index]<57 or merit[index]<-.5:
            if abs(future-found[-1][1])>radius:
                break
            if gaps>8:
                break
            found.append((xx,future))
            gaps+=1
        else:
            new_y=float(indices[index])
            delta=.75*delta+.25*(new_y-found[-1][1])
            found.append((xx,new_y))
            observed+=1
            gaps=0
    if gaps:
        found=found[:-gaps]
    return found,observed


def full_path(ink, x, y, steep=False):
    forward,a=follow(ink,x,y,1,steep)
    reverse,b=follow(ink,x,y,-1,steep)
    path=reverse[:0:-1]+forward
    if (a+b-1)/len(path)<.65:
        return []
    return path


def left_paths(ink, grid, head):
    result=[]
    for fraction in (.11,.08,.14):
        x=round(grid.width*fraction)
        paths=[]
        for y in bands(ink,x):
            if y<3 or y>=grid.height-3:
                continue
            candidate=full_path(ink,x,y)
            if not candidate:
                continue
            a,b=candidate[0],candidate[-1]
            if a[0]>grid.width*.12 or b[0]-a[0]<grid.width*.38 or head and b[1]<a[1]-grid.height*.075:
                continue
            paths.append((y,candidate))
        if len(paths)>len(result):
            result=paths
        if len(result)==5:
            break
    return result


def near_label(labels, grid, x, y, left=False):
    scores={}
    for d,(a,b,c,p) in labels:
        a,b,c,p=a-grid.left,b-grid.top,c-grid.left,p-grid.top
        if left:
            if (-5<=y-p<grid.height*.053 and a<grid.width*.14):
                scores[d]=min(scores.get(d,float("inf")),abs(y-p)+.13*abs(x-c))
        else:
            dx=max(a-x,x-c,0)
            dy=max(b-y,y-p,0)
            if dx<grid.width*.06 and dy<grid.height*.075:
                scores[d]=min(scores.get(d,float("inf")),dx+dy*1.5+.18*abs(a-x))
    ranked=sorted((s,d) for d,s in scores.items())
    if not ranked or len(ranked)>1 and ranked[1][0]-ranked[0][0]<2:
        return None
    return ranked[0][1]


def sample_path(path, grid, flow, scale):
    xs,ys=np.asarray(path).T
    # Take medians locally to suppress a one-pixel jump at an interrupted grid line.
    ys=np.asarray([np.median(ys[max(0,i-2):i+3]) for i in range(len(ys))])
    qs=np.linspace(xs[0],xs[-1],61)
    values=(grid.height-np.interp(qs,xs,ys))*scale
    if np.any(values<=0):
        raise Unmatched("plotted trace falls outside the calibrated axis")
    return [[round(q*flow,5),round(y,6)] for q,y in zip(qs,values)]


def stop_at_head_reversal(path, grid):
    # In the high-flow part of these charts a sustained rise in head is usually
    # a jump onto a crossing efficiency contour. Keep only the supported branch.
    limit=max(6, grid.height*.012)
    y_values=np.asarray([p[1] for p in path])
    trailing=np.convolve(y_values,np.full(9,1/9),mode="valid")
    high=-float("inf")
    index=0
    for i,y in enumerate(trailing):
        if path[i+4][0]<grid.width*.25:
            continue
        if y>high:
            high,index=y,i+4
        elif high-y>limit:
            return path[:index+1]
    return path


def trace_chart(gray, metadata, recognized, source_file):
    panels=[Grid(**item) for item in recognized["grids"]]
    words=recognized["words"]
    q,h,p,n,unit=units_and_scales(words,panels)
    head,power,suction=panels
    diameters=set(metadata["trimDiametersMm"])
    if len(diameters)<3:
        raise Unmatched("no usable source trim table")
    marks=[label_marks(words[f"label{i}"],diameters) for i in range(3)]
    inks=[darkness(gray,g,ls) for g,ls in zip(panels,marks)]
    heads=left_paths(inks[0],head,True)
    powers=left_paths(inks[1],power,False)
    if len(heads) not in (3,4,5,6,7) or len(powers)!=len(heads):
        raise Unmatched(f"trace seed count mismatch (head {len(heads)}, power {len(powers)})")
    hp=[pair[1] for pair in heads]
    pp=[pair[1] for pair in powers]
    all_d=[]
    for curve,shaft in zip(hp,pp):
        left=curve[0]
        at_head=near_label(marks[0],head,*left,True)
        at_power=near_label(marks[1],power,*shaft[-1])
        if at_head and at_power and at_head!=at_power:
            raise Unmatched("OCR diameter labels disagree between head and power")
        max_difference=.15 if at_head is not None and at_head==at_power else .065
        if abs(curve[-1][0]-shaft[-1][0])>head.width*max_difference:
            raise Unmatched("head and power trace endpoints did not match")
        all_d.append(at_head or at_power)
    if all_d[0] is None and max(diameters) in {d for d,_ in marks[2]}:
        all_d[0]=max(diameters)
    if all_d[-1] is None and min(diameters) in {d for d,_ in marks[2]}:
        all_d[-1]=min(diameters)
    if (any(d is None for d in all_d) or all_d!=sorted(set(all_d),reverse=True) or
        all_d[0]!=max(diameters) or all_d[-1]!=min(diameters)):
        raise Unmatched("could not confirm every plotted impeller diameter")
    result=[]
    for i,(d,curve,shaft) in enumerate(zip(all_d,hp,pp)):
        supported=stop_at_head_reversal(curve,head)
        # Trim a small terminal buffer where strokes can touch the diameter text.
        common_end=min(supported[-1][0],shaft[-1][0])-head.width*.016
        if i:
            previous=hp[i-1]
            yprev=np.interp([x for x,_ in supported],[x for x,_ in previous],[y for _,y in previous])
            overlap=[x for (x,y),level in zip(supported,yprev) if previous[0][0]<x<previous[-1][0] and y-level<4]
            if overlap:
                common_end=min(common_end,min(overlap)-4)
        supported=[point for point in supported if point[0]<=common_end]
        shaft=[point for point in shaft if point[0]<=common_end]
        if not supported or not shaft or supported[-1][0]-supported[0][0]<head.width*.34:
            raise Unmatched("matched impeller paths do not stay distinct")
        hc=sample_path(supported,head,q,h)
        pc=sample_path(shaft,power,q,p)
        if not verify_pair(hc,pc):
            raise Unmatched("inconsistent hydraulic efficiency after tracing")
        result.append({"diameterMm":d,"head":hc,"power":pc,"npshr":[]})
    used=[]
    for d in all_d:
        labels=[box for value,box in marks[2] if value==d]
        if not labels:
            continue
        endpoint=hp[all_d.index(d)][-1][0]
        possible=[]
        for l,t,r,b in labels:
            if not (suction.left<l<suction.right):
                continue
            x0=round(l-suction.left)
            x1=round(r-suction.left)
            mid=(t+b)/2-suction.top
            for x in (x1+3,x1+8,x0-5,round(endpoint-5),round(endpoint-17)):
                if not (1<x<suction.width-3) or abs(x-endpoint)>suction.width*.13:
                    continue
                for y in bands(inks[2],x,1,56):
                    if abs(y-mid)>suction.height*.24:
                        continue
                    candidate=full_path(inks[2],x,y,True)
                    if not candidate:
                        continue
                    start,end=candidate[0][0],candidate[-1][0]
                    if (end-start<suction.width*.22 or abs(end-endpoint)>suction.width*.12 or
                        abs(end-x1)>suction.width*.06):
                        continue
                    cost=abs(x-x1)*.24+abs(y-mid)*.56+abs(end-endpoint)*.18
                    possible.append((cost,candidate))
        if not possible:
            continue
        _,winner=min(possible,key=lambda pair:pair[0])
        if any(abs(path[-1][0]-winner[-1][0])<10 and abs(path[-1][1]-winner[-1][1])<8 for path in used):
            raise Unmatched("NPSHr labels point to an overlapping trace")
        used.append(winner)
        result[all_d.index(d)]["npshr"]=sample_path(winner,suction,q,n)
    if len(used)<2:
        raise Unmatched("could not trace two independent NPSHr diameters")
    try:
        witness=selection_witness(result)
    except ValueError as error:
        raise Unmatched("no common rated head, power and suction window") from error
    return {"page":metadata["page"],"sourceFile":source_file,"curveNumber":metadata["curveNumber"],
            "rpm":metadata["rpm"],"impellers":result}, {"units":unit,"scales":[q,h,p,n],"selectionDuty":witness}


def trace_records(args,index,recognized):
    results, audit=[],{}
    metadata={item["page"]:item for item in index["records"]}
    with pdfium.PdfDocument(args.pdf) as doc:
        for i,(number,rec) in enumerate(recognized.items()):
            if args.pages and number not in args.pages:
                continue
            if rec.get("error"):
                audit[number]=rec["error"]
                continue
            page=doc[number-1]
            try:
                gray=np.asarray(page.render(scale=SCALE).to_pil().convert("L"))
                chart,info=trace_chart(gray,metadata[number],rec,index["sourceFile"])
                results.append(chart)
                audit[number]=info
            except Unmatched as error:
                audit[number]=str(error)
            finally:
                page.close()
            if (i+1)%100==0:
                print("Traced",i+1,"OCR records",flush=True)
    payload=(json.dumps(results,separators=(",",":"))+"\n").encode("ascii")
    args.output.write_bytes(gzip.compress(payload,mtime=0) if args.output.suffix==".gz" else payload)
    args.trace_audit.write_text(json.dumps(audit,separators=(",",":"))+"\n",encoding="ascii")
    print("Candidate charts:",len(results), Counter(v for v in audit.values() if isinstance(v,str)),flush=True)


def unit_image(gray, record):
    canvas=Image.new("L",(440,296),255)
    for i,panel in enumerate(record["grids"]):
        x=panel["left"]
        y=round((panel["top"]+panel["bottom"])/2)
        crop=Image.fromarray(gray[y-140:y+140,x-101:x-35]).transpose(Image.Transpose.ROTATE_270)
        crop=crop.resize((420,99),Image.Resampling.BICUBIC)
        canvas.paste(crop,(10,96*i))
    return canvas


def unit_labels(words):
    lines=[]
    for i in range(3):
        row=sorted((word for word in words if 96*i<=word["y"]+word["h"]/2<96*(i+1)),key=lambda word:word["x"])
        lines.append(" ".join(word["t"] for word in row))
    return lines


def confirm_units(args,recognized):
    candidates=json.loads(args.candidates.read_text(encoding="ascii"))
    audit=json.loads(args.trace_audit.read_text(encoding="ascii"))
    output=[]
    temp=(args.cache.parent/"ahlstar-raster-units").resolve()
    temp.mkdir(exist_ok=True)
    with pdfium.PdfDocument(args.pdf) as doc, windows_ocr() as recognize:
        for chart in candidates:
            number=chart["page"]
            if args.pages and number not in args.pages:
                continue
            with_image=doc[number-1]
            try:
                gray=np.asarray(with_image.render(scale=SCALE).to_pil().convert("L"))
                image=unit_image(gray,recognized[number])
            finally:
                with_image.close()
            path=temp/f"units-{number}.png"
            image.save(path)
            labels=unit_labels(recognize(path))
            texts=[re.sub(r"[^a-z]", "", s.lower()) for s in labels]
            us=audit[str(number)]["units"]=="US"
            expect=("ft","bhp","ft") if us else ("m","kw","m")
            names=("head","[pd]ower","npsh")
            if all(re.search(rf"{name}[lfi]{{0,2}}{unit}[li]?$",text) for name,unit,text in zip(names,expect,texts)):
                audit[str(number)]["recognizedUnits"]=labels
                output.append(chart)
            else:
                audit[str(number)]={"blocked":"OCR could not independently confirm vertical units", "recognizedUnits":labels}
    payload=(json.dumps(output,separators=(",",":"))+"\n").encode("ascii")
    args.output.write_bytes(gzip.compress(payload,mtime=0) if args.output.suffix==".gz" else payload)
    args.review_audit.write_text(json.dumps(audit,ensure_ascii=True,separators=(",",":"))+"\n",encoding="ascii")
    print("OCR units confirmed for",len(output),"charts.",flush=True)


def main():
    cli=argparse.ArgumentParser(description=__doc__)
    cli.add_argument("pdf",type=Path)
    cli.add_argument("vector_audit",type=Path)
    cli.add_argument("cache",type=Path)
    cli.add_argument("--pages",nargs="+",type=int)
    cli.add_argument("--index",type=Path)
    cli.add_argument("--output",type=Path)
    cli.add_argument("--trace-audit",type=Path)
    cli.add_argument("--candidates",type=Path)
    cli.add_argument("--review-audit",type=Path)
    args=cli.parse_args()
    seen={}
    if args.cache.exists():
        seen={r["page"]:r for line in args.cache.read_text(encoding="utf-8").splitlines() if line for r in [json.loads(line)]}
    if args.candidates:
        if args.output is None or args.trace_audit is None or args.review_audit is None:
            cli.error("--candidates requires --output, --trace-audit and --review-audit")
        confirm_units(args,seen)
        return
    if args.output:
        if args.index is None or args.trace_audit is None:
            cli.error("--output requires --index and --trace-audit")
        trace_records(args,json.loads(args.index.read_text(encoding="ascii")),seen)
        return
    audit=json.loads(args.vector_audit.read_text(encoding="ascii"))
    targets=list(image_pages(args.pdf,audit))
    print("Image-based source pages:",len(targets),flush=True)
    ocr_records(args.pdf,seen,targets,args.cache.parent,args.cache,set(args.pages or []))


if __name__=="__main__":
    main()
