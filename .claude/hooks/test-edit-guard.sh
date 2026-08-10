#!/usr/bin/env bash
# PreToolUse(Edit|MultiEdit|Write) — friction on changing an existing assertion.
#
# CLAUDE.md: "Never edit an existing test to make a change pass. If a change breaks a
# test, the change is wrong until proven otherwise." That rule was pure honour system;
# this is the mechanical half. It fires only when text being REMOVED from a test file
# contains an assertion — writing a new test, or adding one to an existing file, passes
# straight through, because those are the encouraged paths.
#
# Exit 2 blocks the call and hands the message back, so the justification has to be
# written down where the user can read it, instead of the assertion quietly moving.
# It is friction, not a lock: a stated reason lets the same edit through on retry.
set -uo pipefail

ROOT="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null)}"
payload=$(cat)

file=$(printf '%s' "$payload" | jq -r '.tool_input.file_path // empty')
[ -z "$file" ] && exit 0

# Test files only. The web test harness (test/apiMock.ts, test/setup.ts) is deliberately
# NOT matched — it is scaffolding, not assertions, and gets edited legitimately.
rel="${file#"$ROOT"/}"
case "$rel" in
  tests/Themearr.API.Tests/*.cs) ;;
  tests/e2e/*.spec.ts)           ;;
  src/Themearr.Web/src/*.test.ts|src/Themearr.Web/src/*.test.tsx) ;;
  *) exit 0 ;;
esac

tool=$(printf '%s' "$payload" | jq -r '.tool_name // empty')

case "$tool" in
  Edit)
    removed=$(printf '%s' "$payload" | jq -r '.tool_input.old_string // ""')
    ;;
  MultiEdit)
    removed=$(printf '%s' "$payload" | jq -r '[.tool_input.edits[]?.old_string] | join("\n")')
    ;;
  Write)
    # A Write to a path that doesn't exist yet is a new test — the whole point of
    # "tests first", so let it through. A Write OVER an existing test replaces every
    # assertion in the file in one unreviewable step; Edit is the reviewable form.
    [ -f "$file" ] || exit 0
    echo "Refusing a whole-file Write over the existing test file $rel.
A Write here replaces every assertion at once, which is exactly the change CLAUDE.md
asks to be justified rather than made quietly. Use Edit for the specific change so the
diff shows which assertion moved and why." >&2
    exit 2
    ;;
  *) exit 0 ;;
esac

# xUnit's Assert.*, the [Fact]/[Theory] attributes and their [InlineData] rows (dropping
# a row silently shrinks a Theory's coverage), and vitest/Playwright's expect matchers.
ASSERTIONS='Assert\.|\[Fact\]|\[Theory\]|InlineData|expect\(|toBe|toEqual|toContain|toHaveBeen|toThrow'

printf '%s' "$removed" | grep -qE "$ASSERTIONS" || exit 0

cat >&2 <<EOF
Blocked: this edit removes or rewrites an assertion in $rel.

CLAUDE.md: "Never edit an existing test to make a change pass. If a change breaks a
test, the change is wrong until proven otherwise. Say so rather than adjusting the
assertion."

Before retrying, say out loud which of these it is:
  1. The test is wrong — then state what it asserts, why that is incorrect, and what
     source line proves it (a claim about code carries its file:line).
  2. The change is wrong — then fix the change, not the test.
  3. Neither: a rename, a move, or a new case that leaves the assertion's meaning
     intact — say which, and retry.

Assertions in the removed text:
$(printf '%s' "$removed" | grep -nE "$ASSERTIONS" | head -10)
EOF
exit 2
