# Show Themes — Phase 1d (Frontend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give shows a face — a Shows page, a Queue `Movies | Shows` toggle, a Settings show-library selector and a nav entry — completing Phase 1.

**Architecture:** The two heavy movie components (`MovieGrid`, `SearchModal`) are generalized to take an injected `MediaAdapter` instead of importing `moviesApi`, then reused for shows. Pages stay separate and thin. One small backend addition exposes the show-library selection through the existing settings endpoint, written defensively so an omitted field cannot wipe it.

**Tech Stack:** React 19 + react-router-dom, Vite, Tailwind, Vitest + Testing Library; .NET 10 Web API + xUnit for the settings endpoint.

## Global Constraints

- **Movie behaviour must not change.** The existing vitest files — `movies-refresh-race.test.tsx`, `polls-stay-silent.test.tsx`, `inflight-guards.test.tsx`, `actions-failure.test.tsx`, `queue-race.test.tsx`, `pages-failure.test.tsx` — are the regression guard for the generalization and **must pass unmodified**. Editing one of them to accommodate a refactor is a signal the refactor changed behaviour; stop and reconsider instead.
- Run `npm test`, `npm run lint` and `npx tsc --noEmit` in `src/Themearr.Web` after every frontend task; `dotnet test tests/Themearr.API.Tests` after the backend task. All must be clean.
- **Show status derivation order (from 1c):** `ignored` → local theme file → `plex_has_theme` → `pending`. The UI never recomputes this; it renders what the API returns.
- **`plexTheme` is informational, never blocking.** 1c's API accepts a download for a `plexTheme` show; the UI offers it as **"Download anyway"**.
- **The Queue toggle is component state defaulting to Movies on every visit** — not persisted, not in the URL.
- **No new sync endpoint.** "Sync shows" uses `systemApi.runTask('syncShows')` and polls `systemApi.tasks()`.
- Existing route paths (`/movies`, `/queue`) are unchanged.

---

### Task 1: Expose `selectedShowLibraries` through the settings endpoint

`Database.GetSelectedShowLibraries`/`SetSelectedShowLibraries` have existed since 1a but nothing exposes them.

**Files:**
- Modify: `src/Themearr.API/Controllers/SettingsController.cs`
- Test: `tests/Themearr.API.Tests/ShowLibrarySettingsTests.cs` (create)

**Interfaces:**
- Produces: `GET /api/settings` response gains `selectedShowLibraries` (`Dictionary<string, List<string>>`).
- Produces: `SettingsPayload.SelectedShowLibraries` — **nullable** `Dictionary<string, List<string>>?`, written only when non-null.

- [ ] **Step 1: Write the failing test** (`ShowLibrarySettingsTests.cs`)

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowLibrarySettingsTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    // There is no shared IApiKeyStore double in the test project (ApiAuthMiddlewareTests
    // and ApiKeyEndpointTests each define their own), so this file gets a local one.
    private sealed class StubKeyStore : IApiKeyStore
    {
        public string Current => "test-key";
        public string Regenerate() => "test-key";
    }

    private static SettingsPayload Payload() => new()
    {
        SelectedServers   = [],
        SelectedLibraries = [],
        PathMappings      = [],
        LibraryPaths      = [],
        Advanced          = new Dictionary<string, int>(),
    };

    [Fact]
    public void Get_returns_the_stored_show_libraries()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var result = Assert.IsType<OkObjectResult>(
            TestControllers.NewSettingsController(db, new StubKeyStore()).Get());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal("3", body.GetProperty("selectedShowLibraries")
                              .GetProperty("srv1")[0].GetString());
    }

    [Fact]
    public void Save_writes_show_libraries_when_supplied()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var req = Payload();
        req.SelectedShowLibraries = new() { ["srv1"] = ["7"] };

        TestControllers.NewSettingsController(db, new StubKeyStore()).Save(req);

        Assert.Equal(["7"], db.GetSelectedShowLibraries()["srv1"]);
    }

    /// <summary>
    /// Save() writes the movie library selection unconditionally, so a payload that omits
    /// the show field must NOT be treated as "select nothing" — an older cached frontend
    /// bundle after an upgrade would otherwise silently wipe the operator's show libraries,
    /// looking like Themearr forgetting its own settings.
    /// </summary>
    [Fact]
    public void Save_that_omits_show_libraries_leaves_the_stored_selection_intact()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var req = Payload();               // SelectedShowLibraries deliberately left null
        TestControllers.NewSettingsController(db, new StubKeyStore()).Save(req);

        Assert.Equal(["3"], db.GetSelectedShowLibraries()["srv1"]);
    }

    /// <summary>An explicit empty dictionary IS a real "deselect everything" instruction.</summary>
    [Fact]
    public void Save_with_an_explicit_empty_map_clears_the_selection()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var req = Payload();
        req.SelectedShowLibraries = [];
        TestControllers.NewSettingsController(db, new StubKeyStore()).Save(req);

        Assert.Empty(db.GetSelectedShowLibraries());
    }
}
```

`TestControllers.NewSettingsController(Database, IApiKeyStore)` already exists
(`tests/Themearr.API.Tests/TestControllers.cs:30`), and `IApiKeyStore` is exactly
`string Current { get; }` plus `string Regenerate()`, so `StubKeyStore` above satisfies it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowLibrarySettingsTests"`
Expected: FAIL to compile — `SettingsPayload` has no `SelectedShowLibraries`, and the GET body has no `selectedShowLibraries`.

- [ ] **Step 3: Add the field to the payload and the endpoint**

In `SettingsController.cs`, add to the `Get()` anonymous object, after `selectedLibraries`:

```csharp
        selectedShowLibraries = db.GetSelectedShowLibraries(),
```

In `SettingsPayload`, add:

```csharp
    /// <summary>
    /// Nullable on purpose: <see cref="SettingsController.Save"/> writes the other
    /// collections unconditionally, so an absent field must mean "leave unchanged" rather
    /// than "select nothing". A frontend bundle cached from before this shipped omits it,
    /// and would otherwise wipe the operator's show libraries on their next settings save.
    /// An explicit empty dictionary still means "deselect everything".
    /// </summary>
    public Dictionary<string, List<string>>? SelectedShowLibraries { get; set; }
```

