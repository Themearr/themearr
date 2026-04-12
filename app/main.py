import asyncio
import logging
import os
import re
import shutil
import subprocess
import threading
from collections import deque
from pathlib import Path
from urllib.parse import parse_qs, urlencode, urlparse, urlunparse

import httpx
from fastapi import FastAPI, HTTPException, Query
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
from youtube_search import YoutubeSearch

from app.database import (
    get_all_movies,
    get_library_paths,
    get_movie,
    get_path_mappings,
    get_plex_servers,
    get_selected_libraries,
    get_setting,
    init_db,
    is_setup_complete,
    mark_setup_complete,
    reset_app_state,
    set_library_paths,
    set_path_mappings,
    set_plex_servers,
    set_selected_libraries,
    set_setting,
    set_status,
    upsert_movies,
)
from app.plex import (
    check_login_pin,
    create_login_pin,
    discover_plex_servers,
    fetch_movies,
    get_client_identifier,
    get_user_info,
    list_server_libraries,
)

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("themearr")

app = FastAPI(title="Themearr")

STATIC_DIR = Path(__file__).parent / "static"
VERSION_FILE = Path(os.getenv("THEMEARR_VERSION_FILE", "/opt/themearr/VERSION"))
GITHUB_REPO = os.getenv("GITHUB_REPO", "Themearr/themearr")


def _default_updater_cmd() -> str:
    configured = os.getenv("THEMEARR_UPDATER_CMD", "").strip()
    if configured:
        return configured

    helper = Path("/usr/local/bin/themearr-update")
    if helper.exists():
        if os.geteuid() == 0:
            return str(helper)
        return f"sudo {helper}"

    # Fallback for install paths that do not provide /usr/local/bin/themearr-update.
    deploy_url = "https://raw.githubusercontent.com/Themearr/themearr/main/deploy.sh"
    if os.geteuid() == 0:
        return (
            "TMP_DEPLOY=/tmp/themearr-deploy.sh && "
            f"curl -fsSL {deploy_url} -o \"$TMP_DEPLOY\" && "
            "bash \"$TMP_DEPLOY\" && "
            "systemctl restart themearr"
        )

    return (
        "TMP_DEPLOY=/tmp/themearr-deploy.sh && "
        f"curl -fsSL {deploy_url} -o \"$TMP_DEPLOY\" && "
        "sudo bash \"$TMP_DEPLOY\" && "
        "sudo systemctl restart themearr"
    )


UPDATER_CMD = _default_updater_cmd()

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
def startup() -> None:
    init_db()
    set_setting("app_version", _current_version())
    get_client_identifier()


def _setup_payload() -> dict:
    plex_connected = bool(get_setting("plex_access_token", "").strip())
    selected_servers = get_plex_servers()
    selected_libraries = get_selected_libraries()
    libraries_count = sum(len(v) for v in selected_libraries.values())
    return {
        "setupComplete": is_setup_complete() and libraries_count > 0,
        "plexConnected": plex_connected,
        "plexAccountName": get_setting("plex_account_name", ""),
        "plexServerName": ", ".join([s.get("name", "") for s in selected_servers if s.get("name")]),
        "plexServerUrl": ", ".join([s.get("url", "") for s in selected_servers if s.get("url")]),
        "selectedServers": selected_servers,
        "selectedLibraries": selected_libraries,
        "pathMappings": get_path_mappings(),
        "libraryPaths": get_library_paths(),
    }


# ── API ──────────────────────────────────────────────────────────────────────

@app.get("/api/setup/status")
def setup_status():
    return _setup_payload()


class PlexLoginRequest(BaseModel):
    forward_url: str = ""


@app.post("/api/setup/plex/login")
def start_plex_login(req: PlexLoginRequest):
    return create_login_pin(req.forward_url.strip())


@app.get("/api/setup/plex/login/status")
def plex_login_status(pin_id: int = Query(...), code: str = Query(...)):
    client_identifier = get_setting("plex_client_identifier", "").strip() or get_client_identifier()

    try:
        pin_state = check_login_pin(pin_id, code, client_identifier)
    except RuntimeError as exc:
        raise HTTPException(status_code=400, detail=str(exc))

    if not pin_state["claimed"]:
        return {
            "claimed": False,
            "connected": False,
            "accountName": get_setting("plex_account_name", ""),
            "serverName": get_setting("plex_server_name", ""),
            "serverUrl": get_setting("plex_server_url", ""),
        }

    set_setting("plex_access_token", pin_state["authToken"])
    try:
        user_info = get_user_info(pin_state["authToken"], client_identifier)
        account_name = str(user_info.get("username") or user_info.get("title") or user_info.get("email") or "Plex user").strip()
    except Exception:
        account_name = "Plex user"
    set_setting("plex_account_name", account_name)

    return {
        "claimed": True,
        "connected": True,
        "needsSelection": True,
        "accountName": account_name,
    }


