#!/usr/bin/env bash
# ioResolves every `file.ext:NN` citatn in the given docs and prints the line it points
# at, so a claim can be compared against the source instead of against another document.
#
# Usage: check-citations.sh [file.md ...]
#   With no arguments, checks uncommitted .md files under docs/superpowers/.
#
# Exits 1 if any citation fails to resolve. It cannot tell you a claim is WRONG — only
# that the line exists and what it says. Reading the printed line is the actual check.
set -uo pipefail

ROOT="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null)}"
cd "$ROOT" || exit 1

if [ $# -gt 0 ]; then
  targets=$(printf '%s\n' "$@")
else
  targets=$(git status --porcelain -- docs/superpowers 2>/dev/null | cut -c4- | grep -E '\.md$')
fi

if [ -z "$targets" ]; then
  echo "No target docs. Pass paths explicitly, or edit a doc under docs/superpowers/ first."
  exit 0
fi

# A path-ish token ending in a source extension, then :NN or :NN-MM. Matches a bare
# filename (AutoDownloadService.cs:19) as well as a repo-relative path, because both
# forms appear in the specs.
CITE='[A-Za-z0-9_./-]+\.(cs|ts|tsx|mts|mjs|yml|yaml|json|sh|csproj):[0-9]+(-[0-9]+)?'

bad=0; total=0

while IFS= read -r doc; do
  [ -z "$doc" ] && continue
  if [ ! -f "$doc" ]; then
    printf '%s\n  ! not a file\n' "$doc"; bad=$((bad + 1)); continue
  fi

  printf '\n\033[1m%s\033[0m\n' "$doc"
  found=0

  while IFS= read -r hit; do
    docline=${hit%%:*}
    cite=${hit#*:}
    ref=${cite%:*}
    span=${cite##*:}
    start=${span%%-*}
    end=${span##*-}
    found=1; total=$((total + 1))

    # Resolve against git's index rather than the filesystem, so build output under
    # obj/ and bin/ can never satisfy a citation.
    if [ -f "$ref" ]; then
      path="$ref"
    else
      matches=$(git ls-files "*/$ref" "$ref" 2>/dev/null | sort -u)
      count=$(printf '%s' "$matches" | grep -c . || true)
      if [ "$count" = 0 ]; then
        printf '  \033[31m?\033[0m %-56s no file matching "%s" is tracked  (doc line %s)\n' "$cite" "$ref" "$docline"
        bad=$((bad + 1)); continue
      elif [ "$count" -gt 1 ]; then
        printf '  \033[31m?\033[0m %-56s ambiguous, %s files match  (doc line %s)\n' "$cite" "$count" "$docline"
        printf '%s\n' "$matches" | sed 's/^/        /'
        bad=$((bad + 1)); continue
      fi
      path="$matches"
    fi

    lines=$(wc -l < "$path" | tr -d ' ')
    if [ "$end" -gt "$lines" ]; then
      printf '  \033[31m✗\033[0m %-56s %s has only %s lines  (doc line %s)\n' "$cite" "$path" "$lines" "$docline"
      bad=$((bad + 1)); continue
    fi

    # Print what is actually there. This is the point of the tool: four documents
    # agreeing is one source restated four times — only the source settles it.
    text=$(sed -n "${start}p" "$path" | sed 's/^[[:space:]]*//' | cut -c1-90)
    if [ "$start" = "$end" ]; then
      printf '  \033[32m✓\033[0m %-56s %s\n' "$cite" "$text"
    else
      printf '  \033[32m✓\033[0m %-56s %s …(%s lines)\n' "$cite" "$text" "$((end - start + 1))"
    fi
  done < <(grep -noE "$CITE" "$doc")

  [ "$found" = 0 ] && printf '  \033[33m—\033[0m no file:line citations found\n'
done <<< "$targets"

printf '\n%s citation(s) checked, %s unresolved\n' "$total" "$bad"
[ "$bad" -gt 0 ] && exit 1
exit 0
