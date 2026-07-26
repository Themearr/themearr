# Manual Plex server URL — design

**Date:** 2026-07-26
**Status:** Approved (design), pending implementation plan
**Origin:** Issue [#24](https://github.com/Themearr/themearr/issues/24) — tester's Plex is on a bridged network; Themearr can't reach the address Plex advertises, and there is no way to override it.

## Problem

Themearr only ever uses the server address Plex advertises during OAuth sign-in
(`PlexService.DiscoverServersAsync` → `RankConnections`). When that address is
unreachable from Themearr — Plex on a Docker/LXC bridge, a VLAN, or behind a
`plex.direct` host a container can't resolve — the library source dead-ends with the
health error *"The Plex server is unreachable. Check it is running and the URL in
Settings is correct."* That message names a Settings URL field that **does not exist**:

- **Setup** (`SetupWizard.tsx`) lists only auto-discovered servers as checkboxes; no manual entry.
- **Settings** (`settings/page.tsx`, "Plex Connection") shows `srv.name` / `srv.url` **read-only**.

The operator has no way to correct the address.

## Goals / non-goals

**Goal:** let an operator who has completed setup point Themearr at a reachable Plex
address by editing (and testing) the server URL in Settings, reusing the token already
obtained via OAuth.

**Non-goals (YAGNI — deferred unless proven needed):**
- Manual entry in the **setup wizard** for the zero-discovery case (a *different*, rarer
  failure — most bridged servers still *discover*, since discovery goes through plex.tv,
  not the LAN; they only advertise an unreachable *address*). If people get stuck
  mid-setup, that is the clean follow-up.
- A URL-only "connect without OAuth" mode (a second Plex auth path to maintain).
- Changing the token itself — re-auth remains the existing OAuth flow.

## Architecture

One editable field; **all existing read paths are untouched.** The manual URL
overwrites `url` (and collapses `urls` to `[manual]`) inside the existing
`plex_selected_servers` record. Health (`PlexLibrarySource.CheckAsync`), sync
(`PlexService.FetchMoviesAsync`), and poster fetch already read that field, so nothing
else changes. No new setting, no second source of truth.

## Backend — two **bearer-only** endpoints in `SettingsController`

Mirrors the existing Radarr pair (`POST api/settings/radarr`, `POST api/settings/radarr/test`)
for shape, but with the stricter auth of `/api/update` and `/api/setup/reset`.

**Both endpoints are bearer-only** — gated on `ctx.AuthenticatedWithBearerToken()`,
returning `403` for an API-key request. Rationale: each one sends or binds the **stored
Plex token to an operator-supplied host**, so either is a token-**exfiltration** primitive.
The API key is the lower-trust, externally-held credential (Radarr, webhooks); it must not
be able to point Plex at an attacker host and harvest the token. This is the same gate the
existing privileged operations use.

### `POST /api/settings/plex/test`
Body `{ serverId, url }`. Probes `{normalizedUrl}/identity` with the server's **stored**
token (never a token from the request body), saves nothing. Returns `{ ok: true }` or
`{ ok: false, detail }` reusing `CheckAsync`-style messages: reachable / `401` rejected /
unreachable / timeout. Reuse the `plex-health` named client (short timeout).

### `POST /api/settings/plex/server`
Body `{ serverId, url }`. Validates and normalizes the URL, looks up the stored server
by `serverId`, sets `url = normalized` and `urls = [normalized]`, **keeps the existing
token bound**, and persists.

**Storage — the load-bearing decision.** This must NOT route through
`Database.SetPlexServersMergingTokens`, whose URL-match guard deliberately *drops* the
token when the URL changes. That guard exists because `CheckAsync` is reachable from the
**unauthenticated `/health`** endpoint, so re-binding a real token to an arbitrary host
there would leak it. This authenticated path re-binds the stored token to the new URL
directly (a plain `GetPlexServers` → mutate matching id → `SetPlexServers` write). The
unauthenticated `/health` protection is left exactly as-is — the bearer-only gate above is
what makes the direct rebind safe here.

**URL validation/normalization:**
- Trim; if no scheme, prepend `http://` (Plex local is typically `http` on `:32400`).
- Require scheme `http`/`https` and a non-empty host; reject otherwise with a clear message.
- **Deliberately allow private/loopback hosts** — Plex servers are private, and the
  discovered URLs already are. `HostGuard` is NOT applied here (it guards the
  download/paste-URL path, not the Plex library source).

## Data flow

Settings → Plex Connection → edit URL → **Test** (`/plex/test`, shows inline result) →
**Save** (`/plex/server`, persists) → health re-checks; the "unreachable" error clears
once the new address responds. The health message *"check the URL in Settings"* is now
truthful.

## Frontend — editable "Plex Connection" panel

`settings/page.tsx`: the read-only server row gains a URL `<Input>` + **Test** + **Save**,
matching the Radarr connect UX. Add `plexApi.test(serverId, url)` and
`plexApi.saveUrl(serverId, url)` to `lib/api.ts` (mirroring `radarrApi.test/save`).
Test/Save failures are surfaced inline, never swallowed (per the project's
honest-error / `useResource` ethos).

## Error handling

- **Test:** reachable → success; `401` → "Plex rejected the stored token — sign in again";
  unreachable/timeout → the existing hand-written sentences. No token in any message
  (existing "never leak the token" contract).
- **Save:** invalid URL → 400 with a clear reason; unknown `serverId` → 404; success →
  redacted server echoed back.

## Testing (TDD)

**Backend (xUnit; StubHandler pattern from `PlexLibrarySourceTests`):**
1. Save updates `url` and collapses `urls` to the new address.
2. Save keeps the token bound — `GetPlexServersDict()[serverId].Token` is intact for the new URL.
3. Test returns ok / reports `401` / reports unreachable, per stubbed responses.
4. Test never leaks the token in the message or request URI.
5. **Bearer-only (teeth):** an API-key-authenticated request to *both* `/plex/test` and
   `/plex/server` is refused with `403`, while a bearer request succeeds — the token
   cannot be exfiltrated via the lower-trust credential.
6. **Security invariant (teeth):** `SetPlexServersMergingTokens` still drops the token on
   a URL mismatch with no supplied token — pinning that the unauthenticated `/health`
   path is not weakened by adding the authenticated save path.

**Frontend (Vitest):** the Plex panel renders an editable URL; Test surfaces success and
failure; Save calls the endpoint; a failed Test/Save is visible (not silent).

## Rejected alternative

A separate `plex_manual_url` override setting prepended to the candidate list — two
sources of truth, and every read path (health, sync, poster) would need updating.
Overwriting `url` in the existing record is cleaner and leaves read paths untouched.
