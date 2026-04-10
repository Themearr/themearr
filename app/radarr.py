import httpx
import os
import re
from typing import Callable

from app.database import get_setting, get_library_paths


def _normalize(text: str) -> str:
    return re.sub(r"[^a-z0-9]", "", (text or "").lower())


def _iter_library_dirs(root: str) -> list[str]:
    if not os.path.isdir(root):
        return []

    result = []
    with os.scandir(root) as entries:
        for entry in entries:
            if entry.is_dir(follow_symlinks=False):
                result.append(entry.path)
    return result


def _find_local_folder(title: str, year: int | None, library_paths: list[str]) -> str:
    target = _normalize(title)
    year_str = str(year) if year else ""

    best_path = ""
    best_score = -1

    for root in library_paths:
        for folder in _iter_library_dirs(root):
            folder_name = os.path.basename(folder)
            folder_norm = _normalize(folder_name)
            if not target or target not in folder_norm:
                continue

            score = 5
            if folder_norm.startswith(target):
                score += 1
            if year_str and year_str in folder_name:
                score += 2

            if score > best_score:
                best_score = score
                best_path = folder

    return best_path


async def fetch_movies(log_fn: Callable[[str], None] | None = None) -> list[dict]:
    radarr_url = get_setting("radarr_url", "").strip()
    radarr_api_key = get_setting("radarr_api_key", "").strip()
    library_paths = get_library_paths()

    if not radarr_url or not radarr_api_key:
        raise RuntimeError("Radarr settings have not been configured")
    if not library_paths:
        raise RuntimeError("Local library paths have not been configured")

    if log_fn:
        log_fn(f"Using Radarr URL: {radarr_url}")
        log_fn(f"Scanning local library roots: {', '.join(library_paths)}")

    url = f"{radarr_url.rstrip('/')}/api/v3/movie"
    async with httpx.AsyncClient(timeout=30) as client:
        resp = await client.get(url, params={"apikey": radarr_api_key})
        resp.raise_for_status()
        data = resp.json()

    if log_fn:
        log_fn(f"Fetched {len(data)} movies from Radarr")

    result = []
    for m in data:
        title = m.get("title", "")
        year = m.get("year")
        folder = _find_local_folder(title, year, library_paths)
        if not folder:
            if log_fn:
                log_fn(f"No local match: {title} ({year or '?'})")
            continue

        if log_fn:
            log_fn(f"Matched: {title} ({year or '?'}) -> {folder}")

        result.append(
            {
                "id": m["id"],
                "title": title,
                "year": year,
                "folderName": folder,
            }
        )
    return result
