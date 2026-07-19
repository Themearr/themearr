# Radarr as a library source

**Date:** 2026-07-19
**Status:** Approved, ready for implementation planning

## Goal

Let Themearr read its movie list from Radarr instead of Plex, so it works for
people who do not run Plex at all. Because `theme.mp3` is read by Plex, Jellyfin,
Emby and Kodi alike, sourcing the library from Radarr reaches all of those users
without writing a client for any of them.

## Why this shape

This is sub-project B of the arr-alignment decomposition (A was the System page,
shipped as v1.40.0; C is a native Jellyfin/Emby client).

"Radarr integration" could mean two very different things:

- **Notifier** — Radarr's webhook tells Themearr a movie landed, and Themearr
  fetches its theme immediately. Cheap: no schema change, no poster work, no path
  work. But it makes Themearr *more* Plex-dependent, not less.
- **Library source** — Themearr reads its movie list from Radarr. Radarr becomes
  the source of truth for titles, years, paths and posters. Plex becomes optional.

Only the library source delivers the non-Plex unlock that ranked B above C. The
notifier was therefore rejected: shipping it and calling B done would leave C at
full cost.

Within the library source, **scheduled pull only**. Instant-on-import is deferred:
it needs an authentication path an external tool can use, which realistically
means arr-style `X-Api-Key` support — a separate concern that should not be
dragged into a data-model change. Radarr is local and cheap to poll, so a short
interval delivers most of the benefit (see *Sync cadence*).

## Scope

**In scope:** an `ILibrarySource` abstraction with Plex and Radarr
implementations; folder-based movie identity and the schema migration to reach
it; a shared path resolver; source-aware sync, posters, health and setup;
settings for choosing and configuring the source.

**Out of scope:** Radarr webhooks and instant-on-import; arr-style `X-Api-Key`
authentication; a native Jellyfin/Emby client; running Plex and Radarr as sources
simultaneously.

## Delivery: two plans, two releases

This spec is implemented in two stages, each with its own implementation plan.

| Stage | Contents | User-visible change |
|---|---|---|
| **B1** | `ILibrarySource` + `LibrarySourceResolver`, `PlexLibrarySource`, the `LocalFolderResolver` extraction, folder identity, the schema migration, pruning | **none** — Plex remains the only source |
| **B2** | `RadarrLibrarySource`, settings and secret handling, the setup-wizard branch, source-aware posters, `LibrarySourceCheck`, `TaskRegistry.UpdateInterval` | Radarr becomes selectable |

The split is about blast radius, not tidiness. B1 migrates every existing user's
`movies` table while changing nothing they can see, so a post-upgrade problem has
exactly one plausible cause. Bundled with B2, the same symptom could be the
migration, the extracted resolver, the Radarr client or the wizard — four
suspects at once, on a live install.

B1 must therefore leave behaviour identical: same movies, same statuses, same
history, same poster URLs, same sync cadence. Its success criterion is that a
user cannot tell it shipped.

Everything below describes the finished system. Section headings note which stage
owns them where it is not obvious.

## Decisions

**One source at a time.** A setting selects Plex or Radarr. Merging both was
rejected: it needs two identity spaces, a conflict rule for the same folder
described differently by each, and a merge that can be silently wrong. Arr apps
keep one source of truth for their own domain.

**A movie is its resolved local folder.** Not a Plex rating key, not a TMDB id.
Every source has a folder, it is exactly what Themearr acts on, and it makes
"downloaded" intrinsically true — the answer is whether a theme file sits in that
folder, rather than a status column that can drift from disk. TMDB id was
rejected because a movie with no TMDB match would have no identity; source +
native id was rejected because identity would stay borrowed from whichever tool
is configured, so switching source would re-create the library and orphan all
history.

**Switching source preserves everything.** Plex and Radarr describe the same
folders on the same disk, so after a switch every movie resolves to the id it
already had. Status, ignore flags and history all survive. This falls out of
folder identity and is a primary reason for choosing it.

## Architecture

```
                    ┌──────────────────────┐
SyncService ───────▶│ LibrarySourceResolver │──▶ active ILibrarySource
PosterController ──▶└──────────────────────┘        │
LibrarySourceCheck ─┘                     ┌─────────┴─────────┐
                                   PlexLibrarySource   RadarrLibrarySource
                                          │                   │
                                          └──▶ LocalFolderResolver ◀──┘
```

```csharp
public interface ILibrarySource
{
    string   Name         { get; }   // "plex" | "radarr"
    TimeSpan SyncInterval { get; }   // 24h for Plex, 15m for Radarr

    Task<IReadOnlyList<DiscoveredMovie>> FetchAsync(Action<string> log, CancellationToken ct);
    Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct);

    /// <summary>Null when healthy, otherwise a user-facing reason.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
}

public sealed record DiscoveredMovie(
    string LocalFolder,   // resolved — the identity
    string Title,
    int?   Year,
    string SourceRef,     // rating key / Radarr id, for posters only
    string ReportedPath); // what the source claimed, for diagnostics
```

