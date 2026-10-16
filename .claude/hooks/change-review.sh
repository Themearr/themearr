#!/usr/bin/env bash
# Stop — four whole-change-set questions that no per-file check can ask.
#
# 1. Citation drift in an uncommitted spec/plan (see .claude/skills/check-citations).
# 2. A one-sided change to a movie/show sibling pair.
# 3. A new capability that no user-facing surface heard about.
# 4. A reviewer subagent the change set calls for. Hooks cannot spawn one, so this
#    names it instead — skipping becomes a stated choice, not an oversight.
#
# (3) is the general case and the reason this hook exists. TV shows shipped in 727ab9e
# (#31); the dashboard learned about them in 26ce683 (#38) and history in 5c6969c (#32),
# two and three PRs later. Nothing was broken — the feature was just invisible on every
# surface but its own until someone noticed. The same shape recurs whenever a capability
# lands without the queue, dashboard, history, settings, or system tab being asked
# whether they should show it.
#
# Reports each distinct finding ONCE. A Stop hook that exits 2 on a condition it cannot
# clear would loop forever, so the finding text is hashed and suppressed on repeat: fix
# it and it goes away, deliberately leave it and it stays quiet after the first mention.
set -uo pipefail

ROOT="${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel 2>/dev/null)}"
cd "$ROOT" 2>/dev/null || exit 0

changed=$(git status --porcelain 2>/dev/null | cut -c4-)
[ -z "$changed" ] && exit 0

findings=""

# --- 1. citation drift -------------------------------------------------------------
if printf '%s\n' "$changed" | grep -qE '^docs/superpowers/.*\.md$'; then
  if ! cites=$(bash "$ROOT/.claude/skills/check-citations/check-citations.sh" 2>&1); then
    findings="${findings}
--- citations ---
$(printf '%s' "$cites" | sed 's/\x1b\[[0-9;]*m//g' | grep -E '✗|\?|unresolved')"
  fi
fi

# --- 2. movie/show parity ----------------------------------------------------------
# left|right|label. Substring match, so a directory covers everything under it.
PAIRS='src/Themearr.API/Controllers/MoviesController.cs|src/Themearr.API/Controllers/ShowsController.cs|controller
src/Themearr.API/Services/AutoDownloadService.cs|src/Themearr.API/Services/ShowAutoDownloadService.cs|auto-download worker
src/Themearr.API/Services/AutoSyncService.cs|src/Themearr.API/Services/ShowAutoSyncService.cs|auto-sync worker
src/Themearr.API/Services/SyncService.cs|src/Themearr.API/Services/ShowSyncService.cs|sync service
src/Themearr.Web/src/app/movies|src/Themearr.Web/src/app/shows|page'

lopsided=""
while IFS='|' read -r left right label; do
  [ -z "$left" ] && continue
  l=0; r=0
  printf '%s\n' "$changed" | grep -qF "$left"  && l=1
  printf '%s\n' "$changed" | grep -qF "$right" && r=1
  [ "$l" = "$r" ] && continue
  if [ "$l" = 1 ]; then
    lopsided="${lopsided}
  movie $label changed, show side did not:  $left  →  $right"
  else
    lopsided="${lopsided}
  show $label changed, movie side did not:  $right  →  $left"
  fi
done <<< "$PAIRS"

if [ -n "$lopsided" ]; then
  findings="${findings}
--- movie/show parity ---$lopsided

These run deliberately parallel, and the seam does not span them: ILibrarySource is
movie-only (FetchAsync returns IReadOnlyList<MovieRecord>), so the show half is always a
separate edit. Make the matching change, or say in one line why this one is correctly
one-sided — many are."
fi

# --- 3. capability propagation -----------------------------------------------------
# Every added line in the change set, tracked and untracked alike — scoped to src/.
# Scoping is not an optimisation: without it the first version of this hook matched its
# own grep pattern in .claude/ and reported a schema change on every run.
added=$(git diff HEAD --unified=0 -- src/ 2>/dev/null | grep '^+' | grep -v '^+++')
while IFS= read -r f; do
  [ -n "$f" ] && [ -f "$f" ] && added="$added
$(cat "$f" 2>/dev/null)"
done < <(git ls-files --others --exclude-standard -- src/ 2>/dev/null)

