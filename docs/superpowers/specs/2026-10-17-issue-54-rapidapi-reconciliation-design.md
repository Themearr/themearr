# #54: Reconcile RapidAPI configuration after overlapping mutations

Status: user selected reconciliation; detailed proposal ready for review. Issue: [#54](https://github.com/Themearr/themearr/issues/54). Source baseline: upstream `main` at `e81b25f4c487dc5406b93cab9bf3736d2da4aad9`.

## Evidence and approved direction

Save and Remove apply client mutation stamps independently ([src/Themearr.Web/src/app/settings/page.tsx @ e81b25f, lines 438–487](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings/page.tsx#L438-L487)), while the server performs independent writes ([src/Themearr.API/Controllers/SettingsController.cs @ e81b25f, lines 120–136](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Controllers/SettingsController.cs#L120-L136)). Stamps cannot force server order. The existing regression asserts that the panel is immediately unconfigured after an earlier Replace resolves last ([src/Themearr.Web/src/app/settings-stale-writes.test.tsx @ e81b25f, lines 157–179](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings-stale-writes.test.tsx#L157-L179)). Its status mock reports configured ([src/Themearr.Web/src/app/settings-stale-writes.test.tsx @ e81b25f, line 158](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/settings-stale-writes.test.tsx#L158)), so an immediate awaited reconciliation would conflict with it.

The user chose to retain concurrent clicks and existing tests, reconciling with the server after requests finish. The proposed distinction is immediate intent feedback followed by explicit authoritative convergence. Do not weaken the existing test or hide a contradiction with special-case test logic.

## Reconciliation state machine

Retain latest-click ownership of immediate mutation feedback and edit-stamp ownership of input clearing. Track all pending mutation requests in a ref counter, not two booleans. Every issued mutation advances an activity revision and invalidates pending status reads. Initial mount status, explicit Retry and reconciliation all use the same latest-read/activity guard.

When the last request settles, schedule one trailing 250 ms reconciliation. This short, user-visible 'Checking saved configuration…' phase coalesces a burst of conflicting actions and preserves immediate intent feedback. A new mutation cancels the timer, invalidates an in-flight read and begins another cycle. The timer is a product debounce, not a correctness fence: correctness comes from zero pending mutations and unchanged activity revision when a read returns.

Use the shared boundedRequest helper for an 8 s read budget ([src/Themearr.Web/src/lib/boundedRequest.ts @ e81b25f, line 2](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/lib/boundedRequest.ts#L2)). Apply status only if the component is mounted, pending count is zero and both read and activity stamps still match. A failure shows that configuration is unconfirmed and offers Retry; it must not claim the optimistic value is authoritative. Keep user typing intact and keep credential material out of status/error logs. Cleanup cancels timer and invalidates reads.

If an earlier Replace actually commits after Remove, the eventual panel correctly becomes configured, accompanied by an explanation that server configuration differs from the last action. The existing immediate assertion remains valid during the checking phase; new fake-timer tests must assert eventual server truth. If the untouched suite exposes a genuine permanent-last-click contract, stop and report the incompatibility instead of editing its assertions.

Bound mutation waits at 30 s for usable controls, but do not equate a transport timeout with server cancellation. A timed-out write leaves an unknown outstanding outcome: show 'Configuration may still change; retry status' and perform bounded follow-up reads while the panel remains mounted, at 1 s intervals for at most 30 s. After the window retain the unconfirmed notice and explicit Retry. Do not promise cross-tab ordering or finality for a server write that can complete after every client deadline.

## Acceptance and propagation

Use controlled promises and fake timers to force both POST/DELETE server orders, failure of either mutation, edits during requests, stale mount/retry reads, a new mutation during reconciliation, hung writes and unmount. Existing tests stay untouched and must pass. Settings is the only changed surface; provider and System health already read server configuration, so no new endpoint, database column or dashboard/history/queue capability is needed. Require concurrency review.
