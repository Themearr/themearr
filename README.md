<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="app/static/logo-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="app/static/logo.png">
    <img src="app/static/logo.png" alt="Themearr logo" width="420" />
  </picture>
</p>

<h1 align="center">Themearr</h1>

<p align="center">
  Automatic movie theme song downloader for Plex libraries.
</p>

<p align="center">
  <a href="https://github.com/Themearr/themearr">GitHub</a>
</p>

## What It Does

Themearr helps you add a `theme.mp3` file to each movie folder in your library.
It signs in with Plex, reads your Plex movie library, and uses the media file paths Plex already knows about.

It provides a browser UI where you can:

- Sync movies from Plex
- Search YouTube for likely theme tracks
- Download a selected result with one click
- Open the movie query in YouTube directly
- Paste any video URL and download it
- Auto-advance to the next pending movie after each completed download

## One-Line Proxmox LXC Install

Run this on your Proxmox host:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/Themearr/themearr/compliance/ct/themearr.sh)"
```

After deployment, open the app in your browser and complete first-run setup.

## First-Run Setup

In the web UI, sign in with Plex.

Then click sync to import your Plex movie library.

## Download Workflow

1. Select a movie from the left list.
2. Review up to 3 YouTube matches.
3. Click `Accept & Download` for the best result, or paste a URL and submit.
4. Themearr saves audio as `theme.mp3` in the movie folder.
5. The UI automatically moves to the next pending movie.

## Tech Stack

- FastAPI + Uvicorn
- `yt-dlp` + `ffmpeg`
- Lightweight frontend (HTML + Tailwind + vanilla JS)
- SQLite-backed app state

## Local Development

### Requirements

- Python 3.12+
- `ffmpeg`
- `yt-dlp`

### Run

```bash
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python -m uvicorn app.main:app --reload --host 0.0.0.0 --port 8080
```

Open `http://localhost:8080`.

## Docker

Build and run:

```bash
docker build -t themearr .
docker run --rm -p 8080:8080 themearr
```

Note: you still need accessible movie library paths inside the container for downloads to land in the correct folders.

## Service Install (Debian/LXC)

Project scripts include native deployment helpers:

- `create_lxc.sh` for Proxmox container creation
- `deploy.sh` for pulling and deploying latest code
- `install.sh` for service + dependency setup
- `themearr.service` systemd unit

## Updating

Themearr includes an in-app update flow that runs the server-side updater command and restarts the service.

## License

No license file is currently included in this repository.
