import os
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Callable
from urllib.parse import quote, urlencode

import httpx

from app.database import get_setting, set_setting

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


def create_login_pin() -> dict:
    client_identifier = get_client_identifier()
    with _create_client() as client:
        response = client.post(
            f"{PLEX_API_BASE}/pins",
            data={"strong": "true"},
            headers=_client_headers(client_identifier),
        )
        response.raise_for_status()
        payload = _coerce_payload(response)

    pin_id = int(payload.get("id", 0) or 0)
    code = str(payload.get("code", "")).strip()
    if not pin_id or not code:
        raise RuntimeError("Plex did not return a valid login PIN")

    return {
        "pinId": pin_id,
        "code": code,
        "clientIdentifier": client_identifier,
        "authUrl": build_auth_url(code, client_identifier),
    }


def build_auth_url(code: str, client_identifier: str) -> str:
    params = urlencode(
        {
            "clientID": client_identifier,
            "code": code,
            "context[device][product]": PLEX_PRODUCT,
        },
        quote_via=quote,
    )
    return f"{PLEX_AUTH_BASE}{params}"


def check_login_pin(pin_id: int, code: str, client_identifier: str) -> dict:
    with _create_client() as client:
        response = client.get(
            f"{PLEX_API_BASE}/pins/{pin_id}",
            params={"code": code},
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
            headers=_client_headers(client_identifier, access_token),
        )
        response.raise_for_status()
        return _parse_user_payload(response)


def _pick_server_connection(resource: dict) -> str:
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

    for connection in ranked:
        uri = str(connection.get("uri", "") or "").strip()
        if uri:
            return uri.rstrip("/")

    uri = str(resource.get("uri", "") or "").strip()
    if uri:
        return uri.rstrip("/")

    return ""


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


def resolve_plex_session(access_token: str, client_identifier: str) -> dict:
    user_info = get_user_info(access_token, client_identifier)

    with _create_client() as client:
        response = client.get(
            f"{PLEX_API_BASE}/resources",
            params={"includeHttps": "1", "includeRelay": "1"},
            headers=_client_headers(client_identifier, access_token),
        )
        response.raise_for_status()
        resources = _parse_resources(response)

    servers = [
        resource
        for resource in resources
        if "server" in str(resource.get("provides", "")).lower()
    ]

    if not servers:
        raise RuntimeError("No Plex Media Server was found for this account")

    servers.sort(
        key=lambda resource: (
            str(resource.get("owned", "")).lower() not in {"1", "true"},
            str(resource.get("presence", "")).lower() not in {"1", "true"},
        )
    )

    selected = servers[0]
    server_url = _pick_server_connection(selected)
    server_token = str(selected.get("accessToken", "") or access_token).strip()
    server_name = str(selected.get("name", "") or "").strip()

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


async def fetch_movies(log_fn: Callable[[str], None] | None = None) -> list[dict]:
    access_token = get_setting("plex_access_token", "").strip()
    client_identifier = get_setting("plex_client_identifier", "").strip()
    server_url = get_setting("plex_server_url", "").strip()
    server_token = get_setting("plex_server_token", "").strip()
    server_name = get_setting("plex_server_name", "").strip()

    if not access_token or not client_identifier:
        raise RuntimeError("Plex sign-in has not been completed")

    if not server_url or not server_token:
        session = resolve_plex_session(access_token, client_identifier)
        server_url = session["serverUrl"]
        server_token = session["serverToken"]
        server_name = session["serverName"]
        set_setting("plex_server_url", server_url)
        set_setting("plex_server_token", server_token)
        set_setting("plex_server_name", server_name)
        set_setting("plex_account_name", session["accountName"])

    if log_fn:
        log_fn(f"Using Plex server: {server_name or server_url}")

    async with _create_async_client() as client:
        sections_response = await _get_xml(
            client,
            f"{server_url.rstrip('/')}/library/sections",
            client_identifier,
            server_token,
        )
        sections = _parse_library_sections(sections_response)
        movie_sections = [section for section in sections if str(section.get("type", "")).lower() == "movie"]

        if log_fn:
            log_fn(f"Found {len(movie_sections)} Plex movie libraries")

        result: list[dict] = []
        seen_ids: set[int] = set()

        for section in movie_sections:
            section_title = str(section.get("title", "Movies") or "Movies")
            section_key = str(section.get("key", "") or "").strip()
            if not section_key:
                continue

            if log_fn:
                log_fn(f"Scanning library: {section_title}")

            items = await _fetch_movie_items_for_section(
                client,
                server_url,
                section_key,
                client_identifier,
                server_token,
            )

            for item in items:
                rating_key_raw = str(item.get("ratingKey", "") or "").strip()
                if not rating_key_raw.isdigit():
                    continue

                movie_id = int(rating_key_raw)
                if movie_id in seen_ids:
                    continue

                file_path = _first_media_file(item)
                if not file_path:
                    if log_fn:
                        log_fn(f"Skipping {item.get('title', 'Unknown title')} - no media file path available")
                    continue

                folder = str(Path(file_path).expanduser().resolve().parent)
                if not os.path.isdir(folder):
                    if log_fn:
                        log_fn(f"Skipping {item.get('title', 'Unknown title')} - folder not found: {folder}")
                    continue

                title = str(item.get("title", "") or "").strip()
                year_value = item.get("year")
                year = int(year_value) if str(year_value or "").isdigit() else None

                if log_fn:
                    log_fn(f"Matched: {title} ({year or '?'}) -> {folder}")

                seen_ids.add(movie_id)
                result.append(
                    {
                        "id": movie_id,
                        "title": title,
                        "year": year,
                        "folderName": folder,
                    }
                )

    return result