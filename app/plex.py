import os
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Callable
from urllib.parse import parse_qsl, quote, urlencode, urlparse, urlunparse

import httpx

from app.database import (
    get_library_paths,
    get_path_mappings,
    get_plex_servers,
    get_selected_libraries,
    get_setting,
    set_setting,
)

PLEX_API_BASE = "https://plex.tv/api/v2"
PLEX_AUTH_BASE = "https://app.plex.tv/auth#?"
PLEX_PRODUCT = "Themearr"
PLEX_PLATFORM = "Web"


def _client_headers(client_identifier: str, access_token: str | None = None) -> dict[str, str]:
    headers = {
        "Accept": "application/xml",
        "X-Plex-Product": PLEX_PRODUCT,
        "X-Plex-Platform": PLEX_PLATFORM,
        "X-Plex-Device": PLEX_PRODUCT,
        "X-Plex-Client-Identifier": client_identifier,
        "X-Plex-Version": get_setting("app_version", "dev"),
    }
    if access_token:
        headers["X-Plex-Token"] = access_token
    return headers


def _plex_client_values(client_identifier: str) -> dict[str, str]:
    return {
        "X-Plex-Product": PLEX_PRODUCT,
        "X-Plex-Platform": PLEX_PLATFORM,
        "X-Plex-Device": PLEX_PRODUCT,
        "X-Plex-Client-Identifier": client_identifier,
        "X-Plex-Version": get_setting("app_version", "dev"),
    }


def _augment_forward_url(forward_url: str, pin_id: int, code: str) -> str:
    value = forward_url.strip()
    if not value:
        return ""

    parsed = urlparse(value)
    query = dict(parse_qsl(parsed.query, keep_blank_values=True))
    query["plexPinId"] = str(pin_id)
    query["plexCode"] = code
    return urlunparse((parsed.scheme, parsed.netloc, parsed.path, parsed.params, urlencode(query), parsed.fragment))


def _create_client() -> httpx.Client:
    return httpx.Client(timeout=30)


def _create_async_client() -> httpx.AsyncClient:
    return httpx.AsyncClient(timeout=30)


def get_client_identifier() -> str:
    client_identifier = get_setting("plex_client_identifier", "").strip()
    if not client_identifier:
        client_identifier = str(uuid.uuid4())
        set_setting("plex_client_identifier", client_identifier)
    return client_identifier


def create_login_pin(forward_url: str = "") -> dict:
    client_identifier = get_client_identifier()
    with _create_client() as client:
        response = client.post(
            f"{PLEX_API_BASE}/pins",
            data={
                "strong": "true",
                **_plex_client_values(client_identifier),
            },
            headers=_client_headers(client_identifier),
        )
        response.raise_for_status()
        payload = _coerce_payload(response)

    pin_id = int(payload.get("id", 0) or 0)
    code = str(payload.get("code", "")).strip()
    if not pin_id or not code:
        raise RuntimeError("Plex did not return a valid login PIN")

    effective_forward_url = _augment_forward_url(forward_url, pin_id, code)

    return {
        "pinId": pin_id,
        "code": code,
        "clientIdentifier": client_identifier,
        "authUrl": build_auth_url(code, client_identifier, effective_forward_url),
    }


def build_auth_url(code: str, client_identifier: str, forward_url: str = "") -> str:
    params = urlencode(
        {
            "clientID": client_identifier,
            "code": code,
            "context[device][product]": PLEX_PRODUCT,
            **({"forwardUrl": forward_url} if forward_url else {}),
        },
        quote_via=quote,
    )
    return f"{PLEX_AUTH_BASE}{params}"


def check_login_pin(pin_id: int, code: str, client_identifier: str) -> dict:
    with _create_client() as client:
        response = client.get(
            f"{PLEX_API_BASE}/pins/{pin_id}",
            params={
                "code": code,
                **_plex_client_values(client_identifier),
            },
            headers=_client_headers(client_identifier),
        )

        if response.status_code == 404:
            raise RuntimeError("The Plex login PIN expired. Please try again.")

        response.raise_for_status()
        payload = _coerce_payload(response)

    auth_token = str(payload.get("authToken", "") or "").strip()
    return {
        "claimed": bool(auth_token),
        "authToken": auth_token,
        "id": int(payload.get("id", pin_id) or pin_id),
        "code": str(payload.get("code", code) or code),
        "expiresAt": str(payload.get("expiresAt", "") or ""),
    }


