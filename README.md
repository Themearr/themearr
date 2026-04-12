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
- One-click download via `yt-dlp`
- Paste any video URL to use a custom source
- Downloaded status tracked per movie

## One-line Proxmox LXC install

Run this on your Proxmox host:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/Themearr/ProxmoxVE/main/ct/themearr.sh)"
```

After the container is created, open `http://<container-ip>:8080` and sign in with Plex.

## Tech stack

| Layer | Technology |
|---|---|
| API | .NET 9 Web API (ASP.NET Core) |
| Frontend | Next.js 16 (static export, served by .NET) |
| Database | SQLite via `Microsoft.Data.Sqlite` |
| YouTube | `YoutubeExplode` + `yt-dlp` |
| Audio | `ffmpeg` |

## Local development

### Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org/)
- `yt-dlp` and `ffmpeg` in `PATH`

### Run

```bash
# Terminal 1 — API
dotnet run --project src/Themearr.API

# Terminal 2 — Frontend (dev server with proxy to API)
cd src/Themearr.Web
npm install
NEXT_PUBLIC_API_URL=http://localhost:5000 npm run dev
```

Open `http://localhost:3000`.

## Building a release

Push to `main` — GitHub Actions will automatically:

1. Detect the semver bump from commit messages (`feat:` → minor, `major:` → major, else patch)
2. Build the Next.js frontend (`npm run build`)
3. Publish .NET for `linux-x64` and `linux-arm64`
4. Bundle the frontend into each publish output
5. Create a GitHub release with both tarballs attached

## Updating

Themearr includes an in-app updater (Settings → Updates). It downloads the latest release tarball, preserves your data, and restarts the service. You can also update from the Proxmox web UI.

## Versioning

Releases follow semantic versioning driven by commit message prefixes:

| Prefix | Bump |
|---|---|
| `feat:` | minor |
| `major:` / `BREAKING CHANGE` | major |
| anything else | patch |

## License

MIT
