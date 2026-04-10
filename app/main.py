import os
import subprocess
import logging
import threading
import asyncio
from collections import deque
from pathlib import Path
from urllib.parse import parse_qs, urlencode, urlparse, urlunparse

import httpx
from fastapi import FastAPI, HTTPException, Query
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field
from youtube_search import YoutubeSearch

from app.database import (
    init_db,
    upsert_movies,
    get_all_movies,
    get_movie,
    set_status,
    get_setting,
    set_setting,
    get_library_paths,
    set_library_paths,
    is_setup_complete,
    mark_setup_complete,
    reset_app_state,
)
from app.radarr import fetch_movies

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("themearr")

app = FastAPI(title="Themearr")

STATIC_DIR = Path(__file__).parent / "static"
VERSION_FILE = Path(os.getenv("THEMEARR_VERSION_FILE", "/opt/themearr/VERSION"))
GITHUB_REPO = os.getenv("GITHUB_REPO", "Themearr/themearr")
UPDATER_CMD = os.getenv("THEMEARR_UPDATER_CMD", "sudo /usr/local/bin/themearr-update")
BROWSE_ROOTS = os.getenv("THEMEARR_BROWSE_ROOTS", "/mnt,/media,/movies,/tv")

_update_lock = threading.Lock()
_update_in_progress = False
_update_error = ""
_update_finished = False
_update_started_at = ""
_update_logs: deque[str] = deque(maxlen=300)
_update_log_lock = threading.Lock()

_sync_lock = threading.Lock()
_sync_in_progress = False
_sync_error = ""
_sync_finished = False
_sync_synced = 0
_sync_logs: deque[str] = deque(maxlen=500)
_sync_log_lock = threading.Lock()


def _update_log(message: str) -> None:
    with _update_log_lock:
        _update_logs.append(message.rstrip())


def _update_log_lines() -> list[str]:
    with _update_log_lock:
        return list(_update_logs)


def _sync_log(message: str) -> None:
    with _sync_log_lock:
        _sync_logs.append(message.rstrip())


def _sync_log_lines() -> list[str]:
    with _sync_log_lock:
        return list(_sync_logs)


def _configured_browse_roots() -> list[Path]:
    roots: list[Path] = []
    seen: set[str] = set()
    for raw in BROWSE_ROOTS.split(","):
        value = raw.strip()
        if not value:
            continue
        path = Path(value).expanduser().resolve()
        key = str(path)
        if key in seen:
            continue
        seen.add(key)
        roots.append(path)
    return roots


def _path_under_root(candidate: Path, root: Path) -> bool:
    try:
        candidate.relative_to(root)
        return True
    except ValueError:
        return False


def _path_allowed(candidate: Path, roots: list[Path]) -> bool:
    return any(_path_under_root(candidate, root) for root in roots)


def _normalize_youtube_url(url: str) -> str:
    parsed = urlparse(url)
    host = parsed.netloc.lower()

    if host in {"youtube.com", "www.youtube.com", "m.youtube.com"}:
        query = parse_qs(parsed.query)
        video_id = query.get("v", [""])[0].strip()
        if video_id:
            clean_query = urlencode({"v": video_id})
            return urlunparse((parsed.scheme, parsed.netloc, "/watch", "", clean_query, ""))

    if host == "youtu.be":
        video_id = parsed.path.strip("/")
        if video_id:
            return f"https://youtu.be/{video_id}"

    return url


@app.on_event("startup")
def startup():
    init_db()


def _setup_payload() -> dict:
    return {
        "setupComplete": is_setup_complete(),
        "radarrUrl": get_setting("radarr_url", ""),
        "radarrApiKeySet": bool(get_setting("radarr_api_key", "").strip()),
        "libraryPaths": get_library_paths(),
    }


# ── API ──────────────────────────────────────────────────────────────────────

@app.post("/api/sync")
async def sync_radarr():
    global _sync_in_progress, _sync_error, _sync_finished, _sync_synced

    if not is_setup_complete():
        raise HTTPException(status_code=400, detail="App setup is not complete")

    if _sync_in_progress:
        return {"started": False, "detail": "Sync already in progress"}

    with _sync_lock:
        if _sync_in_progress:
            return {"started": False, "detail": "Sync already in progress"}
        _sync_in_progress = True
        _sync_error = ""
        _sync_finished = False
        _sync_synced = 0
        with _sync_log_lock:
            _sync_logs.clear()

        thread = threading.Thread(target=_run_sync, daemon=True)
        thread.start()

    return {"started": True}


