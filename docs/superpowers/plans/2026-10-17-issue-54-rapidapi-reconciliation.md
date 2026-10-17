# #54 RapidAPI Configuration Reconciliation Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans, or superpowers:subagent-driven-development if selected by the user. Preserve the selected reconciliation behavior and existing tests.

**Goal:** Converge the RapidAPI panel to server truth after conflicting writes settle.

**Architecture:** Preserve immediate latest-click feedback; track outstanding mutations and schedule a guarded status read after quiescence. Share guards across mount, Retry and reconciliation reads.

**Tech Stack:** React, TypeScript, existing boundedRequest helper, Vitest fake timers.

**Spec:** [Design](../specs/2026-10-17-issue-54-rapidapi-reconciliation-design.md).

## Global Constraints

User selected reconciliation, retaining concurrent clicks and existing tests. Never edit an existing test to make a change pass. The trailing reconciliation delay is 250 ms; status budget is 8 s; mutation wait budget is 30 s. Source baseline is upstream `e81b25f`. Never return or log credential material.

## Review Focus

- Earlier Replace commits after later Remove: Task 1.
- Initial status/Retry result lands after a newer mutation: Task 1.
- New typing survives old mutation settlement: existing tests and Task 1.
- Timeout does not imply cancellation of a server write: Task 1.
- Unmount or new mutation invalidates a scheduled/in-flight status read: Task 1.

## Task 1: Add quiescent, stamped authoritative reconciliation

**Files:** Modify `src/Themearr.Web/src/app/settings/page.tsx` and optionally the existing `rapidApiApi` wrappers in `src/Themearr.Web/src/lib/api.ts` to accept AbortSignal; create `src/Themearr.Web/src/app/settings-rapidapi-reconciliation.test.tsx`. Do not modify `settings-stale-writes.test.tsx` or `inflight-guards.test.tsx`.

**Interfaces:** Keep `saveRapidApiKey(): Promise<void>`, `removeRapidApiKey(): Promise<void>`, `checkRapidApiStatus(): Promise<void>`. Add local `beginRapidMutation(): number`, `finishRapidMutation(revision: number): void`, `scheduleRapidReconciliation(): void`, and `readRapidStatus(activityRevision: number): Promise<void>`. Counter ownership is synchronous via refs; UI state distinguishes checking/unconfirmed/confirmed. Reuse `boundedRequest<T>(request, timeoutMs)` ([src/Themearr.Web/src/lib/boundedRequest.ts @ e81b25f, line 2](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/lib/boundedRequest.ts#L2)).

- [ ] Add new fake-timer tests with separately controlled network application order and promise settlement. For Replace then Remove: before the debounce retain immediate unconfigured feedback; at 249 ms issue no reconciliation; at 250 ms fetch once after all writes settled; if server reports configured, show configured with the ordering explanation. Mirror the reverse server order. Assert transient intent and eventual truth separately.
- [ ] Add tests for either mutation failing, mount/Retry status returning stale data, a new mutation while a status read is pending, editing during save, repeated reads, unmount, status timeout/retry, mutation timeout followed by a late server commit, and exhausted follow-up window. Late credentials are never reinserted into inputs. During uncertain outcomes, show the explicit unconfirmed message instead of a false confirmed result.
- [ ] Run `npx vitest run src/app/settings-rapidapi-reconciliation.test.tsx` from the web package; require failure because no post-write server reconciliation occurs. Keep the existing suite unchanged.
- [ ] Add pending-count/activity/read stamps and the trailing timer. Mutations claim revision synchronously; all settles decrement exactly once, even after a local timeout. Mark transport-timeout outcomes uncertain rather than calling them cancelled. Follow uncertain writes with at most 30 one-second bounded status probes while mounted; retain the explicit uncertainty notice after the window. A late settlement invalidates/schedules another read. Cancel timer/read placement on cleanup; do not block conflicting buttons as a substitute for reconciliation.
- [ ] Use the same guard for mount status ([src/Themearr.Web/src/app/settings/page.tsx @ e81b25f, lines 175–179](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings/page.tsx#L175-L179)) and Retry ([src/Themearr.Web/src/app/settings/page.tsx @ e81b25f, lines 542–548](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings/page.tsx#L542-L548)). Status may apply only with zero pending writes and unchanged stamps. Preserve edit-stamp input clearing and mutation-scoped errors ([src/Themearr.Web/src/app/settings/page.tsx @ e81b25f, lines 438–487](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings/page.tsx#L438-L487)). Display checking/unconfirmed feedback and Retry without implying a read can prevent a later cross-tab mutation.
- [ ] Run the new file together with `src/app/settings-stale-writes.test.tsx` and `src/app/inflight-guards.test.tsx`, then full `npm test`, lint and build. If an existing assertion truly requires permanent last-click truth despite contradictory server state, report it; do not rewrite it. Prove removal of the production reconciliation scheduling turns the eventual-convergence regression red, without relying on real-time sleeps.
- [ ] Run concurrency review and E2E, then commit as `fix: reconcile RapidAPI state after overlapping changes`; eventual implementation PR uses `Closes #54`.

## Propagation and limits

No new endpoints/schema or additional product surfaces. The guarantee is convergence after observable in-page requests settle, with visible uncertainty for unknown outcomes. No client-only mechanism can impose server ordering across tabs or prove a timed-out request will never commit.
