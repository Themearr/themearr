import sqlite3
import os
import json
from contextlib import contextmanager

DB_PATH = os.getenv("DB_PATH", "/opt/themearr/data/themearr.db")


def init_db():
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    with get_conn() as conn:
        conn.execute("""
            CREATE TABLE IF NOT EXISTS movies (
                id          INTEGER PRIMARY KEY,
                title       TEXT NOT NULL,
                year        INTEGER,
                folderName  TEXT,
                status      TEXT NOT NULL DEFAULT 'pending'
            )
        """)
        conn.execute("""
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
        """)
        conn.commit()


@contextmanager
def get_conn():
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    try:
        yield conn
    finally:
        conn.close()


def upsert_movies(movies: list[dict]):
    with get_conn() as conn:
        conn.executemany(
            """
            INSERT INTO movies (id, title, year, folderName, status)
            VALUES (:id, :title, :year, :folderName, 'pending')
            ON CONFLICT(id) DO UPDATE SET
                title      = excluded.title,
                year       = excluded.year,
                folderName = excluded.folderName
            """,
            movies,
        )
        conn.commit()


def _movie_folder_exists(folder_name: str | None) -> bool:
    return bool(folder_name) and os.path.isdir(folder_name)


def _theme_file_exists(folder_name: str | None) -> bool:
    if not _movie_folder_exists(folder_name):
        return False
    return os.path.isfile(os.path.join(folder_name, "theme.mp3"))


def _hydrate_movie_row(row: sqlite3.Row) -> dict | None:
    movie = dict(row)
    folder_name = movie.get("folderName") or ""
    if not _movie_folder_exists(folder_name):
        return None

    movie["status"] = "downloaded" if _theme_file_exists(folder_name) else "pending"
    return movie


def get_all_movies() -> list[dict]:
    with get_conn() as conn:
        rows = conn.execute(
            "SELECT id, title, year, folderName, status FROM movies ORDER BY status, title"
        ).fetchall()
        movies = []
        for row in rows:
          movie = _hydrate_movie_row(row)
          if movie:
              movies.append(movie)
        return movies


def get_movie(movie_id: int) -> dict | None:
    with get_conn() as conn:
        row = conn.execute(
            "SELECT id, title, year, folderName, status FROM movies WHERE id = ?",
            (movie_id,),
        ).fetchone()
        if not row:
            return None

        return _hydrate_movie_row(row)


def set_status(movie_id: int, status: str):
    with get_conn() as conn:
        conn.execute("UPDATE movies SET status = ? WHERE id = ?", (status, movie_id))
        conn.commit()


def get_setting(key: str, default: str = "") -> str:
    with get_conn() as conn:
        row = conn.execute("SELECT value FROM settings WHERE key = ?", (key,)).fetchone()
        return row[0] if row else default


def set_setting(key: str, value: str):
    with get_conn() as conn:
        conn.execute(
            """
            INSERT INTO settings (key, value)
            VALUES (?, ?)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """,
            (key, value),
        )
        conn.commit()


def get_path_mappings() -> list[dict]:
    raw = get_setting("path_mappings", "[]")
    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return []

    result = []
    for item in data:
        source = str(item.get("source", "")).strip().rstrip("/")
        target = str(item.get("target", "")).strip().rstrip("/")
        if source and target:
            result.append({"source": source, "target": target})
    return result


def set_path_mappings(mappings: list[dict]):
    normalized = []
    for item in mappings:
        source = str(item.get("source", "")).strip().rstrip("/")
        target = str(item.get("target", "")).strip().rstrip("/")
        if source and target:
            normalized.append({"source": source, "target": target})
    set_setting("path_mappings", json.dumps(normalized))


def get_library_paths() -> list[str]:
    raw = get_setting("library_paths", "[]")
    try:
        values = json.loads(raw)
    except json.JSONDecodeError:
        values = []

    result = []
    for value in values:
        path = str(value).strip().rstrip("/")
        if path:
            result.append(path)

    # Backward compatibility for older installs that only have path_mappings saved.
    if not result:
        for item in get_path_mappings():
            target = str(item.get("target", "")).strip().rstrip("/")
            if target and target not in result:
                result.append(target)

    return result


def set_library_paths(paths: list[str]):
    normalized = []
    for value in paths:
        path = str(value).strip().rstrip("/")
        if path and path not in normalized:
            normalized.append(path)
    set_setting("library_paths", json.dumps(normalized))


def is_setup_complete() -> bool:
    return get_setting("setup_complete", "0") == "1"


def mark_setup_complete():
    set_setting("setup_complete", "1")


def reset_app_state():
    with get_conn() as conn:
        conn.execute("DELETE FROM movies")
        conn.execute("DELETE FROM settings")
        conn.commit()
