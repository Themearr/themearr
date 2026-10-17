# Open issues: review and implementation order

Reviewed 17 October 2026. Repository: [Themearr/themearr](https://github.com/Themearr/themearr). GitHub returned six open issues (#49, #51–#55), no issue comments and no open PRs at review time. Priorities below are recommendations, not GitHub label changes. No issues were commented on, relabelled or closed.

## Baseline and confidence

The working checkout was `9868abc5bf4795899627c2256e78a60c01d2296e`; upstream main had advanced to [e81b25f / PR #59](https://github.com/Themearr/themearr/pull/59). I fetched main without changing the working branch and exported that exact commit to `/tmp/themearr-issue-review-e81b25f`. References to changed upstream code use immutable GitHub permalinks naming that commit and exact line anchors. Former local file/line citations were verified identical in both snapshots and are now immutable baseline permalinks as well, so later implementation cannot silently change their meaning.

The plans account for #59's existing admission locking, generation protection, replacement transaction and modal polling guards. The issues' old line numbers and historical test counts are not treated as current evidence. Reported race windows are supported by source inspection; new deterministic reproductions are the first implementation step, not work already completed by this planning review.

Validation of the upstream snapshot, using existing dependency installations:

| Check | Result |
|---|---|
| Full API suite | 672 passed, 0 failed, 0 skipped |
| Full frontend suite, normal `npm test` | 143 passed across 35 files; no failures/skips |
| Frontend type check and production build | Passed through `npm run build` |
| ESLint | 0 errors, 3 existing warnings |
| E2E against rebuilt SPA and real throwaway API | 11 passed; no failures/skips |
| Native deployment/release regressions | 21 passed |

The earlier checkout initially needed `NODE_OPTIONS=--no-experimental-webstorage` for its frontend tests. That is already fixed by #59's test configuration ([src/Themearr.Web/vite.config.mts @ e81b25f, lines 26–28](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/vite.config.mts#L26-L28)), so no new fix is planned. API and E2E required sandbox escalation for local sockets; both completed successfully. E2E's expected first-run health warnings are not failed tests.

## Issue-by-issue disposition

| Issue | Assessment at current main | Suggested priority / size | Implementation plan |
|---|---|---|---|
| [#51](https://github.com/Themearr/themearr/issues/51) Radarr setup verdict | Still open: result/error/finally are unstamped and Continue saves live fields | High / small | [Verified-input gate](2026-10-17-issue-51-wizard-radarr.md) |
| [#53](https://github.com/Themearr/themearr/issues/53) queue refetch skips | Still open: numeric cursor survives swappable same-media lists | High / medium | [Identity-based cursor](2026-10-17-issue-53-queue-cursor.md) |
| [#52](https://github.com/Themearr/themearr/issues/52) delete/download races | Partly addressed for recheck replacement by #59; ordinary publication, worker reconciliation and deletion accounting remain | High / medium–large | [Shared mutation boundary](2026-10-17-issue-52-theme-delete.md) |
| [#55](https://github.com/Themearr/themearr/issues/55) download protocol | Internal generations exist; public IDs and explicit unknown/pending states remain missing | Medium / medium | [Expose/adopt existing attempt identity](2026-10-17-issue-55-download-attempt-protocol.md) |
| [#54](https://github.com/Themearr/themearr/issues/54) RapidAPI write ordering | Still open: UI stamps cannot force server write order | Low / medium | [Authoritative reconciliation](2026-10-17-issue-54-rapidapi-reconciliation.md) |
| [#49](https://github.com/Themearr/themearr/issues/49) trim/normalize/repair | Feature design needed; independent storage documentation can ship first | Medium / large, three PR stages | [Documentation → processing → repair](2026-10-17-issue-49-audio-processing.md) |

Supporting evidence and acceptance criteria live in the matching [#51 design](../specs/2026-10-17-issue-51-wizard-radarr-design.md), [#53 design](../specs/2026-10-17-issue-53-queue-cursor-design.md), [#52 design](../specs/2026-10-17-issue-52-theme-delete-design.md), [#55 design](../specs/2026-10-17-issue-55-download-attempt-protocol-design.md), [#54 design](../specs/2026-10-17-issue-54-rapidapi-reconciliation-design.md), and [#49 design](../specs/2026-10-17-issue-49-audio-processing-design.md).

## Recommended order and dependencies

1. Implement #51 first: a contained onboarding fix and a small review surface.
2. Implement #53: closes silent item skipping without waiting for backend work.
3. Implement #52: establish one reviewed theme mutation boundary across both media paths.
4. Implement #55: reuse existing generations plus #52 coordination; adopt IDs in queue and modal. It is technically independent of #52, but serializing these avoids conflicting DownloadService edits.
5. Implement #54: the user selected reconciliation; no need to rewrite concurrent-click behavior. It can move earlier if configuration confusion is recurring.
6. Split #49: its README storage note can ship immediately alongside earlier work; optional new-download processing follows review of numeric/packaging defaults; repair waits for #52, #55 and measured processor tests.

Use one implementation PR per issue except #49's staged PRs. Run full gates locally before each PR, keep existing assertions, and demonstrate that reverting each production call-site fix makes its consumer regression fail. For most of these cohesive fixes, native execution with a final independent review is the proportionate default. The #52 and #49 critical sections additionally need the repository's security and concurrency specialists. The user subsequently approved the plans and selected implementation in this chat; the execution record below supersedes this planning-stage note.

## Decisions recorded during this review

- **User-approved #49 direction:** processing off by default; Plex-oriented 30-second preset; explicit preview before repairing existing files. Loudness values, bitrate choices, backup retention and dependency packaging remain recommendations for review.
- **User-approved #54 direction:** retain concurrent clicks and existing tests; reconcile server configuration after requests finish. The plan preserves immediate latest-click feedback and follows it with a visible, debounced authoritative check. No assertion is to be weakened to hide contrary server truth.
- **Recommended #52 semantics:** deletion invalidates every earlier attempt for the canonical folder, including a movie/show alias; a new post-delete download is allowed. Advance revision before filesystem work and retain invalidation on failure. No claim of crash-atomic filesystem/SQLite transactions.
- **Recommended repair recovery:** startup only reads media hashes and updates ledger interpretation. It never restores or re-encodes automatically; ambiguous outcomes require a new preview.
- An independent builder checked all six designs. Its two material questions—failed-delete invalidation and repair crash recovery—were resolved explicitly above and in their specs/plans. Implementation still requires the specified red/green proof and specialist review.

## Capability propagation

| Issue | Movies/shows | Dashboard | Queue/modal | History | Settings/setup | System |
|---|---|---|---|---|---|---|
| #51 | Radarr onboarding only | No change | No change | No change | Current-input verification | No change |
| #52 | Both controllers/workers; shared folders | Corrected existing counts | Corrected existing status | Only real successful commits | No new controls | Existing diagnostics only |
| #53 | Both through shared queue | No change | Cursor/remaining/Up next | No change | No change | No change |
| #54 | No change | No change | No change | No change | Reconciled key panel | Existing server-based health |
| #55 | Both route families | No count changes | Attempt binding in both clients | No migration | No change | Preserve pending-as-active behavior |
| #49 | Both download/repair paths | No fake download counts | Surface processor failures | Separate repair report; preserve provenance | Opt-in options/preset | Encoder health and repair preview/progress/cancel |

## Adjacent finding retained separately

The shows adapter still composes auto-download through manual search/download ([src/Themearr.Web/src/lib/media/adapter.ts @ e81b25f, lines 67–71](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/lib/media/adapter.ts#L67-L71)), although a server-side auto-download route now records automatic provenance ([src/Themearr.API/Controllers/ShowsController.cs @ e81b25f, lines 205–208](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Controllers/ShowsController.cs#L205-L208)). This can cause a matcher-chosen show theme to be recorded as manual, affecting later recheck decisions. Record as a follow-up for triage rather than expand #55 or silently rewrite the existing adapter assertions. No extra GitHub issue has been created by this review.

## Citation verification and delivery

Citation verification covers 65 immutable source references, including Dockerfile, checked against the fetched Git objects for their named revision, paths and line spans. Before conversion to immutable links, the repository citation script also verified all 27 local file/line references with zero unresolved. Printed source lines were checked for semantic agreement; line existence alone is not evidence that a claim is true. Internal Markdown links and whitespace checks passed.

At the planning review, the deliverables were this index, six design documents and six implementation plans; product work began only after the user approved them. Nothing was deployed. The user's original untracked agent/worktree files are preserved. Before turning this documentation into a PR, base that PR on current main so source citations refer to the checked-in baseline.


## Approved execution and draft delivery

The user approved all six plans and requested implementation in the listed order. That approval includes the recommended numeric audio defaults, retained-backup policy and optional packaging strategy. Execution remained in an isolated worktree; the original product checkout was preserved. No PR was merged, no release was published and no production files were repaired.

| Issue/stage | Draft pull request | Base/dependency |
|---|---|---|
| #51 setup verification | [#60](https://github.com/Themearr/themearr/pull/60) | main |
| #53 queue identity | [#61](https://github.com/Themearr/themearr/pull/61) | main |
| #52 coordinated deletion | [#62](https://github.com/Themearr/themearr/pull/62) | main |
| #55 public attempt identity | [#63](https://github.com/Themearr/themearr/pull/63) | #62 |
| #54 key reconciliation | [#64](https://github.com/Themearr/themearr/pull/64) | main |
| #49 storage guidance | [#65](https://github.com/Themearr/themearr/pull/65) | main |
| #49 opt-in processing | [#66](https://github.com/Themearr/themearr/pull/66) | #63 |
| #49 previewed repair | [#67](https://github.com/Themearr/themearr/pull/67) | #66 |
| Proxmox optional tools | [ProxmoxVE #5](https://github.com/Themearr/ProxmoxVE/pull/5) | companion repository |

Combined validation is recorded on branch `codex/open-issues-integration`: 775 API tests with zero failures/skips, 227 frontend tests, lint zero errors/three baseline warnings, production build and 12 real-API E2E tests. Native deployment checks passed 21 tests. The integration merge preserves the setup audio-setting omission guard with the verification imports and takes the attributed storage wording from #65. Each PR also contains its own validation record. Both image architectures passed for #66; final #67 CI status remains visible on that PR.

Security and concurrency reviewers checked the mutation, attempt and audio changes. Repair regression tests reproduced and fixed a staging-symlink overwrite, cancel/finalization races, StrictMode loading, cancellation during a slow poll and recovery of an uncertain new Apply. The encoder now runs in a private workspace; publication staging is exclusive. Startup recovery reads media and updates only ledger interpretation. Plex metadata reclamation remains unmeasured and is not claimed.

Execution rulings: the adapter start result keeps `Promise<unknown>` compatibility while runtime-narrowing the typed API result, preserving the existing hung-request regression; malformed unrelated folders do not defeat alias matching; repair requires configured roots and excludes multiple final themes rather than guessing. Ordinary download/delete operations retain their pre-existing configuration-save linearization limit; repair revalidates configuration inside its publication boundary. Dirty worker reconciliation waits until workers are enabled/configured.

Deferred minor: the settings header's existing Saved feedback can remain visible after another edit; it does not overwrite input or server values. The shows auto-download provenance discrepancy above remains a separate follow-up. Library mounts retain the documented trusted-ancestor filesystem boundary. Retained backups require explicit operator cleanup; no automatic deletion or live Proxmox installation was tested.
