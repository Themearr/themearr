# #49 Audio Processing and Repair Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans, or superpowers:subagent-driven-development if selected by the user. This is a staged feature plan; do not perform production repair or deployment as part of implementation tests.

**Goal:** Offer optional smaller, normalized themes and a previewed, recoverable repair of existing source files.

**Architecture:** Add a local-file processor after either download source, reuse the shared theme commit boundary, then layer a distinct repair preview/job/ledger over the same processor. Ship documentation, new-download processing and repair as separate reviewable PRs.

**Tech Stack:** .NET, SQLite, ffmpeg/ffprobe subprocesses, React/TypeScript, xUnit/Vitest/Playwright and Linux packaging.

**Spec:** [Design](../specs/2026-10-17-issue-49-audio-processing-design.md).

## Global Constraints

Processing disabled by default; a Plex-oriented 30-second preset; explicit preview before repair. User approved these three choices. Other defaults are recommendations: 192 kbps MP3, optional trim null or 5–900 seconds, optional normalization -16 LUFS/-1.5 dBTP/LRA 11, 44.1 kHz stereo. One encoder at a time, 120 s processing budget per file. Never silently apply processing or discard full-length playback. Preserve originals on failure and selection provenance. Never edit an existing test to make a change pass. Source baseline is upstream `e81b25f`; repair builds on #52 and #55.

## Review Focus

- Existing installs and clients omitting new settings retain unprocessed output: Task 2.
- Encoder failure, malformed audio, silence, oversized output, or cancellation preserves a good original: Task 2.
- Preview becomes stale because of download/delete/options changes: Task 3.
- Crash between backup, promotion and ledger completion cannot repeat lossy work or overwrite a newer theme: Task 3.
- Plex metadata growth and retained backups differ from measured source-file savings: Tasks 1 and 4.

## Task 1 / PR A: Document storage growth independently

**Files:** Modify `README.md` only.

**Interfaces:** None. Keep the observation explicitly attributed to issue #49 and one production installation.

