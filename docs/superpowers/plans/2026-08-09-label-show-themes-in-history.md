# Show Themes in Recent Downloads and History — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Include show themes in the dashboard's Recent downloads, and make a show entry distinguishable from a movie entry there and on the History page.

**Architecture:** One query change server-side (`recentActivity` stops filtering to movies and starts carrying `media_type`), then a single shared badge component rendered from both pages. `GetThemeHistory` already returns `mediaType`, so History needs no server work.

**Tech Stack:** .NET 10 + xUnit; React 19, Vite, Tailwind, Vitest + Testing Library.

## Global Constraints

- **`addedThisWeek` stays movie-scoped.** It renders in the movie tile row under the "Movie coverage" heading, so widening it would contradict the labelling shipped in v1.48.0. A show download must not increment it.
- **Badge shows only.** Movie rows render exactly as they do today. At ~1437 movies to ~100 shows, badging every row would add a pill to ~94% of a long list that never needed one.
- **One shared badge component, used by both pages.** Two near-identical copies of this markup is precisely how the queue ended up telling someone triaging shows they had "91 movies left".
- **`mediaType` is typed `string`, not a `'movie' | 'show'` union** — the value crosses the wire, so a union would be a compile-time claim the runtime cannot honour. Render condition is `entry.mediaType === 'show'`.
- **No null handling.** `media_type` is `TEXT NOT NULL DEFAULT 'movie'` (`Database.cs:109`) and the 1b migration backfilled existing rows.
- Keep the `movieId` / `movieTitle` / `movieYear` field names. Historical and inaccurate for shows, but renaming is a wire-format change across two pages for no user-visible gain.
- Run `dotnet test tests/Themearr.API.Tests` after the backend task; `npm test`, `npm run lint`, `npx tsc --noEmit` in `src/Themearr.Web` after the frontend task. Lint must stay at **0 errors and 3 warnings** (three pre-existing, in `login/page.tsx` and `lib/auth.tsx`).

---

### Task 1: Recent downloads includes show themes

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (the `recentActivity` query inside `GetStats`)
- Modify: `tests/Themearr.API.Tests/ThemeHistoryMediaTypeTests.cs` (one existing test — see Step 1)

**Interfaces:**
- Produces: `GetStats().RecentActivity` rows gain `["mediaType"]` (`string`) and are no longer filtered by media type.

- [ ] **Step 1: Update the existing test that asserts the old behaviour**

`ThemeHistoryMediaTypeTests.Dashboard_stats_ignore_show_history` currently pins the exact
behaviour being changed. **This is a deliberate behaviour change, not a regression** — half
the test stays (the `addedThisWeek` scoping) and half inverts (recent activity now includes
shows). Replace that whole test, including its doc comment, with:

```csharp
    /// <summary>
    /// The dashboard's coverage/total/pending come from the movies table, so addedThisWeek —
    /// which sits in the movie tile row — must stay movies-only. Recent downloads is a
    /// chronological activity feed rather than a movie statistic, so it carries both and
    /// labels them.
    /// </summary>
    [Fact]
    public void Dashboard_this_week_stays_movie_only_but_recent_downloads_carries_both()
    {
        using var dir = new TempDir();
        var db = New(dir);

        db.AddThemeHistory("m1", "A Movie", 2001, "Theme", "http://x");
        db.AddThemeHistory("s1", "A Show",  2010, "Intro", "http://y", "show");

        var stats = db.GetStats();

        // Movie-scoped: a show download must not inflate a number shown beside "Movie coverage".
        Assert.Equal(1, stats.AddedThisWeek);

        // …but the activity feed shows both, each carrying which it is.
        var show  = stats.RecentActivity.Single(a => (string)a["movieId"]! == "s1");
        var movie = stats.RecentActivity.Single(a => (string)a["movieId"]! == "m1");
        Assert.Equal("show",  show["mediaType"]);
        Assert.Equal("movie", movie["mediaType"]);
    }

    /// <summary>Most recent first, regardless of media type — it is a time-ordered feed.</summary>
    [Fact]
    public void Recent_downloads_are_ordered_by_recency_across_media_types()
    {
        using var dir = new TempDir();
        var db = New(dir);

        db.AddThemeHistory("m1", "Older Movie", 2001, "Theme", "http://x");
        db.AddThemeHistory("s1", "Newer Show",  2010, "Intro", "http://y", "show");

        var recent = db.GetStats().RecentActivity;

        Assert.Equal("s1", (string)recent[0]["movieId"]!);
        Assert.Equal("m1", (string)recent[1]["movieId"]!);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ThemeHistoryMediaTypeTests"`
Expected: FAIL — `RecentActivity` contains no `s1` row (it is filtered to movies) and no row carries a `mediaType` key.

