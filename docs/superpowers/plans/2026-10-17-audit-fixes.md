# Audit Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Follow test-first steps and preserve existing installations.

**Goal:** Repair all nineteen audited defects without resetting installations or breaking existing API clients.

**Architecture:** Isolate deployment, backend coordination, frontend, and credential/data work into independently owned slices. Extend existing endpoints and database helpers; preserve the movie/show parallel paths. Shared Database edits are limited to disjoint sections with explicit coordination.

**Tech Stack:** Existing .NET, React/Vite, xUnit/Vitest/Playwright and Bash tooling.

**Spec:** `docs/superpowers/specs/2026-10-17-audit-fixes-design.md`

## Global Constraints

Preserve data, keys, selected libraries, path mappings, API routes and media files. No production deployment. No matching-rule changes. Do not edit existing test assertions. Prove regressions red before green. No skipped tests. Lint remains at most three existing warnings.

## Review Focus

- An existing native install with service-owned binaries and an old updater repairs ownership while retaining auth.env and the live database.
- A redacted/cached frontend saves unchanged Plex connections without losing credentials or authorizing a new destination.
- A scan or poll finishing after a user's newer decision cannot undo that decision.
- A show-only configuration and a deselected server retain their distinct selection semantics.
- A rule-version scan waits for setup, preserves manual/kept themes, and retries failures without repeatedly spending quota.

## Task 1: Deployment and updates (findings 1, 4, 15–17, 19)

Files: install.sh, deploy.sh, .github/workflows/release.yml, Services/UpdateService.cs; new shell/process regression tests.

- [x] Add controlled old-install/update fixtures; observe ownership, live-data, curl failure, stderr and absent-checksum failures.
- [x] Keep trusted application/updater files root-owned; upgrade old installations safely; stage application extraction without restoring stale data.
- [x] Drain both process streams, enforce cancellation/timeout, check script downloads before execution, and serialize full releases.
- [x] Run shell and API process regressions; retain existing installer variables and service identity.

## Task 2: Credentials, library selection and health (findings 2, 3, 9, 12–14)

Files: Data/Database.cs credential sections only; Controllers/SetupController.cs; Services/PlexService.cs; Services/Sources/RadarrLibrarySource.cs; Services/Health/LibraryPathsCheck.cs; Services/ShowSyncService.cs; new API regression files.

- [x] Add redacted discovery/legacy selection, alternate URL, per-server deselection, malformed-entry pruning, show-health and log tests; run red.
- [x] Store discovered credentials server-side, reattach only trusted destinations, and support old token-bearing submissions.
- [x] Respect empty movie selections; mark incomplete Radarr data; propagate show path failures; sanitize callback logs.
- [x] Run all new and existing API tests.

## Task 3: Background state and rechecks (findings 5, 6, 8, 11)

Files: Services/DownloadService.cs, ThemeRecheckService.cs, AutoSyncService.cs, ShowAutoSyncService.cs, TaskRegistry.cs, new AutoRecheckService.cs, Program.cs; Data/Database.cs recheck/provenance sections only; new API regression files.

- [x] Add real consumer regression tests for simultaneous starts, stale scans, pending show runs and rule-version startup; run red.
- [x] Atomically reserve jobs and protect attempt ownership. Commit scan verdicts only against evaluated state/provenance.
- [x] Publish show running state and expose pending task state additively for old callers.
- [x] Register a cancellation-aware rule-version scan runner; persist only successful scans; wait until setup.
- [x] Run targeted and full API checks; coordinate Database edits with Task 2.

## Task 4: Frontend capability and polling (findings 7, 10, 18)

Files: components/media/SearchModal.tsx; new queue recheck component; lib/api.ts/types.ts; queue/page.tsx, dashboard/page.tsx, shows/page.tsx, system/page.tsx; test environment config; new frontend tests.

- [x] Add behavioral regressions for pending start/old completion, recheck controls/actions/counts and queued show task state; observe red.
- [x] Gate modal polling on start success, bound requests, and ignore stale attempts.
- [x] Integrate recheck APIs, needs-attention UI and both dashboard counts; preserve triage behavior and old-server optional fields.
- [x] Make jsdom storage explicit for the normal npm test command; retain all existing assertions.
- [x] Run frontend tests, lint, typecheck/build and E2E.

## Task 5: Integration and review

- [x] Check all nineteen findings against final changes and compatibility fixtures.
- [x] Run citation checking; update baseline references to final implementation where necessary.
- [x] Run security and concurrency specialist reviews; address actionable findings with regression tests.
- [x] Run full API/frontend/E2E gate, shell regressions, Semgrep and dependency checks. Record limitations accurately.

## Review-driven compatibility additions

- Existing private native configuration keeps service-group read/traverse access when ownership is sealed, including failed download paths. Native install/data symlinks fail safely before privileged writes.
- Wizard re-login prefers newly discovered trusted credentials; ordinary settings retain the selected credential.
- Successful recheck replacements carry the evaluated history identity through admission and a short SQLite write transaction. Keep, Ignore, new history and physical deletion cannot interleave between final validation and file publication.
- Movie/show download endpoints return conflict when another attempt already owns the item; System displays accepted tasks as queued.

## Capability propagation decisions

Dashboard and Queue expose rechecking; System reports pending tasks and show path failures. Settings retains configuration/provenance semantics and existing auto-download controls. History already records every successful replacement, so no history format change is needed. Movies and Shows retain their ordinary pages and parallel action endpoints.

## Final validation

All nineteen findings are addressed. Final verification: 672 API tests, 143 frontend tests, 11 E2E tests and 21 deployment/release tests passed, with no skips. Frontend build/typecheck passed; lint retained its baseline of zero errors and three warnings. Citation checking resolved all six source references. Security, concurrency and independent integration reviews have no remaining actionable findings.

The release version fixture executes the shipped workflow block against real temporary Git histories, including superseded pending pushes and manual dispatch. E2E uses a unique disposable database per run so worker configuration cannot delete the live test database.

Semgrep reported zero findings across 209 rules and 140 targets, with 40 partial-parsing diagnostics limiting coverage. Existing dependency checks reported no known vulnerabilities. Native upgrade tests simulate ownership/system commands; a live systemd upgrade under separate root/service identities was not performed. No production installation was changed or release published.
