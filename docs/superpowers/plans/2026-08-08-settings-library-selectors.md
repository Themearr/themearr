# Settings Library Selectors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator change which Plex movie libraries Themearr watches, from Settings, without re-running setup or factory-resetting (#32).

**Architecture:** A **Movie Libraries** section in Settings mirroring the Show Libraries section shipped in v1.46.0, reusing the `plexLibraries` fetch that section already performs. Both sections then gain a post-save "Sync now" prompt, because saving alone changes nothing observable until a sync runs. Frontend-only — the settings endpoint already accepts `selectedLibraries` unconditionally.

**Tech Stack:** React 19, Vite, Tailwind, Vitest + Testing Library. No backend changes.

## Global Constraints

- **No backend change.** `SettingsPayload.SelectedLibraries` is already non-nullable (`SettingsController.cs:292`) and written unconditionally (`:40`). Do not make it nullable — that was required for `SelectedShowLibraries` only because that field was *new*, and an older frontend omitting it must not wipe the stored value.
- **Existing settings tests must pass unmodified:** `settings-load.test.tsx`, `settings-plex-url.test.tsx`, `settings-show-libraries.test.tsx`. Editing one to accommodate a change is a signal the change broke something — stop rather than adjust the test.
- Run `npm test`, `npm run lint` and `npx tsc --noEmit` in `src/Themearr.Web` after every task. Lint must stay at **0 errors and 3 warnings** — three pre-existing warnings live in `login/page.tsx` and `lib/auth.tsx`; any fourth is yours.
- **Both sections always render**, showing an explanatory line when the server reports no libraries of that type. Never conditionally hide them.
- The removal hint must state that **`theme.mp3` files on disk are never touched** — that is what makes unticking a safe, recoverable action.
- No confirmation dialog for unticking. It is recoverable (identity is folder-derived, ignored movies are exempt, and `SyncService` skips pruning entirely when a sync returns zero movies).

---

### Task 1: Movie Libraries section

**Files:**
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`
- Test: `src/Themearr.Web/src/app/settings-movie-libraries.test.tsx` (create)

**Interfaces:**
- Consumes: `settings.selectedLibraries` (`Record<string, string[]>`, already on the `Settings` type), the `plexLibraries` state already populated by `loadSettings` for Show Libraries.
- Produces: state `movieLibs`, functions `toggleMovieLib(serverId, key)` and `saveMovieLibraries()`.

- [ ] **Step 1: Write the failing test** (`settings-movie-libraries.test.tsx`)

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
      { key: '1', title: 'Films', type: 'movie' },
      { key: '2', title: 'Kids Films', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

describe('Settings movie-library selector', () => {
  it('lists movie libraries, pre-ticked from the stored selection', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    // selectedLibraries was { srv1: ['1'] }, so only Films starts ticked.
    expect((screen.getByLabelText('Films') as HTMLInputElement).checked).toBe(true)
    expect((screen.getByLabelText('Kids Films') as HTMLInputElement).checked).toBe(false)
  })

  it('saves the selection as selectedLibraries', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Kids Films')).toBeTruthy())

    await user.click(screen.getByLabelText('Kids Films'))
    await user.click(screen.getByRole('button', { name: /Save movie libraries/i }))

    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalled())
    const payload = vi.mocked(api.settingsApi.save).mock.calls[0][0]
    expect(payload.selectedLibraries).toEqual({ srv1: ['1', '2'] })
  })

  /** The hint is the only thing telling an operator that unticking is safe. */
  it('explains that unticking never deletes theme files', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    expect(screen.getByText(/never deleted from disk/i)).toBeTruthy()
  })

  it('explains itself when the server reports no movie libraries', async () => {
    vi.mocked(api.setupApi.plexLibraries).mockResolvedValue({
      libraries: { srv1: [{ key: '3', title: 'TV Shows', type: 'show' }] },
    } as never)

    renderPage()

    await waitFor(() => expect(screen.getByText(/No movie libraries found/i)).toBeTruthy())
  })
})
```

`Films` and `Kids Films` are movie-type and only the movie list renders them; `TV Shows` is
show-type and belongs to the Show Libraries section, so the "movie-only filter works" claim
is carried by the fourth test — an all-shows server must render the no-movie-libraries line
rather than listing the show library here.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/settings-movie-libraries.test.tsx`
Expected: FAIL — no "Save movie libraries" button and no movie checkboxes exist.

- [ ] **Step 3: Add the state**

In `settings/page.tsx`, beside the existing show-library state (around line 17-21):

```tsx
  // ── Movie libraries (#32: previously only selectable in the setup wizard) ────
  const [movieLibs,       setMovieLibs]       = useState<Record<string, string[]>>({})
  const [savingMovieLibs, setSavingMovieLibs] = useState(false)
  const [movieLibsSaved,  setMovieLibsSaved]  = useState(false)
  const [movieLibsError,  setMovieLibsError]  = useState('')