def _coerce_payload(response: httpx.Response) -> dict:
    content_type = response.headers.get("content-type", "").lower()
    if "json" in content_type:
        return response.json()

    text = response.text.strip()
    if not text:
        return {}

    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        return {}

    payload = dict(root.attrib)
    for child in root:
        tag = child.tag.split("}")[-1]
        payload[tag] = child.attrib if child.attrib else (child.text or "")
    return payload


def _coerce_elements(response: httpx.Response) -> tuple[ET.Element | None, list[dict]]:
    content_type = response.headers.get("content-type", "").lower()
    if "json" in content_type:
        payload = response.json()
        return None, payload if isinstance(payload, list) else []

    text = response.text.strip()
    if not text:
        return None, []

    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        return None, []

    return root, []


def _parse_user_payload(response: httpx.Response) -> dict:
    payload = _coerce_payload(response)
    if payload:
        return payload

    return {}


def get_user_info(access_token: str, client_identifier: str) -> dict:
    with _create_client() as client:
        response = client.get(
            f"{PLEX_API_BASE}/user",
            params={
                **_plex_client_values(client_identifier),
                "X-Plex-Token": access_token,
            },
            headers=_client_headers(client_identifier, access_token),
        )
        response.raise_for_status()
        return _parse_user_payload(response)


def _rank_server_connections(resource: dict) -> list[str]:
    connections = resource.get("connections") or []
    if not isinstance(connections, list):
        connections = []

    ranked: list[dict] = []
    for connection in connections:
        if not isinstance(connection, dict):
            continue
        ranked.append(connection)

    ranked.sort(
        key=lambda connection: (
            str(connection.get("local", "")).lower() not in {"1", "true"},
            str(connection.get("protocol", "")).lower() != "https",
        )
    )

    uris: list[str] = []
    for connection in ranked:
        uri = str(connection.get("uri", "") or "").strip().rstrip("/")
        if uri and uri not in uris:
            uris.append(uri)

    uri = str(resource.get("uri", "") or "").strip().rstrip("/")
    if uri and uri not in uris:
        uris.append(uri)

    return uris


def _parse_resources(response: httpx.Response) -> list[dict]:
    content_type = response.headers.get("content-type", "").lower()
    if "json" in content_type:
        payload = response.json()
        if isinstance(payload, list):
            return payload
        if isinstance(payload, dict):
            return payload.get("devices", []) if isinstance(payload.get("devices", []), list) else []
        return []

    root = ET.fromstring(response.text)
    devices: list[dict] = []
    for device in root.findall("Device"):
        resource = dict(device.attrib)
        resource["connections"] = [dict(connection.attrib) for connection in device.findall("Connection")]
        devices.append(resource)
    return devices


def _coerce_bool(value: str) -> bool:
    return str(value or "").strip().lower() in {"1", "true", "yes", "on"}


def discover_plex_servers(access_token: str, client_identifier: str) -> list[dict]:
    with _create_client() as client:
        response = client.get(
            "https://plex.tv/api/resources",
            params={
                "includeHttps": "1",
                "includeRelay": "1",
                **_plex_client_values(client_identifier),
                "X-Plex-Token": access_token,
            },
            headers=_client_headers(client_identifier, access_token),
        )
        response.raise_for_status()
        resources = _parse_resources(response)

    servers: list[dict] = []
    for resource in resources:
        if "server" not in str(resource.get("provides", "")).lower():
            continue

        server_id = str(resource.get("clientIdentifier", "") or "").strip()
        if not server_id:
            continue

        server_urls = _rank_server_connections(resource)
        if not server_urls:
            continue

        server_url = server_urls[0]

        servers.append(
            {
                "id": server_id,
                "name": str(resource.get("name", "") or "").strip() or server_url,
                "url": server_url,
                "urls": server_urls,
                "token": str(resource.get("accessToken", "") or access_token).strip(),
                "owned": _coerce_bool(resource.get("owned", "")),
                "presence": _coerce_bool(resource.get("presence", "")),
            }
        )

    servers.sort(key=lambda s: (not s.get("owned", False), not s.get("presence", False), s.get("name", "")))
    return servers


