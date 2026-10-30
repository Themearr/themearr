# #53: Keep queue position stable when lists change

Status: reviewed proposal; implementation not started. Issue: [#53](https://github.com/Themearr/themearr/issues/53). Source baseline: upstream `main` at `e81b25f4c487dc5406b93cab9bf3736d2da4aad9`.

## Evidence

The queue stores a numeric index ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, line 45](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L45)), indexes a replaceable pending list ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, lines 152–153](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L152-L153)), and advances by incrementing that index ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, lines 276–284](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L276-L284)). Fetch results identify the media type, not the selected item ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, lines 114–120](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L114-L120)). The reported A/B/C race remains possible by inspection. `useResource` deliberately retains prior data during refresh ([src/Themearr.Web/src/lib/useResource.ts @ e81b25f, lines 12–15](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/lib/useResource.ts#L12-L15)); changing that global behavior is unnecessary.

The movies retry comment still describes unstamped responses ([src/Themearr.Web/src/app/movies/page.tsx @ e81b25f, lines 57–73](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/movies/page.tsx#L57-L73)) although loadMovies checks its request stamp ([src/Themearr.Web/src/app/movies/page.tsx @ e81b25f, lines 44–53](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/movies/page.tsx#L44-L53)). Correct that comment as requested in the issue.

## Cursor model

Introduce a small pure queue cursor module with state `{ media, currentKey, visitedKeys }`. Keys are qualified by media type. The page remains the owner of fetching, polling and effects; this module decides only which pending item is selected.

- On a new media selection, start a new browsing session: clear visited keys and choose the first eligible item once the matching list is available. Preserve current switch/reset behavior.
- On Skip, Ignore success or download success, mark only the acted-on key visited. Choose the first unvisited eligible successor in the latest list, wrapping to any remaining unvisited item if needed. Failed actions do not mark visited.
- On a new list, retain the selected key if it is still pending and unvisited. If it disappeared, use its next surviving unvisited successor from the preceding order; otherwise choose the first unvisited item in the new order. Never retain a numeric offset across list versions.
- Up next selects its explicit key. Items bypassed by this deliberate jump count as visited for this session, preserving forward-navigation semantics; a refresh cannot jump back over that deliberate choice. A media switch/re-entry resets session visits as today.
- Completed/ignored items must not reappear from a stale fetched snapshot. New pending items remain eligible even when a prior snapshot was exhausted. Show 'All caught up!' only when the accepted matching list has no unvisited pending item.

Derive displayed ordinal, remaining count and Up next from the cursor/eligible ordering. Retain existing request stamps, download-attempt ownership, media binding, timeout limits, search invalidation and synchronous on-screen-key updates. A cursor fixes selection identity; it does not replace async ownership guards.

## Acceptance and propagation

Deferred list tests reproduce A finishing while a same-media refetch waits, then a new `[B,C]` snapshot: B must be next. Cover Skip with a shrinking snapshot, reordered lists, a removed current key, new items after exhaustion, intentional Up next jumps and same raw IDs across media. Exercise the rendered QueuePage as well as the pure model. Keep every existing queue assertion unchanged; the issue's historical test count is not a current baseline.

This changes the shared queue for movies and shows. No API, database, dashboard, history, settings or System change is needed. Keep the newly added needs-attention component integrated ([src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, line 608](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L608), [src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, line 630](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L630), [src/Themearr.Web/src/app/queue/page.tsx @ e81b25f, line 687](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.Web/src/app/queue/page.tsx#L687)). Require concurrency review.
