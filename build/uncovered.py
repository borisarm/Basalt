#!/usr/bin/env python3
"""Lists uncovered lines / partially covered branches of an assembly in a merged Cobertura report."""
import os
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
assembly = sys.argv[2] if len(sys.argv) > 2 else ""
for package in root.iter("package"):
    if assembly and package.get("name") != assembly:
        continue
    for cls in package.iter("class"):
        name = os.path.basename(cls.get("filename", "").replace(chr(92), "/"))
        for line in cls.iter("line"):
            partial = line.get("branch") == "true" and not line.get("condition-coverage", "").startswith("100")
            if line.get("hits") == "0" or partial:
                print(name, line.get("number"), line.get("condition-coverage", ""))
