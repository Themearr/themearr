#!/usr/bin/env bash
# Stop — run the suites that gate the release, scoped to the half that actually changed.
#
# Why this exists: ci.yml gates PRs on frontend/api/e2e, but it reports minutes later and
# only tells you *that* something broke. The suites are ~5s, ~7s and ~8s; running them
# here means the failure lands while the change is still in context. And a merge to main
# publishes a GHCR release, so a red test that slips past both is a shipped regression.
#
# Scoped to uncommitted changes (`git status --porcelain`) so conversational turns and
# already-committed work don't re-run it.
set -uo pipefail

ROOT="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null)}"
cd "$ROOT" 2>/dev/null || exit 0

changed=$(git status --porcelain 2>/dev/null | cut -c4-)
[ -z "$changed" ] && exit 0

web=0; api=0; e2e=0
printf '%s\n' "$changed" | grep -qE 'src/Themearr\.Web/.*\.tsx?$' && web=1
printf '%s\n' "$changed" | grep -qE '\.cs$'                       && api=1

# E2E is the only suite that runs the shipped shape: the .NET host serving the built
# bundle on one origin. It exists because the unit suites structurally cannot see that
# wiring — the frontend runs in jsdom against a mocked `@/lib/api`, the API exercises
# handlers in isolation. So trigger it on the wiring it actually asserts, not on every
# .cs file: the request pipeline, the auth boundary and the route surface underneath it
# (Program.cs, ApiAuthMiddleware, Controllers/), the response headers it must apply to
# static and /api responses alike (SecurityHeaders), the bundle it serves (vite config,
# index.html, the router mount), and the suite itself.
printf '%s\n' "$changed" | grep -qE \
  'src/Themearr\.API/(Program\.cs|Services/(ApiAuthMiddleware|SecurityHeaders)\.cs|Controllers/)|src/Themearr\.Web/(vite\.config\.mts|index\.html|src/main\.tsx)|tests/e2e/' && e2e=1

[ "$web" = 0 ] && [ "$api" = 0 ] && [ "$e2e" = 0 ] && exit 0

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

if [ "$e2e" = 1 ]; then
  if [ ! -d "$ROOT/tests/e2e/node_modules" ]; then
    # Reported, not skipped silently. tests/e2e is a separate package precisely so the
    # release's `npm ci` never pulls Playwright, which means a fresh clone has no deps
    # here and the suite would otherwise vanish from the gate without saying so.
    fail="${fail}
--- e2e ---
tests/e2e has no node_modules — E2E did not run.
Fix: (cd tests/e2e && npm ci && npx playwright install chromium)"
  # Playwright points ASPNETCORE_WEBROOT at src/Themearr.Web/out, so the bundle must
  # exist and be current — a stale out/ silently tests the previous change. ~2s.
  elif ! out=$(cd "$ROOT/src/Themearr.Web" && npm run build 2>&1); then
    fail="${fail}
--- e2e (frontend build) ---
$(printf '%s' "$out" | tail -30)"
  elif ! out=$(cd "$ROOT/tests/e2e" && npx --no-install playwright test 2>&1); then
    fail="${fail}
--- playwright ---
$(printf '%s' "$out" | tail -40)"
  fi
fi

if [ -n "$fail" ]; then
  echo "Release gate failed. These suites gate the GHCR release on merge to main:${fail}" >&2
  exit 2
fi

exit 0
