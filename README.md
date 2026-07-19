<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/Themearr.Web/public/logo.svg">
    <source media="(prefers-color-scheme: light)" srcset="src/Themearr.Web/public/logo-dark.svg">
    <img src="src/Themearr.Web/public/logo.svg" alt="Themearr" height="48" />
  </picture>
</p>

<p align="center">
  Automatic movie theme song downloader for Plex libraries.
</p>

<p align="center">
  <a href="https://github.com/Themearr/themearr/releases">Releases</a> ·
  <a href="https://github.com/Themearr/ProxmoxVE">Proxmox Scripts</a>
</p>

---

## What it does

Themearr signs in with your Plex account, reads your movie libraries, and helps you add a `theme.mp3` to every movie folder — the file Plex uses to play background music while browsing.

- Browse your full Plex library as a poster grid
- Auto-search YouTube for each movie's theme
- One-click download to `theme.mp3`
- Automatic background downloading across the whole library
- Paste any video URL to use a custom source
- Downloaded status tracked per movie, verified against what's on disk

## Downloads require a RapidAPI key

Theme audio is fetched through the [youtube-mp36](https://rapidapi.com/ytjar/api/youtube-mp36) API on RapidAPI. **Downloads will not work until you add your RapidAPI key and username** in **Settings → RapidAPI**. Plex sign-in and library browsing work without it — only downloading a `theme.mp3` needs it. The free RapidAPI tier is quota-limited, so Themearr backs off automatically when the quota is exhausted.

## Install

### Proxmox LXC (one-line)

Run this on your Proxmox host:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/Themearr/ProxmoxVE/main/ct/themearr.sh)"
```

The installer generates an access token, prints it at the end, and saves a copy to `/root/themearr.creds`. Open `http://<container-ip>:8080`, enter the token, and sign in with Plex.

### Docker

A multi-arch image (`amd64` / `arm64`) is published to GHCR on every release.

```bash
# 1. Get the compose file
curl -fsSL https://raw.githubusercontent.com/Themearr/themearr/main/docker-compose.yml -o docker-compose.yml

# 2. Generate the required access token
echo "THEMEARR_AUTH_TOKEN=$(openssl rand -hex 32)" > .env

# 3. Edit docker-compose.yml — point the movie volume at your library:
#      - /path/to/your/movies:/movies
#    It must be WRITABLE (no ":ro") — Themearr writes theme.mp3 into movie folders.

docker compose up -d
```

Open `http://127.0.0.1:8080` and enter the token from `.env`.

> The compose file publishes the port to `127.0.0.1` only. For remote access, put a reverse proxy (Caddy/nginx) in front with its own TLS and auth.

## Configuration

### Access token (required)

The API refuses to start without `THEMEARR_AUTH_TOKEN` — there is no unauthenticated mode. The Proxmox installer generates one for you; for Docker you set it yourself (see above). Every client enters this token once.

### Library paths & path mappings

This is the setting people most often get wrong, and it's what causes **`Skipping <title> — unresolved path`** during sync.

Themearr writes `theme.mp3` **into your movie folders**, so it has to reach your files at a path *it* can see — which is usually **not** the path Plex reports.

- **Local Library Paths** — where your movie folders live *as Themearr sees them* (e.g. `/movies` in Docker, or `/mnt/media/Movies` in an LXC).
- **Path Mappings** — translate the path **Plex reports** into the path **Themearr sees**.

Example — Plex on Windows, Themearr in Docker:

| Plex reports | Themearr sees | Mapping to add |
|---|---|---|
| `P:\Movies\Heat (1995)\heat.mkv` | `/movies/Heat (1995)` | `P:\Movies` → `/movies` |

If sync logs `Skipping <title> — unresolved path: <path>`, that logged path is exactly what Plex reported — map its parent folder to wherever it's mounted in Themearr. Windows-style (`\`) paths are handled automatically.

Also make sure the movie mount is **writable** — a read-only mount resolves fine but silently fails every download.

## Updating

- **In-app:** Settings → Updates. Downloads the latest release, preserves your data, and restarts.
- **Docker:** `docker compose pull && docker compose up -d`
- **Proxmox / bare metal:** the in-app updater, or re-run the community install script.

> **Upgrading a pre-.NET-10 install:** releases from v1.39.10 onward need the **ASP.NET Core 10** runtime. Containers created earlier were provisioned with .NET 9, so install the runtime first:
> ```bash
> curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
> bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet
> ```
> If you forget, nothing breaks — the updater checks for the runtime *before* changing any files and aborts with these instructions, leaving your running install untouched. Docker is unaffected (the runtime ships in the image).

## Tech stack

| Layer | Technology |
|---|---|
| API | .NET 10 Web API (ASP.NET Core, LTS) |
| Frontend | React 19 + Vite (static SPA, served by .NET) |
| Routing | React Router |
| Database | SQLite via `Microsoft.Data.Sqlite` |
| YouTube search | `YoutubeExplode` |
| Theme download | [youtube-mp36 RapidAPI](https://rapidapi.com/ytjar/api/youtube-mp36) |
| Tests | xUnit |

## Local development

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/)
- A [youtube-mp36 RapidAPI](https://rapidapi.com/ytjar/api/youtube-mp36) key + username (added in **Settings → RapidAPI**) — required for downloads

### Run

```bash
# Terminal 1 — API (set any token you like for local dev)
THEMEARR_AUTH_TOKEN=dev-token-at-least-16-chars dotnet run --project src/Themearr.API

# Terminal 2 — Frontend (dev server with proxy to API)
cd src/Themearr.Web
npm install
npm run dev   # proxies /api to the .NET backend on :5000
```

Open `http://localhost:3000`. The frontend is a static SPA — in production it's built to `src/Themearr.Web/out/` and served by the .NET app from `wwwroot` (with an SPA fallback, so deep links like `/movies` work).

### Checks

```bash
dotnet test                  # .NET test suite

cd src/Themearr.Web
npm run lint                 # ESLint
npx tsc --noEmit             # typecheck
npm run build                # production build -> out/
```

## Building a release

Push to `main` — GitHub Actions will automatically:

1. Detect the semver bump from commit messages (`feat:` → minor, `major:` → major, else patch)
2. Build the frontend (Vite) and publish .NET for `linux-x64` and `linux-arm64`
3. Bundle the frontend into each publish output
4. Create a GitHub release with both tarballs **plus SHA-256 checksums** (verified by `install.sh` / `deploy.sh`)
5. Build and push the multi-arch Docker image to `ghcr.io/themearr/themearr` (`:latest` and `:vX.Y.Z`)

Changes that don't affect the shipped app (docs, `.gitignore`, workflows) don't cut a release.

## Versioning

Releases follow semantic versioning driven by commit message prefixes:

| Prefix | Bump |
|---|---|
| `feat:` | minor |
| `major:` / `BREAKING CHANGE` | major |
| anything else | patch |

## License

MIT
