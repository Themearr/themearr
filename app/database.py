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
                id             TEXT PRIMARY KEY,
                plex_server_id TEXT NOT NULL,
                plex_rating_key TEXT NOT NULL,
                title          TEXT NOT NULL,
                year           INTEGER,
                sourcePath     TEXT,
                folderName     TEXT,
                status         TEXT NOT NULL DEFAULT 'pending',
                UNIQUE(plex_server_id, plex_rating_key)
            )
        """)
        conn.execute("""
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
        """)
        _migrate_movies_table(conn)
        conn.commit()


def _migrate_movies_table(conn: sqlite3.Connection):
    columns = conn.execute("PRAGMA table_info(movies)").fetchall()
    column_names = [str(row[1]) for row in columns]
    required = {
        "id",
        "plex_server_id",
        "plex_rating_key",
        "title",
        "year",
        "sourcePath",
        "folderName",
        "status",
    }
    if required.issubset(set(column_names)):
        return

    conn.execute("ALTER TABLE movies RENAME TO movies_legacy")
    conn.execute("""
        CREATE TABLE movies (
            id              TEXT PRIMARY KEY,
            plex_server_id  TEXT NOT NULL,
            plex_rating_key TEXT NOT NULL,
            title           TEXT NOT NULL,
            year            INTEGER,
            sourcePath      TEXT,
            folderName      TEXT,
            status          TEXT NOT NULL DEFAULT 'pending',
            UNIQUE(plex_server_id, plex_rating_key)
        )
    """)

    legacy_columns = set(column_names)
    if {"id", "title", "year", "folderName", "status"}.issubset(legacy_columns):
        rows = conn.execute("SELECT id, title, year, folderName, status FROM movies_legacy").fetchall()
        for row in rows:
            legacy_id = str(row[0])
            composite_id = f"legacy:{legacy_id}"
            conn.execute(
                """
                INSERT INTO movies (
                    id,
                    plex_server_id,
                    plex_rating_key,
                    title,
                    year,
                    sourcePath,
                    folderName,
                    status
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    composite_id,
                    "legacy",
                    legacy_id,
                    row[1],
                    row[2],
                    "",
                    row[3],
                    row[4] or "pending",
                ),
            )

    conn.execute("DROP TABLE movies_legacy")


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
            INSERT INTO movies (
                id,
                plex_server_id,
                plex_rating_key,
                title,
                year,
                sourcePath,
                folderName,
                status
            )
            VALUES (
                :id,
                :plex_server_id,
                :plex_rating_key,
                :title,
                :year,
                :sourcePath,
                :folderName,
                'pending'
            )
            ON CONFLICT(id) DO UPDATE SET
                plex_server_id = excluded.plex_server_id,
                plex_rating_key = excluded.plex_rating_key,
                title = excluded.title,
                year = excluded.year,
                sourcePath = excluded.sourcePath,
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
            """
            SELECT
                id,
                plex_server_id,
                plex_rating_key,
                title,
                year,
                sourcePath,
                folderName,
                status
            FROM movies
            ORDER BY status, title
            """
        ).fetchall()
        movies = []
        for row in rows:
          movie = _hydrate_movie_row(row)
          if movie:
              movies.append(movie)
        return movies


def get_movie(movie_id: str) -> dict | None:
    with get_conn() as conn:
        row = conn.execute(
            """
            SELECT
                id,
                plex_server_id,
                plex_rating_key,
                title,
                year,
                sourcePath,
                folderName,
                status
            FROM movies
            WHERE id = ?
            """,
            (movie_id,),
        ).fetchone()
        if not row:
            return None

        return _hydrate_movie_row(row)


def set_status(movie_id: str, status: str):
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


def get_json_setting(key: str, default):
    raw = get_setting(key, "")
    if not raw:
        return default
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return default


def set_json_setting(key: str, value):
    set_setting(key, json.dumps(value))


def get_plex_servers() -> list[dict]:
    value = get_json_setting("plex_selected_servers", [])
    return value if isinstance(value, list) else []


def set_plex_servers(servers: list[dict]):
    normalized = []
    for server in servers:
        if not isinstance(server, dict):
            continue
        server_id = str(server.get("id", "")).strip()
        if not server_id:
            continue
        normalized.append(
            {
                "id": server_id,
                "name": str(server.get("name", "")).strip(),
                "url": str(server.get("url", "")).strip().rstrip("/"),
                "token": str(server.get("token", "")).strip(),
                "owned": bool(server.get("owned", False)),
                "presence": bool(server.get("presence", False)),
            }
        )
    set_json_setting("plex_selected_servers", normalized)


def get_selected_libraries() -> dict[str, list[str]]:
    value = get_json_setting("plex_selected_libraries", {})
    if not isinstance(value, dict):
        return {}
    result: dict[str, list[str]] = {}
    for server_id, keys in value.items():
        sid = str(server_id).strip()
        if not sid:
            continue
        if not isinstance(keys, list):
            continue
        result[sid] = [str(k).strip() for k in keys if str(k).strip()]
    return result


def set_selected_libraries(value: dict[str, list[str]]):
    normalized: dict[str, list[str]] = {}
    for server_id, keys in value.items():
        sid = str(server_id).strip()
        if not sid:
            continue
        unique = []
        for key in keys:
            library_key = str(key).strip()
            if library_key and library_key not in unique:
                unique.append(library_key)
        normalized[sid] = unique
    set_json_setting("plex_selected_libraries", normalized)


def get_path_mappings() -> list[dict]:
    data = get_json_setting("path_mappings", [])
    if not isinstance(data, list):
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
    set_json_setting("path_mappings", normalized)


def get_library_paths() -> list[str]:
    values = get_json_setting("library_paths", [])
    if not isinstance(values, list):
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
    set_json_setting("library_paths", normalized)


def is_setup_complete() -> bool:
    return get_setting("setup_complete", "0") == "1"


def mark_setup_complete():
    set_setting("setup_complete", "1")


def reset_app_state():
    with get_conn() as conn:
        conn.execute("DELETE FROM movies")
        conn.execute("DELETE FROM settings")
        conn.commit()
