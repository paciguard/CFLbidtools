"""Join the BB3/BB4 registers to the metric DWG/EXB extractions, in workbook order."""

import argparse
import gzip
import json
import math
import re
from pathlib import Path

from openpyxl import load_workbook


UNITS = {"head": "m", "shaft_power": "kW", "npshr": "m", "efficiency": "%"}
SCHEMAS = {"BB3": "bb3-digitized-curves-v1", "BB4": "bb4-native-curves-v1"}
CONFIRMED_BB3_SIZES = {"125-7": "125-75", "520-12": "520-120", "1000-22": "1000-220", "1200-20": "1200-200"}


def register_rows(file, file_column):
    book = load_workbook(file, read_only=True, data_only=True)
    try:
        rows = iter(book["Curve Register"].values)
        headers = next((row for row in rows if row[0] == "Curve No."), None)
        cols = ("Curve No.", "Pump type", "Pump size", "Speed (rpm)", file_column)
        if not headers or not set(cols).issubset(headers):
            raise ValueError(f"Missing register columns in {file.name}")
        for row in rows:
            values = tuple(row[headers.index(col)] for col in cols)
            if any(value is not None for value in values):
                yield values
    finally:
        book.close()


def check_geometry(data):
    for trace in data["traces"]:
        if trace["unit"] != UNITS.get(trace["quantity"]):
            raise ValueError(f"Unexpected units: {data['sourceFile']}: {trace['quantity']}")
        for segment in trace["segments"]:
            if len(segment) < 2 or not all(len(point) == 2 and all(math.isfinite(x) for x in point) for point in segment):
                raise ValueError(f"Invalid trace points: {data['sourceFile']}")


def maximum_trace_ready(data, api):
    valid = [t for t in data["traces"] if len(t["diametersMm"]) == 1 and len(t["segments"]) == 1 and
             all(b[0] > a[0] for a, b in zip(t["segments"][0], t["segments"][0][1:]))]
    heads = [t["diametersMm"][0] for t in valid if t["quantity"] == "head"]
    if len(heads) < 2:
        return False
    at_max = [t["quantity"] for t in valid if t["diametersMm"] == [max(heads)]]
    return all(at_max.count(q) == 1 for q in (UNITS if api == "BB4" else ("head", "shaft_power", "npshr")))


