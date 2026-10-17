# #51: Bind setup verification to the tested Radarr values

Status: reviewed proposal; implementation not started. Issue: [#51](https://github.com/Themearr/themearr/issues/51).

## Evidence and intent

All source citations in this review target upstream `main` at `e81b25f4c487dc5406b93cab9bf3736d2da4aad9`, exported for inspection. The user's checkout was `9868abc`; refresh the implementation branch from current main before executing.

The wizard now lives at `src/Themearr.Web/src/components/setup/SetupWizard.tsx`, rather than the path quoted in the issue. A test writes its result, error and busy flag without an ownership check ([src/Themearr.Web/src/components/setup/SetupWizard.tsx @ e81b25f, lines 111–122](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/components/setup/SetupWizard.tsx#L111-L122)). Continue trusts that result but saves the live field values ([src/Themearr.Web/src/components/setup/SetupWizard.tsx @ e81b25f, lines 125–133](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/components/setup/SetupWizard.tsx#L125-L133)). Edits clear only the verdict ([src/Themearr.Web/src/components/setup/SetupWizard.tsx @ e81b25f, line 337](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/components/setup/SetupWizard.tsx#L337), [src/Themearr.Web/src/components/setup/SetupWizard.tsx @ e81b25f, line 344](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/components/setup/SetupWizard.tsx#L344)). This confirms the reported stale-success path by inspection; a deterministic new test must reproduce it before implementation.

## Proposed behavior

- Every URL edit, key edit, new test and departure from the Radarr step invalidates the previous verification generation. Edits clear stale verdicts and step errors immediately.
- A successful verification records its normalized URL/key pair. Continue requires both a successful current generation and equality with those exact normalized values.
- A response can change the verdict, error or spinner only while it owns the current generation and step. Editing a pending test releases that obsolete busy state so the new values can be tested.
- Saving captures the verified pair and prevents edits/back navigation until that save settles. Successful saving advances once; a failed save remains on the Radarr step with useful feedback. This prevents a second stale-state window during the save itself.
- Plex onboarding and preserved path mappings retain existing behavior. Never log credentials.

## Acceptance and scope

Hold test A; edit URL or key; resolve A successfully; Continue must neither call save nor advance. Repeat with A rejecting, with two tests resolving in reverse order, and with Back then re-entry. A fresh successful B must allow exactly one save of B. Existing wizard tests remain unchanged.

This is a frontend setup correction. No database, API route, dashboard, history, queue or System change is needed. Radarr is a movie source; there is no show-side Radarr wizard to mirror. Require concurrency review of implementation.