# What counts as "a new capability" rather than a fix: a net-new endpoint, a net-new
# table or column, or a net-new controller/service/page. A bugfix touches none of these,
# which is what keeps this quiet on ordinary work.
capability=""
printf '%s\n' "$added" | grep -qE '\[Http(Get|Post|Put|Delete|Patch)' && capability="a new endpoint"
printf '%s\n' "$added" | grep -qiE 'CREATE TABLE|ADD COLUMN|ALTER TABLE' && capability="${capability:+$capability, }a schema change"
newfiles=$(printf '%s\n' "$changed" | grep -E '^src/Themearr\.(API/(Controllers|Services)|Web/src/app)/' || true)
while IFS= read -r f; do
  [ -n "$f" ] && ! git ls-files --error-unmatch "$f" >/dev/null 2>&1 && capability="${capability:+$capability, }a new file ($f)" && break
done <<< "$newfiles"

if [ -n "$capability" ]; then
  # name|paths that count as this surface having been touched
  SURFACES='dashboard|src/Themearr.API/Controllers/StatsController.cs src/Themearr.Web/src/app/dashboard
queue|src/Themearr.Web/src/app/queue
history|src/Themearr.API/Controllers/HistoryController.cs src/Themearr.Web/src/app/history
settings|src/Themearr.API/Controllers/SettingsController.cs src/Themearr.Web/src/app/settings
system (health/tasks)|src/Themearr.API/Controllers/SystemController.cs src/Themearr.Web/src/app/system
movies|src/Themearr.API/Controllers/MoviesController.cs src/Themearr.Web/src/app/movies
shows|src/Themearr.API/Controllers/ShowsController.cs src/Themearr.Web/src/app/shows
the frontend API client|src/Themearr.Web/src/lib/api.ts'

  untouched=""
  while IFS='|' read -r name paths; do
    [ -z "$name" ] && continue
    hit=0
    for p in $paths; do printf '%s\n' "$changed" | grep -qF "$p" && hit=1; done
    [ "$hit" = 0 ] && untouched="${untouched}
  $name"
  done <<< "$SURFACES"

  if [ -n "$untouched" ]; then
    findings="${findings}
--- capability propagation ---
This change set adds $capability. These surfaces were not touched:$untouched

Which of them should show it, count it, or let the user act on it? TV shows shipped in
#31 and the dashboard and history only caught up in #38 and #32 — nothing was broken,
the feature was just invisible everywhere but its own page for three PRs. Name the ones
that genuinely do not apply and move on; the answer is often 'none', but it should be an
answer rather than an omission."
  fi
fi

# --- 4. reviewers that nothing else can trigger -------------------------------------
# A hook is a shell command and cannot spawn a subagent, so these two reviewers depend
# entirely on someone deciding to run them. This closes that gap the only way available:
# it cannot invoke them, but it can make skipping them a stated choice rather than an
# oversight. Files listed because the reviewer's own description says it covers them.
SECURITY_FILES='Services/HostGuard.cs Services/ApiAuthMiddleware.cs Services/PosterUrlSigner.cs
Services/PlexPath.cs Services/ThemeFiles.cs Services/LogSanitizer.cs Services/SecurityHeaders.cs
Program.cs Controllers/AuthController.cs Controllers/PosterController.cs Controllers/WebhookController.cs'

CONCURRENCY_FILES='Services/AutoDownloadService.cs Services/AutoSyncService.cs
Services/ShowAutoDownloadService.cs Services/ShowAutoSyncService.cs Services/SyncService.cs
Services/ShowSyncService.cs Services/TaskRegistry.cs Services/PollBackoff.cs
Services/DownloadService.cs src/Themearr.Web/src/lib/useResource.ts'

reviewers=""
sec=""; for f in $SECURITY_FILES;    do printf '%s\n' "$changed" | grep -qF "$f" && sec="$sec $f"; done
con=""; for f in $CONCURRENCY_FILES; do printf '%s\n' "$changed" | grep -qF "$f" && con="$con $f"; done

[ -n "$sec" ] && reviewers="${reviewers}
  security-invariant-reviewer —$sec"
[ -n "$con" ] && reviewers="${reviewers}
  concurrency-reviewer —$con"

if [ -n "$reviewers" ]; then
  findings="${findings}
--- reviewers this change set calls for ---$reviewers

Nothing can invoke these automatically. Run them, or say in one line why this change does
not need them — but do not simply not mention them."
fi

[ -z "$findings" ] && exit 0

# Report each distinct finding once. See the header.
marker="$ROOT/.git/themearr-change-review.last"
hash=$(printf '%s' "$findings" | shasum | cut -d' ' -f1)
[ -f "$marker" ] && [ "$(cat "$marker" 2>/dev/null)" = "$hash" ] && exit 0
printf '%s' "$hash" > "$marker"

echo "Change-set review:${findings}" >&2
exit 2
