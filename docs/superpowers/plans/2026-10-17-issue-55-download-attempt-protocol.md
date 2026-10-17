# #55 Download Attempt Protocol Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans, or superpowers:subagent-driven-development if selected by the user. Keep backend and client contracts aligned; existing callers remain compatible.

**Goal:** Let clients track an accepted download attempt without consuming another attempt's terminal result.

**Architecture:** Expose the generation already reserved atomically by DownloadService, add explicit status states and optional attempt-qualified reads, then adopt the additive contract in both polling UIs.

**Tech Stack:** .NET, xUnit, React/TypeScript, Vitest, existing API/adapter layer.

**Spec:** [Design](../specs/2026-10-17-issue-55-download-attempt-protocol-design.md).

## Global Constraints

Source baseline is upstream `e81b25f`; do not reimplement atomic reservation from #59. Never edit an existing test to make a change pass. Preserve all legacy response fields, bool Start callers, HTTP 409 admission behavior, media-qualified keys, timeout and stale-response guards. No database migration.

## Review Focus

- Retry sees a previous attempt's terminal status: Tasks 1 and 3.
- Simultaneous starts or watchdog-replaced jobs cross-contaminate state/logs: Task 1.
- Unknown key, pending admission, superseded attempt and restart differ: Tasks 1–3.
- Both media types and all six start paths carry attempt identity: Task 2.
- Cached old client/new server and new client/old server still work: Tasks 2–3.

## Task 1: Return identity from atomic admission and publish typed status snapshots

**Files:** Modify `src/Themearr.API/Services/DownloadService.cs`; create `src/Themearr.API/Services/DownloadProtocol.cs` and `tests/Themearr.API.Tests/DownloadAttemptProtocolTests.cs`.

**Interfaces:** Add `DownloadStartResult(bool Started, Guid? AttemptId)` and `StartAttempt(string id, string youtubeUrl, string mediaType, string pickedBy, int? matchScore = null, RecheckDownloadExpectation? recheck = null): DownloadStartResult`. Keep current bool Start signature as a wrapper. Keep `object GetStatus(string id, string mediaType = "movie")` as the legacy-call wrapper and add a distinct named `GetAttemptStatus(string id, string mediaType, Guid? requestedAttempt): DownloadStatusDto` to avoid overload/reflection ambiguity. DTO JSON retains lower-camel legacy fields and adds `attemptId`, `known`, `state`, optional `currentAttemptId`.

- [ ] Add consumer tests for two admitted retries returning distinct nonempty IDs, simultaneous starts returning one successful reservation, pending before the folder gate opens, running after it opens, success/error transitions, unknown untouched key and superseded requested ID. Assert old attempt logs/results never appear in requested-attempt status; same raw ID for movie/show has separate identities. Reuse held providers, not timer sleeps. Include the existing watchdog-generation protections rather than replacing their assertions.
- [ ] Run `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter FullyQualifiedName~DownloadAttemptProtocolTests`; first use assertions against the baseline status shape to demonstrate missing protocol behavior. A compilation failure from a new DTO alone is not the regression proof.
- [ ] Extract reservation into StartAttempt; return the generation allocated inside the existing lock ([src/Themearr.API/Services/DownloadService.cs @ e81b25f, lines 87–99](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Services/DownloadService.cs#L87-L99)). Associate state/logs with that attempt snapshot and compare generation when transitioning pending→running/terminal. Map pending and running to legacy inProgress true. Unknown has both legacy booleans false. Superseded never claims successful completion or exposes a newer job's logs. Keep IsAnyInProgress true for admitted pending work.
- [ ] Run targeted/full API tests; demonstrate changing the returned generation or removing the requested-generation comparison breaks real service tests. Restore, then commit as `feat: expose attempt-aware download status`.

## Task 2: Propagate IDs through movie/show routes and frontend adapters

**Files:** Modify `src/Themearr.API/Controllers/MoviesController.cs`, `ShowsController.cs`, `src/Themearr.Web/src/lib/types.ts`, `api.ts`, `media/adapter.ts`; create `tests/Themearr.API.Tests/DownloadAttemptEndpointTests.cs` and `src/Themearr.Web/src/lib/media/download-attempt-adapter.test.ts`.

**Interfaces:** Successful start adds `attemptId: string`; status accepts `?attemptId=<guid>`. Malformed GUID returns HTTP 400 through explicit validation, not an unhandled parse. Web `DownloadStartResponse` has optional attemptId for old servers; `DownloadStatus` has optional new protocol fields for the same reason. Preserve the existing `(id, init?)` adapter signature ([src/Themearr.Web/src/lib/media/adapter.ts @ e81b25f, lines 15–16](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/lib/media/adapter.ts#L15-L16)) and add `downloadStatus(id: string, init?: RequestInit, attemptId?: string): Promise<DownloadStatus>`.

- [ ] Add endpoint assertions for movie/show manual download, pasted URL and server auto-download routes: success ID equals that reserved by the service; rejected starts remain 409; legacy field names/values remain. Add status query tests for match/mismatch/unknown/malformed GUID and auth-boundary rejection without credentials. Web tests assert URL query encoding, AbortSignal forwarding and composed show auto-download passing through its download result.
- [ ] Run the new endpoint/adapter tests and observe the missing fields/query handling on baseline. Implement all six route responses by calling StartAttempt once, never Start followed by a status lookup. Retain bool Start for workers. Add URL query handling without changing route paths or auth exemptions.
- [ ] Run new tests, all existing API and web tests unchanged, lint and build. Revert one movie and one show response wiring to prove the new endpoint assertions fail. Commit as `feat: carry download attempt ids through media APIs`.

## Task 3: Bind both queue and modal polling to the accepted attempt

**Files:** Modify `src/Themearr.Web/src/app/queue/page.tsx` and `src/Themearr.Web/src/components/media/SearchModal.tsx`; create `src/Themearr.Web/src/app/queue-server-attempt.test.tsx` and `src/Themearr.Web/src/components/media/search-modal-attempt.test.tsx`.

**Interfaces:** Add optional serverAttemptId to the existing local download binding; retain its local client-attempt stamp and ready/startSettled fields. Local identity protects UI ownership; server identity protects which job the response describes.

- [ ] Hold retry start, release an old terminal status, then return the new start ID; assert neither page settles/advances/closes from the old result. Cover movie/show collision, direct/provider paths, unknown and superseded responses, logs belonging to another attempt, unmount and lost-contact timeout. For an old server with omitted fields, assert existing legacy guards still work. Run the new files red against current consumers.
- [ ] Capture the ID from every start result, include it in polling requests, and accept terminal/log updates only for that ID. Unknown or superseded after acceptance releases controls with a lost-tracking explanation; it does not auto-retry or mark downloaded. Keep existing wait budgets, boundedRequest use, single-flight polling and on-screen identity guards. Retain the #53 cursor model if already landed.
- [ ] Run new tests, full web/API suites, lint/build and E2E. Revert queue and modal ID checks independently to demonstrate the new consumer tests have teeth. Run concurrency and security reviewers, then commit as `fix: poll only the accepted download attempt`.

## Delivery and propagation

One PR can carry these sequential commits; expose the server fields before relying on them client-side. System active-work detection includes pending; no new dashboard counts, history records or settings controls are needed. Keep the separate show auto-download provenance discrepancy recorded in the review index. Prefer #52 before this work to avoid competing edits to DownloadService, though there is no protocol dependency. Eventual PR uses `Closes #55`.
