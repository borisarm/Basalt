#!/usr/bin/env bash
# Local helper: runs the given test projects with coverage, merges the reports and applies the gate.
# Usage: build/cover.sh tests/Basalt.Domain.Tests [tests/...]
set -uo pipefail
cd "$(dirname "$0")/.."
rm -rf artifacts/test-results artifacts/coverage
for project in "$@"; do
  name=$(basename "$project")
  dotnet test --project "$project" -- --coverage --coverage-output-format cobertura \
    --coverage-settings "$(pwd -W 2>/dev/null || pwd)/build/coverage.runsettings" \
    --results-directory "$(pwd -W 2>/dev/null || pwd)/artifacts/test-results/$name" | grep -E "total:|correcto:|error:|failed|Failed" || true
done
dotnet tool run reportgenerator -reports:"artifacts/test-results/**/*.cobertura.xml" \
  -targetdir:"artifacts/coverage" -reporttypes:"Cobertura;TextSummary" > /dev/null
python build/coverage-gate.py artifacts/coverage/Cobertura.xml
