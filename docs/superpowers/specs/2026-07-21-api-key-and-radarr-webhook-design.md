# API key and Radarr webhook

**Date:** 2026-07-21
**Status:** Approved, ready for implementation planning

## Goal

Give Themearr an API key that external tools can authenticate with, and a Radarr
webhook that fetches a theme the moment a movie is imported instead of waiting for
the next scheduled sync.

## Why this shape

This is sub-project D of the arr-alignment work (A was the System page, v1.40.0;
B was Radarr as a library source, v1.41.0 and v1.42.0).

The original framing was "arr-style API", which bundles three things. Interrogating
what each would actually be used for removed two of them:

- **Versioned `/api/v1` — rejected.** Every controller sits at `/api/…` with no
  version segment, and the frontend calls those paths directly. Introducing a
  version means moving all eight route prefixes and rewriting the frontend client,
  for an app whose only consumer is its own UI. Radarr is on `/api/v3` because it
  has had a v1 and a v2 and third parties depend on it; Themearr adopting `/api/v1`
  on day one buys the appearance of that maturity and none of the substance.
- **Dashboard visibility (Homarr, Organizr) — rejected.** Those recognise arr apps
  by Radarr's specific contracts — `/api/v3/queue`, `/api/v3/calendar`, health in
  arr's exact shape. Themearr has no queue in that shape and no calendar. Appearing
  in them would mean impersonating Radarr's API, which is substantial work for a
  tile and would surface fields that do not mean what the dashboard assumes.

What remains is the part with substance: an API key, and the webhook it unlocks.

**Honest sizing.** Radarr already syncs every 15 minutes, so instant-on-import saves
an average of about seven minutes on a theme song. Its stronger justification is
correctness rather than speed: event-driven instead of polling Radarr 96 times a day
whether anything changed or not.

## Scope

**In scope:** an API key generated and stored by Themearr; `X-Api-Key` accepted
alongside the existing bearer token; a Radarr webhook endpoint that triggers a sync
on import; settings UI to view and regenerate the key; README instructions.

**Out of scope:** API versioning; dashboard compatibility; scoped or per-client
keys; notifications; anything that changes how the frontend authenticates.

## Decisions

**A separate API key, not the existing access token.** `THEMEARR_AUTH_TOKEN` is the
master credential: set in the environment, required at startup, held by every
browser session. Reusing it would put that credential in Radarr's config, and
rotating it would mean editing the environment file, restarting the service,
re-entering it in every browser, and updating Radarr — with no way to revoke
Radarr's access alone. A separate key is regenerable independently and is what arr
apps do.

**No scoping.** A restricted key that could trigger a sync but not change settings
was considered and rejected: it requires a permission concept the codebase does not
have, for a key only the operator and Radarr will ever hold.

**A dedicated webhook endpoint, not the existing task-run endpoint.** Pointing
Radarr at `POST /api/system/tasks/syncLibrary/run` would need no new surface, but
that endpoint cannot distinguish Radarr's event types — health checks and rename
events would each start a full library sync, and Radarr's Test button would
silently start one too.

**Trigger a sync rather than fetching the movie directly.** Parsing the movie out of
Radarr's payload and fetching just its theme was rejected: the movie is not in
Themearr's database yet, so it would mean inserting it directly and duplicating the
resolve-and-upsert logic `SyncService` already owns — a second write path into the
movie table that would drift.

## Architecture

### The API key

Stored in the `api_key` setting, generated on first start if absent: 32 random bytes
rendered as 64 lowercase hex characters, via `RandomNumberGenerator`. Seeded in
`Program.cs` alongside the existing app-version seeding.

### Authentication

`ApiAuthMiddleware` currently reads the expected token once at construction:

```csharp
private readonly byte[] _expected = LoadToken(config, log);
```

That is sound because `THEMEARR_AUTH_TOKEN` is immutable for the process lifetime.
The API key is not — it can be regenerated — so it must be read per request.

**The database is read only when an `X-Api-Key` header is actually present.** The
browser sends `Authorization: Bearer …` and never sets that header, so the hot path
— every page load, the 60-second health poll, the 3-second sync poll — takes the
existing in-memory comparison and never touches the database. The read happens only
on requests from Radarr or a script, which arrive a few times an hour at most.

