#!/usr/bin/env python3
"""Coverage gate (spec 09 §10): checks line/branch coverage per assembly in a merged Cobertura report."""
import sys
import xml.etree.ElementTree as ET

# assembly prefix -> (min line %, min branch %)
THRESHOLDS = [
    ("Basalt.Domain", 95.0, 90.0),
    ("Basalt.Application", 90.0, 85.0),
    ("Basalt.Presentation", 85.0, 75.0),
    ("Basalt.Adapters.FileSystem", 80.0, 70.0),
    ("Basalt.Adapters.Markdown", 80.0, 70.0),
    ("Basalt.Adapters.Index", 80.0, 70.0),
    ("Basalt.Adapters.GitHub", 80.0, 70.0),
]


def main(path: str) -> int:
    root = ET.parse(path).getroot()
    failures = []
    for package in root.iter("package"):
        name = package.get("name", "")
        for prefix, min_line, min_branch in THRESHOLDS:
            if name != prefix:
                continue
            lines = [l for l in package.iter("line")]
            if not lines:
                print(f"{name}: no executable lines (skipped)")
                continue
            line_rate = float(package.get("line-rate", "0")) * 100
            branch_rate = float(package.get("branch-rate", "0")) * 100
            has_branches = any(l.get("branch") == "true" for l in lines)
            status = "OK"
            if line_rate < min_line or (has_branches and branch_rate < min_branch):
                status = "FAIL"
                failures.append(name)
            print(f"{name}: lines {line_rate:.1f}% (min {min_line}), branches {branch_rate:.1f}% (min {min_branch}) {status}")
    if failures:
        print("Coverage gate failed for: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "artifacts/coverage/Cobertura.xml"))