def imported(folder, api, serial):
    workbook = folder / f"{api}_Curve_Register.xlsx"
    staging = json.loads((folder / f"{api}_Curves.pending.json").read_text(encoding="utf-8-sig"))
    if staging["schema"] != SCHEMAS[api] or staging["flowUnit"] != "m3/h":
        raise ValueError(f"Unexpected {api} geometry schema or units")
    sources = {row["sourceFile"]: row for row in staging["sources"]}
    if len(sources) != len(staging["sources"]):
        raise ValueError(f"Duplicate drawing in {api} extraction")
    for code, style, size, rpm, filename in register_rows(workbook, "Source DWG" if api == "BB3" else "Source EXB"):
        if filename not in sources:
            raise ValueError(f"No unique matching {api} drawing: {filename}")
        source = sources.pop(filename)
        code, size = str(code or "").strip(), str(size or "").strip()
        if not size or not code.startswith(f"{api}_LO_") or code.endswith("_"):
            raise ValueError(f"Missing {api} register code or size: {filename}")
        if source["rpm"] != rpm:
            raise ValueError(f"Register and drawing RPM disagree: {filename}")
        types = [item.strip() for item in str(style).split("/")]
        if types != (["XDA"] if api == "BB3" else ["XMC", "XMD"]):
            raise ValueError(f"Unexpected {api} pump types: {types}")
        check_geometry(source)
        register_code = code
        notes = []
        if api == "BB3":
            blockers = list(source["selectionBlockers"])
            source_size = source["size"]
            prefix = "BB3_LO_SCSK_"
            if (CONFIRMED_BB3_SIZES.get(size) == source_size and code.startswith(prefix) and
                    code.removeprefix(prefix).strip() == size):
                notes.append(f"User confirmed size {source_size}; the workbook entered {size}.")
                code, size = prefix + source_size, source_size
            if source_size != size or code != prefix + size:
                blockers.append(f"Register size/code ({size}, {code}) disagrees with drawing size {source_size}.")
            limits = source["stageLimits"]
            if source["sourceModel"] == "SCSK125-75*n" and source["rpm"] == 2980:
                limits = [3, 12]
                notes.append("User-confirmed operating stage range: 3-12.")
            special = []
            minimum = source["minimumFlowM3h"]
            original = " / ".join(source["sourceCurveReferences"]) or source["sourceModel"]
        else:
            # The identity blocker is resolved by this register. Use only the measured full-trim curves.
            if staging.get("basis") != "single_stage" or source["referenceStages"] != 1:
                raise ValueError(f"Unexpected scaling basis: {filename}")
            blockers = [issue for issue in source["selectionBlockers"] if issue not in (
                "Assign curve number, pump type and size before production catalogue activation.",
                "Review hydraulic consistency and approve trim/stage calculation policy before selection.")]
            if not source["validation"]["status"].startswith("geometry_checks_passed") or not (
                0 <= source["validation"]["efficiencyDifferencePercentagePoints"] <= 5):
                blockers.append("BB4 drawing did not pass the head/power/efficiency consistency check.")
            source_size = re.fullmatch(r"M\s*([0-9.]+-[0-9.]+)×n([A-Z]+)", source["sourceModel"])
            if not source_size or re.sub(r"\s+", "", size) != f"M{source_size[1]}_{source_size[2]}" or code != f"BB4_LO_{size}":
                raise ValueError(f"Register and BB4 drawing model/variant disagree: {filename}")
            source_size = f"M{source_size[1]}_{source_size[2]}"
            cfg = source["stageConfiguration"]
            limits = [cfg["defaultMinimum"], cfg["defaultMaximum"]]
            special = cfg["specialStages"]
            minimum = source["printedMinimumFlowM3h"]
            original = source["sourceModel"]
        if not (source["referenceStages"] == 1 and limits and
                1 <= limits[0] <= limits[1] and all(int(n) == n and n > 0 for n in special)):
            if not blockers:
                blockers.append("Single-stage reference or permitted stage range is not confirmed.")
        if not maximum_trace_ready(source, api):
            blockers.append("Complete labelled maximum-impeller curves are not available.")
        notes.append("Preliminary selection uses published RPM and maximum impeller only.")
        serial += 1
        yield {
            "curveNumber": f"{code}_{serial}", "registerCode": register_code, "apiType": api, "pumpTypes": types,
            "size": size, "sourceSize": source_size, "rpm": int(rpm), "referenceStages": source["referenceStages"],
            "stageLimits": limits, "specialStages": special, "minimumFlowM3h": minimum,
            "sourceFile": filename, "sourceModel": source["sourceModel"], "originalCurveNumber": original,
            "sourceSha256": source["sourceSha256"], "sourceKind": source.get("sourceKind"),
            "selectionBlockers": blockers, "notes": notes, "traces": source["traces"],
            "efficiencyContours": source.get("efficiencyContours", []),
        }
    if sources:
        raise ValueError(f"Unlisted drawings in {workbook.name}: {', '.join(sources)}")


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("outputs", type=Path, help="Directory containing BB3 and BB4 staging/register folders")
    cli.add_argument("curve_numbers", type=Path)
    cli.add_argument("output", type=Path)
    args = cli.parse_args()
    existing = json.loads(args.curve_numbers.read_text(encoding="utf-8-sig"))
    rows = []
    for api in SCHEMAS:
        serial = max(int(row["Number"].rsplit("_", 1)[1]) for row in existing if row["Number"].startswith(api + "_"))
        group = list(imported(args.outputs / api, api, serial))
        rows.extend(group)
        print(f"{api}: {len(group)} records, {sum(not row['selectionBlockers'] for row in group)} selectable; "
              f"serials {serial + 1}-{serial + len(group)}.")
        for row in group:
            if any("Register size/code" in issue for issue in row["selectionBlockers"]):
                print(f"Review {row['curveNumber']}: printed size {row['sourceSize']}")
    data = json.dumps(rows, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    args.output.write_bytes(gzip.compress(data, compresslevel=9, mtime=0))


if __name__ == "__main__":
    main()
