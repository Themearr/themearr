# #52 Theme Deletion Coordination Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans, or superpowers:subagent-driven-development if the user selects delegated execution. Execute tasks sequentially; the mutation boundary is shared.

**Goal:** Make deletion invalidate older downloads and keep disk, stored status and recheck provenance consistent.

**Architecture:** Extend the existing DownloadService admission lock and job generation with a folder revision. Route delete and worker reconciliation through that boundary, reusing the SQLite writer reservation introduced by #59.

**Tech Stack:** .NET, SQLite, xUnit; existing filesystem and Plex helpers.

**Spec:** [Design](../specs/2026-10-17-issue-52-theme-delete-design.md).

## Global Constraints

Source baseline is upstream `e81b25f`; start implementation from current main. Never edit an existing test to make a change pass. Preserve the folder landing gate ([src/Themearr.API/Services/DownloadService.cs @ e81b25f, lines 18–26](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Services/DownloadService.cs#L18-L26)), synchronous delete signatures, containment checks and best-effort Plex refresh. Never hold SQLite or the admission lock over provider/network I/O. All code uses the same order: admission lock → SQLite write reservation. No path may acquire that folder gate while holding the admission lock.

## Review Focus

- Manual and ordinary automatic downloads, not just recheck replacement: Task 1.
- Movie/show jobs sharing a canonical folder or trailing-separator alias: Task 1.
- Delete while no final file exists but an earlier job is staged/waiting: Task 1.
- Worker disk-check/status-write race and raw stored-status honesty: Task 2.
- Disk/DB failure after partial filesystem work must report failure: Task 1.

## Task 1: Serialize deletion with final publication and invalidate older attempts

**Files:** Modify `src/Themearr.API/Services/DownloadService.cs`, `src/Themearr.API/Data/Database.cs`, `src/Themearr.API/Services/ThemeFiles.cs`, `src/Themearr.API/Controllers/MoviesController.cs`, `src/Themearr.API/Controllers/ShowsController.cs`; create `tests/Themearr.API.Tests/DeleteDownloadRaceTests.cs` and `tests/Themearr.API.Tests/ThemeMutationFailureTests.cs`.

**Interfaces:** Add `bool DownloadService.DeleteTheme(string id, string mediaType)`; retain controller `IActionResult DeleteTheme(string id)`. Add `Database.CommitDownloadedTheme(string id, string mediaType, string title, int? year, string? themeTitle, string url, string pickedBy, int? matchScore, Action publishFiles): void` for the ordinary branch. Extend the internal admitted attempt with canonical folder/revision; retain public bool Start compatibility. Extend the delete DB helper to reset both movie/show rows whose resolved folder is the target, including no-file status reconciliation, while preserving ignored/Plex flags.

- [ ] Add held-provider/held-publication regressions, parameterized for movie/show: `delete_before_publish_invalidates_old_attempt`, `delete_after_publish_leaves_pending_without_theme`, `delete_during_container_change_never_reports_a_surviving_theme`, `delete_without_final_file_invalidates_staged_work`, `shared_folder_delete_invalidates_both_media`, and `fresh_post_delete_attempt_can_succeed`. Assert raw SQL status, final files, history count/provenance, recheck clearing and job failure. Use TaskCompletionSource barriers or a narrow injected internal commit observer, never delay-based race reproduction. New helper tests must be accompanied by real controller/service consumer tests.
- [ ] Add failures for denied file deletion, a vanished enumerated file, failed status persistence after deletion, and an expired attempt's cleanup while a new one waits. Assert no successful delete on a real deletion error; no success/history insertion from an invalidated attempt; newer staging survives. Preserve all assertions in `DownloadServiceFolderGateTests`, `AuditReplacementOwnershipTests` and `PlexRefreshAfterDeleteTests`.
- [ ] Run `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter 'FullyQualifiedName~DeleteDownloadRaceTests|FullyQualifiedName~ThemeMutationFailureTests'`; require failures demonstrating the issue on the baseline.
- [ ] Capture the current folder revision under the existing admission lock when reserving a job, before Task.Run can be delayed behind a delete. Under the same lock, revalidate generation/revision before publication; place ordinary publication/status/history/recheck changes in the short SQLite writer transaction. Route both controllers to coordinated deletion after their current validation ([src/Themearr.API/Controllers/MoviesController.cs @ e81b25f, lines 75–80](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Controllers/MoviesController.cs#L75-L80), [src/Themearr.API/Controllers/ShowsController.cs @ e81b25f, lines 220–225](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Controllers/ShowsController.cs#L220-L225)). Re-read roots/folder in the coordinated operation. Increment the folder revision before stale work can publish. Only cleanup staging while holding its existing folder gate.
- [ ] Keep `ThemeFiles.DeleteThemes`' bool signature. Within serialized in-process mutations, check that an enumerated final file still exists before deleting/counting it; preserve `.part`/`.ytdl`. Acknowledge external writers are outside the lock guarantee. Advance revision before filesystem work and retain that invalidation on partial deletion/DB failure. Surface filesystem errors. On DB failure after filesystem deletion, leave a visible failure and add its canonical folder to a coalesced dirty-folder set; do not imply a filesystem rollback occurred.
- [ ] Run new tests and full API suite; require `Failed: 0, Skipped: 0`. Revert the coordinated controller call and separately the publication revision check; each relevant consumer regression must turn red. Restore and commit as `fix: coordinate theme deletion with download publication`.

## Task 2: Close worker reconciliation's check/write window

**Files:** Modify the same DownloadService/Database sections plus `src/Themearr.API/Services/AutoDownloadService.cs` and `src/Themearr.API/Services/ShowAutoDownloadService.cs`; create `tests/Themearr.API.Tests/WorkerDeleteReconciliationRaceTests.cs`.

**Interfaces:** Add `bool DownloadService.ReconcileTheme(string id, string mediaType)`, returning true only when a usable final theme was observed and stored downloaded within the shared mutation boundary, plus `void ReconcileDirtyFolders()` to retry coalesced mutation failures before the prefilter. Failed retries retain the folder and emit sanitized diagnostics. Workers retain their stored-pending prefilter and skip filesystem scanning for an idle library with no dirty folders.

- [ ] Add controlled check/delete/write tests through each worker. After deletion wins, raw stored status must remain pending and the next candidate selection must include the item. After reconciliation wins first, subsequent delete must still leave pending. Include the shared-folder case, partial-delete and DB-failure retry cases proving old attempts stay invalidated, and a prefilter-stat-count assertion preserving the existing idle-library cost. Drain dirty folders before either worker's prefilter; remove a dirty entry only after successful reconciliation.
- [ ] Run `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter FullyQualifiedName~WorkerDeleteReconciliationRaceTests` and record the baseline failure.
- [ ] Move the worker's disk-presence check and conditional status update into ReconcileTheme, using the established admission → SQLite order. Replace both uncoordinated pairs ([src/Themearr.API/Services/AutoDownloadService.cs @ e81b25f, lines 271–273](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Services/AutoDownloadService.cs#L271-L273), [src/Themearr.API/Services/ShowAutoDownloadService.cs @ e81b25f, lines 241–243](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Services/ShowAutoDownloadService.cs#L241-L243)). Do not introduce a full-library disk scan.
- [ ] Run targeted/full API tests, prove each worker call-site reversion fails its regression, and commit as `fix: serialize pending-theme reconciliation with deletion`.

## Whole-change review and delivery

Run security-invariant-reviewer and concurrency-reviewer. Inspect lock ordering across controller, worker, admission, SQLite and recheck paths. Run frontend tests/build/lint and E2E to retain existing delete feedback. Dashboard/queue need no new controls; they consume corrected status. History records only successful publication. Settings/System require no new fields. Suggested PR links `Closes #52`. This establishes the mutation boundary #49 repair must reuse; #55 should reuse its attempt model without reimplementing it.