In `Save()`, after `db.SetSelectedLibraries(req.SelectedLibraries);`:

```csharp
        if (req.SelectedShowLibraries is not null)
            db.SetSelectedShowLibraries(req.SelectedShowLibraries);
```

- [ ] **Step 4: Confirm the setup path does not clobber show libraries**

Run: `grep -n "SetSelectedLibraries\|SetSelectedShowLibraries" src/Themearr.API/Controllers/SetupController.cs`
Expected: only `SetSelectedLibraries` appears — `SetupController` never writes show libraries, so a re-run of setup leaves them alone. If `SetSelectedShowLibraries` does appear, stop and report it; the setup wizard is out of scope for this plan.

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Controllers/SettingsController.cs tests/Themearr.API.Tests/ShowLibrarySettingsTests.cs
git commit -m "feat: expose selectedShowLibraries through the settings endpoint"
```

---

### Task 2: Frontend types, `showsApi`, and the media adapters

**Files:**
- Modify: `src/Themearr.Web/src/lib/types.ts`
- Modify: `src/Themearr.Web/src/lib/api.ts`
- Create: `src/Themearr.Web/src/lib/media/adapter.ts`
- Modify: `src/Themearr.Web/src/test/apiMock.ts`
- Test: `src/Themearr.Web/src/lib/media/adapter.test.ts` (create)

**Interfaces:**
- Produces: `type MediaStatus = 'pending' | 'downloaded' | 'plexTheme' | 'ignored'`
- Produces: `interface MediaItem`, `interface Show extends MediaItem { plexHasTheme: boolean }`
- Produces: `showsApi` with `list, search, download, downloadUrl, downloadStatus, deleteTheme, ignoreShow, unignoreShow, themeAudioObjectUrl`
- Produces: `interface MediaAdapter`, and the values `moviesAdapter` / `showsAdapter`

- [ ] **Step 1: Write the failing test** (`src/lib/media/adapter.test.ts`)

```ts
import { describe, it, expect, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const { moviesAdapter, showsAdapter } = await import('@/lib/media/adapter')

describe('media adapters', () => {
  it('movies expose three statuses and shows expose four', () => {
    expect(moviesAdapter.statuses).toEqual(['pending', 'downloaded', 'ignored'])
    expect(showsAdapter.statuses).toEqual(['pending', 'downloaded', 'plexTheme', 'ignored'])
  })

  it('each adapter routes to its own API surface', async () => {
    await moviesAdapter.ignore('m1')
    expect(api.moviesApi.ignoreMovie).toHaveBeenCalledWith('m1')
    expect(api.showsApi.ignoreShow).not.toHaveBeenCalled()

    await showsAdapter.ignore('s1')
    expect(api.showsApi.ignoreShow).toHaveBeenCalledWith('s1')
  })

  it('search normalises both shapes to { results }', async () => {
    vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [{ videoId: 'v1' }] } as never)
    vi.mocked(api.showsApi.search).mockResolvedValue({ show: {}, results: [{ videoId: 'v2' }] } as never)

    expect((await moviesAdapter.search('m1')).results[0].videoId).toBe('v1')
    expect((await showsAdapter.search('s1')).results[0].videoId).toBe('v2')
  })

  it('labels differ so the grid copy is media-appropriate', () => {
    expect(moviesAdapter.labels.searchPlaceholder).toBe('Search movies…')
    expect(showsAdapter.labels.searchPlaceholder).toBe('Search shows…')
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/lib/media/adapter.test.ts`
Expected: FAIL — `@/lib/media/adapter` does not exist.

- [ ] **Step 3: Add the types**

In `src/lib/types.ts`, add:

```ts
/** Every status any media type can report. Movies never use 'plexTheme'. */
export type MediaStatus = 'pending' | 'downloaded' | 'plexTheme' | 'ignored'

/** The shape MediaGrid renders. `Movie` is assignable to this (its status is a subset). */
export interface MediaItem {
  id: string
  source: string
  sourceRef: string
  title: string
  year: number | null
  sourcePath: string | null
  folderName: string
  status: MediaStatus
  posterUrl: string | null
}

export interface Show extends MediaItem {
  /** True when Plex already has a theme for this show (Plex Pass or a local file it found). */
  plexHasTheme: boolean
}

export interface ShowStats {
  total: number
  downloaded: number
  plexTheme: number
  pending: number
  ignored: number
  coverage: number
}
```

Leave the existing `Movie` interface untouched — its narrower `status` union is assignable
to `MediaStatus`, so nothing about movies needs to change.

Also add the new field to the existing `Settings` interface in the same file. It must land
here in Task 2, not later: the Shows page in Task 5 reads
`settingsApi.get().selectedShowLibraries` to decide between its two empty states, so the
type has to exist before then or Task 5 will not typecheck.

```ts
  /** Optional: absent on a response from a server older than 1d. */
  selectedShowLibraries?: Record<string, string[]>
```

- [ ] **Step 4: Add `showsApi`**

In `src/lib/api.ts`, after `moviesApi`, add. Note the routes are the namespaced 1c shape
(`/api/shows/{id}/...`), unlike the movie routes' legacy un-namespaced paths:

```ts
export const showsApi = {
  list: () => request<Show[]>('/api/shows'),

  search: (showId: string, q?: string) =>
    request<{ show: Show; results: YoutubeResult[] }>(
      `/api/shows/${encodeURIComponent(showId)}/search${q ? `?q=${encodeURIComponent(q)}` : ''}`
    ),

  download: (showId: string, videoId: string) =>
    request<{ started: boolean; showId: string }>(`/api/shows/${encodeURIComponent(showId)}/download`, {
      method: 'POST',
      body: JSON.stringify({ videoId }),
    }),

  downloadUrl: (showId: string, url: string) =>
    request<{ started: boolean; showId: string }>(`/api/shows/${encodeURIComponent(showId)}/download-url`, {
      method: 'POST',
      body: JSON.stringify({ url }),
    }),

  downloadStatus: (showId: string, init?: RequestInit) =>
    request<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>(
      `/api/shows/${encodeURIComponent(showId)}/download/status`, init),

  deleteTheme: (showId: string) =>
    request<{ deleted: boolean }>(`/api/shows/${encodeURIComponent(showId)}/theme`, { method: 'DELETE' }),

  ignoreShow: (showId: string) =>
    request<{ ignored: boolean }>(`/api/shows/${encodeURIComponent(showId)}/ignore`, { method: 'POST' }),

  unignoreShow: (showId: string) =>
    request<{ ignored: boolean }>(`/api/shows/${encodeURIComponent(showId)}/unignore`, { method: 'POST' }),

  stats: () => request<ShowStats>('/api/stats/shows'),

  // Same bearer-fetch-to-object-URL dance as moviesApi.themeAudioObjectUrl: an <audio>
  // element can't send an Authorization header. Caller revokes the URL.
  themeAudioObjectUrl: async (showId: string) => {
    const token = getAuthToken()
    const res = await fetch(
      `${BASE}/api/shows/${encodeURIComponent(showId)}/theme/audio`,
      { headers: token ? { Authorization: `Bearer ${token}` } : undefined },
    )
    if (res.status === 401) {
      clearAuthToken()
      if (typeof window !== 'undefined') window.location.href = '/login'
      throw new Error('Unauthorized')
    }
    if (!res.ok) throw new Error(`Audio fetch failed (${res.status})`)
    const blob = await res.blob()
    return URL.createObjectURL(blob)
  },
}
```

Add `Show` and `ShowStats` to the type import at the top of `api.ts`.

- [ ] **Step 5: Add `showsApi` to the test mock**

In `src/test/apiMock.ts`, alongside `moviesApi`:

```ts
    showsApi: group(
      'list', 'search', 'download', 'downloadUrl', 'downloadStatus',
      'deleteTheme', 'ignoreShow', 'unignoreShow', 'stats', 'themeAudioObjectUrl',
    ),
```

- [ ] **Step 6: Create the adapters** (`src/lib/media/adapter.ts`)

```ts
import { moviesApi, showsApi } from '@/lib/api'
import type { MediaItem, MediaStatus, YoutubeResult } from '@/lib/types'

/**
 * What MediaGrid and SearchModal need from a media type. Injecting this — rather than
 * importing moviesApi directly — is what lets shows reuse the windowing, in-flight guards
 * and refresh-staleness logic instead of owning a second copy of it.
 */
export interface MediaAdapter {
  list(): Promise<MediaItem[]>
  search(id: string, q?: string): Promise<{ results: YoutubeResult[] }>
  download(id: string, videoId: string): Promise<unknown>
  downloadUrl(id: string, url: string): Promise<unknown>
  downloadStatus(id: string, init?: RequestInit):
    Promise<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>
  ignore(id: string): Promise<unknown>
  unignore(id: string): Promise<unknown>
  deleteTheme(id: string): Promise<{ deleted: boolean }>
  themeAudioObjectUrl(id: string): Promise<string>

  /** Which filter chips the grid renders, in order. */
  statuses: MediaStatus[]
  labels: { plural: string; searchPlaceholder: string; emptyTitle: string }
}

export const moviesAdapter: MediaAdapter = {
  list:                () => moviesApi.list(),
  search:              (id, q) => moviesApi.search(id, q),
  download:            (id, videoId) => moviesApi.download(id, videoId),
  downloadUrl:         (id, url) => moviesApi.downloadUrl(id, url),
  downloadStatus:      (id, init) => moviesApi.downloadStatus(id, init),
  ignore:              id => moviesApi.ignoreMovie(id),
  unignore:            id => moviesApi.unignoreMovie(id),
  deleteTheme:         id => moviesApi.deleteTheme(id),
  themeAudioObjectUrl: id => moviesApi.themeAudioObjectUrl(id),

  statuses: ['pending', 'downloaded', 'ignored'],
  labels: { plural: 'movies', searchPlaceholder: 'Search movies…', emptyTitle: 'No movies yet' },
}

export const showsAdapter: MediaAdapter = {
  list:                () => showsApi.list(),
  search:              (id, q) => showsApi.search(id, q),
  download:            (id, videoId) => showsApi.download(id, videoId),
  downloadUrl:         (id, url) => showsApi.downloadUrl(id, url),
  downloadStatus:      (id, init) => showsApi.downloadStatus(id, init),
  ignore:              id => showsApi.ignoreShow(id),
  unignore:            id => showsApi.unignoreShow(id),
  deleteTheme:         id => showsApi.deleteTheme(id),
  themeAudioObjectUrl: id => showsApi.themeAudioObjectUrl(id),

  // 'plexTheme' sits between downloaded and ignored: it is covered, but not by us.
  statuses: ['pending', 'downloaded', 'plexTheme', 'ignored'],
  labels: { plural: 'shows', searchPlaceholder: 'Search shows…', emptyTitle: 'No shows yet' },
}
```

- [ ] **Step 7: Run tests + typecheck**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS, clean.

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.Web/src/lib src/Themearr.Web/src/test/apiMock.ts
git commit -m "feat: showsApi, media types and the movie/show adapters"
```

---

### Task 3: Generalize `SearchModal` onto the adapter

**Files:**
- Create: `src/Themearr.Web/src/components/media/SearchModal.tsx` (moved from `components/movies/`)
- Delete: `src/Themearr.Web/src/components/movies/SearchModal.tsx`
- Modify: `src/Themearr.Web/src/components/movies/MovieGrid.tsx` (import path), `src/Themearr.Web/src/app/queue/page.tsx` if it imports SearchModal

**Interfaces:**
- Consumes: `MediaAdapter` (Task 2)
- Produces: `<SearchModal item={MediaItem} adapter={MediaAdapter} onClose={() => void} onDownloaded={(id: string) => void} />`

- [ ] **Step 1: Move the file and swap `movie` for `item` + `adapter`**

`git mv src/Themearr.Web/src/components/movies/SearchModal.tsx src/Themearr.Web/src/components/media/SearchModal.tsx`

Then change only these things — the JSX body stays byte-identical apart from `movie.` → `item.`:

```tsx
import { useEffect, useState } from 'react'
import type { MediaItem, YoutubeResult } from '@/lib/types'
import type { MediaAdapter } from '@/lib/media/adapter'
import { Button, Modal, Spinner, Input } from '@/components/ui'

interface SearchModalProps {
  item: MediaItem
  adapter: MediaAdapter
  onClose: () => void
  onDownloaded: (id: string) => void
}

export function SearchModal({ item, adapter, onClose, onDownloaded }: SearchModalProps) {
```

Replace the four `moviesApi.*` calls with `adapter.*`:

- `moviesApi.downloadStatus(movie.id)` → `adapter.downloadStatus(item.id)`
- `moviesApi.search(movie.id)` → `adapter.search(item.id)`
- `moviesApi.download(movie.id, videoId)` → `adapter.download(item.id, videoId)`
- `moviesApi.downloadUrl(movie.id, manualUrl.trim())` → `adapter.downloadUrl(item.id, manualUrl.trim())`

And in the polling effect's dependency array, `movie.id` → `item.id`. The `data.results`
read is unchanged because the adapter normalises both response shapes to `{ results }`.

- [ ] **Step 2: Update the call site in `MovieGrid.tsx`**

```tsx
import { SearchModal } from '@/components/media/SearchModal'
import { moviesAdapter } from '@/lib/media/adapter'
```

```tsx
      <SearchModal
        item={movie}
        adapter={moviesAdapter}
        onClose={onClose}
        onDownloaded={id => onUpdated(id, 'downloaded')}
      />
```

- [ ] **Step 3: Find and fix any other importer**

Run: `grep -rn "components/movies/SearchModal\|from './SearchModal'" src/Themearr.Web/src`
Expected: no remaining references to the old path. Update any that appear.

- [ ] **Step 4: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS with **no test file edited**. If a movie test now fails, the move changed behaviour — revert and re-examine rather than adjusting the test.

- [ ] **Step 5: Commit**

```bash
git add -A src/Themearr.Web/src/components
git commit -m "refactor: SearchModal takes a media adapter instead of importing moviesApi"
```

---

### Task 4: Generalize `MovieGrid` into `MediaGrid`, with `plexTheme`

**Files:**
- Create: `src/Themearr.Web/src/components/media/MediaGrid.tsx` (moved from `components/movies/MovieGrid.tsx`)
- Delete: `src/Themearr.Web/src/components/movies/MovieGrid.tsx`
- Modify: `src/Themearr.Web/src/app/movies/page.tsx` (import + props)
- Test: `src/Themearr.Web/src/components/media/media-grid-plextheme.test.tsx` (create)

**Interfaces:**
- Consumes: `MediaAdapter`, `MediaStatus` (Task 2); `SearchModal` (Task 3)
- Produces: `<MediaGrid items={MediaItem[]} adapter={MediaAdapter} onUpdated={(id: string, status: MediaStatus) => void} emptyDescription={string} />`

- [ ] **Step 1: Write the failing test** (`media-grid-plextheme.test.tsx`)

```tsx
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const { MediaGrid } = await import('@/components/media/MediaGrid')
const { showsAdapter, moviesAdapter } = await import('@/lib/media/adapter')

const show = (over: Partial<Record<string, unknown>> = {}) => ({
  id: 's1', source: 'plex', sourceRef: 'srv1:1', title: 'The Wire', year: 2002,
  sourcePath: '/tv/The Wire', folderName: '/tv/The Wire', posterUrl: null,
  status: 'plexTheme', plexHasTheme: true, ...over,
}) as never

describe('MediaGrid with the shows adapter', () => {
  it('renders a Plex theme filter chip that movies do not get', () => {
    const { unmount } = render(
      <MediaGrid items={[show()]} adapter={showsAdapter} onUpdated={vi.fn()} emptyDescription="" />)
    expect(screen.getByRole('button', { name: /Plex theme/i })).toBeTruthy()
    unmount()

    render(<MediaGrid items={[]} adapter={moviesAdapter} onUpdated={vi.fn()} emptyDescription="" />)
    expect(screen.queryByRole('button', { name: /Plex theme/i })).toBeNull()
  })

  it('offers Download anyway for a show Plex already themes', async () => {
    const user = userEvent.setup()
    render(<MediaGrid items={[show()]} adapter={showsAdapter} onUpdated={vi.fn()} emptyDescription="" />)

    await user.click(screen.getByRole('button', { name: /The Wire/ }))

    // Informational, not blocking — 1c's API accepts the download.
    expect(screen.getByText(/Plex already has a theme/i)).toBeTruthy()
    expect(screen.getByRole('button', { name: /Download anyway/i })).toBeTruthy()
  })

  it('uses the adapter search placeholder', () => {
    render(<MediaGrid items={[show()]} adapter={showsAdapter} onUpdated={vi.fn()} emptyDescription="" />)
    expect(screen.getByPlaceholderText('Search shows…')).toBeTruthy()
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/components/media/media-grid-plextheme.test.tsx`
Expected: FAIL — `@/components/media/MediaGrid` does not exist.

- [ ] **Step 3: Move the file and generalize the props**

`git mv src/Themearr.Web/src/components/movies/MovieGrid.tsx src/Themearr.Web/src/components/media/MediaGrid.tsx`

Replace the header (imports through the `visible` filter) with:

```tsx
import { useEffect, useRef, useState } from 'react'
import type { MediaItem, MediaStatus } from '@/lib/types'
import type { MediaAdapter } from '@/lib/media/adapter'
import { Button, EmptyState, Spinner } from '@/components/ui'
import { SearchModal } from './SearchModal'

interface MediaGridProps {
  items: MediaItem[]
  adapter: MediaAdapter
  onUpdated: (id: string, status: MediaStatus) => void
  /** Context-dependent empty-state copy — the page knows the source, the grid doesn't. */
  emptyDescription: string
}

type Filter = 'all' | MediaStatus

const STATUS_LABEL: Record<MediaStatus, string> = {
  pending:    'Pending',
  downloaded: 'Downloaded',
  plexTheme:  'Plex theme',
  ignored:    'Ignored',
}

export function MediaGrid({ items, adapter, onUpdated, emptyDescription }: MediaGridProps) {
  const [filter,   setFilter]   = useState<Filter>('all')
  const [search,   setSearch]   = useState('')
  const [selected, setSelected] = useState<MediaItem | null>(null)

  const countOf = (s: MediaStatus) => items.filter(i => i.status === s).length
  const ignored = countOf('ignored')

  const visible = items.filter(i => {
    if (filter !== 'all' && i.status !== filter)     return false
    if (filter === 'all' && i.status === 'ignored')  return false
    if (search.trim()) {
      const q = search.toLowerCase()
      return i.title.toLowerCase().includes(q) || String(i.year ?? '').includes(q)
    }
    return true
  })
```

Keep the windowing block (`BATCH`, `limit`, `pagedFor`, the `IntersectionObserver` effect)
**exactly as it is** — it is the reason this component is being shared rather than copied.

Replace the filter-chip array with one driven by the adapter. Ignored still only appears
when there are some; `plexTheme` only appears when the adapter declares it:

```tsx
          {([
            ['all', `All (${items.length - ignored})`],
            ...adapter.statuses
              .filter(s => s !== 'ignored' || ignored > 0)
              .map(s => [s, `${STATUS_LABEL[s]} (${countOf(s)})`] as [Filter, string]),
          ] as [Filter, string][]).map(([val, label]) => (
```

Point the search input and empty state at the adapter/props:

```tsx
            placeholder={adapter.labels.searchPlaceholder}
```
```tsx
          title={search ? `No ${adapter.labels.plural} match your search` : adapter.labels.emptyTitle}
          description={search ? 'Try a different search term' : emptyDescription}
```

Rename the card and modal components and thread the adapter through:
`MovieCard` → `MediaCard` (prop `movie` → `item`), `MovieActionModal` → `MediaActionModal`,
`ThemeAudioPreview`'s `movieId` prop → `{ id, adapter }` calling
`adapter.themeAudioObjectUrl(id)`, and the two `moviesApi` calls in the modal
(`deleteTheme`, `unignoreMovie`) → `adapter.deleteTheme` / `adapter.unignore`.

In `MediaActionModal`, add the `plexTheme` branch alongside the existing `downloaded` and
`ignored` ones, and make the modal open straight to search for `plexTheme` only when the
user asks:

```tsx
  const [view, setView] = useState<'default' | 'search'>(item.status === 'pending' ? 'search' : 'default')
```
(unchanged — `plexTheme` deliberately does NOT auto-open search; the whole point is that
Themearr does not fill these by default.)

```tsx
          {item.status === 'plexTheme' && (
            <div className="space-y-3">
              <p className="text-sm text-[#667085]">
                Plex already has a theme for this show, so Themearr skips it. Downloading one
                writes a <code className="text-[#98A2B3]">theme.mp3</code> into the show folder,
                which takes priority over Plex&apos;s own.
              </p>
              <Button variant="secondary" className="w-full" size="sm" onClick={() => setView('search')}>
                Download anyway
              </Button>
            </div>
          )}
```

In `MediaCard`, add the badge. Keep the green tick for `downloaded` and give `plexTheme`
its own muted mark so the two never read as the same thing:

```tsx
        {item.status === 'plexTheme' && (
          <div
            title="Plex already has a theme"
            className="absolute bottom-1.5 right-1.5 flex h-5 items-center rounded-full bg-[#344054] px-1.5 text-[9px] font-semibold text-[#D0D5DD]"
          >
            PLEX
          </div>
        )}
```

and include `plexTheme` in the hover overlay wording:

```tsx
            {item.status === 'plexTheme' && <>Plex theme</>}
```

- [ ] **Step 4: Update the movies page**

In `src/app/movies/page.tsx`:

```tsx
import { MediaGrid } from '@/components/media/MediaGrid'
import { moviesAdapter } from '@/lib/media/adapter'
```

```tsx
        <MediaGrid
          items={movies}
          adapter={moviesAdapter}
          onUpdated={onMovieUpdated}
          emptyDescription={`Sync your ${sourceLabel} library to get started`}
        />
```

Where `sourceLabel` is whatever the page already computes for `MovieGrid`'s `sourceLabel`
prop, and `onMovieUpdated` is its existing handler (its `Movie['status']` parameter widens
to `MediaStatus` without change at the call site).

Run `grep -rn "MovieGrid" src/Themearr.Web/src` and fix any remaining reference.

- [ ] **Step 5: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS. The new `plexTheme` tests go green **and every existing movie test passes
unmodified**. If one needed editing, the refactor changed movie behaviour — stop.

- [ ] **Step 6: Commit**

```bash
git add -A src/Themearr.Web/src
git commit -m "feat: MediaGrid (adapter-driven) with plexTheme chip, badge and Download anyway"
```

---

### Task 5: Shows page and nav entry

**Files:**
- Create: `src/Themearr.Web/src/app/shows/page.tsx`
- Modify: `src/Themearr.Web/src/main.tsx` (route), `src/Themearr.Web/src/components/layout/Sidebar.tsx` (nav item)
- Test: `src/Themearr.Web/src/app/shows-page.test.tsx` (create)

**Interfaces:**
- Consumes: `showsApi`, `showsAdapter` (Task 2), `MediaGrid` (Task 4), `settingsApi`, `systemApi`
- Produces: route `/shows` rendering `ShowsPage`

- [ ] **Step 1: Write the failing test** (`src/app/shows-page.test.tsx`)

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const ShowsPage = (await import('@/app/shows/page')).default

function renderPage() {
  return render(<MemoryRouter><AuthProvider><ShowsPage /></AuthProvider></MemoryRouter>)
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.systemApi.tasks).mockResolvedValue([] as never)
})

describe('Shows page', () => {
  it('tells the operator to pick a show library when none are selected', async () => {
    vi.mocked(api.showsApi.list).mockResolvedValue([] as never)
    vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: {} } as never)

    renderPage()

    await waitFor(() => expect(screen.getByText(/No show libraries selected/i)).toBeTruthy())
    expect(screen.getByRole('link', { name: /Settings/i })).toBeTruthy()
  })

  it('lists shows once libraries are selected', async () => {
    vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
    vi.mocked(api.showsApi.list).mockResolvedValue([{
      id: 's1', source: 'plex', sourceRef: 'srv1:1', title: 'The Wire', year: 2002,
      sourcePath: '/tv/The Wire', folderName: '/tv/The Wire', posterUrl: null,
      status: 'pending', plexHasTheme: false,
    }] as never)

    renderPage()

    await waitFor(() => expect(screen.getByText('The Wire')).toBeTruthy())
  })

  it('triggers the syncShows task, not the movie sync', async () => {
    const user = (await import('@testing-library/user-event')).default.setup()
    vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
    vi.mocked(api.showsApi.list).mockResolvedValue([] as never)
    vi.mocked(api.systemApi.runTask).mockResolvedValue({ started: true } as never)

    renderPage()
    await waitFor(() => expect(api.showsApi.list).toHaveBeenCalled())
    await user.click(screen.getByRole('button', { name: /Sync shows/i }))

    expect(api.systemApi.runTask).toHaveBeenCalledWith('syncShows')
    expect(api.syncApi.start).not.toHaveBeenCalled()
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/shows-page.test.tsx`
Expected: FAIL — `@/app/shows/page` does not exist.

- [ ] **Step 3: Create the Shows page**

Model it on `src/app/movies/page.tsx`, keeping that page's `loadSeq` staleness guard —
copy its shape rather than inventing a new one:

```tsx
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { showsApi, settingsApi, systemApi } from '@/lib/api'
import type { Show } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { MediaGrid } from '@/components/media/MediaGrid'
import { showsAdapter } from '@/lib/media/adapter'
import { Button, EmptyState, ErrorIcon, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

export default function ShowsPage() {
  const [shows, setShows] = useState<Show[]>([])
  const [hasLibraries, setHasLibraries] = useState<boolean | null>(null)
  const [syncing, setSyncing] = useState(false)
  const [syncError, setSyncError] = useState<string | null>(null)
  const [refreshError, setRefreshError] = useState<string | null>(null)

  // Same monotonic-stamp guard the movies page uses: the Sync flow and the initial
  // load can both be in flight, and a slower earlier response must not overwrite a
  // newer one.
  const loadSeq = useRef(0)
  const loadShows = useCallback(async () => {
    const mine = ++loadSeq.current
    try {
      const list = await showsApi.list()
      if (mine !== loadSeq.current) return
      setShows(list)
      setRefreshError(null)
    } catch (e) {
      if (mine !== loadSeq.current) return
      setRefreshError(e instanceof Error && e.message ? e.message : 'Request failed')
    }
  }, [])

  useEffect(() => { void loadShows() }, [loadShows])

  // Whether any show library is selected decides between "no shows yet" and the
  // actionable "you haven't opted in" empty state.
  useEffect(() => {
    settingsApi.get()
      .then(s => setHasLibraries(
        Object.values(s.selectedShowLibraries ?? {}).some(v => v.length > 0)))
      .catch(() => setHasLibraries(true))   // don't accuse the user of misconfiguring on a failed read
  }, [])

  async function runSync() {
    setSyncing(true)
    setSyncError(null)
    try {
      await systemApi.runTask('syncShows')
      await pollUntilSyncFinishes()
      await loadShows()
    } catch (e) {
      setSyncError(e instanceof Error && e.message ? e.message : 'Could not start the sync')
    } finally {
      setSyncing(false)
    }
  }

  // Reads the shared task snapshot rather than a shows-specific status endpoint.
  // Silent on failure: this poll doesn't drive the page's content, so a dropped
  // request must not disturb what's already shown.
  async function pollUntilSyncFinishes() {
    for (let i = 0; i < 150; i++) {                 // ~5 minutes at 2s
      await new Promise(r => setTimeout(r, 2000))
      try {
        const tasks = await systemApi.tasks()
        if (!tasks.find(t => t.id === 'syncShows')?.isRunning) return
      } catch { /* keep waiting */ }
    }
  }

  return (
    <AppShell
      title="Shows"
      actions={
        <Button size="sm" onClick={runSync} loading={syncing}>Sync shows</Button>
      }
    >
      {syncError && (
        <div className="mb-4 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">{syncError}</p>
        </div>
      )}
      {refreshError && (
        <div className="mb-4 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh shows: {refreshError}</p>
        </div>
      )}

      {shows.length === 0 && hasLibraries === false ? (
        <EmptyState
          icon={<ErrorIcon />}
          title="No show libraries selected"
          description="Themearr only syncs the Plex show libraries you choose."
          action={<Link to="/settings" className="text-sm text-[#CC3333] hover:underline">Choose them in Settings →</Link>}
        />
      ) : (
        <MediaGrid
          items={shows}
          adapter={showsAdapter}
          onUpdated={(id, status) =>
            setShows(prev => prev.map(s => (s.id === id ? { ...s, status } : s)))}
          emptyDescription="Sync your Plex show libraries to get started"
        />
      )}
    </AppShell>
  )
}
```

Both component signatures are already correct for this usage:
`EmptyState({ icon?, title, description?, action? })` at
`src/components/ui/index.tsx:175`, and `AppShell({ children, title?, actions? })` at
`src/components/layout/AppShell.tsx:13`.

- [ ] **Step 4: Register the route and the nav item**

`src/main.tsx`, after the `/settings` route:

```tsx
          <Route path="/shows" element={<ShowsPage />} />
```

with `import ShowsPage from '@/app/shows/page'` alongside the other page imports.

`src/components/layout/Sidebar.tsx` — insert between the Movies and History entries:

```tsx
  {
    href: '/shows',
    label: 'Shows',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="2" y="7" width="20" height="13" rx="2" />
        <path d="m8 3 4 4 4-4" />
      </svg>
    ),
  },
```

No badge: the Movies badge costs a `syncApi.status` poll and shows would need a second one
against a different task. Not worth it until asked for.

- [ ] **Step 5: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS, clean.

- [ ] **Step 6: Commit**

```bash
git add -A src/Themearr.Web/src
git commit -m "feat: Shows page and nav entry"
```

---

### Task 6: Queue `Movies | Shows` toggle

**Files:**
- Modify: `src/Themearr.Web/src/app/queue/page.tsx`
- Test: `src/Themearr.Web/src/app/queue-shows-toggle.test.tsx` (create)

**Interfaces:**
- Consumes: `moviesAdapter`, `showsAdapter` (Task 2)

- [ ] **Step 1: Write the failing test** (`queue-shows-toggle.test.tsx`)

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const QueuePage = (await import('@/app/queue/page')).default

const item = (over: Record<string, unknown>) => ({
  id: 'x', source: 'plex', sourceRef: 'r', year: 2002, sourcePath: '/p',
  folderName: '/p', posterUrl: null, status: 'pending', ...over,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
  vi.mocked(api.moviesApi.list).mockResolvedValue([item({ id: 'm1', title: 'A Movie' })] as never)
  vi.mocked(api.showsApi.list).mockResolvedValue([
    item({ id: 's1', title: 'The Wire', plexHasTheme: false }),
    item({ id: 's2', title: 'Severance', status: 'plexTheme', plexHasTheme: true }),
  ] as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><QueuePage /></AuthProvider></MemoryRouter>)
}

describe('Queue media toggle', () => {
  it('defaults to Movies', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('A Movie')).toBeTruthy())
    expect(api.showsApi.list).not.toHaveBeenCalled()
  })

  it('switching to Shows triages shows instead', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByText('A Movie')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /^Shows$/ }))

    await waitFor(() => expect(screen.getByText('The Wire')).toBeTruthy())
    // plexTheme shows are not outstanding work — they never enter triage, matching
    // GetPendingShows filtering on plex_has_theme = 0.
    expect(screen.queryByText('Severance')).toBeNull()
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/queue-shows-toggle.test.tsx`
Expected: FAIL — there is no Shows button on the queue.

- [ ] **Step 3: Add the toggle and route the page through an adapter**

In `src/app/queue/page.tsx`:

```tsx
import { moviesAdapter, showsAdapter } from '@/lib/media/adapter'

// Component state, deliberately not persisted and not in the URL: someone who once
// looked at shows would otherwise return to the Queue, find an empty triage list and
// conclude it is broken. Movies are the default library and the safe default view.
const [media, setMedia] = useState<'movies' | 'shows'>('movies')
const adapter = media === 'movies' ? moviesAdapter : showsAdapter
```

Change the list resource to depend on the adapter, and swap every `moviesApi.*` call in the
page for its `adapter.*` equivalent (`search`, `download`, `downloadUrl`, `downloadStatus`,
`ignoreMovie` → `ignore`):

```tsx
const { data: items, error: itemsError, retry: retryItems } =
  useResource(useCallback(() => adapter.list(), [adapter]))
const pending = items ? items.filter(i => i.status === 'pending') : null
```

Reset the queue position when the media type changes, so switching doesn't land on an index
from the other list:

```tsx
const [pagedForMedia, setPagedForMedia] = useState(media)
if (pagedForMedia !== media) { setPagedForMedia(media); setCurrentIdx(0) }
```

Render the toggle above the queue body:

```tsx
      <div className="mb-5 flex items-center gap-1 rounded-lg bg-[#101828] border border-[#1D2939] p-1 w-fit">
        {(['movies', 'shows'] as const).map(m => (
          <button
            key={m}
            onClick={() => setMedia(m)}
            className={`rounded-md px-3 py-1.5 text-xs font-medium capitalize transition-all
              ${media === m ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm' : 'text-[#667085] hover:text-[#D0D5DD]'}`}
          >
            {m}
          </button>
        ))}
      </div>
```

`pending` already filters `status === 'pending'`, so `plexTheme` shows are excluded with no
extra code — the manual queue and the auto-download worker agree on what counts as
outstanding.

- [ ] **Step 4: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS — including `queue-race.test.tsx` **unmodified**.

- [ ] **Step 5: Commit**

```bash
git add -A src/Themearr.Web/src/app/queue
git commit -m "feat: Queue Movies|Shows toggle"
```

---

### Task 7: Settings show-library selector

**Files:**
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`
- Test: `src/Themearr.Web/src/app/settings-show-libraries.test.tsx` (create)

**Interfaces:**
- Consumes: `settingsApi.get`/`save` with `selectedShowLibraries` (Task 1), `setupApi.plexLibraries`

- [ ] **Step 1: Write the failing test** (`settings-show-libraries.test.tsx`)

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const SettingsPage = (await import('@/app/settings/page')).default

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k' } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://p', urls: ['http://p'] }],
    selectedLibraries: { srv1: ['1'] },
    selectedShowLibraries: {},
    pathMappings: [], libraryPaths: [],
    advanced: { maxSearchDirs: 20000, searchDepth: 4 },
    autoDownload: false, autoSync: false, lastAutoSyncAt: '',
  } as never)
  vi.mocked(api.setupApi.plexLibraries).mockResolvedValue({
    libraries: { srv1: [
      { key: '1', title: 'Movies', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

describe('Settings show-library selector', () => {
  it('lists only show-type libraries', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText(/Show libraries/i)).toBeTruthy())

    expect(screen.getByLabelText(/TV Shows/i)).toBeTruthy()
    // 'Movies' is a movie-type library and belongs to the existing selector, not this one.
    expect(screen.queryByLabelText(/^Movies$/)).toBeNull()
  })

  it('saves the selection as selectedShowLibraries', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText(/TV Shows/i)).toBeTruthy())

    await user.click(screen.getByLabelText(/TV Shows/i))
    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))

    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalled())
    const payload = vi.mocked(api.settingsApi.save).mock.calls[0][0] as Record<string, unknown>
    expect(payload.selectedShowLibraries).toEqual({ srv1: ['3'] })
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/settings-show-libraries.test.tsx`
Expected: FAIL — there is no "Show libraries" section.

- [ ] **Step 3: Add the section**

Add to the Settings page, in the Plex area near the existing connection panel. Load the
library list with the same `setupApi.plexLibraries(servers)` call the setup wizard uses,
then render only `type === 'show'` entries:

```tsx
const [showLibs, setShowLibs] = useState<Record<string, PlexLibrary[]>>({})
const [selectedShowLibs, setSelectedShowLibs] = useState<Record<string, string[]>>({})
const [savingShowLibs, setSavingShowLibs] = useState(false)
const [showLibsSaved, setShowLibsSaved] = useState(false)
const [showLibsError, setShowLibsError] = useState('')

