# Dashboard Show Stats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show TV show coverage on the dashboard, so an operator with shows enabled can see how they're doing without visiting the Shows page.

**Architecture:** A second `useResource` call to `showsApi.stats()` — an endpoint that has existed since 1c and has never had a caller — renders a Shows block below the existing movie content. Frontend-only; no API or schema changes.

**Tech Stack:** React 19, Vite, Tailwind, Vitest + Testing Library.

## Global Constraints

- **Frontend only.** No files under `src/Themearr.API` may change. Confirm with `git diff --stat main -- src/Themearr.API` returning nothing.
- **The movies section keeps every value and panel it has.** The single permitted change is its heading: `Library coverage` → `Movie coverage`.
- **The Shows block renders last**, below the two "recent" panels — never between the movie tiles and those panels.
- **Gate the block on `showStats.total > 0`.** A movie-only dashboard must be visually unchanged.
- **Show coverage is `(downloaded + plexTheme) / total`** — computed server-side (`Database.cs:797`); the UI renders it and must caption it **"N of M shows covered"**, never "downloaded".
- **All four show tiles link to `/shows`**, including Pending. The queue's media toggle is component state, so `/queue` would land on movies.
- Existing dashboard tests must pass **unmodified**. Run `npm test`, `npm run lint`, `npx tsc --noEmit` in `src/Themearr.Web` after every task; lint must stay at **0 errors and 3 warnings** (three pre-existing, in `login/page.tsx` and `lib/auth.tsx`).

---

### Task 1: Shows section on the dashboard

**Files:**
- Modify: `src/Themearr.Web/src/app/dashboard/page.tsx`
- Test: `src/Themearr.Web/src/app/dashboard-show-stats.test.tsx` (create)

**Interfaces:**
- Consumes: `showsApi.stats()` → `Promise<ShowStats>` where `ShowStats` is `{ total, downloaded, plexTheme, pending, ignored, coverage }` (already in `lib/types.ts`), and `useResource<T>(fetcher) => { data, error, loading, retry }`.
- Produces: a `coverageColorFor(pct: number): string` helper used by both coverage bars.

- [ ] **Step 1: Write the failing test** (`dashboard-show-stats.test.tsx`)

```tsx
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const DashboardPage = (await import('@/app/dashboard/page')).default

const movieStats = {
  total: 1451, downloaded: 1264, pending: 187, ignored: 4,
  coverage: 87.1, addedThisWeek: 12, recentActivity: [], recentlyAdded: [],
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.statsApi.get).mockResolvedValue(movieStats as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><DashboardPage /></AuthProvider></MemoryRouter>)
}

/** The Shows block is a labelled region so assertions can be scoped to it. */
const showsSection = () => within(screen.getByRole('region', { name: 'Shows' }))

describe('Dashboard show stats', () => {
  it('shows nothing about shows on a movie-only install', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 0, downloaded: 0, plexTheme: 0, pending: 0, ignored: 0, coverage: 0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByText(/Movie coverage/i)).toBeTruthy())

    expect(screen.queryByRole('region', { name: 'Shows' })).toBeNull()
    expect(screen.queryByText(/Plex theme/i)).toBeNull()
  })

  it('renders show coverage and tiles once shows exist', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 253, downloaded: 9, plexTheme: 153, pending: 91, ignored: 0, coverage: 64.0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shows' })).toBeTruthy())

    const shows = showsSection()
    expect(shows.getByText(/Show coverage/i)).toBeTruthy()
    expect(shows.getByText('64%')).toBeTruthy()
    // "covered", not "downloaded" — a plexTheme show counts toward the bar.
    expect(shows.getByText(/162 of 253 shows covered/i)).toBeTruthy()
    expect(shows.getByText('Plex theme')).toBeTruthy()
    expect(shows.getByText('153')).toBeTruthy()
    expect(shows.getByText('91')).toBeTruthy()
  })

  /** /queue would land on the movies queue — its media toggle is component state. */
  it('points every show tile at /shows', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 253, downloaded: 9, plexTheme: 153, pending: 91, ignored: 2, coverage: 64.0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shows' })).toBeTruthy())

    // Scoped: the movie tiles carry three of these same four labels, so an unscoped
    // getByText would match two elements and throw.
    for (const label of ['Pending', 'Downloaded', 'Plex theme', 'Ignored']) {
      const tile = showsSection().getByText(label).closest('a')
      expect(tile?.getAttribute('href')).toBe('/shows')
    }
  })

  it('keeps the movie numbers untouched', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 253, downloaded: 9, plexTheme: 153, pending: 91, ignored: 0, coverage: 64.0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByText(/Movie coverage/i)).toBeTruthy())

    expect(screen.getByText('87.1%')).toBeTruthy()
    expect(screen.getByText(/1264 of 1451 movies/i)).toBeTruthy()
    expect(screen.getByText('This week')).toBeTruthy()   // movie-only tile, still there
  })
})
```

