# Themearr

Automatic theme-song downloader for Plex, Jellyfin, Emby and Kodi libraries. Self-hosted:
users run it via Docker/GHCR, `install.sh`, or a Proxmox script.

## Layout

| Path | What |
|---|---|
| `src/Themearr.API/` | .NET 10 web API — 12 controllers, ~30 services. The whole backend. |
| `src/Themearr.Web/` | React 19 + Vite 8 + Tailwind 4 SPA. Built to `out/`, injected as `wwwroot`. |
| `tests/Themearr.API.Tests/` | xUnit. Every security invariant below is pinned here. |
| `docs/superpowers/` | Specs and plans, one pair per feature. Written before implementing. |

Two extension seams: `ILibrarySource` (Plex, Radarr) and `IThemeAudioProvider` (RapidAPI).
Add a source/provider by implementing the interface, not by branching in callers.

## Commands

Frontend commands run from `src/Themearr.Web`.

```bash
npm test                  # vitest, ~5s
npx tsc --noEmit          # ~2s
npm run lint              # eslint, ~2s
npm run build             # tsc --noEmit && vite build
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo   # ~7s
```

E2E lives in `tests/e2e` as its own npm package, so the release's `npm ci` never pulls
Playwright or a browser. It needs the frontend built first — the config points
`ASPNETCORE_WEBROOT` at `out/` and boots the real API on :5099 with a throwaway DB.

```bash
cd src/Themearr.Web && npm run build     # required: E2E serves this bundle
cd tests/e2e && npm test                 # ~4s
```

The full gate is ~16s. There is no excuse for guessing whether something passes — run it.

Green is `Failed: 0, Skipped: 0` — read both fields. A skipped test is invisible in a run
that says "Passed", and skips are how coverage quietly disappears. The API suite is the
big one (400+); if a run reports two digits you filtered by accident.

## Security invariants

These five hold the security surface. Changing one, or bypassing it in a new code path,
is a security change and should be treated as one.

- **`HostGuard`** — SSRF chokepoint. Rejects private/loopback/link-local/CGNAT/IPv6-ULA
  hosts, fails closed on DNS error, and unwraps IPv4-mapped IPv6. Must re-run on **every
  redirect hop**, not just the initial URL — redirect-following is the classic bypass.
- **`PlexPath`** / **`ThemeFiles`** — path handling and containment. Plex may report
  Windows `\` paths while Themearr runs on Linux; normalize via `PlexPath`, never
  `System.IO.Path` alone. Theme writes are atomic so a killed download can't leave a
  corrupt `theme.*` behind.
- **`LogSanitizer.Clean`** — wrap any user-controlled value before it reaches a log.
  Strips CR/LF to prevent forged log lines (CWE-117).
- **`PosterUrlSigner`** — posters are exempt from bearer auth because `<img>` can't send
  an Authorization header, so they self-authenticate via an expiring HMAC. Keep the key
  domain-separated from the raw auth token.
- **`ApiAuthMiddleware.RequiresAuth`** — the auth boundary. Guards `/api/*` except
  `/api/auth` and `/api/poster`. **Widening a prefix here silently exposes a whole
  namespace and no other test will catch it.** `AuthBoundaryTests` is the guard.

Background services (`AutoDownloadService`, `AutoSyncService`, `ShowAutoSyncService`,
`TaskRegistry`, `PollBackoff`) run concurrently with request handling. Races are a
recurring bug class here — `queue-race`, `movies-refresh-race` and `inflight-guards`
tests exist because of real regressions.

## Conventions

- **Tests first.** Both suites gate the release. Write the failing test, then the fix.
- **Never edit an existing test to make a change pass.** If a change breaks a test, the
  change is wrong until proven otherwise. Say so rather than adjusting the assertion.
- **A test must fail for the right reason.** Revert the change and watch a test go red
  before calling it covered. Tests on a *new* class fail to compile whether or not the
  call site is wired: reverting `YoutubeService`'s `bestMatch` line to the old `score > 0`
  rule once left all 429 tests green, because every test bound to `ThemeMatch` and none to
  its use.
- **Lint baseline is 0 errors, 3 warnings.** The three live in `login/page.tsx` and
  `lib/auth.tsx`. A fourth is yours — fix it.
- **Comments explain _why_, not _what_.** Match the existing density: the non-obvious
  decision, the bypass being defended against, the case that made it necessary. See
  `HostGuard.cs` or `ApiAuthMiddleware.cs` for the house style.
- **Cite, don't restate.** A claim about code — a constant's value, a signature, a default
  — carries its `file:line`, in specs and plans as much as in comments. Writing the
  citation is what forces the lookup. `NoMatchCooldown` was documented as 24h in a spec, a
  plan, a code comment and a test comment before anyone opened `AutoDownloadService.cs:19`
  and read `FromHours(6)`. Four documents agreeing is one source restated four times.
- Nullable is enabled on both projects. Don't make a settings field nullable to dodge a
  warning — `SelectedLibraries` is non-nullable deliberately, so an older frontend
  omitting it can't wipe stored values.
- Use the LSP tools (`csharp-ls`, TypeScript) for find-references and rename rather than
  grep — both language servers are configured.

## Release model

**Merging to `main` publishes a release.** `.github/workflows/release.yml` derives the
next semver tag from commit messages (`feat:`/`minor:` → minor, `!:`/`BREAKING CHANGE` →
major, otherwise patch), builds both halves, and pushes tarballs plus a multi-arch image
to GHCR. Only `paths-ignore` (docs, `.github/**`, `LICENSE`) avoids cutting a release —
a commit prefix does not.

`.github/workflows/ci.yml` gates PRs on `frontend`, `api` and `e2e`, so a broken PR is
caught before it can ship. It is not a substitute for running the gate locally — the full
suite is ~16s, and CI tells you *that* something failed long after you could have known
*why*.
