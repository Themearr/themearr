---
name: check-citations
description: Verify every file:line citation in a spec, plan, or design doc actually resolves, and read what the cited line says. Use after writing or editing anything under docs/superpowers/, before opening a PR that adds a spec or plan, and whenever a doc states a constant, default, signature, or threshold. Also use when asked to check citations, verify a spec's claims, or confirm a documented value is still true.
---

# Checking citations

CLAUDE.md: *"A claim about code — a constant's value, a signature, a default — carries
its `file:line`, in specs and plans as much as in comments. Writing the citation is what
forces the lookup."*

The failure this guards against is documented and real: `NoMatchCooldown` was written as
24h in a spec, a plan, a code comment and a test comment before anyone opened
`AutoDownloadService.cs:19` and read `FromHours(6)`. **Four documents agreeing is one
source restated four times.** Consensus across documents is not evidence; the source is.

## Run it

```bash
bash .claude/skills/check-citations/check-citations.sh                    # uncommitted docs/superpowers/*.md
bash .claude/skills/check-citations/check-citations.sh docs/superpowers/specs/foo.md
```

It resolves each `file.ext:NN` (and `NN-MM` ranges) against git's index and prints the
line it points at:

```
  ✓ Services/AutoDownloadService.cs:19   private static readonly TimeSpan NoMatchCooldown  = TimeSpan.FromHours(6);
  ✗ Services/YoutubeService.cs:812       src/Themearr.API/Services/YoutubeService.cs has only 72 lines  (doc line 2)
  ? ThemeMatcher.cs:44                   no file matching "ThemeMatcher.cs" is tracked  (doc line 3)
```

Exit 1 if anything is unresolved.

## What it does and does not prove

The script only proves the line **exists** and shows you its text. It cannot tell you the
claim is right — matching the printed line against what the doc asserts is your job, and
it is the whole point. A ✓ next to a line that says something different from the
surrounding paragraph is the finding.

Three failure modes it catches:

| Symptom | What happened |
|---|---|
| `?` no file matching | The file was renamed, or never existed — a citation invented to satisfy the convention |
| `✗` only N lines | The file shrank; the line number drifted, so the citation now points somewhere arbitrary |
| `✓` but the text contradicts the doc | The value changed under the doc — the `NoMatchCooldown` case exactly |

## After running

Fix the doc, not the citation format. If a cited line has drifted, re-read the
surrounding code before repointing the number — a drifted line number usually means the
logic moved too, and the claim may no longer hold at all.

If a doc has **no** citations and makes claims about code (a threshold, a default, an
interval, an interface's shape), that is itself the finding: the claims were never
looked up. Add the citations, which forces the lookup.