```

- [ ] **Step 4: Seed it from the loaded settings**

In `loadSettings`, next to the existing `setShowLibs(...)` line:

```tsx
    setMovieLibs(s.selectedLibraries ?? {})
```

- [ ] **Step 5: Add the toggle and save handlers**

Beside `toggleShowLib` / `saveShowLibraries`:

```tsx
  function toggleMovieLib(serverId: string, key: string) {
    setMovieLibsSaved(false)
    setMovieLibs(prev => {
      const cur = prev[serverId] ?? []
      return { ...prev, [serverId]: cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key] }
    })
  }

  // Same whole-object save as the show libraries: the endpoint takes one payload and
  // writes the other collections unconditionally, so a partial object would clear them.
  // Unlike selectedShowLibraries this field is not nullable server-side — it has always
  // been written unconditionally — so no special absent-means-unchanged handling applies.
  async function saveMovieLibraries() {
    if (!settings) return
    setSavingMovieLibs(true)
    setMovieLibsError('')
    try {
      const next = { ...settings, selectedLibraries: movieLibs }
      await settingsApi.save(next)
      setSettings(next)
      setMovieLibsSaved(true)
    } catch (e) {
      setMovieLibsError((e as Error)?.message || 'Could not save the movie libraries.')
    } finally {
      setSavingMovieLibs(false)
    }
  }
```

- [ ] **Step 6: Add the section**

Insert immediately **before** the existing `<Section title="Show Libraries" …>` block, so
movies (the primary media type) read first:

```tsx
        {/* Movie libraries — #32: previously only selectable during first-run setup */}
        <Section title="Movie Libraries" hint="Which Plex libraries Themearr scans for movies. You can change this at any time — you don't need to re-run setup.">
          <div className="space-y-3">
            {Object.entries(plexLibraries).flatMap(([serverId, libs]) =>
              libs.filter(l => l.type === 'movie').map(l => (
                <label key={`${serverId}:${l.key}`} className="flex items-center gap-2 text-sm text-[#D0D5DD]">
                  <input
                    type="checkbox"
                    checked={(movieLibs[serverId] ?? []).includes(l.key)}
                    onChange={() => toggleMovieLib(serverId, l.key)}
                  />
                  {l.title}
                </label>
              )))}

            {Object.values(plexLibraries).every(libs => !libs.some(l => l.type === 'movie')) && (
              <p className="text-sm text-[#667085]">No movie libraries found on your Plex server.</p>
            )}

            <p className="text-xs text-[#667085]">
              Unticking a library removes its movies from Themearr on the next sync. Their
              theme files are <strong className="text-[#98A2B3]">never deleted from disk</strong>,
              and re-ticking the library restores them.
            </p>

            <div className="flex items-center gap-3">
              <Button size="sm" onClick={saveMovieLibraries} loading={savingMovieLibs}>
                Save movie libraries
              </Button>
              {movieLibsSaved && <p className="text-xs text-[#12B76A]">Saved ✓</p>}
            </div>
            {movieLibsError && <p className="text-xs text-[#FDA29B]">{movieLibsError}</p>}
          </div>
        </Section>
```

(The `Saved ✓` here is replaced by the sync prompt in Task 2.)

- [ ] **Step 7: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS — new tests green, existing settings tests green **and unmodified**, lint 0 errors / 3 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.Web/src/app/settings/page.tsx src/Themearr.Web/src/app/settings-movie-libraries.test.tsx
git commit -m "feat: select Plex movie libraries from Settings (#32)"
```

---

### Task 2: Post-save "Sync now" prompt for both sections

Saving changes nothing observable until a sync runs. Without this, ticking a box appears to
do nothing — the exact dead end that produced #32.

