#!/usr/bin/env python3
"""
build_and_analyze — der "Compiler-als-Verifier"-Layer.

Laeuft `dotnet build`, parst Roslyn/MSBuild-Diagnostics zu strukturiertem JSON,
gibt eine knappe Zusammenfassung aus und setzt den Exit-Code = Build-Status.
Gedacht als Tool, das ein Coding-Agent nach jeder Aenderung aufruft, statt rohen
Compiler-Output zu interpretieren.

Nutzung:
    python build_and_analyze.py [PROJEKT_ODER_SLN] [-- <weitere dotnet args>]
    cat sample_output.txt | python build_and_analyze.py --parse-stdin   # zum Testen

Exit-Codes:
    0  Build gruen (keine Errors)
    1  Build hat Errors
    2  dotnet nicht gefunden / Aufruf fehlgeschlagen
"""
import json
import re
import subprocess
import sys

# Faengt sowohl Zeilen MIT Dateiposition als auch reine "error CSxxxx:"-Zeilen.
# Also handles errors without error codes (like Dalamud.NET.Sdk errors)
DIAG = re.compile(
    r"^(?:(?P<file>.+?)\((?P<line>\d+),(?P<col>\d+)\):\s+)?"
    r"(?P<sev>error|warning)\s+(?P<code>[A-Za-z]+\d+|):\s+"
    r"(?P<msg>.*?)(?:\s+\[(?P<proj>[^\]]+)\])?\s*$"
)


def parse(text):
    seen = set()
    diags = []
    for raw in text.splitlines():
        m = DIAG.match(raw.strip())
        if not m:
            continue
        d = {
            "severity": m.group("sev"),
            "code": m.group("code"),
            "file": m.group("file"),
            "line": int(m.group("line")) if m.group("line") else None,
            "col": int(m.group("col")) if m.group("col") else None,
            "message": m.group("msg").strip(),
        }
        # MSBuild gibt dieselbe Diagnose oft pro Projekt mehrfach aus -> dedupe
        key = (d["code"], d["file"], d["line"], d["col"], d["message"])
        if key in seen:
            continue
        seen.add(key)
        diags.append(d)
    return diags


def run_dotnet(args):
    cmd = ["dotnet", "build", "-clp:NoSummary", "--nologo"] + args
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True)
    except FileNotFoundError:
        print(json.dumps({"ok": False, "error": "dotnet nicht im PATH"}), file=sys.stderr)
        sys.exit(2)
    return proc.stdout + "\n" + proc.stderr


def main():
    argv = sys.argv[1:]
    if "--parse-stdin" in argv:
        text = sys.stdin.read()
    else:
        text = run_dotnet(argv)

    diags = parse(text)
    errors = [d for d in diags if d["severity"] == "error"]
    warnings = [d for d in diags if d["severity"] == "warning"]
    ok = len(errors) == 0

    result = {
        "ok": ok,
        "errors": len(errors),
        "warnings": len(warnings),
        "diagnostics": diags,
    }
    # Maschinenlesbar fuer den Agenten:
    print(json.dumps(result, ensure_ascii=False, indent=2))

    # Menschenlesbar fuer dich (auf stderr, stoert das JSON nicht):
    status = "GRUEN ✅" if ok else "ROT ❌"
    print(f"\n[build_and_analyze] {status} — {len(errors)} Errors, {len(warnings)} Warnings",
          file=sys.stderr)
    for d in errors[:15]:
        loc = f"{d['file']}:{d['line']}" if d["file"] else "(?)"
        print(f"  ❌ {d['code']} {loc} — {d['message']}", file=sys.stderr)

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()