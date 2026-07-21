# Frontend Failure Handling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop Themearr's web UI from reporting failures as success, and add the smallest test setup that can actually catch that class of bug.

**Architecture:** A `useResource` hook gives initial loads three states — loading, failed, loaded — so an empty list can no longer mean "we never found out". Three user-action sites are fixed inline, and one queue poll gets an in-flight guard. Vitest with jsdom and Testing Library renders the pages against a failing API and asserts the *absence* of their reassuring empty-state copy.

**Tech Stack:** React 19, Vite, TypeScript, Vitest, jsdom, @testing-library/react.

**Spec:** `docs/superpowers/specs/2026-07-21-frontend-failure-handling-design.md`

## Global Constraints

- **The rule that sorts every site:** a user-initiated action that fails must say so; a background poll that fails stays silent; an initial load that fails must show an error, never an empty state.
- **The five background-poll sites must keep failing silently** and must not be changed: `settings:95` (update status), `settings:294` (version re-check), `movies:45` (sync status), `system:87` (tasks), `login:39` (Plex PIN polling). Blanking a populated view over one dropped request is worse than a stale value — this was a deliberate earlier fix.
- **Only `settings:55` gates the Settings page.** `settings:56` (version display) and `settings:57` (is a RapidAPI key stored) are supplementary: their failure must not block the rest of Settings.
- **Page tests assert the ABSENCE of the reassuring copy**, not merely that an error appeared. A page can show an error banner and still render "All caught up!" underneath — that is the current bug wearing a hat.
- The exact strings that must not appear on failure: `All caught up!` (`app/queue/page.tsx:210`), `No downloads yet` (`app/history/page.tsx:69`), `No movies yet` (`components/movies/MovieGrid.tsx:114`).
- Existing empty-state copy is kept — it simply stops being reachable by failure.
- Do not add end-to-end browser tests, snapshot tests, or coverage thresholds.
- Frontend checks from `src/Themearr.Web`: `npx tsc --noEmit`, `npm run lint` (expect 0 errors and exactly 3 pre-existing warnings — 1 in `src/app/login/page.tsx`, 2 in `src/lib/auth.tsx`), `npm run build`.
- The backend suite has **265** tests and must stay green; this plan does not touch `src/Themearr.API`.

---

### Task 1: The test harness

Everything after this depends on it, so it lands first with a trivial test proving the setup works.

**Files:**
- Modify: `src/Themearr.Web/package.json`
- Modify: `src/Themearr.Web/vite.config.ts`
- Create: `src/Themearr.Web/src/test/setup.ts`
- Test: `src/Themearr.Web/src/test/harness.test.tsx`

**Interfaces:**
- Produces: `npm test` (runs `vitest run`), a jsdom environment with Testing Library matchers, and the `@` alias working inside tests

- [ ] **Step 1: Install the dev dependencies**

```bash
cd /Users/devlin/Documents/GitHub/themearr/src/Themearr.Web
npm install -D vitest jsdom @testing-library/react @testing-library/dom @testing-library/jest-dom
```

- [ ] **Step 2: Add the setup file**

Create `src/Themearr.Web/src/test/setup.ts`:

```ts
import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Each test renders a page; without this, the previous test's DOM is still
// mounted and queries match the wrong render.
afterEach(cleanup)
```

- [ ] **Step 3: Configure Vitest**

In `src/Themearr.Web/vite.config.ts`, change the import so the config accepts a `test` block:

```ts
import { defineConfig } from 'vitest/config'
```

and add this property to the config object, alongside `plugins`, `resolve`, `build` and `server`:

```ts
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Only our own tests; node_modules and the build output are excluded by default.
    include: ['src/**/*.test.{ts,tsx}'],
  },
```

Leave every existing property exactly as it is — `outDir: 'out'` in particular is the contract the release workflow and Dockerfile both depend on.

- [ ] **Step 4: Add the test script**

In `src/Themearr.Web/package.json`, add to `scripts`:

```json
    "test": "vitest run",
```

- [ ] **Step 5: Write a test that proves the harness works**