- [ ] **Step 3: Widen the query**

In `Database.cs`, inside `GetStats()`, replace the `recentActivity` block's comment, SQL and
row projection:

```csharp
        // Last 5 downloaded themes, movies and shows alike. Unlike the numbers above this is
        // a chronological activity feed rather than a movie statistic, so it is not scoped by
        // media type — each row carries its own so the UI can label it.
        var recentActivity = new List<Dictionary<string, object?>>();
        conn.Query(
            "SELECT id, movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at, media_type FROM theme_history ORDER BY id DESC LIMIT 5",
            r =>
            {
                while (r.Read())
                    recentActivity.Add(new Dictionary<string, object?>
                    {
                        ["id"]           = r.GetInt64(0),
                        ["movieId"]      = r.GetString(1),
                        ["movieTitle"]   = r.GetString(2),
                        ["movieYear"]    = r.IsDBNull(3) ? null : r.GetInt32(3),
                        ["themeTitle"]   = r.IsDBNull(4) ? null : r.GetString(4),
                        ["sourceUrl"]    = r.IsDBNull(5) ? null : r.GetString(5),
                        ["downloadedAt"] = r.GetString(6),
                        ["mediaType"]    = r.GetString(7),
                    });
            });
```

Leave the `addedThisWeek` query above it **exactly as it is**, including its
`AND media_type = 'movie'`.

- [ ] **Step 4: Run the full backend suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/ThemeHistoryMediaTypeTests.cs
git commit -m "feat: recent downloads carries show themes and their media type"
```

---

### Task 2: Label show entries on both pages

**Files:**
- Modify: `src/Themearr.Web/src/lib/types.ts` (`HistoryEntry`)
- Modify: `src/Themearr.Web/src/components/ui/index.tsx` (new `ShowBadge`)
- Modify: `src/Themearr.Web/src/app/dashboard/page.tsx`, `src/Themearr.Web/src/app/history/page.tsx`
- Test: `src/Themearr.Web/src/app/show-theme-history-badge.test.tsx` (create)

**Interfaces:**
- Consumes: `HistoryEntry.mediaType` from Task 1's API change.
- Produces: `<ShowBadge mediaType={string} />` — renders a "Show" pill when `mediaType === 'show'`, otherwise nothing.

- [ ] **Step 1: Write the failing test** (`show-theme-history-badge.test.tsx`)

```tsx
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

const entries = [
  { id: 2, movieId: 's1', movieTitle: 'Beyond Paradise', movieYear: 2023,
    themeTitle: 'Beyond Paradise Theme', sourceUrl: null,
    downloadedAt: '2026-08-09T00:00:00Z', mediaType: 'show' },
  { id: 1, movieId: 'm1', movieTitle: 'Project Hail Mary', movieYear: 2026,
    themeTitle: 'Life is Reason', sourceUrl: null,
    downloadedAt: '2026-08-08T00:00:00Z', mediaType: 'movie' },
]

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.showsApi.stats).mockResolvedValue({
    total: 0, downloaded: 0, plexTheme: 0, pending: 0, ignored: 0, coverage: 0,
  } as never)
  vi.mocked(api.statsApi.get).mockResolvedValue({
    total: 10, downloaded: 10, pending: 0, ignored: 0, coverage: 100, addedThisWeek: 1,
    recentActivity: entries, recentlyAdded: [],
  } as never)
  vi.mocked(api.historyApi.get).mockResolvedValue(entries as never)
})

function renderPage(ui: React.ReactElement) {
  return render(<MemoryRouter><AuthProvider>{ui}</AuthProvider></MemoryRouter>)
}

/**
 * Scopes assertions to one entry's title line. getByText matches the <p> exactly even
 * though it also contains the year span and the badge: Testing Library's getNodeText reads
 * only an element's DIRECT text children, and both of those are nested in spans.
 */
const titleLine = (title: string) => within(screen.getByText(title))

