# Security Policy

## Supported versions

Only the **latest release** is supported. Themearr ships an in-app updater
(Settings → Updates) and versioned Docker images, so please update to the
newest version before reporting — the issue may already be fixed.

| Version | Supported |
| ------- | --------- |
| Latest release | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Instead, use GitHub's private vulnerability reporting:
[**Report a vulnerability**](https://github.com/Themearr/themearr/security/advisories/new)
(also reachable via the repository's **Security** tab).

You'll get a response as soon as possible — this is a solo-maintained
project, so please allow a few days. Confirmed vulnerabilities will be fixed
in a new release and credited to you in the advisory unless you prefer
otherwise.

## Scope

Themearr is designed to run on a private network behind its access token
(`THEMEARR_AUTH_TOKEN`), with Docker binding to `127.0.0.1` by default.
Reports in these areas are especially valuable:

- Authentication bypass (anything reachable without a valid token)
- SSRF via user-supplied URLs (Radarr/Plex endpoints, custom download URLs)
- Path traversal / writes outside configured movie library folders
- Injection of any kind (SQL, command, etc.)

Reports that assume the app is deliberately exposed to the public internet
without a reverse proxy are still welcome, but that deployment is already
documented as unsupported in the README.