Create `src/Themearr.Web/src/test/harness.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react'
import { describe, it, expect } from 'vitest'

describe('test harness', () => {
  it('renders a component into jsdom', () => {
    render(<p>hello from jsdom</p>)

    expect(screen.getByText('hello from jsdom')).toBeInTheDocument()
  })

  it('resolves the @ alias', async () => {
    const api = await import('@/lib/api')

    expect(typeof api.getAuthToken).toBe('function')
  })
})
```

- [ ] **Step 6: Run it**

Run: `cd src/Themearr.Web && npm test`
Expected: PASS — 2 tests passed.

- [ ] **Step 7: Confirm nothing else broke**

Run: `cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build`
Expected: typecheck clean, lint 0 errors with the 3 pre-existing warnings, build succeeds into `out/`.

If lint now reports errors inside test files, add the test glob to the ESLint config the way the existing config handles other file groups — do not disable rules inline.

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.Web/package.json src/Themearr.Web/package-lock.json \
        src/Themearr.Web/vite.config.ts src/Themearr.Web/src/test/
git commit -m "test(web): add a Vitest and Testing Library harness"
```

---

### Task 2: `useResource`

**Files:**
- Create: `src/Themearr.Web/src/lib/useResource.ts`
- Test: `src/Themearr.Web/src/lib/useResource.test.ts`

**Interfaces:**
- Produces:
  ```ts
  function useResource<T>(fetcher: () => Promise<T>): {
    data: T | null
    error: string | null
    loading: boolean
    retry: () => void
  }
  ```
  `loading` is true until the first attempt settles. On failure `error` holds a message and `data` stays `null`. `retry()` clears the error and fetches again.

- [ ] **Step 1: Write the failing tests**

Create `src/Themearr.Web/src/lib/useResource.test.ts`:

```ts
import { renderHook, waitFor, act } from '@testing-library/react'
import { describe, it, expect, vi } from 'vitest'
import { useResource } from '@/lib/useResource'

describe('useResource', () => {
  it('starts loading, then exposes the data', async () => {
    const { result } = renderHook(() => useResource(() => Promise.resolve(['a', 'b'])))

    expect(result.current.loading).toBe(true)
    await waitFor(() => expect(result.current.loading).toBe(false))

    expect(result.current.data).toEqual(['a', 'b'])
    expect(result.current.error).toBeNull()
  })

  it('exposes an error and leaves data null when the fetch fails', async () => {
    const { result } = renderHook(() =>
      useResource(() => Promise.reject(new Error('boom'))))

    await waitFor(() => expect(result.current.loading).toBe(false))

    expect(result.current.error).not.toBeNull()
    // The whole point: a failure must not look like an empty result.
    expect(result.current.data).toBeNull()
  })

  it('retry clears the error and fetches again', async () => {
    let attempt = 0
    const fetcher = vi.fn(() => {
      attempt++
      return attempt === 1 ? Promise.reject(new Error('boom')) : Promise.resolve(['ok'])
    })
    const { result } = renderHook(() => useResource(fetcher))
    await waitFor(() => expect(result.current.error).not.toBeNull())

    act(() => result.current.retry())

    await waitFor(() => expect(result.current.data).toEqual(['ok']))
    expect(result.current.error).toBeNull()
    expect(fetcher).toHaveBeenCalledTimes(2)
  })

  it('ignores a slow first response that settles after a retry', async () => {
    // Without this guard a stale response can overwrite a newer one.
    let resolveFirst: (v: string[]) => void = () => {}
    let call = 0
    const fetcher = () => {
      call++
      return call === 1
        ? new Promise<string[]>(res => { resolveFirst = res })
        : Promise.resolve(['second'])
    }
    const { result } = renderHook(() => useResource(fetcher))

    act(() => result.current.retry())
    await waitFor(() => expect(result.current.data).toEqual(['second']))
    act(() => resolveFirst(['first']))

    await waitFor(() => expect(result.current.data).toEqual(['second']))
  })
})
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/Themearr.Web && npm test -- useResource`
Expected: FAIL — cannot resolve `@/lib/useResource`.

- [ ] **Step 3: Implement**

Create `src/Themearr.Web/src/lib/useResource.ts`:

```ts
import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * Loads a resource with three outcomes rather than two.
 *
 * The bug this exists to prevent: pages used `null` or `[]` to mean both "nothing
 * here" and "we never found out", so a failed request rendered as a reassuring
 * empty state — "No movies yet", "All caught up!" — and an outage was
 * indistinguishable from an empty library. Keeping `error` separate from `data`
 * makes that confusion unrepresentable.
 */
