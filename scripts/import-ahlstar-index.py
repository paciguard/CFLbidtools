"""Build the Workbench's AHLSTAR source index from the manufacturer PDF."""
import argparse
import json
import re
from collections import Counter
from pathlib import Path

import pypdfium2 as pdfium


SHIFTED = {code: code + 29 for code in range(11, 94) if code not in (13, 32)}
TRANSLATION = str.maketrans(SHIFTED | {154: 216})
CURVE = re.compile(r"\b(Characteristic|Speed|Variable Speed)\s+Curve\s+([A-Z]\d{4,7})\b", re.I)
MODEL = re.compile(r"\bAHLSTAR\s+([A-Z]{1,8})\s*([0-9]+[-\u2013][0-9]+[A-Z]?(?:[-\u2013][0-9]+[A-Z]?)?)([^\r\n]*)", re.I)
SPEED = re.compile(r"(\d{2,5})(?:\s*(?:\.{2,3}|\u2026|[-\u2013]|to)\s*(\d{2,5}))?\s*r\s*p\s*m\b", re.I)
DIAMETER_ROW = re.compile(r"\s*(\d{2,4}(?:[.,]\s*\d+)?)\s*(?:\s+\S+)*\s*")
LAYOUT_MODEL = re.compile(r"^\s*([A-Z]{1,8})\s*(\d+-\d+[A-Z]?)\s+\(([^\r\n()]+)\)", re.M)
LAYOUT_NUMBER = re.compile(r"\b([A-Z]\d{4,7})\s*/\s*\d+\b")


def decode(text):
    # Two PostScript fonts expose glyph codes 29 below ASCII; decode only lines
    # identified by a shifted header or control-coded numbers in its table.
    result = []
    for line in text.split("\n"):
        body = line.rstrip("\r")
        if re.search(r"[\x0b-\x1f\x9a]|\$\+/67\$5|&KDUDFWHULVWLF|6SHHG\s*&XUYH|,03\(//\(5|,PSHOOHU", body):
            body = body.translate(TRANSLATION)
        result.append(body)
    return "\n".join(result)


def impeller_table(text, start):
    table = re.search(r"IMPELLER\s+DIA", text[start:], re.I)
    if not table:
        return []
    values = []
    for line in text[start + table.end():].splitlines():
        row = DIAMETER_ROW.fullmatch(line)
        if row:
            value = float(row[1].replace(" ", "").replace(",", "."))
            if value < 40 or value > 1800 or (values and value >= values[-1]):
                break
            values.append(value)
        elif values:
            break
        elif not re.search(r"^(?:\s|[\u00d8\u00f8DdK(])", line) and line.strip():
            break
    return values


def scan(text, page):
    normalized = decode(text)
    curve = CURVE.search(normalized)
    if not curve:
        if "Curve No / Version" not in normalized:
            return None
        model = LAYOUT_MODEL.search(normalized)
        number = LAYOUT_NUMBER.search(normalized)
        if not model or not number:
            return None
        speed = re.search(r"^\s*[\d.,]+\s*\(\d+(?:[.,]\d+)?\)\s+(\d{2,5})\s*$", normalized, re.M)
        if not speed and "CW viewed from coupling" in normalized:
            speed = re.search(r"\[\s*(?:m3/h|l/s|USGPM)\s*\][ \t]*\n[ \t]*(\d{3,4})[ \t]*\n[ \t]*mm\b",
                              normalized.split("CW viewed from coupling", maxsplit=1)[1])
        impeller = re.search(r"Impeller\s+type\s*:\s*([^/\r\n]+)", normalized, re.I)
        diameters = re.findall(r"^\s*(\d{2,4}(?:[.,]\d+)?)\s+mm\s*$", normalized, re.M)
        return {
            "page": page, "family": model[1], "size": model[2], "curveNumber": number[1], "kind": "fixed",
            "rpm": int(speed[1]) if speed else None, "minRpm": None, "maxRpm": None, "nominalSize": model[3].strip(),
            "impeller": impeller[1].strip() if impeller else None,
            "trimDiametersMm": [],
            "plottedDiametersMm": sorted({float(value.replace(",", ".")) for value in diameters}, reverse=True),
        }
    models = [match for match in MODEL.finditer(normalized) if match.start() < curve.start()]
    model = models[-1] if models else MODEL.search(normalized)
    if not model:
        return None
    family, size, suffix = model.groups()
    nominal = re.search(r"\(([^()\r\n]+)\)", suffix)
    speed = SPEED.search(normalized, curve.end())
    rpm_start = int(speed[1]) if speed else None
    rpm_end = int(speed[2]) if speed and speed[2] else None
    kind = "speed" if rpm_end is not None or "speed" in curve[1].lower() else "fixed"
    impeller = re.search(r"Impeller\s+type\s*:\s*([^\r\n]+)", normalized[curve.end():], re.I)
    return {
        "page": page, "family": family.upper(), "size": size,
        "curveNumber": curve[2].upper(), "kind": kind, "rpm": rpm_start if rpm_end is None else None,
        "minRpm": rpm_start if rpm_end is not None else None, "maxRpm": rpm_end,
        "nominalSize": nominal[1].strip() if nominal else None,
        "impeller": impeller[1].split("/")[0].strip() if impeller else None,
        "trimDiametersMm": impeller_table(normalized, curve.end()),
    }


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("pdf", type=Path)
    cli.add_argument("output", type=Path)
    cli.add_argument("--audit", type=Path)
    args = cli.parse_args()
    with pdfium.PdfDocument(args.pdf) as document:
        records, other_pages = [], []
        for index, page in enumerate(document, 1):
            text_page = page.get_textpage()
            text = text_page.get_text_range()
            text_page.close()
            page.close()
            record = scan(text, index)
            if record:
                records.append(record)
            else:
                other_pages.append([index, " ".join(decode(text).split())[:400]])
        data = {"sourceFile": args.pdf.name, "pageCount": len(document), "records": records}
    args.output.write_text(json.dumps(data, ensure_ascii=True, separators=(",", ":")) + "\n", encoding="ascii")
    if args.audit:
        args.audit.write_text(json.dumps(other_pages, ensure_ascii=True, indent=2) + "\n", encoding="ascii")
    print(f"{len(records)} curve pages; {len(other_pages)} other pages")
    print(dict(sorted(Counter(record["family"] for record in records).items())))


if __name__ == "__main__":
    main()