def resolve_plex_session(access_token: str, client_identifier: str) -> dict:
    user_info = get_user_info(access_token, client_identifier)
    servers = discover_plex_servers(access_token, client_identifier)

    if not servers:
        raise RuntimeError("No Plex Media Server was found for this account")

    selected = servers[0]
    server_url = str(selected["url"]).strip()
    server_token = str(selected["token"]).strip()
    server_name = str(selected["name"]).strip()

    if not server_url:
        raise RuntimeError("Unable to determine a usable Plex server URL")

    return {
        "accountName": str(user_info.get("username") or user_info.get("title") or user_info.get("email") or "Plex user").strip(),
        "serverName": server_name or server_url,
        "serverUrl": server_url,
        "serverToken": server_token,
    }


def _parse_library_sections(response: httpx.Response) -> list[dict]:
    root = ET.fromstring(response.text)
    sections: list[dict] = []
    for directory in root.findall("Directory"):
        sections.append(dict(directory.attrib))
    return sections


def _parse_movie_items(response: httpx.Response) -> list[dict]:
    root = ET.fromstring(response.text)
    items: list[dict] = []
    for video in root.findall("Video"):
        item = dict(video.attrib)
        item["media"] = []
        for media in video.findall("Media"):
            media_item = dict(media.attrib)
            media_item["parts"] = [dict(part.attrib) for part in media.findall("Part")]
            item["media"].append(media_item)
        items.append(item)
    return items


async def _get_xml(client: httpx.AsyncClient, url: str, client_identifier: str, access_token: str, params: dict | None = None) -> httpx.Response:
    request_params = dict(params or {})
    request_params["X-Plex-Token"] = access_token
    response = await client.get(url, params=request_params, headers=_client_headers(client_identifier, access_token))
    response.raise_for_status()
    return response


async def _fetch_movie_items_for_section(
    client: httpx.AsyncClient,
    server_url: str,
    section_key: str,
    client_identifier: str,
    access_token: str,
) -> list[dict]:
    items: list[dict] = []
    page_size = 200
    start = 0

    while True:
        response = await _get_xml(
            client,
            f"{server_url.rstrip('/')}/library/sections/{section_key}/all",
            client_identifier,
            access_token,
            {
                "type": "1",
                "X-Plex-Container-Start": str(start),
                "X-Plex-Container-Size": str(page_size),
            },
        )
        root = ET.fromstring(response.text)
        items.extend(_parse_movie_items(response))

        size = int(root.attrib.get("size", "0") or 0)
        total_size = int(root.attrib.get("totalSize", str(size)) or size)
        if size <= 0 or start + size >= total_size:
            break
        start += size

    return items


def _first_media_file(item: dict) -> str:
    for media in item.get("media", []):
        if not isinstance(media, dict):
            continue
        for part in media.get("parts", []):
            if not isinstance(part, dict):
                continue
            file_path = str(part.get("file", "") or "").strip()
            if file_path:
                return file_path
    return ""


def _normalize_path(value: str) -> str:
    return str(value or "").strip().rstrip("/")


def _apply_path_mappings(source_file_path: str) -> str:
    source_value = _normalize_path(source_file_path)
    if not source_value:
        return ""

    source_parent = _normalize_path(str(Path(source_value).expanduser().parent))
    for mapping in get_path_mappings():
        source_prefix = _normalize_path(mapping.get("source", ""))
        target_prefix = _normalize_path(mapping.get("target", ""))
        if not source_prefix or not target_prefix:
            continue

        if source_parent == source_prefix:
            return target_prefix

        prefixed = f"{source_prefix}/"
        if source_parent.startswith(prefixed):
            suffix = source_parent[len(source_prefix):].lstrip("/")
            return _normalize_path(os.path.join(target_prefix, suffix))

    return source_parent