export function useResource<T>(fetcher: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [attempt, setAttempt] = useState(0)

  // Identifies the newest request, so a slow earlier one cannot overwrite it.
  const latest = useRef(0)
  const fetcherRef = useRef(fetcher)
  fetcherRef.current = fetcher

  useEffect(() => {
    const mine = ++latest.current
    setLoading(true)
    fetcherRef.current()
      .then(value => {
        if (mine !== latest.current) return
        setData(value)
        setError(null)
      })
      .catch((e: unknown) => {
        if (mine !== latest.current) return
        setError(e instanceof Error && e.message ? e.message : 'Request failed')
      })
      .finally(() => {
        if (mine === latest.current) setLoading(false)
      })
  }, [attempt])

  const retry = useCallback(() => {
    setError(null)
    setAttempt(a => a + 1)
  }, [])

  return { data, error, loading, retry }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/Themearr.Web && npm test -- useResource`
Expected: PASS — 4 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.Web/src/lib/useResource.ts src/Themearr.Web/src/lib/useResource.test.ts
git commit -m "feat(web): add useResource so a failed load cannot look empty"
```

---

### Task 3: The three pages that lie

**Files:**
- Modify: `src/Themearr.Web/src/app/movies/page.tsx`
- Modify: `src/Themearr.Web/src/app/history/page.tsx`
- Modify: `src/Themearr.Web/src/app/queue/page.tsx`
- Test: `src/Themearr.Web/src/app/pages-failure.test.tsx`

**Interfaces:**
- Consumes: `useResource` (Task 2)

These three currently render reassuring copy when their load fails. Change **only** the initial load in each; leave every poll alone.

- [ ] **Step 1: Write the failing tests**

Create `src/Themearr.Web/src/app/pages-failure.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('@/lib/api', () => ({
  getAuthToken: () => 'test-token',
  setAuthToken: vi.fn(),
  clearAuthToken: vi.fn(),
  moviesApi:   { list: vi.fn(), search: vi.fn(), download: vi.fn() },
  historyApi:  { get: vi.fn() },
  settingsApi: { get: vi.fn(), save: vi.fn() },
  syncApi:     { start: vi.fn(), status: vi.fn() },
  statsApi:    { get: vi.fn() },
  versionApi:  { get: vi.fn(), refresh: vi.fn() },
  systemApi:   { health: vi.fn(), tasks: vi.fn(), runTask: vi.fn() },
  setupApi:    { status: vi.fn(), logout: vi.fn() },
  rapidApiApi: { status: vi.fn(), save: vi.fn(), remove: vi.fn() },
  radarrApi:   { get: vi.fn(), save: vi.fn(), test: vi.fn() },
  apiKeyApi:   { get: vi.fn(), regenerate: vi.fn() },
  authApi:     { verify: vi.fn() },
}))

const api = await import('@/lib/api')

// The pages render inside AppShell, which needs a router and the auth context.
function renderPage(ui: React.ReactElement) {
  return render(<MemoryRouter>{ui}</MemoryRouter>)
}

beforeEach(() => {
  vi.clearAllMocks()
  // Everything a page might poll resolves harmlessly; only the load under test fails.
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
})

describe('a failed load never renders reassuring copy', () => {
  it('Movies does not claim the library is empty', async () => {
    vi.mocked(api.moviesApi.list).mockRejectedValue(new Error('server down'))
    const { default: MoviesPage } = await import('@/app/movies/page')

    renderPage(<MoviesPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByText(/No movies yet/i)).toBeNull()
  })

  it('History does not claim there are no downloads', async () => {
    vi.mocked(api.historyApi.get).mockRejectedValue(new Error('server down'))
    const { default: HistoryPage } = await import('@/app/history/page')

    renderPage(<HistoryPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByText(/No downloads yet/i)).toBeNull()
  })

  it('Queue does not claim everything is caught up', async () => {
    vi.mocked(api.moviesApi.list).mockRejectedValue(new Error('server down'))
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
    const { default: QueuePage } = await import('@/app/queue/page')

    renderPage(<QueuePage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByText(/All caught up/i)).toBeNull()
  })
})

describe('a successful empty load still shows the empty state', () => {
  it('Movies says the library is empty when it genuinely is', async () => {
    vi.mocked(api.moviesApi.list).mockResolvedValue([] as never)
    const { default: MoviesPage } = await import('@/app/movies/page')

    renderPage(<MoviesPage />)

    await waitFor(() => expect(screen.queryByText(/No movies yet/i)).not.toBeNull())
  })
})
```

The mocked module must export every name the pages import. Read `src/Themearr.Web/src/lib/api.ts` and include them all — a missing export fails at import time with an unhelpful error.

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/Themearr.Web && npm test -- pages-failure`
Expected: FAIL — the reassuring copy is present because the pages still treat a failure as empty.

- [ ] **Step 3: Fix the three loads**

In each page, replace the initial load with `useResource` and render three branches: loading, error (with a Retry button calling `retry()`), and loaded.

- `app/movies/page.tsx` — the load at line 16 (`try { setMovies(await moviesApi.list()) } catch { /* ignore */ }`). Leave the sync-status poll at line 45 exactly as it is.
- `app/history/page.tsx` — the load at line 23. The page's Refresh button should call `retry()`.
- `app/queue/page.tsx` — the load at line 34 (`.catch(() => setPending([]))`). Leave the download-status poll alone; Task 5 handles it.

Match each page's existing error styling — read a neighbouring section rather than inventing a new look. The error text must contain a word the tests match (`couldn't load`, `could not load`, or `failed`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/Themearr.Web && npm test -- pages-failure`
Expected: PASS — 4 tests passed.

- [ ] **Step 5: Verify the whole frontend**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build`
Expected: all tests pass, typecheck clean, lint 0 errors with 3 pre-existing warnings, build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.Web/src/app/movies/page.tsx src/Themearr.Web/src/app/history/page.tsx \
        src/Themearr.Web/src/app/queue/page.tsx src/Themearr.Web/src/app/pages-failure.test.tsx
git commit -m "fix(web): stop a failed load rendering as an empty library"
```

---

### Task 4: Settings and Dashboard loads

**Files:**
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`
- Modify: `src/Themearr.Web/src/app/dashboard/page.tsx`
- Test: `src/Themearr.Web/src/app/settings-load.test.tsx`

**Interfaces:**
- Consumes: `useResource` (Task 2)

**The important distinction here.** `settings:55` (`settingsApi.get()`) genuinely gates the page — the Library Source, API Key and RapidAPI sections all sit behind it, so a failure must show an error and a retry rather than an endless spinner. But `settings:56` (version) and `settings:57` (RapidAPI configured) are **supplementary**: if either fails, its own small area should say so while the rest of Settings stays usable. Applying the gating treatment to all three would trade one stranding bug for another.

`dashboard:13` (`statsApi.get()`) is a plain gating load — the page has nothing else.

- [ ] **Step 1: Write the failing tests**

Create `src/Themearr.Web/src/app/settings-load.test.tsx`, reusing the same `vi.mock('@/lib/api', …)` block as `pages-failure.test.tsx` (copy it — the mock is per-file):

```tsx
// … same vi.mock('@/lib/api', …) block and beforeEach as pages-failure.test.tsx …

describe('Settings load failures', () => {
  it('shows an error with a retry instead of spinning forever', async () => {
    vi.mocked(api.settingsApi.get).mockRejectedValue(new Error('server down'))
    const { default: SettingsPage } = await import('@/app/settings/page')

    renderPage(<SettingsPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeNull()
  })

  it('a failed version check does not block the rest of Settings', async () => {
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false, autoSync: false } as never)
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
    vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
    vi.mocked(api.versionApi.get).mockRejectedValue(new Error('github down'))
    const { default: SettingsPage } = await import('@/app/settings/page')

    renderPage(<SettingsPage />)

    // The page still works: a settings control is present despite the version failure.
    await waitFor(() => expect(screen.queryByText(/RapidAPI/i)).not.toBeNull())
  })
})

