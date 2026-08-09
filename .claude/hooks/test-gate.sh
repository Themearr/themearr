#!/usr/bin/env bash
# Stop — run the suites that gate the release, scoped to the half that actually changed.
#
# Why this exists: release.yml runs `npm test` and `dotnet test`, but it triggers on
# push to main, and a merge to main publishes a GHCR release. There is no PR-time CI,
# so a red test is caught only after the merge that ships it. This is the real gate.
#
# Scoped to uncommitted changes (`git status --porcelain`) so conversational turns and
# already-committed work don't re-run it. ~5s frontend, ~7s API, ~12s both.
set -uo pipefail

ROOT="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null)}"
cd "$ROOT" 2>/dev/null || exit 0

changed=$(git status --porcelain 2>/dev/null | cut -c4-)
[ -z "$changed" ] && exit 0

web=0; api=0
printf '%s\n' "$changed" | grep -qE 'src/Themearr\.Web/.*\.tsx?$' && web=1
printf '%s\n' "$changed" | grep -qE '\.cs$'                       && api=1
[ "$web" = 0 ] && [ "$api" = 0 ] && exit 0

fail=""

if [ "$web" = 1 ]; then
  if ! out=$(cd "$ROOT/src/Themearr.Web" && npx --no-install vitest run 2>&1); then
    fail="${fail}
--- vitest ---
$(printf '%s' "$out" | tail -30)"
  fi
fi

if [ "$api" = 1 ]; then
  if ! out=$(dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo 2>&1); then
    fail="${fail}
--- dotnet test ---
$(printf '%s' "$out" | tail -30)"
  fi
fi

if [ -n "$fail" ]; then
  echo "Release gate failed. These suites gate the GHCR release on merge to main:${fail}" >&2
  exit 2
fi

exit 0