function toggleShowLib(serverId: string, key: string) {
  setShowLibsSaved(false)
  setSelectedShowLibs(prev => {
    const cur = prev[serverId] ?? []
    return { ...prev, [serverId]: cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key] }
  })
}

async function saveShowLibraries() {
  setSavingShowLibs(true)
  setShowLibsError('')
  try {
    // Send the whole settings payload the page already holds, with the show selection
    // replaced — the endpoint takes one object, and omitting a field it does write
    // unconditionally would clear it.
    await settingsApi.save({ ...currentSettingsPayload(), selectedShowLibraries: selectedShowLibs })
    setShowLibsSaved(true)
  } catch (e) {
    setShowLibsError((e as Error)?.message || 'Could not save the show libraries.')
  } finally {
    setSavingShowLibs(false)
  }
}
```

`currentSettingsPayload()` is whatever the page already builds for its existing save action
— reuse it rather than assembling a second one, so the two saves cannot disagree. Populate
`selectedShowLibs` from `settingsApi.get()`'s `selectedShowLibraries` in the page's existing
settings-load effect, and `showLibs` from `setupApi.plexLibraries(selectedServers)`.
(`Settings.selectedShowLibraries` was already added to `types.ts` in Task 2.)

Markup — each checkbox needs a label association so the test's `getByLabelText` works:

```tsx
<div className="space-y-3">
  <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">Show libraries</p>
  <p className="text-sm text-[#667085]">
    Themearr only looks for show themes in the libraries you pick here. Leave them all
    unticked to keep shows switched off.
  </p>
  {Object.entries(showLibs).map(([serverId, libs]) =>
    libs.filter(l => l.type === 'show').map(l => (
      <label key={`${serverId}:${l.key}`} className="flex items-center gap-2 text-sm text-[#D0D5DD]">
        <input
          type="checkbox"
          checked={(selectedShowLibs[serverId] ?? []).includes(l.key)}
          onChange={() => toggleShowLib(serverId, l.key)}
        />
        {l.title}
      </label>
    )))}
  <Button size="sm" onClick={saveShowLibraries} loading={savingShowLibs}>Save show libraries</Button>
  {showLibsSaved && <p className="text-xs text-[#12B76A]">Saved ✓</p>}
  {showLibsError && <p className="text-xs text-[#FDA29B]">{showLibsError}</p>}