describe('Dashboard load failure', () => {
  it('shows an error rather than an empty dashboard', async () => {
    vi.mocked(api.statsApi.get).mockRejectedValue(new Error('server down'))
    const { default: DashboardPage } = await import('@/app/dashboard/page')

    renderPage(<DashboardPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
  })
})
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/Themearr.Web && npm test -- settings-load`
Expected: FAIL — Settings spins with no error, Dashboard renders nothing.

- [ ] **Step 3: Fix the loads**

- `settings:55` → `useResource`, with an error state and a Retry button gating the page.
- `settings:56` and `:57` → keep them independent of that gate. Their failure must leave the rest of Settings rendered; showing nothing, or a small inline note, is fine — blocking the page is not.
- `dashboard:13` → `useResource` with an error state.

Leave `settings:95` (update-status poll) and `settings:294` (version re-check poll) untouched.

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/Themearr.Web && npm test -- settings-load`
Expected: PASS — 3 tests passed.

- [ ] **Step 5: Verify the whole frontend**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build`
Expected: all clean.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.Web/src/app/settings/page.tsx src/Themearr.Web/src/app/dashboard/page.tsx \
        src/Themearr.Web/src/app/settings-load.test.tsx
git commit -m "fix(web): show an error instead of stranding Settings and Dashboard"
```

---

### Task 5: The three actions that claim success

**Files:**
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`
- Modify: `src/Themearr.Web/src/app/queue/page.tsx`
- Test: `src/Themearr.Web/src/app/actions-failure.test.tsx`

The three sites:

- `settings:135` — **Check for updates**: `catch { /* ignore */ }` means the spinner stops and nothing is said.
- `settings:156` — **Remove RapidAPI key**: `await rapidApiApi.remove().catch(() => null)` then unconditionally `setRapidApiOk(false)`, so the UI reports the key gone when the DELETE failed and it is still stored and still spending quota.
- `queue:43` — **Auto toggle**: `setAutoMode(next)` before a save wrapped in `catch { /* ignore */ }`, so the switch stays on while the server setting is unchanged and the background worker never starts.

- [ ] **Step 1: Write the failing tests**

Create `src/Themearr.Web/src/app/actions-failure.test.tsx`, reusing the same `vi.mock('@/lib/api', …)` block, and add:

```tsx
describe('an action that fails does not report success', () => {
  it('Remove does not claim the RapidAPI key is gone', async () => {
    const user = userEvent.setup()
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false, autoSync: false } as never)
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
    vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
    vi.mocked(api.rapidApiApi.remove).mockRejectedValue(new Error('server down'))
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const remove = await screen.findByRole('button', { name: /remove/i })
    await user.click(remove)

    // The key is still stored, so the UI must not say otherwise.
    await waitFor(() => expect(screen.queryByText(/couldn't|could not|failed/i)).not.toBeNull())
  })

  it('the Auto toggle does not stay on when the save fails', async () => {
    const user = userEvent.setup()
    vi.mocked(api.moviesApi.list).mockResolvedValue([] as never)
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
    vi.mocked(api.settingsApi.save).mockRejectedValue(new Error('server down'))
    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    const toggle = await screen.findByRole('button', { name: /auto/i })
    await user.click(toggle)

    await waitFor(() => expect(screen.queryByText(/couldn't|could not|failed/i)).not.toBeNull())
  })
})
```

Add `import userEvent from '@testing-library/user-event'` and install it:

```bash
cd /Users/devlin/Documents/GitHub/themearr/src/Themearr.Web && npm install -D @testing-library/user-event
```

If the Auto control is not a `button` in the DOM, query it however it is actually rendered — read the component rather than forcing the markup to match the test.

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/Themearr.Web && npm test -- actions-failure`
Expected: FAIL — no failure message appears; the UI reports success.

- [ ] **Step 3: Fix the three actions**

- `settings:135` — surface the failure using the section's existing error state; keep the `finally` that clears the spinner.
- `settings:156` — only set the removed state when the request succeeded; on failure show an error and leave the state saying a key is still configured.
- `queue:43` — either save first and update the toggle on success, or revert the optimistic update on failure. Either is fine; the toggle must not end up on when the save failed. Also stop it writing back the whole settings object if a narrower update is available — it currently clobbers a concurrent Settings-page edit.

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/Themearr.Web && npm test -- actions-failure`
Expected: PASS — 2 tests passed.

- [ ] **Step 5: Verify the whole frontend**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build`
Expected: all clean.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.Web/src/app/settings/page.tsx src/Themearr.Web/src/app/queue/page.tsx \
        src/Themearr.Web/src/app/actions-failure.test.tsx
git commit -m "fix(web): stop failed actions reporting success"
```

---

### Task 6: The queue poll that skips a movie

**Files:**
- Modify: `src/Themearr.Web/src/app/queue/page.tsx`
- Test: `src/Themearr.Web/src/app/queue-race.test.tsx`

`queue:122` polls download status every second with an `await` inside and no in-flight guard. If a response takes longer than the interval, two callbacks are in flight; both observe `finished`, and both call `advanceQueue()` — the `clearInterval` in the first is too late for the second. The index advances twice and **a movie is silently skipped**, with no theme and no message.

- [ ] **Step 1: Write the failing test**

Create `src/Themearr.Web/src/app/queue-race.test.tsx`, reusing the same `vi.mock('@/lib/api', …)` block:

```tsx
it('a slow status response cannot advance the queue twice', async () => {
  vi.useFakeTimers()
  const three = [
    { id: 'a', title: 'A', year: 2001, status: 'pending' },
    { id: 'b', title: 'B', year: 2002, status: 'pending' },
    { id: 'c', title: 'C', year: 2003, status: 'pending' },
  ]
  vi.mocked(api.moviesApi.list).mockResolvedValue(three as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)

  // Every status call reports finished, and resolves slower than the 1s interval.
  vi.mocked(api.moviesApi.search).mockResolvedValue([] as never)
  const slowFinished = () =>
    new Promise(res => setTimeout(() => res({ inProgress: false, finished: true, error: '' }), 2500))
  const downloadStatus = vi.fn(slowFinished)
  ;(api.moviesApi as unknown as { downloadStatus: unknown }).downloadStatus = downloadStatus

  const { default: QueuePage } = await import('@/app/queue/page')
  renderPage(<QueuePage />)

  await vi.advanceTimersByTimeAsync(6000)

  // Two overlapping polls must not both advance: the queue moves by one, not two.
  expect(screen.queryByText(/^C$/)).toBeNull()
  vi.useRealTimers()
})
```

Read `app/queue/page.tsx` and `lib/api.ts` first: use the real name of the download-status function and the real shape of a queue item, and drive the component the way it actually starts a download. If the test cannot be written against the component as it stands, say so rather than reshaping the component to suit the test.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd src/Themearr.Web && npm test -- queue-race`
Expected: FAIL — the queue advances past two movies.

- [ ] **Step 3: Add an in-flight guard**

Guard the poll so a callback returns immediately when a previous one has not settled — a `useRef<boolean>` set before the `await` and cleared in a `finally`. Also make `advanceQueue()` safe to call twice, so the guard is not the only thing preventing a double advance.

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd src/Themearr.Web && npm test -- queue-race`
Expected: PASS.

- [ ] **Step 5: Verify the whole frontend**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build`
Expected: all clean.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.Web/src/app/queue/page.tsx src/Themearr.Web/src/app/queue-race.test.tsx
git commit -m "fix(web): stop a slow status poll skipping a movie in the queue"
```

---

### Task 7: Wire the tests into CI, and confirm the silent sites are still silent

**Files:**
- Modify: `.github/workflows/release.yml`
- Test: `src/Themearr.Web/src/app/polls-stay-silent.test.tsx`

- [ ] **Step 1: Write a test pinning the deliberate silence**

Five poll sites are *supposed* to swallow failures — blanking a populated view over one dropped request is worse than a stale value, and that was a deliberate earlier fix. Nothing currently stops a future change "fixing" them.

Create `src/Themearr.Web/src/app/polls-stay-silent.test.tsx`, reusing the same `vi.mock('@/lib/api', …)` block:

```tsx
it('a failed background poll does not blank an already-loaded page', async () => {
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.systemApi.tasks)
    .mockResolvedValueOnce([{ id: 'syncLibrary', name: 'Sync Library', interval: '1.00:00:00',
      lastRunUtc: null, lastDurationMs: null, lastResult: null, nextRunUtc: null, isRunning: false }] as never)
    .mockRejectedValue(new Error('dropped poll'))
  const { default: SystemPage } = await import('@/app/system/page')

  renderPage(<SystemPage />)
  await screen.findByText(/Sync Library/i)

  // A later poll fails; the row that was already loaded must survive.
  await new Promise(r => setTimeout(r, 50))
  expect(screen.queryByText(/Sync Library/i)).not.toBeNull()
})
```

- [ ] **Step 2: Run it**

Run: `cd src/Themearr.Web && npm test -- polls-stay-silent`
Expected: PASS — this pins existing correct behaviour rather than driving a change.

- [ ] **Step 3: Add the test step to the release workflow**

In `.github/workflows/release.yml`, change the frontend build step from:

```yaml
      - name: Build frontend
        working-directory: src/Themearr.Web
        run: |
          npm ci
          npm run build
```

to:

```yaml
      - name: Build frontend
        working-directory: src/Themearr.Web
        run: |
          npm ci
          npm test
          npm run build
```

A harness that never runs unattended is only a thing you can run; this is what makes it a gate.

- [ ] **Step 4: Confirm the workflow is still valid**

Run:
```bash
cd /Users/devlin/Documents/GitHub/themearr
ruby -ryaml -e 'd=YAML.load(File.read(".github/workflows/release.yml")); puts "valid YAML"; puts d["jobs"].keys.join(", ")'
```
Expected: `valid YAML` and `release, docker`.

- [ ] **Step 5: Full verification**

```bash
cd /Users/devlin/Documents/GitHub/themearr
dotnet test
cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build
```
Expected: 265 backend tests pass, all frontend tests pass, typecheck clean, lint 0 errors with 3 pre-existing warnings, build succeeds.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/release.yml src/Themearr.Web/src/app/polls-stay-silent.test.tsx
git commit -m "ci: run the frontend tests on every release build"
```

---

## Self-review notes

**Spec coverage.** The rule and its classification → Tasks 3, 4, 5 (and Task 7 pins the sites that stay silent); `useResource` → Task 2; the three lying pages → Task 3; Settings gating vs supplementary loads → Task 4; the three actions → Task 5; the queue race → Task 6; the harness → Task 1; CI → Task 7.

**Two tasks touch the same files.** Tasks 3 and 5 both edit `queue/page.tsx`, and Tasks 4 and 5 both edit `settings/page.tsx`. They change different functions, so this is not a conflict, but the tasks must run in order and a reviewer should expect the second diff to sit alongside the first rather than replace it.

**The API mock block is duplicated across four test files.** That is deliberate — `vi.mock` is per-file and hoisted, so a shared helper cannot be imported into it without care. If the implementer finds a clean way to share it, that is an improvement; forcing one is not.

**Two places where the plan tells the implementer to check rather than assume.** The Auto toggle's rendered role in Task 5, and the real download-status function name and queue-item shape in Task 6. Both were written from a partial reading, and Task 6 explicitly says to report back rather than reshape the component if the test cannot be written as described.

**Type consistency.** `useResource`'s returned shape — `{ data, error, loading, retry }` — is defined in Task 2 and consumed in Tasks 3 and 4. The empty-state strings asserted absent in Task 3 are the exact ones in the codebase today: `No movies yet`, `No downloads yet`, `All caught up!`.