Three of the four tile labels — `Pending`, `Downloaded`, `Ignored` — appear in **both** the
movie and show tile rows, so an unscoped `getByText` matches two elements and throws. Every
show assertion is therefore scoped through `showsSection()`, which is why Step 7 renders the
block as a labelled `<section>`: it gives the test a stable, semantic handle instead of a
class selector, and gives the page a proper landmark.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/dashboard-show-stats.test.tsx`
Expected: FAIL — there is no "Movie coverage" heading (it currently reads "Library coverage") and no Shows section.

- [ ] **Step 3: Import the show stats API**

In `dashboard/page.tsx`, widen the API import. No type import is needed — `showsApi.stats()`
is already typed as `Promise<ShowStats>`, so `showStats` is inferred:

```tsx
import { showsApi, statsApi } from '@/lib/api'
```

- [ ] **Step 4: Fetch show stats alongside movie stats**

Immediately after the existing `useResource` call (line 14):

```tsx
  // Supplementary: the dashboard must render without it. Its own useResource keeps
  // "failed" distinct from "empty", so a failure can never render as 0% coverage.
  // This endpoint has existed since 1c and had no caller until now.
  const { data: showStats } = useResource(useCallback(() => showsApi.stats(), []))
```

- [ ] **Step 5: Share the coverage colour between both bars**

Replace the single-use constant (line 39):

```tsx
  const coverageColor = stats.coverage >= 80 ? '#12B76A' : stats.coverage >= 40 ? '#F79009' : '#BB0000'
```

with a helper plus its movie-side use — there are two coverage bars now:

```tsx
  const coverageColorFor = (pct: number) =>
    pct >= 80 ? '#12B76A' : pct >= 40 ? '#F79009' : '#BB0000'
  const coverageColor = coverageColorFor(stats.coverage)
```

- [ ] **Step 6: Rename the movies heading**

The dashboard's `total`, `downloaded`, `pending`, `ignored` and `addedThisWeek` are all
movie-scoped, so "Library coverage" was already imprecise; a second coverage figure below it
would make it wrong. Change line 49's text only:

```tsx
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider mb-1">Movie coverage</p>
```

- [ ] **Step 7: Render the Shows block**

Insert **after** the closing `</div>` of the bottom-panels grid and **before** the closing
`</div>` of `space-y-6` — i.e. as the last child, below both "recent" panels. Those panels
are movie-only, so placing Shows above them would leave two unlabelled movie panels sitting
under a Shows heading.

```tsx
        {/* ── Shows ───────────────────────────────────────────────────────
            Only when shows actually exist. A movie-only dashboard is unchanged,
            rather than carrying an empty block that implies something is broken. */}
        {showStats && showStats.total > 0 && (
          // A labelled landmark, not a bare div: three of the four tile labels also exist
          // in the movie row above, so this is what lets tests (and a screen reader)
          // tell the two apart.
          <section aria-label="Shows" className="space-y-3">
            <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-5">
              <div className="flex items-end justify-between mb-3">
                <div>
                  <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider mb-1">Show coverage</p>
                  <p className="text-4xl font-bold" style={{ color: coverageColorFor(showStats.coverage) }}>
                    {showStats.coverage}%
                  </p>
                </div>
                {/* "covered", not "downloaded": a show Plex already themes counts toward
                    the bar, and the Plex theme tile below breaks that out. */}
                <p className="text-sm text-[#667085] pb-1">
                  {showStats.downloaded + showStats.plexTheme} of {showStats.total} shows covered
                </p>
              </div>
              <div className="h-2 w-full rounded-full bg-[#1D2939] overflow-hidden">
                <div
                  className="h-full rounded-full transition-all duration-700"
                  style={{
                    width: `${Math.min(showStats.coverage, 100)}%`,
                    backgroundColor: coverageColorFor(showStats.coverage),
                  }}
                />
              </div>
            </div>

            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              {[
                { label: 'Pending',    value: showStats.pending,    color: '#F79009' },
                { label: 'Downloaded', value: showStats.downloaded, color: '#12B76A' },
                { label: 'Plex theme', value: showStats.plexTheme,  color: '#98A2B3' },
                { label: 'Ignored',    value: showStats.ignored,    color: '#475467' },
              ].map(({ label, value, color }) => (
                // All four go to /shows, including Pending: the queue's Movies|Shows
                // toggle is component state, so /queue would land on movies.
                <Link
                  key={label}
                  to="/shows"
                  className="rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-4 hover:border-[#344054] transition-colors"
                >
                  <p className="text-xs text-[#667085] mb-1">{label}</p>
                  <p className="text-2xl font-bold" style={{ color }}>{value}</p>
                </Link>
              ))}
            </div>
          </section>
        )}
