"""Join extracted metric traces to the BB2 curve-number workbook by PDF filename."""

import argparse
from collections import Counter
import json
from pathlib import Path
import re

from openpyxl import load_workbook


def file_key(value):
    return " ".join(value.casefold().removesuffix(".pdf").split())


def imported(workbook_path, curves_path):
    book = load_workbook(workbook_path, read_only=True, data_only=True)
    try:
        rows = list(book["PDF File List"].values)
    finally:
        book.close()
    start = rows.index(("Curve No.", "pump type and size")) + 1
    expected = next(int(value) for label, value in rows[:start] if label == "Files")
    base = json.loads(curves_path.read_text(encoding="utf-8"))
    if base["schema"] != "bb2-digitized-curves-v1" or base["flow_unit"] != "m3/h":
        raise ValueError("BB2 extraction must use metric units and the supported schema")
    sheets = {file_key(sheet["source_file"]): sheet for sheet in base["sheets"]}
    if len(sheets) != len(base["sheets"]) or len(sheets) != expected:
        raise ValueError("PDF filenames are not unique or do not match the workbook's file count")
    for code, filename in rows[start:]:
        if code is None and filename is None:
            continue
        if not isinstance(code, str) or not re.fullmatch(r"BB2_LO_[1-9][0-9]*", code):
            raise ValueError(f"Invalid workbook curve number: {code!r}")
        key = file_key(filename)
        if key not in sheets:
            raise ValueError(f"Missing or duplicate PDF match: {filename!r}")
        sheet = sheets.pop(key)
        label = re.sub(r"\s+CAD$", "", filename.strip(), flags=re.I)
        model = re.search(r"\b(HDD|KBB)\s*(\d[0-9A-Za-z.-]*)$", label, re.I)
        if not model:
            raise ValueError(f"Missing pump type and size in {filename!r}")
        family, size = model[1].upper(), model[2].lower()
        size = re.sub(r"^(\d+(?:\.\d+)?)([a-z])(?=-)", lambda m: m[1]+m[2].upper(), size)
        size = re.sub(r"-(a|g)$", lambda m: m[0].upper(), size)
        if size == "40-1802" and sheet["printed_model"] == "HDD40-180*2":
            size = "40-180x2"
        notes = list(sheet["notes"])
        blockers = []
        series = sheet["series"]
        expected_units = {"head": "m", "efficiency": "%", "shaft_power": "kW", "npshr": "m"}
        if {s["quantity"]: s["unit"] for s in series} != expected_units or any(
            expected_units[s["quantity"]] != s["unit"] for s in series
        ):
            raise ValueError(f"Unexpected or missing units in {filename!r}")
        if any(s["status"] == "partial_trace" for s in series):
            blockers.append("Incomplete vector trace; verify against the source PDF.")
        if any(len(s["impeller_diameters_mm"] or []) > 1 for s in series):
            blockers.append("The PDF specifies paired impeller diameters; the selector does not solve paired trims.")
        if any("versus" in note and "shaft power" in note for note in notes):
            blockers.append("The plotted efficiency disagrees with head and shaft power; verify the source.")
        if any("Confirm before selection" in note for note in notes):
            blockers.append("The stage count in the PDF conflicts with the model designation.")
        if not sheet["printed_model"].startswith(family):
            notes.append(f"Workbook identifies {family} {size}; PDF header reads {sheet['printed_model']}.")
        yield {
            "curveNumber": code, "family": family, "size": size, "rpm": sheet["rpm"],
            "stages": sheet["stages_as_printed"], "sourceFile": sheet["source_file"],
            "sourcePage": sheet["source_page"], "sourceModel": sheet["printed_model"],
            "originalCurveNumber": sheet["original_curve_number"] or sheet["drawing_filename_reference"]
            or sheet["printed_model"],
            "sourceSha256": sheet["sha256"], "workbookLabel": filename,
            "notes": notes, "selectionBlockers": blockers,
            "traces": [{"quantity": s["quantity"], "unit": s["unit"], "status": s["status"],
                        "diametersMm": s["impeller_diameters_mm"] or [],
                        "segments": [part["points"] for part in s["segments"]]} for s in series]
        }
    if sheets:
        raise ValueError(f"PDFs missing from workbook: {sorted(sheets)}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workbook", type=Path)
    parser.add_argument("extraction", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    rows = list(imported(args.workbook, args.extraction))
    if len({row["curveNumber"] for row in rows}) != len(rows):
        raise ValueError("Duplicate curve numbers in workbook")
    args.output.write_text("[\n" + ",\n".join(json.dumps(r, ensure_ascii=True, separators=(",", ":"))
                                               for r in rows) + "\n]\n", encoding="utf-8")
    print(f"{len(rows)} curves; {sum(not r['selectionBlockers'] for r in rows)} selectable.")
    print(dict(Counter(r["family"] for r in rows)))
    print("Review only:", ", ".join(r["curveNumber"] for r in rows if r["selectionBlockers"]))


if __name__ == "__main__":
    main()