describe('show themes are labelled in download history', () => {
  it('badges the show row and not the movie row on the dashboard', async () => {
    const { default: DashboardPage } = await import('@/app/dashboard/page')
    renderPage(<DashboardPage />)

    await waitFor(() => expect(screen.getByText('Beyond Paradise')).toBeTruthy())

    expect(titleLine('Beyond Paradise').getByText('Show')).toBeTruthy()
    expect(titleLine('Project Hail Mary').queryByText('Show')).toBeNull()
  })

  it('badges the show row and not the movie row on the History page', async () => {
    const { default: HistoryPage } = await import('@/app/history/page')
    renderPage(<HistoryPage />)

    await waitFor(() => expect(screen.getByText('Beyond Paradise')).toBeTruthy())

    expect(titleLine('Beyond Paradise').getByText('Show')).toBeTruthy()
    expect(titleLine('Project Hail Mary').queryByText('Show')).toBeNull()
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/show-theme-history-badge.test.tsx`
Expected: FAIL — no element with the text "Show" exists on either page.

- [ ] **Step 3: Add `mediaType` to the history type**

In `src/lib/types.ts`, add to `HistoryEntry`:

```ts
  /**
   * "movie" | "show". Typed as string rather than a union: it crosses the wire, so a
   * union would be a compile-time claim the runtime cannot honour.
   */
  mediaType: string
```

`DashboardStats.recentActivity` is already `HistoryEntry[]`, so this covers both pages.

- [ ] **Step 4: Add the shared badge**

In `src/components/ui/index.tsx`, alongside the other small components:

```tsx
/**
 * Marks a history entry as a show. Renders nothing for movies: at roughly 1437 movies to
 * 100 shows, badging every row would add a pill to ~94% of a long list that never needed
 * one. Shared by the dashboard and History because two copies of a media-type label is
 * exactly how the queue came to tell someone triaging shows they had "91 movies left".
 */
export function ShowBadge({ mediaType }: { mediaType: string }) {
  if (mediaType !== 'show') return null
  return (
    <span className="ml-1.5 rounded bg-[#344054] px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[#D0D5DD] align-middle">
      Show
    </span>
  )
}
```

- [ ] **Step 5: Render it on the dashboard**

In `src/app/dashboard/page.tsx`, add `ShowBadge` to the existing `@/components/ui` import,
then in the Recent downloads row put it after the year:

```tsx
                      <p className="text-sm font-medium text-[#F9FAFB] truncate">
                        {entry.movieTitle}
                        {entry.movieYear && <span className="ml-1.5 font-normal text-[#667085]">({entry.movieYear})</span>}
                        <ShowBadge mediaType={entry.mediaType} />
                      </p>
```

- [ ] **Step 6: Render it on the History page**

In `src/app/history/page.tsx`, add `ShowBadge` to the existing `@/components/ui` import,
then in the title row:

```tsx
                  <p className="text-sm font-medium text-[#F9FAFB]">
                    {entry.movieTitle}
                    {entry.movieYear && (
                      <span className="ml-1.5 font-normal text-[#667085]">({entry.movieYear})</span>
                    )}
                    <ShowBadge mediaType={entry.mediaType} />
                  </p>
```

- [ ] **Step 7: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS — new tests green, existing dashboard and history tests green **and unmodified**, lint 0 errors / 3 warnings.

If an existing test fails with `Cannot read properties of undefined (reading 'then')`, that
is the known mock artifact — a page fetching an endpoint the test file does not mock. Add
the missing `mockResolvedValue` to that file's `beforeEach`; do not change any assertion.

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.Web/src
git commit -m "feat: label show themes in recent downloads and history"
```

---

## Final verification

- [ ] `dotnet test tests/Themearr.API.Tests` — green, 0 warnings.
- [ ] `cd src/Themearr.Web && npm test && npm run lint && npx tsc --noEmit` — clean, 0 lint errors, 3 pre-existing warnings.
- [ ] `git diff --stat main --diff-filter=M -- '*.test.tsx'` — no existing **frontend** test modified. (One existing **backend** test is deliberately rewritten in Task 1; that is the behaviour change itself.)
- [ ] Manual (maintainer's box): a recently downloaded show appears in the dashboard's Recent downloads with a **Show** pill, and the same pill appears against show entries on the History page while movie entries are unchanged.

## Self-review notes

- **Spec coverage:** `recentActivity` unfiltered and carrying `mediaType` (Task 1 Step 3); `addedThisWeek` left movie-scoped (Task 1 Step 3, pinned by the test in Step 1); `HistoryEntry.mediaType` typed `string` (Task 2 Step 3); shared badge, shows only (Task 2 Step 4); rendered on both pages (Steps 5–6); recency ordering across media types (Task 1 Step 1's second test).
- **Type consistency:** `mediaType` is the key in the API row dict, the field on `HistoryEntry`, and the prop on `ShowBadge` — one name throughout. `ShowBadge` takes `mediaType: string` and is imported from `@/components/ui` at both call sites.
- **One existing backend test is rewritten**, deliberately: `Dashboard_stats_ignore_show_history` pinned the exact behaviour being changed. Its `addedThisWeek` half is preserved verbatim, because that scoping is *not* changing — only the recent-activity half inverts.
- **Not in this plan:** a Movies/Shows filter on History (badge makes shows recognisable, not findable — its own layout decision); renaming the `movie*` history fields; a "This week" tile for shows.