def _run_sync() -> None:
    global _sync_in_progress, _sync_error, _sync_finished, _sync_synced

    try:
        _sync_log("Starting Radarr sync...")
        movies = asyncio.run(fetch_movies(log_fn=_sync_log))
        _sync_log(f"Upserting {len(movies)} matched movies into the local database")
        upsert_movies(movies)
        _sync_synced = len(movies)
        _sync_log(f"Sync complete. {len(movies)} movies available locally.")
    except Exception as exc:
        _sync_error = str(exc)
        _sync_log(f"Sync failed: {exc}")
    finally:
        _sync_finished = True
        _sync_in_progress = False


@app.get("/api/sync/status")
def sync_status():
    return {
        "inProgress": _sync_in_progress,
        "finished": _sync_finished,
        "error": _sync_error,
        "synced": _sync_synced,
        "logs": _sync_log_lines(),
    }


@app.get("/api/setup/status")
def setup_status():
    return _setup_payload()


class SetupRequest(BaseModel):
    radarr_url: str = Field(min_length=1)
    radarr_api_key: str = ""
    library_paths: list[str] = Field(default_factory=list)


@app.post("/api/setup")
def save_setup(req: SetupRequest):
    existing_key = get_setting("radarr_api_key", "").strip()
    api_key = req.radarr_api_key.strip() or existing_key
    if not api_key:
        raise HTTPException(status_code=400, detail="Radarr API key is required")

    library_paths = [p.strip().rstrip("/") for p in req.library_paths if p.strip()]
    if not library_paths:
        raise HTTPException(status_code=400, detail="At least one local library path is required")

    set_setting("radarr_url", req.radarr_url.strip())
    set_setting("radarr_api_key", api_key)
    set_library_paths(library_paths)
    mark_setup_complete()
    return _setup_payload()


@app.post("/api/setup/reset")
def reset_setup():
    reset_app_state()
    return _setup_payload()


@app.get("/api/fs/browse")
def browse_filesystem(path: str | None = Query(default=None)):
    roots = [root for root in _configured_browse_roots() if root.exists() and root.is_dir()]
    if not roots:
        raise HTTPException(status_code=500, detail="No browse roots are available")

    current = Path(path).expanduser().resolve() if path else roots[0]
    if not current.exists() or not current.is_dir():
        raise HTTPException(status_code=404, detail="Directory not found")
    if not _path_allowed(current, roots):
        raise HTTPException(status_code=403, detail="Directory is outside allowed roots")

    entries = []
    for child in sorted(current.iterdir(), key=lambda p: p.name.lower()):
        try:
            resolved = child.resolve()
        except OSError:
            continue

        if not resolved.is_dir() or not _path_allowed(resolved, roots):
            continue

        entries.append({"name": child.name, "path": str(resolved)})

    parent = current.parent.resolve()
    parent_path = str(parent) if parent != current and _path_allowed(parent, roots) else ""

    return {
        "path": str(current),
        "parent": parent_path,
        "roots": [str(root) for root in roots],
        "entries": entries,
    }


@app.get("/api/movies")
def list_movies():
    return get_all_movies()


@app.get("/api/search/{movie_id}")
def search_youtube(movie_id: int):
    movie = get_movie(movie_id)
    if not movie:
        raise HTTPException(status_code=404, detail="Movie not found")

    query = f"{movie['title']} {movie['year']} theme song"
    log.info("YouTube search: %s", query)

    try:
        results = YoutubeSearch(query, max_results=3).to_dict()
    except Exception as exc:
        raise HTTPException(status_code=502, detail=f"YouTube search error: {exc}")

    videos = []
    for r in results:
        vid_id = r.get("id", "")
        # youtube-search returns ids prefixed with /watch?v= sometimes
        if vid_id.startswith("/watch?v="):
            vid_id = vid_id[len("/watch?v="):]
        videos.append(
            {
                "videoId": vid_id,
                "title": r.get("title", ""),
                "thumbnail": r.get("thumbnails", [None])[0],
                "duration": r.get("duration", ""),
                "channel": r.get("channel", ""),
            }
        )
    return {"movie": movie, "results": videos}


class DownloadRequest(BaseModel):
    movie_id: int
    video_id: str


class DownloadUrlRequest(BaseModel):
    movie_id: int
    url: str


