#!/usr/bin/env bash
# Runs every test project except the excluded ones, collecting Cobertura coverage.
# Usage: build/test.sh [--coverage] [excluded-project-name ...]
set -euo pipefail

coverage=()
if [[ "${1:-}" == "--coverage" ]]; then
  coverage=(--coverage --coverage-output-format cobertura --coverage-settings "$(pwd)/build/coverage.runsettings")
  shift
fi

excluded=("Basalt.Testing" "Basalt.Testing.FakeGitHub" "Basalt.Benchmarks" "$@")
status=0
for project in tests/*/*.csproj; do
  name=$(basename "$project" .csproj)
  skip=false
  for e in "${excluded[@]}"; do [[ "$name" == "$e" ]] && skip=true; done
  $skip && continue
  echo "::group::$name"
  dotnet test --project "$project" --no-build -c "${CONFIGURATION:-Release}" -- "${coverage[@]}" ${TEST_FILTER:---filter-not-trait Category=Performance} --ignore-exit-code 8 --results-directory "$(pwd)/artifacts/test-results/$name" || status=1
  echo "::endgroup::"
done
exit $status
