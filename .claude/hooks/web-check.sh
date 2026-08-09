#!/usr/bin/env bash
# PostToolUse(Edit|Write) — typecheck and lint the frontend when a TS/TSX file under
# src/Themearr.Web changes. ~4s combined.
#
# Exits 2 on failure so the output is fed back to Claude to fix immediately, rather
# than surfacing at the end of the turn when the context has moved on. Every plan doc
# in docs/superpowers/ restates this check by hand; this runs it instead.
set -uo pipefail

ROOT="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null)}"
WEB="$ROOT/src/Themearr.Web"

file=$(jq -r '.tool_input.file_path // .tool_response.filePath // empty')
case "$file" in
  "$WEB"/*.ts|"$WEB"/*.tsx) ;;
  *) exit 0 ;;
esac

cd "$WEB" 2>/dev/null || exit 0

if ! out=$(npx --no-install tsc --noEmit 2>&1); then
  { echo "tsc --noEmit failed:"; echo "$out" | head -40; } >&2
  exit 2
fi

# Baseline is 0 errors / 3 warnings — the three live in login/page.tsx and lib/auth.tsx.
# Gate on the count, not just errors: a 4th warning is new and belongs to this change.
lint=$(npx --no-install eslint . -f json 2>/dev/null)
errors=$(printf '%s' "$lint" | jq '[.[].errorCount] | add // 0' 2>/dev/null || echo 0)
warnings=$(printf '%s' "$lint" | jq '[.[].warningCount] | add // 0' 2>/dev/null || echo 0)

if [ "$errors" -gt 0 ] || [ "$warnings" -gt 3 ]; then
  {
    echo "eslint: $errors errors, $warnings warnings (baseline: 0 errors, 3 warnings)"
    npx --no-install eslint . 2>&1 | tail -40
  } >&2
  exit 2
fi

exit 0