def _source_parent(source_file_path: str) -> str:
    source_value = _normalize_path(source_file_path)
    if not source_value:
        return ""
    return _normalize_path(str(Path(source_value).expanduser().parent))


def _path_parts(path: str) -> list[str]:
    cleaned = _normalize_path(path)
    if not cleaned:
        return []
    return [part for part in cleaned.split("/") if part]


def _find_path_by_source_suffix(source_file_path: str) -> str:
    roots = [root for root in get_library_paths() if os.path.isdir(root)]
    if not roots:
        return ""

    source_parent = _source_parent(source_file_path)
    source_parts = _path_parts(source_parent)
    if not source_parts:
        return ""

    # Try deterministic suffix joins first (fast path).
    max_suffix_parts = min(6, len(source_parts))
    for root in roots:
        normalized_root = _normalize_path(root)
        for size in range(max_suffix_parts, 0, -1):
            suffix = source_parts[-size:]
            candidate = _normalize_path(os.path.join(normalized_root, *suffix))
            if candidate and os.path.isdir(candidate):
                return candidate

    # Bounded scan fallback using folder basename from Plex source path.
    target_basename = source_parts[-1].lower()
    if not target_basename:
        return ""

    max_dirs = int(get_setting("max_search_dirs", "20000") or "20000")
    max_depth = int(get_setting("search_depth", "4") or "4")
    visited = 0

    for root in roots:
        for current, dirs, _files in os.walk(root):
            visited += 1
            if visited > max_dirs:
                return ""

            current_basename = os.path.basename(current).strip().lower()
            if current_basename == target_basename:
                return _normalize_path(current)

            rel = os.path.relpath(current, root)
            depth = 0 if rel == "." else rel.count(os.sep) + 1
            if depth >= max_depth:
                dirs[:] = []

    return ""


def resolve_local_folder(source_file_path: str) -> tuple[str, str]:
    direct_source_parent = _source_parent(source_file_path)
    if direct_source_parent and os.path.isdir(direct_source_parent):
        return direct_source_parent, "direct"

    mapped_folder = _apply_path_mappings(source_file_path)
    if mapped_folder and os.path.isdir(mapped_folder):
        return mapped_folder, "mapping"

    suffix_match = _find_path_by_source_suffix(source_file_path)
    if suffix_match and os.path.isdir(suffix_match):
        return suffix_match, "suffix"

    return "", "unresolved"


def _movie_record_id(server_id: str, rating_key: str) -> str:
    return f"{server_id}:{rating_key}"


def _candidate_server_urls(server: dict) -> list[str]:
    urls: list[str] = []
    raw_urls = server.get("urls")
    if isinstance(raw_urls, list):
        for entry in raw_urls:
            value = str(entry or "").strip().rstrip("/")
            if value and value not in urls:
                urls.append(value)

    primary = str(server.get("url", "") or "").strip().rstrip("/")
    if primary and primary not in urls:
        urls.insert(0, primary)

    return urls


async def _list_movie_sections_with_fallback(
    client: httpx.AsyncClient,
    server_urls: list[str],
    server_token: str,
    client_identifier: str,
) -> tuple[str, list[dict]]:
    last_exc: Exception | None = None
    for url in server_urls:
        try:
            sections_response = await _get_xml(
                client,
                f"{url}/library/sections",
                client_identifier,
                server_token,
            )
            return url, _parse_library_sections(sections_response)
        except Exception as exc:
            last_exc = exc

    if last_exc:
        raise last_exc
    raise RuntimeError("No usable Plex server URL was provided")


