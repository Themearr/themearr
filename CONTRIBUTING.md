# Contributing to Themearr

Thanks for your interest in contributing! Themearr is a small project with a
single maintainer, so contributions of every size are welcome — bug reports,
docs fixes, and code alike.

## Before you start

- **Bug?** Open a [bug report](https://github.com/Themearr/themearr/issues/new?template=bug_report.yml).
- **Idea?** Open a [feature request](https://github.com/Themearr/themearr/issues/new?template=feature_request.yml)
  first for anything non-trivial, so we can agree on the approach before you
  spend time on code.
- **Security issue?** Please **don't** open a public issue — see
  [SECURITY.md](SECURITY.md).

## Development setup

Requirements and run instructions live in the README's
[Local development](README.md#local-development) section. The short version:

```bash
# Terminal 1 — API (set any token you like for local dev)
THEMEARR_AUTH_TOKEN=dev-token-at-least-16-chars dotnet run --project src/Themearr.API

# Terminal 2 — Frontend (dev server with proxy to API)
cd src/Themearr.Web
npm install
npm run dev
```

## Checks to run before opening a PR

These are the same gates the release workflow runs — a PR that fails them
can't ship:

```bash
dotnet test                  # .NET (API) test suite

cd src/Themearr.Web
npm test                     # frontend test suite
npm run lint                 # ESLint
npx tsc --noEmit             # typecheck
npm run build                # production build -> out/
```

## Commit messages — they drive releases

**Merging to `main` automatically cuts a release** (build, GitHub release,
Docker image) whenever the change touches the shipped app. The version bump is
derived from commit message prefixes:

| Prefix | Bump |
|---|---|
| `feat:` | minor |
| `major:` / `BREAKING CHANGE` / `!:` | major |
| anything else (`fix:`, `chore:`, …) | patch |

So please use conventional-style prefixes (`feat:`, `fix:`, `docs:`,
`chore:`), and reserve `feat:` for actual user-facing features.

Docs-only changes (`*.md`, `LICENSE`, `.github/**`) do **not** trigger a
release.

## Pull requests

- Target the `main` branch.
- Keep PRs focused — one fix or feature per PR.
- Include tests for behavior changes; the API test suite covers the
  security-critical paths (auth, SSRF guard, path containment, DB layer), so
  changes there especially need coverage.
- Fill in the PR template — it's short.

That's it. If anything here is unclear, open an issue and ask.