`SyncService` stops knowing what Plex is: it asks the resolver for the active
source, receives `DiscoveredMovie`s, and upserts by folder.

### `PlexLibrarySource`

A thin adapter over the existing `PlexService`. Its XML parsing, connection
ranking and token handling are untouched — this is not a rewrite of working code,
only an interface over it. `CheckAsync` is the existing `/identity` ping.

### `LocalFolderResolver` (extraction)

`ResolveLocalFolder`, `ApplyPathMappings` and `FindBySuffix` are currently
**private inside `PlexService`**. Path mapping is not a Plex problem — it is a
"the tool reporting paths sees a different filesystem than Themearr" problem, and
Radarr in a container hits it identically, reporting `/movies/Heat (1995)` where
Themearr sees `/mnt/media/Movies/Heat (1995)`.

This extraction is what makes folder identity well-defined: both sources report
paths from their own perspective, and only after resolution do two different
strings become the same folder, and therefore the same movie. Existing Path
Mappings settings keep working unchanged and apply to Radarr immediately.

### `RadarrLibrarySource`

`GET {url}/api/v3/movie` with an `X-Api-Key` header.

| Radarr field | Used as |
|---|---|
| `path` | fed through `LocalFolderResolver` → the identity |
| `title`, `year` | display |
| `id` | `source_ref`, for poster fetches only |
| `hasFile` | filter — `false` means monitored but not downloaded, so skip |

`CheckAsync` calls `/api/v3/system/status`. Posters come from
`/api/v3/mediacover/{id}/poster.jpg`.

## Schema and migration

```sql
movies (
    id          TEXT PRIMARY KEY,        -- short stable hash of folderName
    folderName  TEXT NOT NULL UNIQUE,    -- resolved local folder = the real identity
    source      TEXT NOT NULL,           -- 'plex' | 'radarr' — who last reported it
    source_ref  TEXT,                    -- opaque to everything but its own source; posters only
    title       TEXT NOT NULL,
    year        INTEGER,
    sourcePath  TEXT,                    -- what the source claimed, for diagnostics
    status      TEXT NOT NULL DEFAULT 'pending',
    ignored     INTEGER NOT NULL DEFAULT 0,
    synced_at   TEXT
)
```

**`source_ref` is opaque to everything except the source that issued it.** This
matters for Plex specifically: `PlexImageUrl` needs both the server *and* the
rating key to build a poster URL, so a rating key alone would break poster
loading for anyone with more than one Plex server. `PlexLibrarySource` therefore
stores `"{serverId}:{ratingKey}"` — which is exactly today's movie id — and parses
it back when fetching a poster. `RadarrLibrarySource` stores its numeric movie id.
No other component may interpret this field.

The id is the first 16 hex characters of the SHA-256 of the normalised folder,
rather than the folder itself, because ids appear in URLs (`/api/movies/{id}/theme`,
`/api/download/status/{id}`). A raw path there needs escaping, reads badly, and
leaks the filesystem layout to the browser. Hashing keeps the id stable,
URL-safe, and recomputable from `folderName` alone, so no mapping table is stored.

Normalisation before hashing: trailing directory separator trimmed, ordinal
comparison, no case folding (Themearr runs on Linux, where paths are
case-sensitive).

**Migration** follows the pattern already in `Database.Init()` — `PRAGMA
table_info` to detect the old shape, rename, recreate, copy, drop — with two
changes:

1. **It runs in a transaction.** The existing precedent is unguarded, so a
   failure partway leaves `movies_legacy` renamed with no `movies` table: a dead
   install. SQLite supports transactional DDL, so a failed migration rolls back
   and the app starts on the old schema. This migration touches every user's
   library, so this is required, not optional.
2. Rows carry `folderName` already, so old→new ids are computed directly from it
   with no lookup. `status` and `ignored` are carried across, preserving
   downloaded state and ignore choices, and `source` is set to `plex` with
   `source_ref` set to the row's existing `"{plex_server_id}:{plex_rating_key}"`
   id so posters keep working. `theme_history.movie_id` is rewritten through the
   same mapping; history rows already denormalise `movie_title` and `movie_year`,
   so any that fail to map still display correctly rather than going blank.

Edge cases: rows with an empty `folderName` (legacy, pre-resolution) are dropped,
since they cannot be acted on. Two movies resolving to one folder collide — first
wins, the rest are logged.

## Sync behaviour

```
AutoSyncService ─▶ SyncService ─▶ resolver ─▶ active source .FetchAsync()
                                                    │
                                   LocalFolderResolver (shared mappings)
                                                    │
                                   upsert by folder ─▶ prune unseen (guarded)
```

**Sync now prunes.** Today ids come from Plex, so re-syncing updates rows in
place and a movie deleted from Plex lingers forever. Under folder identity,
changing Path Mappings makes everything resolve to new folders and new ids, so
the old rows would become permanent phantoms inflating every count.

After a sync that **completes successfully and returns a non-zero count**, rows
the source did not report are deleted. Both halves of that guard matter: an
unguarded prune plus a failed sync equals an empty library.

