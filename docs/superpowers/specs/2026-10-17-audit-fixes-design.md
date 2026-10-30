# Audit fixes for existing installations

Implement all nineteen findings from the code audit requested in this chat. Existing databases, auth tokens, integration keys, selected libraries, path mappings, downloaded files and public API route shapes must survive upgrading. No production deployment or release is part of this task.

## Safety and compatibility

Native upgrades must leave the live data directory untouched, repair service-writable application/updater ownership, and keep only data service-writable. Keep existing installation paths and systemd service identity. Download and verify/extract to staging before installing application files. Preserve unknown user configuration. Fail accurately on process/download errors and serialize release publication.

Plex discovery must keep tokens server-side while preserving the existing server-object shape and old frontend submissions. Resolve redacted discovery/selection objects against trusted discovered/stored server records. Reusing a credential must reuse only its trusted connection destinations. Movie deselection is explicit per server, like shows.

Concurrent downloads must claim an item atomically and prevent stale completions from overwriting newer attempts. Recheck verdicts must not overwrite a concurrent Keep, Ignore, replacement or delete. Show task state must cover queued/running/completed work accurately. Frontend status polling must wait for the start request and ignore abandoned requests.

## Complete the recheck capability

Keep the existing endpoints and states. The queue gets a shared movie/show "Needs attention" list, current theme/reason, Keep and Find replacement actions, plus Re-check themes with bounded single-flight progress polling. Both dashboard coverage areas show needs-attention counts and navigate to the queue. Existing queue triage and downloaded files remain intact while replacements are found.

Register a background rule-version recheck runner. Delay until setup completes; run once per newer rules version and persist success only after a completed scan. Respect manual provenance and permanent kept decisions. Existing matching/provenance rules remain unchanged; no paid search is added to the judging scan. The implementation must not delete existing themes before a successful replacement.

## Remaining repairs

Treat malformed/skipped Radarr entries as incomplete input for pruning. Report unresolved show paths alongside movie paths. Sanitize show sync callback logs. Make the documented frontend test command work under the configured Node runtime. Preserve optional-checksum handling for older releases.

## Validation

Add regression tests before changes, observing failures against the existing behavior. Never weaken existing tests. Include old-settings/database fixtures, mocked external HTTP, controlled concurrency, and sandboxed shell/update fixtures. Run the full API, frontend, lint, typecheck/build and E2E gate; require zero skips. Run security/concurrency specialist reviews and fresh static/dependency scans. Matching rules and storage schema do not change.

## Implementation references

Trusted credential resolution is in `src/Themearr.API/Data/Database.cs:414`; wizard discovery and redaction start at `src/Themearr.API/Controllers/SetupController.cs:70`. Download admission starts at `src/Themearr.API/Services/DownloadService.cs:80`. Recheck verdict publication uses conditional state/history validation at `src/Themearr.API/Services/ThemeRecheckService.cs:126`. The ordinary queue remains pending-only at `src/Themearr.Web/src/app/queue/page.tsx:124`, with needs-attention items rendered separately. The unchanged rule-version marker is `src/Themearr.API/Services/ThemeMatch.cs:212`.