async def list_server_libraries(
    server_url: str,
    server_token: str,
    client_identifier: str,
    server_urls: list[str] | None = None,
) -> list[dict]:
    candidates = list(server_urls or [])
    candidates.append(server_url)
    server_urls = []
    for value in candidates:
        normalized = str(value or "").strip().rstrip("/")
        if normalized and normalized not in server_urls:
            server_urls.append(normalized)

    if not server_urls:
        return []

    async with _create_async_client() as client:
        _selected_url, sections = await _list_movie_sections_with_fallback(
            client,
            server_urls,
            server_token,
            client_identifier,
        )

    libraries: list[dict] = []
    for section in sections:
        if str(section.get("type", "")).lower() != "movie":
            continue
        section_key = str(section.get("key", "")).strip()
        if not section_key:
            continue
        libraries.append(
            {
                "key": section_key,
                "title": str(section.get("title", "Movies") or "Movies"),
                "type": "movie",
            }
        )
    return libraries


async def fetch_movies(log_fn: Callable[[str], None] | None = None) -> list[dict]:
    access_token = get_setting("plex_access_token", "").strip()
    client_identifier = get_setting("plex_client_identifier", "").strip()

    if not access_token or not client_identifier:
        raise RuntimeError("Plex sign-in has not been completed")

    selected_servers = get_plex_servers()
    selected_libraries = get_selected_libraries()
    if not selected_servers:
        session = resolve_plex_session(access_token, client_identifier)
        selected_servers = [
            {
                "id": "legacy",
                "name": session["serverName"],
                "url": session["serverUrl"],
                "token": session["serverToken"],
                "owned": True,
                "presence": True,
            }
        ]

    if not selected_servers:
        raise RuntimeError("No Plex servers are selected")

    async with _create_async_client() as client:
        result: list[dict] = []
        seen_ids: set[str] = set()

        for server in selected_servers:
            server_id = str(server.get("id", "")).strip()
            server_name = str(server.get("name", "")).strip()
            server_urls = _candidate_server_urls(server)
            server_token = str(server.get("token", "")).strip()
            if not server_id or not server_urls or not server_token:
                continue

            if log_fn:
                log_fn(f"Using Plex server: {server_name or server_urls[0]}")

            active_server_url, sections = await _list_movie_sections_with_fallback(
                client,
                server_urls,
                server_token,
                client_identifier,
            )
            movie_sections = [section for section in sections if str(section.get("type", "")).lower() == "movie"]
            selected_keys = set(selected_libraries.get(server_id, []))
            if selected_keys:
                movie_sections = [section for section in movie_sections if str(section.get("key", "")).strip() in selected_keys]

            if log_fn:
                log_fn(f"Found {len(movie_sections)} selected movie libraries on {server_name or server_id}")

            for section in movie_sections:
                section_title = str(section.get("title", "Movies") or "Movies")
                section_key = str(section.get("key", "") or "").strip()
                if not section_key:
                    continue

                if log_fn:
                    log_fn(f"Scanning library: {section_title}")

                items = await _fetch_movie_items_for_section(
                    client,
                    active_server_url,
                    section_key,
                    client_identifier,
                    server_token,
                )

                for item in items:
                    rating_key = str(item.get("ratingKey", "") or "").strip()
                    if not rating_key:
                        continue

                    movie_id = _movie_record_id(server_id, rating_key)
                    if movie_id in seen_ids:
                        continue

                    file_path = _first_media_file(item)
                    if not file_path:
                        if log_fn:
                            log_fn(f"Skipping {item.get('title', 'Unknown title')} - no media file path available")
                        continue

                    title = str(item.get("title", "") or "").strip()
                    year_value = item.get("year")
                    year = int(year_value) if str(year_value or "").isdigit() else None

                    resolved_folder, resolution_mode = resolve_local_folder(file_path)
                    if not resolved_folder:
                        if log_fn:
                            log_fn(f"Skipping {title or 'Unknown title'} - unresolved path from Plex metadata: {file_path}")
                        continue

                    if log_fn:
                        log_fn(f"Matched: {title} ({year or '?'}) -> {resolved_folder} [{resolution_mode}]")

                    seen_ids.add(movie_id)
                    result.append(
                        {
                            "id": movie_id,
                            "plex_server_id": server_id,
                            "plex_rating_key": rating_key,
                            "title": title,
                            "year": year,
                            "sourcePath": file_path,
                            "folderName": resolved_folder,
                        }
                    )

    return result