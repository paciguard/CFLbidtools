"""Apply the edited HDD/KBB curve register without changing hydraulic data."""

import argparse
import json
import re
from pathlib import Path

from openpyxl import load_workbook


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def migrate(workbook, catalog, assignments):
    candidates = [row for row in catalog if row["family"] in ("HDD", "KBB") and "previousCurveNumber" not in row]
    pending = {row["curveNumber"]: row for row in candidates}
    if not pending or len(pending) != len(candidates):
        raise ValueError("Expected unrenumbered HDD/KBB rows with unique current curve numbers")
    occupied = {row["Number"].casefold() for row in assignments}
    occupied.update(row["curveNumber"].casefold() for row in catalog if row["curveNumber"] not in pending)
    serial = max(int(match[1]) for number in occupied
                 if (match := re.fullmatch(r"bb2_(?:.*_)?([1-9][0-9]*)", number)))
    changes = {}
    fields = ("Current Curve No.", "New Curve No.", "New Pump Type", "Current Pump Type",
              "Pump Size", "Source PDF SHA-256")
    book = load_workbook(workbook, read_only=True, data_only=True)
    try:
        rows = book["Curve Register"].iter_rows(min_row=5, values_only=True)
        header = next(rows)
        indexes = [header.index(field) for field in fields]
        for line, row in enumerate(rows, 6):
            if not any(value is not None for value in row):
                continue
            old, prefix, series, family, size, sha = (row[i] for i in indexes)
            source = pending.pop(old, None)
            if source is None or (family, size, sha) != (source["family"], source["size"], source["sourceSha256"]):
                raise ValueError(f"Row {line} does not match one unique source curve: {old!r}")
            if not isinstance(prefix, str) or not re.fullmatch(r"BB2_[A-Za-z0-9_.-]+", prefix.strip()):
                raise ValueError(f"Row {line} has an invalid BB2 curve code: {prefix!r}")
            if series != "XDB":
                raise ValueError(f"Row {line} must use pump type XDB")
            serial += 1
            number = f"{prefix.strip()}_{serial}"
            if number.casefold() in occupied:
                raise ValueError(f"Row {line} duplicates curve number {number}")
            occupied.add(number.casefold())
            changes[old] = {**source, "curveNumber": number, "series": series, "previousCurveNumber": old}
    finally:
        book.close()
    if pending:
        raise ValueError(f"Missing register rows for: {', '.join(pending)}")
    return [changes.get(row["curveNumber"], row) for row in catalog], changes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("workbook", "catalog", "assignments", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    rows, changes = migrate(args.workbook, read_json(args.catalog), read_json(args.assignments))
    args.output.write_text("[\n" + ",\n".join(json.dumps(row, ensure_ascii=True, separators=(",", ":"))
                                                for row in rows) + "\n]\n", encoding="utf-8", newline="\n")
    numbers = [row["curveNumber"] for row in changes.values()]
    print(f"Renumbered {len(numbers)} curves as XDB: {numbers[0]} through {numbers[-1]}.")


if __name__ == "__main__":
    main()