@app.get("/api/setup/plex/servers")
def plex_servers():
    access_token = get_setting("plex_access_token", "").strip()
    client_identifier = get_setting("plex_client_identifier", "").strip() or get_client_identifier()
    if not access_token:
        raise HTTPException(status_code=400, detail="Plex sign-in is required first")

    try:
        servers = discover_plex_servers(access_token, client_identifier)
    except Exception as exc:
        raise HTTPException(status_code=502, detail=f"Plex server discovery failed: {exc}")

    return {"servers": servers}


class PlexLibrariesRequest(BaseModel):
    servers: list[dict]


@app.post("/api/setup/plex/libraries")
async def plex_libraries(req: PlexLibrariesRequest):
    client_identifier = get_setting("plex_client_identifier", "").strip() or get_client_identifier()
    payload: dict[str, list[dict]] = {}

    for server in req.servers:
        server_id = str(server.get("id", "")).strip()
        server_url = str(server.get("url", "")).strip()
        server_urls = server.get("urls") if isinstance(server.get("urls"), list) else []
        server_token = str(server.get("token", "")).strip()
        if not server_id or not server_url or not server_token:
            continue
        try:
            payload[server_id] = await list_server_libraries(
                server_url,
                server_token,
                client_identifier,
                [str(url) for url in server_urls],
            )
        except Exception as exc:
            raise HTTPException(status_code=502, detail=f"Failed to list libraries for {server_id}: {exc}")

    return {"libraries": payload}


class PlexSelectionRequest(BaseModel):
    servers: list[dict]
    selected_libraries: dict[str, list[str]]
    path_mappings: list[dict] = []
    library_paths: list[str] = []


@app.post("/api/setup/plex/selection")
def save_plex_selection(req: PlexSelectionRequest):
    if not req.servers:
        raise HTTPException(status_code=400, detail="Select at least one Plex server")

    total = 0
    for keys in req.selected_libraries.values():
        total += len(keys)
    if total == 0:
        raise HTTPException(status_code=400, detail="Select at least one movie library")

    set_plex_servers(req.servers)
    set_selected_libraries(req.selected_libraries)
    set_path_mappings(req.path_mappings)
    set_library_paths(req.library_paths)

    # Compatibility keys for older code paths and status display.
    primary = req.servers[0]
    set_setting("plex_server_name", str(primary.get("name", "")).strip())
    set_setting("plex_server_url", str(primary.get("url", "")).strip())
    set_setting("plex_server_token", str(primary.get("token", "")).strip())

    mark_setup_complete()
    return _setup_payload()


@app.get("/api/settings")
def get_settings_payload():
    return {
        "selectedServers": get_plex_servers(),
        "selectedLibraries": get_selected_libraries(),
        "pathMappings": get_path_mappings(),
        "libraryPaths": get_library_paths(),
        "advanced": {
            "maxSearchDirs": int(get_setting("max_search_dirs", "20000") or "20000"),
            "searchDepth": int(get_setting("search_depth", "4") or "4"),
        },
    }


class SettingsPayload(BaseModel):
    selectedServers: list[dict]
    selectedLibraries: dict[str, list[str]]
    pathMappings: list[dict]
    libraryPaths: list[str]
    advanced: dict = {}


@app.post("/api/settings")
def save_settings_payload(req: SettingsPayload):
    set_plex_servers(req.selectedServers)
    set_selected_libraries(req.selectedLibraries)
    set_path_mappings(req.pathMappings)
    set_library_paths(req.libraryPaths)

    max_search_dirs = int(req.advanced.get("maxSearchDirs", 20000) or 20000)
    search_depth = int(req.advanced.get("searchDepth", 4) or 4)
    set_setting("max_search_dirs", str(max(500, min(max_search_dirs, 100000))))
    set_setting("search_depth", str(max(1, min(search_depth, 10))))

    if req.selectedServers:
        primary = req.selectedServers[0]
        set_setting("plex_server_name", str(primary.get("name", "")).strip())
        set_setting("plex_server_url", str(primary.get("url", "")).strip())
        set_setting("plex_server_token", str(primary.get("token", "")).strip())

    if req.selectedServers and sum(len(v) for v in req.selectedLibraries.values()) > 0:
        mark_setup_complete()

    return get_settings_payload()