- [ ] Add a short storage note near download/storage setup: approximately 3.7 GB per 1,000 movies in the reported installation, variability by duration/bitrate, media-folder plus potential Plex-imported copies, and separate volumes to inspect. Use theme.* rather than promising every received file is MP3; current promotion already sniffs the container ([src/Themearr.API/Services/ThemeFiles.cs @ e81b25f, lines 193–215](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Services/ThemeFiles.cs#L193-L215)).
- [ ] Check the wording against the issue and Plex's linked refresh/cleanup documentation. State that source-file optimization alone does not demonstrate reclaimed Plex metadata space. Do not add a meaningless executable test for prose. Run citation checking, inspect Markdown links/diff, and commit as `docs: explain theme audio storage growth`.

## Task 2 / PR B: Optional processing for new downloads, including usable configuration

**Files:** Create `src/Themearr.API/Services/AudioProcessingOptions.cs`, `IThemeAudioProcessor.cs`, `FfmpegThemeAudioProcessor.cs`, `Health/AudioProcessingCheck.cs`; modify `DownloadService.cs`, `ThemeFiles.cs`, `Data/Database.cs`, `Controllers/SettingsController.cs`, `Program.cs`, `Dockerfile`, `install.sh`, `README.md`, web `lib/types.ts`, `lib/api.ts`, `app/settings/page.tsx`; create API `AudioProcessingOptionsTests.cs`, `AudioProcessingDownloadTests.cs`, `FfmpegProcessTests.cs` and web `app/settings-audio-processing.test.tsx`. Adapt the existing bounded process-runner pattern rather than invoking a shell ([src/Themearr.API/Services/UpdateProcessRunner.cs @ e81b25f, line 8](https://github.com/Themearr/themearr/blob/e81b25f4c487dc5406b93cab9bf3736d2da4aad9/src/Themearr.API/Services/UpdateProcessRunner.cs#L8)).

**Interfaces:** Define immutable `AudioProcessingOptions(bool Enabled, int? TrimSeconds, bool Normalize, int BitrateKbps)`, default `(false, null, false, 192)`; `IThemeAudioProcessor.ProcessAsync(string inputPath, string outputPath, AudioProcessingOptions options, Action<string> progress, CancellationToken ct): Task<AudioProcessingResult>`; result records actual bytes, duration and output fingerprint. Settings GET adds audioProcessing; omitted POST object preserves stored settings, and malformed provided values return HTTP 400. Do not use nullable collection changes to bypass existing selected-library semantics.

- [ ] Write service-consumer tests asserting disabled processing calls no encoder and preserves bytes/container; enabled processing runs for provider and direct URL paths, both media types, but publishes only verified MP3. Add settings round-trip/omitted-field/validation tests and rendered preset/opt-in tests. Prove a real DownloadService consumer fails when the processor is not wired, rather than accepting new-class-only coverage.
- [ ] Add process tests with controlled child processes: concurrent stdout/stderr drainage, timeout, cancellation killing descendants, nonzero exit, invalid/silent measurement JSON, no audio stream, output byte limit, stderr retention cap and malicious argument characters. Add an integration fixture using real ffmpeg to verify 30 s within encoding tolerance, codec/container, and two-pass normalization of the same trimmed segment. The Linux packaging job must run these integration tests with the declared encoder dependency; never silently skip them on absent ffmpeg.
- [ ] Run the new tests red. Implement options validation/persistence, safe subprocess invocation with local-only protocol allowlist and one-encoder semaphore. Stream progress through sanitized existing logging. Hold no SQLite lock during encoding. Use unique staged files, verify output, then reuse #52 publication revision check. An explicitly enabled but unavailable encoder returns actionable failure and leaves a good theme intact. Disabled processing retains current no-encoder behavior.
- [ ] Add the settings controls, exact disabled default, optional trim/normalization and explicit Plex preset; explain shared-player full-length impact. Add a System health check reporting available/unavailable/disabled without breaking ordinary download readiness when processing is off. Supply ffmpeg/ffprobe in Docker runtime for both target architectures; native script detects missing tools and documents an explicit opt-in installation path rather than failing unprocessed installs. Read Docker/GitHub Actions skills before editing those artifacts. Coordinate the separate ProxmoxVE installer dependency change; do not claim local repository changes cover it.
- [ ] Run full API/frontend gate, lint/build, real-encoder integration tests and amd64/arm64 image smoke checks (version, MP3 encoder availability, fixture processing under the non-root runtime user). Run security-invariant and concurrency review. Show the disabled path stays byte-identical with the production processor call reverted. Commit the reviewed feature slice as `feat: add opt-in theme audio processing`.

## Task 3 / PR C backend: Preview, apply and recover repair jobs

**Files:** Create `src/Themearr.API/Services/AudioRepairService.cs`, `AudioRepairModels.cs`, `Controllers/AudioRepairController.cs`; modify `Data/Database.cs` for additive repair ledger methods/schema and `Program.cs` for singleton lifecycle; create API `AudioRepairPreviewTests.cs`, `AudioRepairApplyTests.cs`, `AudioRepairRecoveryTests.cs`, `AudioRepairEndpointTests.cs`. Reuse Task 2's processor and #52's commit coordination; do not overload the matching-rule recheck service.

**Interfaces:** `PreviewAsync(AudioRepairPreviewRequest request, CancellationToken ct): Task<AudioRepairPreview>`; `StartApply(Guid previewId, IReadOnlyList<string> itemIds): AudioRepairStartResult`; `GetStatus(): AudioRepairStatus`; `Cancel(Guid runId): bool`. Authenticated routes are the four methods specified in the design. Preview uses server-selected library items, never caller-supplied paths. Keep one 15-minute preview, one active job, immutable options and file/revision fingerprints.

- [ ] Test preview is read-only/network-free, bounds/canonicalizes candidates, excludes ignored items by default, includes manually selected kept/manual files without changing provenance, deduplicates shared movie/show folders, and rejects outside-root or malicious paths. Test expired preview, options change, duplicate apply, unauthorized calls and invalid item IDs. Status must not expose unrelated paths or secrets.
- [ ] Add deterministic apply races with held processor/commit barriers: deleting, replacing or editing source after preview skips the stale item; cross-type shared folders process once; cancellation preserves originals; output validation failure restores/preserves bytes; no new download history/provenance event appears. Assert actual source bytes, backup bytes and error counts separately. Require idempotency: applying the same completed fingerprint/options does not encode again.
- [ ] Add durable schema `audio_repair_runs` and `audio_repair_items` with run/item IDs, immutable options hash, canonical input/backup/staging paths, input/output hashes, before/after/backup sizes and lifecycle status/error/timestamps. Store intent/output hash before publication. Do not migrate or reset existing history/settings. Methods validate IDs internally and bind SQL values.
- [ ] Implement preview/apply and the per-file lifecycle: planned → backed_up → prepared → applying → succeeded/failed/stale/cancelled. Backups use `.themearr-audio-backup-<run>-<item>` outside theme.*; check free space for temporary output+backup, retain input until verified output and backup exist, and validate source hash/revision again inside #52's mutation boundary. Never hold a DB transaction during probing or encoding. Publish with the common commit boundary, then record final ledger result. Recoverable backup remains until explicit operator cleanup outside this first version.
- [ ] Add crash-window tests after backup, output preparation, publication and before final ledger write. Startup performs read-only file verification: an exact expected output hash can finalize ledger success; ambiguous state is interrupted and requires a fresh preview. Startup never restores a backup, rewrites media or resumes encoding automatically. Explicit future recovery is allowed only after revalidating current input/revision, so it cannot overwrite a newer download.
- [ ] Run the new tests red before each implementation part, then full API suite with zero skips. Revert commit-time fingerprint/revision checking and prove the stale-preview consumer tests fail. Run the two specialist reviewers before committing the repair engine.

## Task 4 / PR C frontend and delivery: Repair controls, reporting and Plex guidance

**Files:** Modify web `lib/types.ts`, `lib/api.ts`, `app/system/page.tsx`, `app/settings/page.tsx`, `app/history/page.tsx`; create `components/system/AudioRepairPanel.tsx`, `components/system/audio-repair-panel.test.tsx`, and `tests/e2e/audio-repair.spec.ts`; update `README.md`. Keep the repair report linked from history without presenting it as a downloaded selection.

**Interfaces:** Typed preview/apply/status/cancel wrappers mirror Task 3; panel binding uses run ID, immutable preview ID/options and stamped, bounded single-flight polls. Backend status publishes one coherent snapshot for progress, outcomes and errors.

- [ ] Add tests for preview selection then explicit Apply, changed/expired options disabling Apply, progress/cancel, failed/stale per-file results, lost status contact/retry and unmount. Confirm no apply request before explicit confirmation of the selected preview. Show estimated values distinctly from actual source savings, retained backup bytes and unknown Plex metadata savings. Keep failure feedback visible on both movie/show download paths through their shared adapter/modal.
- [ ] Implement the panel and report links. After each committed repair ask Plex for best-effort item refresh outside the mutation boundary; failures are warnings in the repair report, not false repair failures. Link Plex Clean Bundles guidance, state that imported-copy reclamation is unproven until measured, and never manipulate Plex metadata paths.
- [ ] Use generated local audio fixtures and a throwaway library/DB for E2E. Verify preview cannot mutate files, apply reduces source duration/bytes as configured, backup remains intact, retry does not repeat lossy conversion, and movie/show scope works. Separately perform a controlled Plex before/after experiment against supported server versions before claiming reclaimed metadata bytes; no production repair is part of the test gate.
- [ ] Run frontend tests/lint/build, full API suite, E2E, native dependency checks and container checks. Recheck dashboard/queue/history/settings/System propagation against the design; download coverage/history selection counts must not increase from repair alone. Obtain security-invariant and concurrency reviews, then link `Closes #49` only after all staged acceptance criteria are satisfied.

## Remaining approval and release boundaries

The three core UX choices are approved; numeric loudness targets, backup retention/cleanup UX and packaging strategy are recommendations to review before product implementation. Ship PR A independently. PR B can proceed after reviewing those defaults and the #52 mutation API. PR C waits for #52, #55, the processor's measured integration tests and coordinated native/Proxmox dependency support. Do not promise a completion date or Plex-side reclamation without that evidence.