def _current_version() -> str:
    env_version = os.getenv("APP_VERSION", "").strip()
    if env_version:
        return env_version

    if VERSION_FILE.exists():
        value = VERSION_FILE.read_text(encoding="utf-8").strip()
        if value:
            return value
    return "dev"


def _latest_main_version() -> str:
    url = f"https://api.github.com/repos/{GITHUB_REPO}/commits/main"
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "themearr"}
    resp = httpx.get(url, timeout=10, headers=headers)
    resp.raise_for_status()
    return resp.json()["sha"][:12]


def _run_update() -> None:
    global _update_in_progress, _update_error, _update_finished
    try:
        _update_log(f"Starting update command: {UPDATER_CMD}")
        proc = subprocess.Popen(
            f"stdbuf -oL -eL {UPDATER_CMD}",
            shell=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
        )
        assert proc.stdout is not None
        for line in proc.stdout:
            _update_log(line)
        return_code = proc.wait()
        _update_log(f"Update command exited with code {return_code}")
        if return_code != 0:
            _update_error = f"Update command exited with code {return_code}"
    except Exception as exc:
        _update_error = str(exc)
        _update_log(f"Update failed: {exc}")
    finally:
        _update_finished = True
        _update_in_progress = False


@app.get("/api/version")
def app_version():
    current = _current_version()
    latest = ""
    check_error = ""

    try:
        latest = _latest_main_version()
    except Exception as exc:
        log.warning("Version check failed: %s", exc)
        check_error = str(exc)

    return {
        "current": current,
        "latest": latest,
        "updateAvailable": bool(latest and current != latest),
        "updating": _update_in_progress,
        "updateError": _update_error,
        "checkError": check_error,
        "repo": GITHUB_REPO,
    }


@app.post("/api/update")
def app_update():
    global _update_in_progress, _update_error, _update_finished, _update_started_at

    if _update_in_progress:
        return {"started": False, "detail": "Update already in progress"}

    with _update_lock:
        if _update_in_progress:
            return {"started": False, "detail": "Update already in progress"}
        _update_in_progress = True
        _update_error = ""
        _update_finished = False
        _update_started_at = "now"
        with _update_log_lock:
            _update_logs.clear()

        thread = threading.Thread(target=_run_update, daemon=True)
        thread.start()

    return {"started": True}


@app.get("/api/update/status")
def update_status():
    return {
        "inProgress": _update_in_progress,
        "finished": _update_finished,
        "error": _update_error,
        "startedAt": _update_started_at,
        "logs": _update_log_lines(),
    }


def _download_theme_for_url(movie_id: int, url: str):
    movie = get_movie(movie_id)
    if not movie:
        raise HTTPException(status_code=404, detail="Movie not found")

    folder = movie["folderName"]
    if not folder:
        raise HTTPException(status_code=400, detail="Movie has no folder path")

    output_template = os.path.join(folder, "theme.%(ext)s")
    normalized_url = _normalize_youtube_url(url)

    cmd = [
        "yt-dlp",
        "-x",
        "--audio-format", "mp3",
        "--audio-quality", "0",
        "--no-playlist",
        "--max-downloads", "1",
        "-o", output_template,
        normalized_url,
    ]
    log.info("Running: %s", " ".join(cmd))

    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    except subprocess.TimeoutExpired:
        raise HTTPException(status_code=504, detail="Download timed out after 15 minutes")

    if proc.returncode != 0:
        log.error("yt-dlp stderr: %s", proc.stderr)
        raise HTTPException(
            status_code=500,
            detail=f"yt-dlp failed (exit {proc.returncode}): {proc.stderr[-500:]}",
        )

    set_status(movie_id, "downloaded")
    return {"status": "downloaded", "movie_id": movie_id}


@app.post("/api/download")
def download_theme(req: DownloadRequest):
    url = f"https://www.youtube.com/watch?v={req.video_id}"
    return _download_theme_for_url(req.movie_id, url)


@app.post("/api/download-url")
def download_theme_url(req: DownloadUrlRequest):
    url = req.url.strip()
    if not (url.startswith("http://") or url.startswith("https://")):
        raise HTTPException(status_code=400, detail="Invalid URL")

    return _download_theme_for_url(req.movie_id, url)


# ── Static files ─────────────────────────────────────────────────────────────

app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")


@app.get("/{full_path:path}")
def serve_spa(full_path: str):
    return FileResponse(str(STATIC_DIR / "index.html"))