**Sync cadence follows the source.** 24 hours is a Plex number — Plex is
expensive to scan. Radarr is local and cheap, so `ILibrarySource.SyncInterval` is
24h for Plex and 15 minutes for Radarr. This is what delivers "a new import gets
its theme shortly after landing" without a webhook.

`TaskRegistry` gains `UpdateInterval(id, interval)`, because re-`Register`ing to
change the interval would replace the entry and wipe the last-run state.

## Settings and secrets

New settings: `library_source` (`plex` | `radarr`, default `plex`), `radarr_url`,
`radarr_api_key`.

`radarr_api_key` is a credential exactly like a Plex token, and `Database`
already has `GetPlexServersRedacted()` / `SetPlexServersMergingTokens()` for this
case: never send a stored secret to the browser, and treat an incoming blank as
"keep what you had". Radarr's key gets the same treatment.

## Health

`ILibrarySource.CheckAsync` means one `LibrarySourceCheck` replaces
`PlexReachableCheck`, asking the resolver for the active source and reporting
whatever it says. Two separate checks where only one can ever be relevant would
be misleading. A third source gets a health check for free.

The existing unresolved-path warning (`last_sync_unresolved_count`) now covers
both sources, since Radarr paths can be unmapped exactly as Plex paths can.

## Onboarding

The setup wizard is currently hard-wired to Plex: `server-select →
library-select → path-config`, where the first two steps are Plex concepts. A
Jellyfin user with Radarr has no Plex account, cannot reach `setup_complete`, and
so every health check reports healthy while nothing ever syncs. Shipping the
source without changing the wizard would produce a feature only existing Plex
users could reach.

```
                    ┌─ plex ─▶ server-select ─▶ library-select ─┐
source-select ──────┤                                           ├──▶ path-config ──▶ done
                    └─ radarr ─▶ radarr-connect ────────────────┘
```

`path-config` is already source-agnostic and is reused untouched.
`radarr-connect` takes a URL and API key with a **Test connection** button,
because a wrong key discovered at first sync is far worse than one discovered
while typing it.

Existing installs are unaffected: `setup_complete` stays `1` and `library_source`
defaults to `plex`, so nobody is re-prompted.

## Posters

The CSP is `img-src 'self'`, so external image URLs are blocked by design and
`PosterController` proxies. It now asks the resolver for the active source's
stream: Plex's photo transcoder, or Radarr's mediacover endpoint with the API key
attached server-side. `PosterUrlSigner`'s signed expiring URLs are unchanged, so
the credential never reaches the browser.

## Failure handling

The System page shipped in v1.40.0 surfaces most of this already. A wrong API key
or unreachable Radarr makes `FetchAsync` throw, `SyncService` catches it, the
Tasks tab shows the failure, and `LibrarySourceCheck` puts the reason on Health.
No new error plumbing.

Explicit requirements:

- The migration runs in a transaction (above).
- Prune only after a successful sync with a non-zero count (above).
- The Radarr API key never appears in a health message, a task result, or a log —
  hand-written messages, `LogSanitizer` for logs. Same rule as the Plex token.
- Folder collisions: first wins, rest logged.

## Testing

The riskiest work is not the new Radarr client — it is **moving working code**.
`LocalFolderResolver` extracts logic that every existing user's path resolution
depends on. Characterization tests are written against the current behaviour
*first*, then the extraction is performed. This is the technique that caught a
dropped `@lim` parameter during the `Database.Query` refactor earlier in this
project; it works precisely because the test is written against code that still
passes.

| Unit | Tests |
|---|---|
| `LocalFolderResolver` | characterization: direct hit, mapping applied, Windows `\` paths, suffix fallback, unresolved |
| Folder→id | stable across calls; trailing separator normalised; distinct folders produce distinct ids |
| Migration | old-schema DB → `status` and `ignored` preserved, history remapped, empty-folder rows dropped, rollback on failure |
| `RadarrLibrarySource` | field mapping; `hasFile:false` skipped; 401 and unreachable give clean messages; API key absent from every message |
| `LibrarySourceResolver` | picks by setting; defaults to plex; unknown value falls back safely |
| Prune | runs on success with results; does **not** run on failure or on zero results |
| `LibrarySourceCheck` | delegates to the active source and surfaces its reason |

## Success criteria

1. A fresh install with no Plex can complete setup by choosing Radarr, entering a
   URL and API key, and configuring library paths.
2. With Radarr as the source, the movie list matches Radarr's downloaded movies,
   and posters render.
3. An existing Plex install upgrades with every movie's downloaded status, ignore
   flag and history intact.
4. Switching an existing install from Plex to Radarr preserves status and history,
   because folders are unchanged.
5. A movie removed from the source disappears from Themearr after the next
   successful sync; a failed sync removes nothing.
6. A wrong Radarr API key produces a clear message on the Health tab, and the key
   itself appears nowhere in the UI or logs.
7. `dotnet test` passes with the new tests added.