@app.post("/api/setup/reset")
def reset_setup():
    reset_app_state()
    return _setup_payload()


@app.get("/api/sync")
def sync_status_redirect():
    return {"detail": "Use POST /api/sync"}


@app.post("/api/sync")
async def sync_plex():
    global _sync_in_progress, _sync_error, _sync_finished, _sync_synced

    if not (_setup_payload()["setupComplete"]):
        raise HTTPException(status_code=400, detail="Plex sign-in is not complete")

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
        _sync_log("Starting Plex sync...")
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


@app.get("/api/movies")
def list_movies():
    return get_all_movies()


@app.get("/api/search/{movie_id}")
def search_youtube(movie_id: str):
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
    for result in results:
        video_id = result.get("id", "")
        if video_id.startswith("/watch?v="):
            video_id = video_id[len("/watch?v="):]
        videos.append(
            {
                "videoId": video_id,
                "title": result.get("title", ""),
                "thumbnail": result.get("thumbnails", [None])[0],
                "duration": result.get("duration", ""),
                "channel": result.get("channel", ""),
            }
        )
    return {"movie": movie, "results": videos}


class DownloadRequest(BaseModel):
    movie_id: str
    video_id: str


class DownloadUrlRequest(BaseModel):
    movie_id: str
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


def _latest_release_version() -> str:
    url = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "themearr"}
    response = httpx.get(url, timeout=10, headers=headers)
    response.raise_for_status()
    return str(response.json().get("tag_name", "")).strip()


def _normalize_semver_display(value: str) -> str:
    raw = str(value or "").strip()
    parsed = _parse_semver(raw)
    if not parsed:
        return raw
    major, minor, patch = parsed
    return f"v{major}.{minor}.{patch}"


def _parse_semver(value: str) -> tuple[int, int, int] | None:
    match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", str(value or "").strip())
    if not match:
        return None
    return int(match.group(1)), int(match.group(2)), int(match.group(3))


def _is_update_available(current: str, latest: str) -> bool:
    if str(current or "").strip().lower() in {"", "dev", "unknown"}:
        return False

    current_semver = _parse_semver(current)
    latest_semver = _parse_semver(latest)
    if current_semver and latest_semver:
        return latest_semver > current_semver
    return bool(latest and current != latest)


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
    current = _normalize_semver_display(_current_version())
    latest = ""
    check_error = ""

    try:
        latest = _normalize_semver_display(_latest_release_version())
    except Exception as exc:
        log.warning("Version check failed: %s", exc)
        check_error = str(exc)

    return {
        "current": current,
        "latest": latest,
        "updateAvailable": _is_update_available(current, latest),
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


def _download_theme_for_url(movie_id: str, url: str):
    movie = get_movie(movie_id)
    if not movie:
        raise HTTPException(status_code=404, detail="Movie not found")

    folder = movie["folderName"]
    if not folder:
        raise HTTPException(status_code=400, detail="Movie has no folder path")

    output_template = os.path.join(folder, "theme.%(ext)s")
    normalized_url = _normalize_youtube_url(url)

    if shutil.which("yt-dlp") is None:
        raise HTTPException(status_code=500, detail="yt-dlp is not installed or not in PATH")

    if shutil.which("deno") is None:
        raise HTTPException(status_code=500, detail="deno is not installed or not in PATH")

    cmd = [
        "yt-dlp",
        "-x",
        "--audio-format",
        "mp3",
        "--audio-quality",
        "0",
        "--no-playlist",
        "-o",
        output_template,
        normalized_url,
    ]
    log.info("Running: %s", " ".join(cmd))

    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    except subprocess.TimeoutExpired:
        raise HTTPException(status_code=504, detail="Download timed out after 15 minutes")

    if proc.returncode != 0:
        stderr_tail = (proc.stderr or "").strip()[-1200:]
        stdout_tail = (proc.stdout or "").strip()[-1200:]
        combined_tail = "\n".join(part for part in [stderr_tail, stdout_tail] if part)
        log.error("yt-dlp failed rc=%s stderr=%r stdout=%r", proc.returncode, stderr_tail, stdout_tail)
        raise HTTPException(
            status_code=500,
            detail=f"yt-dlp failed (exit {proc.returncode}): {combined_tail or 'no output captured'}",
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
