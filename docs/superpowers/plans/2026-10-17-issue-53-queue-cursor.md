# #53 Queue Cursor Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans, or superpowers:subagent-driven-development if selected by the user. Keep model and page integration in one test/review cycle.

**Goal:** Keep every untriaged queue item reachable while a same-media refetch changes the list.

**Architecture:** Replace persisted numeric position with a media-qualified selected key and per-session visited keys. Keep async response ownership in QueuePage.

**Tech Stack:** React, TypeScript, Vitest, Testing Library.

**Spec:** [Design](../specs/2026-10-17-issue-53-queue-cursor-design.md).

## Global Constraints

Start from upstream main; citations target `e81b25f`. Never edit an existing test to make a change pass. Keep all existing queue assertions and polling/timeout protections. Keep the needs-attention component integrated. Do not change useResource's retained-data contract.

## Review Focus

- A finishes while a refetch is pending and `[B,C]` lands: Task 1.
- Skip with a shrinking or reordered snapshot: Task 1.
- A removed selected key or new item after exhaustion: Task 1.
- Movie/show raw ID collision and switch-away/back: Task 1.
- Intentional Up next jump retains forward-navigation behavior: Task 1.

## Task 1: Integrate a key-based cursor into the rendered queue

**Files:** Create `src/Themearr.Web/src/lib/media/queueCursor.ts` and `queueCursor.test.ts`; modify `src/Themearr.Web/src/app/queue/page.tsx` and `src/Themearr.Web/src/app/movies/page.tsx`; create `src/Themearr.Web/src/app/queue-refetch-cursor.test.tsx`.

**Interfaces:** Define `MediaKind = 'movies' | 'shows'`, `QueueCursor = { media: MediaKind; currentKey: string | null; visitedKeys: ReadonlySet<string> }`, `queueKey(media: MediaKind, id: string): string`, and pure `rebaseCursor(cursor: QueueCursor, previous: readonly string[], next: readonly string[]): QueueCursor`, `advanceCursor(cursor: QueueCursor, orderedKeys: readonly string[], actedKey: string): QueueCursor`, `selectCursor(cursor: QueueCursor, orderedKeys: readonly string[], key: string): QueueCursor`. Helpers return new state/sets; no fetches or effects. A missing acted-on key is a no-op, never permission to advance a different key.

- [ ] Add deferred consumer tests: finish A while the movies→shows→movies refetch is held, then resolve `[B,C]` and assert B is shown/searchable; Skip A before a shrinking refetch cannot display caught-up while B remains. Add model and page cases for preserved current key after reorder, removed current key's surviving successor, intentional Up next jump, new pending items after exhaustion, ignore failure, duplicate finish tick and colliding IDs. Assert visible title, next search ID, remaining count and actual API arguments.
- [ ] Run `npx vitest run src/app/queue-refetch-cursor.test.tsx` from the web package and require the existing QueuePage to fail at the skipped-B/false-empty assertions before adding the model. Then add pure-model tests and observe their behavioral failures once the API is defined.
- [ ] Implement the pure transitions per the spec, including successor selection, visited filtering and explicit jump semantics. Store cursor plus the accepted list's media/order together so data and selection are rebased as one transition. Reset the browsing session on media change; reject abandoned fetch generations. Derive display ordinal/remaining/Up next from eligible keys, replacing index mutation ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, line 278](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L278)) and index-based Up next selection ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, line 726](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L726)).
- [ ] Wire Skip, ignore success, download success, auto-skip and Up next through key-based transitions. Preserve synchronous onScreenKeyRef invalidation and existing attempt/media/search gates. Refresh events must not clear a newer download's state. Correct the stale movies retry comment using the real loadSeq checks ([src/Themearr.Web/src/app/movies/page.tsx @ e81b25f, lines 44–53](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/movies/page.tsx#L44-L53)); inspect the shows comment and adjust only if it makes the same obsolete claim.
- [ ] Run `npx vitest run src/lib/media/queueCursor.test.ts src/app/queue-refetch-cursor.test.tsx`, then `npm test`, `npm run lint` and `npm run build`. Require all old assertions unchanged, no skips and lint at zero errors/three existing warnings. Temporarily restore numeric page advancement while leaving the helper: the consumer regression must go red, proving the helper is actually wired.
- [ ] Run concurrency review and E2E against the fresh build; commit as `fix: anchor queue progress to media identity` and reference `Closes #53` in the implementation PR.

## Dependencies and propagation

This can ship before #55; local cursor identity and backend attempt identity address separate problems. If #55 lands first, retain its bindings while modifying queue state. Both media types use this shared page; no server/schema/dashboard/history/settings/System additions are required.
