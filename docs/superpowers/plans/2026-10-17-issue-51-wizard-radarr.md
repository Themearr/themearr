# #51 Radarr Setup Verification Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans, or superpowers:subagent-driven-development if the user selects delegated execution. Steps use checkbox syntax. This document authorizes no implementation by itself.

**Goal:** Prevent a stale Radarr test from authorizing untested setup values.

**Architecture:** Keep verification ownership and the tested credential pair in SetupWizard; use request/edit generations rather than changing the API.

**Tech Stack:** React, TypeScript, Vitest and Testing Library.

**Spec:** [Design](../specs/2026-10-17-issue-51-wizard-radarr-design.md).

## Global Constraints

Start from current upstream main; source citations target `e81b25f`. Never edit an existing test to make a change pass. Preserve Plex onboarding and stored path mappings. Never log credentials. No product code has been changed in this review.

## Review Focus

- Editing URL and editing key both invalidate a verification: Task 1.
- A stale catch/finally cannot clear a newer verdict or spinner: Task 1.
- Back/re-entry cannot revive an abandoned request: Task 1.
- Saving cannot advance with a different pair than the verified one: Task 1.
- Unrelated Plex setup/path mappings remain usable: existing suite plus Task 1.

## Task 1: Bind the gate to its verified input snapshot

**Files:** Modify `src/Themearr.Web/src/components/setup/SetupWizard.tsx`; create `src/Themearr.Web/src/components/setup/setup-wizard-radarr-races.test.tsx`. Preserve `setup-wizard-path-mappings.test.tsx` unchanged.

**Interfaces:** Retain `testRadarrConnection(): Promise<void>` and `confirmRadarr(): Promise<void>` ([src/Themearr.Web/src/components/setup/SetupWizard.tsx @ e81b25f, line 111](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/components/setup/SetupWizard.tsx#L111), [src/Themearr.Web/src/components/setup/SetupWizard.tsx @ e81b25f, line 125](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/components/setup/SetupWizard.tsx#L125)). Add local `invalidateRadarrVerification(): void`; verified input shape `{ url: string; apiKey: string; generation: number }`. No exported API.

- [ ] Add rendered-wizard tests with manually settled promises: `url_edit_cannot_reuse_old_success`, `key_edit_cannot_reuse_old_success`, `old_failure_cannot_replace_new_result`, `old_finally_cannot_clear_new_spinner`, `back_invalidates_pending_test`, `verified_pair_is_saved_once`, and `save_failure_releases_controls`. Assert `radarrApi.save` is never called for untested values, stale errors disappear on edit, and valid B saves exactly B then displays path configuration. During save, edits/Back are disabled and a repeated Continue does not issue another save.
- [ ] From `src/Themearr.Web`, run `npx vitest run src/components/setup/setup-wizard-radarr-races.test.tsx`; require behavioral failures at the untested-save/stale-feedback assertions, not missing imports or fixture failures.
- [ ] Implement generation claiming/invalidation, normalized pair equality and step ownership around every test settle path. Invalidation releases the obsolete testing flag. Capture the verified pair for saving and add a synchronous single-flight guard for save admission. Wire both input changes and Radarr Back through invalidation. Match the settings stamp approach ([src/Themearr.Web/src/app/settings/page.tsx @ e81b25f, lines 622–641](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings/page.tsx#L622-L641)).
- [ ] Run the new file and `npm test`; require all tests passing and no skips. Temporarily restore the original unconditional verdict assignment and demonstrate the new consumer test fails, then restore the fix. Run `npm run lint` and `npm run build`; require zero errors and no additional warnings beyond the three existing warnings.
- [ ] Run the concurrency reviewer, inspect its findings, and commit only the new tests and wizard change as `fix: bind Radarr setup verification to current values`.

## Completion and propagation

No backend, migration or additional product surface is required. Run the repository E2E package against the freshly built frontend before opening a PR. Link `Closes #51` only in the eventual implementation PR. Keep this plan/spec in the documentation review separately if desired.