**Files:**
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`
- Test: `src/Themearr.Web/src/app/settings-library-sync-prompt.test.tsx` (create)

**Interfaces:**
- Consumes: `syncApi.start()`, `systemApi.runTask(id)`, and Task 1's `movieLibsSaved` / `saveMovieLibraries`.
- Produces: local component `LibrarySyncPrompt`, state `syncingLibs`, `libSyncStarted`.

- [ ] **Step 1: Write the failing test** (`settings-library-sync-prompt.test.tsx`)

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
      { key: '1', title: 'Films', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
  vi.mocked(api.systemApi.runTask).mockResolvedValue({ started: true } as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

describe('library save offers a sync', () => {
  it('is not shown before saving', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    expect(screen.queryByRole('button', { name: /Sync now/i })).toBeNull()
  })

  it('offers a movie sync after saving movie libraries, and starts it', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Save movie libraries/i }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Sync now/i })).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Sync now/i }))

    await waitFor(() => expect(api.syncApi.start).toHaveBeenCalled())
    // Movies use the ordinary sync, never the shows task.
    expect(api.systemApi.runTask).not.toHaveBeenCalled()
  })

  it('offers a show sync after saving show libraries, using the syncShows task', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('TV Shows')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Sync now/i })).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Sync now/i }))

    await waitFor(() => expect(api.systemApi.runTask).toHaveBeenCalledWith('syncShows'))
    expect(api.syncApi.start).not.toHaveBeenCalled()
  })

  /**
   * Both prompts can be on screen at once. A shared started-flag would let the movie
   * sync mark the show prompt as "Sync started ✓" — claiming a sync that never ran.
   */
  it('starting one sync does not mark the other as started', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Save movie libraries/i }))
    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: /Sync now/i })).toHaveLength(2))

    // Start only the movie one.
    await user.click(screen.getAllByRole('button', { name: /Sync now/i })[0])

    await waitFor(() => expect(api.syncApi.start).toHaveBeenCalled())
    // The show section must still be offering its sync, not claiming one ran.
    expect(screen.getAllByRole('button', { name: /Sync now/i })).toHaveLength(1)
    expect(screen.getAllByText(/Sync started/i)).toHaveLength(1)
    expect(api.systemApi.runTask).not.toHaveBeenCalled()
  })
})
```

The last test assumes the Movie Libraries section renders **before** Show Libraries (Task 1
Step 6 places it there), so index `[0]` is the movie prompt.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run src/app/settings-library-sync-prompt.test.tsx`
Expected: FAIL — no "Sync now" button exists.

- [ ] **Step 3: Import the two sync APIs**

`settings/page.tsx` currently imports `{ apiKeyApi, plexApi, radarrApi, rapidApiApi, settingsApi, setupApi, versionApi }`. Add `syncApi` and `systemApi`:

```tsx
import { apiKeyApi, plexApi, radarrApi, rapidApiApi, settingsApi, setupApi, syncApi, systemApi, versionApi } from '@/lib/api'
```

- [ ] **Step 4: Add the shared prompt component**

At the bottom of `settings/page.tsx`, beside the file's other local components:

```tsx
/**
 * Shown after a library selection is saved. Saving only records which libraries Themearr
 * watches — nothing is imported or removed until a sync runs — so without this the save
 * appears to do nothing, which is how #32 came about.
 *
 * Deliberately not auto-dismissed: it is a "you still need to do this" reminder, and a
 * timed disappearance would defeat that.
 */
function LibrarySyncPrompt({ onSync, syncing, started }: {
  onSync: () => void
  syncing: boolean
  started: boolean
}) {
  if (started) return <p className="text-xs text-[#12B76A]">Sync started ✓</p>
  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg border border-[#1D2939] bg-[#0C111D] px-3 py-2">
      <p className="text-xs text-[#D0D5DD]">Saved — run a sync to apply the change.</p>
      <Button size="sm" variant="secondary" onClick={onSync} loading={syncing}>Sync now</Button>
    </div>
  )
}
```

- [ ] **Step 5: Add the sync state and handler**

Beside the movie-library state from Task 1:

Both sections' prompts can be on screen at once — save movies, then save shows, and there
are two. So this state is **keyed by which kind of sync it refers to**, never a shared
boolean: with a plain `started` flag, clicking one section's *Sync now* would flip the other
section's prompt to "Sync started ✓" and claim a sync that never ran.

```tsx
  type LibKind = 'movies' | 'shows'

  // Which kind of sync is currently starting, and which kind has been started. Keyed
  // rather than boolean because both library sections can show a prompt simultaneously,
  // and a shared flag would let one section's click report success in the other.
  const [syncingLibs,    setSyncingLibs]    = useState<LibKind | null>(null)
  const [libSyncStarted, setLibSyncStarted] = useState<LibKind | null>(null)

  async function startLibrarySync(kind: LibKind) {
    setSyncingLibs(kind)
    try {
      if (kind === 'movies') await syncApi.start()
      else                   await systemApi.runTask('syncShows')
      setLibSyncStarted(kind)
    } catch (e) {
      const msg = (e as Error)?.message || 'Could not start the sync.'
      if (kind === 'movies') setMovieLibsError(msg)
      else                   setShowLibsError(msg)
    } finally {
      setSyncingLibs(null)
    }
  }