```

- [ ] **Step 8: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS — new tests green, existing dashboard tests green **and unmodified**, lint 0 errors / 3 warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Themearr.Web/src/app/dashboard/page.tsx src/Themearr.Web/src/app/dashboard-show-stats.test.tsx
git commit -m "feat: show TV show coverage on the dashboard"
```

---

### Task 2: Surface a show-stats failure instead of hiding it

If show stats fail while movie stats succeed, the block simply vanishes — indistinguishable
from "you have no shows". That is the silent-empty pattern this project has shipped twice;
it gets a notice.

**Files:**
- Modify: `src/Themearr.Web/src/app/dashboard/page.tsx`
- Test: `src/Themearr.Web/src/app/dashboard-show-stats.test.tsx` (append)

**Interfaces:**
- Consumes: `useResource`'s `error` field from Task 1's show-stats call.

- [ ] **Step 1: Write the failing tests** (append inside the existing `describe`)

```tsx
  it('says so when show stats fail but the rest of the dashboard loaded', async () => {
    vi.mocked(api.showsApi.stats).mockRejectedValue(new Error('Service Unavailable'))

    renderPage()
    await waitFor(() => expect(screen.getByText(/Movie coverage/i)).toBeTruthy())

    // Hiding the block here is indistinguishable from "no shows" — say what happened.
    await waitFor(() => expect(screen.getByText(/Couldn't load show stats/i)).toBeTruthy())
    expect(screen.getByText(/Service Unavailable/i)).toBeTruthy()
  })

  it('does not add a show-stats notice when the whole dashboard failed', async () => {
    vi.mocked(api.statsApi.get).mockRejectedValue(new Error('down'))
    vi.mocked(api.showsApi.stats).mockRejectedValue(new Error('down'))

    renderPage()

    // The existing whole-page error screen already covers this.
    await waitFor(() => expect(screen.getByText(/Couldn't load the dashboard/i)).toBeTruthy())
    expect(screen.queryByText(/Couldn't load show stats/i)).toBeNull()
  })
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/Themearr.Web && npx vitest run src/app/dashboard-show-stats.test.tsx`
Expected: FAIL on the first — no "Couldn't load show stats" text exists. The second passes already (the early error-screen return means nothing else renders), which is the correct starting state.

- [ ] **Step 3: Capture the show-stats error**

Change Task 1's show-stats call to also take its error:

```tsx
  const { data: showStats, error: showStatsError } = useResource(useCallback(() => showsApi.stats(), []))
```

- [ ] **Step 4: Render the notice**

Directly above the `{showStats && showStats.total > 0 && (` block from Task 1:

```tsx
        {/* Only reachable when the movie stats loaded — a total outage returns the error
            screen above, so this never double-reports. Shown rather than swallowed: a
            missing block is indistinguishable from "no shows", and that ambiguity is
            exactly what hid two earlier bugs. */}
        {showStatsError && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">Couldn&apos;t load show stats: {showStatsError}</p>
          </div>
        )}
```

- [ ] **Step 5: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS, lint 0 errors / 3 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.Web/src/app/dashboard/page.tsx src/Themearr.Web/src/app/dashboard-show-stats.test.tsx
git commit -m "fix: report a show-stats failure on the dashboard instead of hiding the section"
```

---

## Final verification

- [ ] `cd src/Themearr.Web && npm test && npm run lint && npx tsc --noEmit` — clean, 0 lint errors, 3 pre-existing warnings.
- [ ] `git diff --stat main -- src/Themearr.API` returns nothing — this change is frontend-only.
- [ ] `git diff --stat main --diff-filter=M -- '*.test.tsx'` returns nothing — no existing test modified.
- [ ] `dotnet test tests/Themearr.API.Tests` — unchanged and green.
- [ ] Manual (maintainer's box): the dashboard shows **Movie coverage** and, below the recent panels, **Show coverage** with the Plex theme count matching what the Shows page reports.

## Self-review notes

- **Spec coverage:** Shows block with coverage bar and four tiles (Task 1 Step 7); gated on `total > 0` (Step 7); placed last, below the recent panels (Step 7); `Library coverage` → `Movie coverage` (Step 6); "N of M shows covered" wording (Step 7); all tiles → `/shows` (Step 7); failure notice with the movie-stats-succeeded condition (Task 2).
- **Type consistency:** `showStats` / `showStatsError` from one `useResource<ShowStats>` call, and `coverageColorFor(pct: number)` used by both bars, are named identically across both tasks. Task 2 Step 3 widens Task 1 Step 4's destructuring rather than adding a second call.
- **Not in this plan:** a "This week" tile for shows (needs `GetShowStats` to read history — backend); labelling show entries on the History page (`GetThemeHistory` is unfiltered, so they already appear there unlabelled — separate page, separate change); `?media=shows` deep-linking into the show queue.