Both credentials grant identical access and are compared with
`CryptographicOperations.FixedTimeEquals`. A request with neither, or with a wrong
value, gets the existing `401` and `WWW-Authenticate` response; no new failure shape.

The two existing carve-outs are unchanged: `/api/auth/*` stays unauthenticated, and
`/api/poster` continues to self-authenticate through its signed expiring query
string. Neither accepts an API key, and neither needs to.

### The webhook

`POST /api/webhook/radarr`. It sits under `/api/*`, so the existing middleware
guards it and the API key works there without an exemption.

| Radarr `eventType` | Behaviour |
|---|---|
| `Download` | triggers a library sync |
| `Test` | 200 with a friendly message, no sync |
| anything else | 200, ignored, no sync |

**Every recognised request returns 200, including ignored events.** A 400 on an
unrecognised `eventType` would make Radarr report the connection as failing and,
depending on version, disable it. Silence has to look like success.

The single exception is a body that is not valid JSON, or has no `eventType` at all
— that is a malformed request rather than an event Themearr chooses to ignore, and
returns 400. Radarr never sends one; the case exists so a misdirected client gets a
useful answer instead of a 500.

`Test` is handled explicitly so that configuring the connection in Radarr gives
immediate feedback rather than deferring the discovery of a wrong URL or key to the
next import.

The handler returns immediately — triggering is a channel write, not a sync — so
Radarr never waits on Themearr's work.

### Debounce comes for free

Radarr importing a batch fires one webhook per movie. `TaskRegistry`'s trigger is a
`Channel` of capacity 1 with `FullMode.Wait`, so the second and subsequent writes
return `false` and are dropped: twenty webhooks produce one sync. This was built in
v1.40.0 so an impatient *Run now* clicker could not queue five library syncs, and it
is exactly the property a webhook endpoint needs.

## Settings and documentation

Themearr's own key is **visible and copyable** — its purpose is to be pasted into
Radarr. This is deliberately the opposite treatment from Radarr's key, which
Themearr holds and never shows back. The difference is direction of trust: one is a
credential held on someone else's behalf, the other is a credential issued to them.

Settings shows the key with a copy button, a **Regenerate** button warning that it
breaks any existing Radarr connection, and the webhook URL ready to paste.

The README documents the Radarr side — Settings → Connect → Webhook, `POST`, the
URL, and a custom header `X-Api-Key` — plus two caveats:

- **The webhook is most useful when Radarr is the library source.** With Plex as the
  source and Radarr only downloading, an import fires the webhook, Themearr syncs
  Plex, and Plex may not have scanned the file yet — so the theme still waits for a
  later sync. This is documented rather than gated: gating would silently do nothing
  for a configured webhook, leaving the operator to wonder why.
- Radarr builds predating custom webhook headers cannot send `X-Api-Key`. Support
  was merged upstream on 2024-11-27.

## Testing

| Unit | Tests |
|---|---|
| `ApiAuthMiddleware` | valid `X-Api-Key` passes; wrong key 401s; no credential 401s; **`Bearer` still works unchanged**; a regenerated key takes effect without a restart |
| | **no database read when no `X-Api-Key` header is present** — asserted by counting reads against a counting database |
| Key generation | created on first start; stable across restarts; regenerate produces a different value |
| `WebhookController` | `Download` triggers a sync; `Test` returns 200 **and does not** trigger; an unknown `eventType` returns 200 and does not trigger; malformed JSON returns 400 rather than 500 |

Three of the four webhook tests assert that something *does not* happen. A spurious
sync is invisible in normal use, so that is precisely the behaviour that would
regress unnoticed.

The debounce needs no new test: `TaskRegistryTests` already pins that rapid triggers
coalesce to one.

## Success criteria

1. A request carrying a valid `X-Api-Key` is accepted on any `/api/*` endpoint; a
   wrong or absent one is rejected with 401.
2. The existing frontend continues to work with no change to how it authenticates.
3. Pressing **Test** on the Radarr webhook connection returns success without
   starting a sync.
4. Importing a movie in Radarr causes its theme to be fetched without waiting for
   the scheduled sync.
5. Importing several movies at once results in one sync, not one per movie.
6. Regenerating the key immediately invalidates the old one, without restarting the
   service and without logging any browser out.
7. The API key appears nowhere in a log line or a URL.
8. `dotnet test` passes with the new tests added.