```

Also clear that section's started-marker when it is saved again, so a second save offers the
sync afresh rather than showing a stale "Sync started ✓". Add to `saveMovieLibraries`, next
to its existing `setSavingMovieLibs(true)`:

```tsx
    setLibSyncStarted(s => (s === 'movies' ? null : s))
```

and the mirror in `saveShowLibraries`, next to `setSavingShowLibs(true)`:

```tsx
    setLibSyncStarted(s => (s === 'shows' ? null : s))
```

- [ ] **Step 6: Swap both `Saved ✓` markers for the prompt**

In the **Movie Libraries** section, replace:

```tsx
              {movieLibsSaved && <p className="text-xs text-[#12B76A]">Saved ✓</p>}
```

with:

```tsx
              {movieLibsSaved && (
                <LibrarySyncPrompt
                  onSync={() => startLibrarySync('movies')}
                  syncing={syncingLibs === 'movies'}
                  started={libSyncStarted === 'movies'}
                />
              )}
```

In the **Show Libraries** section, replace:

```tsx
            {showLibsSaved && <p className="text-xs text-[#12B76A]">Saved ✓</p>}
```

with:

```tsx
            {showLibsSaved && (
              <LibrarySyncPrompt
                onSync={() => startLibrarySync('shows')}
                syncing={syncingLibs === 'shows'}
                started={libSyncStarted === 'shows'}
              />
            )}
```

- [ ] **Step 7: Run the full frontend suite**

Run: `cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint`
Expected: PASS — existing settings tests green **and unmodified**, lint 0 errors / 3 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.Web/src/app/settings/page.tsx src/Themearr.Web/src/app/settings-library-sync-prompt.test.tsx
git commit -m "feat: offer a sync after saving a library selection"
```

---

## Final verification

- [ ] `cd src/Themearr.Web && npm test && npm run lint && npx tsc --noEmit` — clean, 0 lint errors, 3 pre-existing warnings.
- [ ] `dotnet test tests/Themearr.API.Tests` — still 377 green. No backend file should appear in `git diff main --stat`; confirm with `git diff --stat main -- src/Themearr.API` returning nothing.
- [ ] No existing test file modified: `git diff --stat main --diff-filter=M -- '*.test.tsx'` returns nothing.
- [ ] Boot the app (build the frontend, copy `src/Themearr.Web/out` to `src/Themearr.API/wwwroot`, run the API with `THEMEARR_AUTH_TOKEN` and `DB_PATH` set) and confirm in a browser that Settings shows **Movie Libraries** above **Show Libraries**, both with their explanatory empty-state text on an install with no Plex server.
- [ ] Manual (maintainer's box, live Plex): tick a second movie library, save, click **Sync now**, confirm the new library's movies appear on the Movies page — the exact flow #32 asked for.

## Self-review notes

- **Spec coverage:** Movie Libraries section with movie-only filter, always-rendered empty state and the removal hint (Task 1); post-save Sync-now prompt on both sections with movies→`syncApi.start` and shows→`runTask('syncShows')`, persisting until synced or re-edited (Task 2). No backend task, per the spec's "no backend change" finding.
- **Type consistency:** `movieLibs`/`toggleMovieLib`/`saveMovieLibraries`/`movieLibsSaved`/`movieLibsError` mirror the existing `showLibs`/`toggleShowLib`/`saveShowLibraries`/`showLibsSaved`/`showLibsError` exactly. `LibrarySyncPrompt` takes booleans (`onSync`, `syncing`, `started`); the page holds `LibKind | null` state and narrows at each call site (`syncingLibs === 'movies'`), so the component stays media-agnostic while the state cannot conflate the two sections.
- **Bug caught in self-review:** the first draft used a shared boolean `libSyncStarted`. Since both prompts can be visible simultaneously, starting the movie sync would have flipped the show prompt to "Sync started ✓" without a show sync running. Now keyed by kind, with a regression test.
- **Not in this plan:** the `/setup` `pathMappings: []` wipe (separate latent bug, worth its own issue); adding a nav link to `/setup`; Radarr-source behaviour beyond the empty-state message.