</div>
```

`Settings.selectedShowLibraries` already exists on the type from Task 2, so the payload
typechecks without further changes here.

- [ ] **Step 4: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS — including `settings-load.test.tsx` and `settings-plex-url.test.tsx`
**unmodified**.

- [ ] **Step 5: Commit**

```bash
git add -A src/Themearr.Web/src
git commit -m "feat: Settings show-library selector"
```

---

## Final verification

- [ ] `dotnet test tests/Themearr.API.Tests` — green, 0 warnings.
- [ ] `cd src/Themearr.Web && npm test && npm run lint && npx tsc --noEmit` — all clean.
- [ ] **No existing test file was modified.** Run `git diff --stat main -- src/Themearr.Web/src/app/*.test.tsx` and confirm only new files appear. A changed existing test means the generalization altered movie behaviour.
- [ ] Boot the app and click through: `/shows` renders, the nav entry appears between Movies and History, the Queue toggle switches lists, Settings lists only show-type libraries.
- [ ] Manual (maintainer's box, live Plex): select a show library in Settings, hit **Sync shows**, confirm a Plex-Pass-themed show shows the PLEX badge and a themeless one shows as pending; download a theme for the themeless one and watch it flip to downloaded.

## Self-review notes

- **Spec coverage:** backend `selectedShowLibraries` incl. the non-clobbering rule (Task 1); adapter + types + `showsApi` (Task 2); SearchModal generalization (Task 3); MediaGrid generalization with the `plexTheme` chip, badge and "Download anyway" (Task 4); Shows page, nav, sync-via-task-registry and the opt-in empty state (Task 5); Queue toggle incl. the non-persistence rule (Task 6); Settings selector (Task 7).
- **Type consistency:** `MediaStatus`, `MediaItem`, `Show`, `ShowStats`, `MediaAdapter`, `moviesAdapter`/`showsAdapter`, `MediaGrid`'s `{ items, adapter, onUpdated, emptyDescription }` and `SearchModal`'s `{ item, adapter, onClose, onDownloaded }` are used identically across tasks. `showsApi.ignoreShow`/`unignoreShow` are named for the API surface; the adapter exposes them as `ignore`/`unignore`.
- **Movie behaviour preserved:** no movie route changes, no movie test edits, and the windowing/staleness/in-flight logic is moved rather than rewritten. Tasks 3, 4, 6 and 7 each gate on the existing suite passing unmodified.
- **Not in this plan:** webhook show-sync trigger; show auto-download debug endpoint (`GetDiagnostics()` stays unreferenced through Phase 1); Sonarr; ThemerrDB.
- **Known risk:** Task 7 depends on the Settings page's existing payload builder, which this plan does not reproduce (it is 960 lines and the shape is already correct). If no single reusable builder exists there, extract one as the first step of Task 7 rather than assembling a second payload — two builders that can disagree is exactly how the omitted-field wipe in Task 1 would get triggered from inside our own frontend.
