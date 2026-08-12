# Theme Re-Check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user re-check an already-populated library so themes chosen under older matching rules are replaced when the rules improve.

**Architecture:** A free judging pass re-evaluates each stored pick against today's rules using evidence already in `theme_history`; only picks that now fail cost an API call. Auto-picked failures are re-queued for automatic replacement, hand-picked ones are flagged for review, and no file is deleted until a replacement has landed. See the spec: `docs/superpowers/specs/2026-08-12-theme-recheck-design.md`.

**Tech Stack:** .NET 10 / xUnit / SQLite (Microsoft.Data.Sqlite), React 19 + Vite 8 + Tailwind 4 / vitest, Playwright for E2E.

## Global Constraints

- **Movies and shows are parallel code paths.** Every task below that touches one must touch the other. `ILibrarySource.FetchAsync` is movie-only (`ILibrarySource.cs:21`), so nothing is shared automatically.
- **Never edit an existing test to make a change pass.** If a change breaks a test, the change is wrong until proven otherwise.
- **A test must fail for the right reason.** Every task's "run it and watch it fail" step states the expected failure message. If it fails differently, stop.
- **Paste the red output into your report, verbatim.** A report showing only green cannot distinguish a test that binds from one that would pass with the implementation deleted. If test and implementation were written together, go back: disable the implementation, capture the failure, restore it, capture the pass. Both outputs go in the report.
- **Restore with `git checkout <file>`, never `mv` from a `.bak`.** `sed -i.bak` preserves the original's mtime, so restoring by `mv` hands MSBuild a source file older than the compiled assembly — it skips the rebuild and the next run tests the *previous* build. That produced a false red here; the same mechanism produces a false **green** when you restore a broken change, which is the direction that ships bugs. If you must restore by hand, `touch` the file afterwards.
- **Green is `Failed: 0, Skipped: 0`** — read both fields.
- **Lint baseline is 0 errors, 3 warnings.** A fourth is yours.
- **Comments explain _why_.** Match the density of `HostGuard.cs` / `ApiAuthMiddleware.cs`.
- **Cite, don't restate.** Any claim about code carries its `file:line`.
- **`LogSanitizer.Clean`** wraps any user-controlled value reaching a log.
- **Nullable is enabled on both projects.** Do not make a settings field nullable to dodge a warning.
- Use the LSP tools (`csharp-ls`, TypeScript) for find-references and rename rather than grep.

**Verification gate** (~16s total, run from repo root unless noted):

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint
cd src/Themearr.Web && npm run build && cd ../../tests/e2e && npm test
```

## Wave Structure

Tasks 1–5 are **serial** — they are what everything else compiles against, and they own every shared file. Tasks 6–9 are **parallel** and file-disjoint by construction. Tasks 10–11 are **serial** verification.

| Wave | Tasks | Files owned |
|---|---|---|
| 0 (serial) | 1–5 | `ThemeMatch.cs`, `Database.cs`, `DownloadService.cs`, controller call sites, `Program.cs` (singleton registration) |
| 1 (parallel ×4) | 6, 7, 8, 9 | 6: `AutoRecheckService.cs` + `Program.cs` · 7: `AutoDownloadService.cs`, `ShowAutoDownloadService.cs` · 8: controllers · 9: frontend |
| 2 (serial) | 10–11 | E2E, review |

**No wave-1 task may edit a file owned by another wave-1 task.** If one appears to need to, stop and report it rather than editing.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Themearr.API/Services/ThemeRecheckService.cs` | The scan. Walks the library, judges stored picks, writes flags. No API calls. |
| `src/Themearr.API/Services/AutoRecheckService.cs` | Decides *when* a scan runs: once, after a `RulesVersion` bump. |
| `src/Themearr.API/Controllers/RecheckController.cs` | `api/themes/recheck` — start, status, flagged list. |
| `tests/Themearr.API.Tests/RejectionReasonTests.cs` | The judging rule, including the legacy-score branch. |
| `tests/Themearr.API.Tests/RejectionReasonFingerprintTests.cs` | Pins `RulesVersion` to the rules' actual behaviour. |
| `tests/Themearr.API.Tests/RecheckSchemaTests.cs` | Migrations, on fresh and upgraded databases. |
| `tests/Themearr.API.Tests/RecheckStoreTests.cs` | The new `Database` methods. |
| `tests/Themearr.API.Tests/ThemeRecheckServiceTests.cs` | Scan selection and provenance resolution. |
| `tests/Themearr.API.Tests/AutoRecheckServiceTests.cs` | The version gate. |
| `tests/Themearr.API.Tests/RecheckWorkerTests.cs` | Worker candidate ordering and the inverted-outcome hazard. |
| `tests/Themearr.API.Tests/RecheckEndpointTests.cs` | The new routes, including auth. |
| `src/Themearr.Web/src/app/queue/recheck.test.tsx` | Queue "Needs attention" section. |
| `src/Themearr.Web/src/app/dashboard-needs-attention.test.tsx` | Dashboard tile. |

**Modified:**

| File | Change |
|---|---|
| `src/Themearr.API/Services/ThemeMatch.cs` | `RulesVersion`, `RejectionReason` |
| `src/Themearr.API/Data/Database.cs` | migrations, recheck queries, history provenance, stats counts |
| `src/Themearr.API/Services/DownloadService.cs` | `Start` provenance params; clear recheck on success |
| `src/Themearr.API/Controllers/MoviesController.cs` | provenance at call sites; recheck endpoints |
| `src/Themearr.API/Controllers/ShowsController.cs` | provenance at call sites; recheck endpoints; **new show auto-download endpoint** |
| `src/Themearr.API/Controllers/StatsController.cs` | `needsAttention` |
| `src/Themearr.API/Services/AutoDownloadService.cs` | second candidate source |
| `src/Themearr.API/Services/ShowAutoDownloadService.cs` | second candidate source |
| `src/Themearr.API/Program.cs` | register both new services |
| `src/Themearr.Web/src/lib/api.ts`, `types.ts`, `media/adapter.ts` | recheck client + types |
| `src/Themearr.Web/src/app/queue/page.tsx` | "Needs attention" section, re-check button |
| `src/Themearr.Web/src/app/dashboard/page.tsx` | count tile |

---

# WAVE 0 — SERIAL FOUNDATION

## Task 1: The judging rule

**Files:**
- Modify: `src/Themearr.API/Services/ThemeMatch.cs`
- Test: `tests/Themearr.API.Tests/RejectionReasonTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `ThemeMatch.RulesVersion` (`const int`), and
  `static string? ThemeMatch.RejectionReason(string videoTitle, string? mediaTitle, int? year, int? storedScore)` — returns `null` to keep the theme, else the reason string.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/RejectionReasonTests.cs`:

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the rule that decides whether a theme ALREADY on disk still passes today's
/// standards. Distinct from ThemeMatchTests, which pins the rule for choosing a new
/// one: this one judges a single stored pick with no candidate field to rank it
/// against, and with only the evidence `theme_history` kept.
/// </summary>
public class RejectionReasonTests
{
    [Fact]
    public void StoredPick_thatIsATrailer_isRejected()
    {
        // Issue #39's exact failure, still on disk in libraries synced before #41.
        var reason = ThemeMatch.RejectionReason(
            "Interstellar - Official Trailer", "Interstellar", 2014, storedScore: 45);

        Assert.Equal("looks like a trailer, clip or promo", reason);
    }

    [Fact]
    public void StoredPick_titledLikeTheFilmItself_isRejectedForPromoBeforeEvidence()
    {
        // "The Score (2001) Official Trailer" certifies itself as music via its own
        // title (ThemeMatch.cs:42-45). The promo veto must run FIRST, or the film's
        // name launders the trailer through the evidence check.
        var reason = ThemeMatch.RejectionReason(
            "The Score (2001) Official Trailer", "The Score", 2001, storedScore: 38);

        Assert.Equal("looks like a trailer, clip or promo", reason);
    }

    [Fact]
    public void StoredPick_withNoMusicWordAtAll_isRejected()
    {
        var reason = ThemeMatch.RejectionReason(
            "Blade Runner 2049 - Mesa", "Blade Runner 2049", 2017, storedScore: 45);

        Assert.Equal("nothing in the title says it is the work's music", reason);
    }

    [Fact]
    public void StoredPick_withEvidenceAndAPositiveStoredScore_isKept()
    {
        Assert.Null(ThemeMatch.RejectionReason(
            "Interstellar Main Theme - Hans Zimmer", "Interstellar", 2014, storedScore: 65));
    }

    [Fact]
    public void StoredPick_withAStoredScoreAtOrBelowZero_isRejected()
    {
        // Reproduces IsConfident's `score > 0` exactly (ThemeMatch.cs:104) — which is
        // the entire point of persisting the score.
        Assert.Equal("ranked below the confidence floor",
            ThemeMatch.RejectionReason("Some Theme", "Some Film", 2001, storedScore: 0));
    }

    [Fact]
    public void LegacyPick_scoringZeroOnlyForLackOfADurationBonus_isKept()
    {
        // THE false-flag guard. "opening credits" is a MusicPhrase (ThemeMatch.cs:25)
        // but contributes nothing to Score, and "[ost]" misses the literal " ost"
        // (ThemeMatch.cs:147) that HasMusicEvidence matches via ContainsWord. So this
        // scored 15 at download time on its duration bonus alone and recomputes to 0.
        // Rejecting at `<= 0` here would re-download a theme that was always fine.
        Assert.Null(ThemeMatch.RejectionReason(
            "Opening Credits [OST]", "Dune", 2021, storedScore: null));
    }

    [Fact]
    public void LegacyPick_withActiveNegativeMarkers_isRejected()
    {
        // 30 title + 15 theme - 30 ranked - 20 "every " = -5. A negative score means
        // the title carries real negative signal, not a missing bonus.
        Assert.Equal("the title carries negative match signals",
            ThemeMatch.RejectionReason(
                "Every Star Wars Theme Ranked", "Star Wars", 1977, storedScore: null));
    }

    [Fact]
    public void LegacyPick_withCleanEvidence_isKept()
    {
        Assert.Null(ThemeMatch.RejectionReason(
            "Dune Main Theme (Original Soundtrack)", "Dune", 2021, storedScore: null));
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter RejectionReasonTests
```

Expected: **compile error** — `'ThemeMatch' does not contain a definition for 'RejectionReason'`. A compile failure is the correct red here; there is no call site to bind yet.

- [ ] **Step 3: Implement**

Add to `src/Themearr.API/Services/ThemeMatch.cs`, after `IsConfident`:

```csharp
/// <summary>
/// Bumped whenever the rules above change. <c>AutoRecheckService</c> compares this
/// against the stored <c>match_rules_version</c> setting and runs one re-check when the
/// code is newer, so a release that improves matching revisits themes chosen under the
/// old rules instead of only helping future downloads.
///
/// <c>RejectionReasonFingerprintTests</c> hashes this method's verdicts over a fixed
/// corpus and pins the hash beside this number, so changing the rules without changing
/// this is a build failure rather than a silent no-op.
/// </summary>
public const int RulesVersion = 1;

/// <summary>
/// Whether a theme ALREADY on disk still passes today's rules. Null means keep it;
/// otherwise the string is the reason, stored in <c>recheck_reason</c> and shown in the
/// queue. Distinct from <see cref="IsConfident"/>, which chooses among a ranked field —
/// here there is one stored pick and no field to rank it against.
///
/// <paramref name="storedScore"/> is the score recorded when the pick was made, and it
/// cannot be recomputed faithfully: <see cref="Score"/> reads channel and duration,
/// neither of which is persisted, and six of the nine <see cref="MusicPhrases"/> plus
/// both <see cref="MusicWords"/> contribute nothing to it. "Opening Credits [OST]"
/// therefore scores 15 at download time on its duration bonus and 0 on review — and
/// rejecting at `<= 0` would re-download a theme that was always fine.
///
/// So a null storedScore (a row written before this feature) takes a deliberately
/// weaker test: reject only a NEGATIVE recomputed score. Negative means the title
/// carries active negative markers (ThemeMatch.cs:168-172); zero means the evidence
/// word simply carries no weight in Score, which says nothing about quality. Legacy
/// rows under-flag rather than false-flag, which is the right direction of error for a
/// feature whose promise is that it never churns a good theme.
/// </summary>
public static string? RejectionReason(
    string videoTitle, string? mediaTitle, int? year, int? storedScore)
{
    var vt = videoTitle.ToLowerInvariant();

    // Ordered before the evidence check for the same reason IsConfident vetoes outright:
    // a promo's title can legitimately carry a music word when the work itself is called
    // "The Score", and evidence alone would launder it through.
    if (PromoMarkers.Any(m => vt.Contains(m, StringComparison.Ordinal)))
        return "looks like a trailer, clip or promo";

    if (!HasMusicEvidence(videoTitle))
        return "nothing in the title says it is the work's music";

    if (storedScore is { } score)
        return score > 0 ? null : "ranked below the confidence floor";

    var recomputed = Score(videoTitle, channel: "", duration: null, mediaTitle, year);
    return recomputed < 0 ? "the title carries negative match signals" : null;
}
```

- [ ] **Step 4: Run the tests and verify they pass**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter RejectionReasonTests
```

Expected: `Passed: 8, Failed: 0, Skipped: 0`.

- [ ] **Step 5: Prove the false-flag test fails for the right reason**

Temporarily change `recomputed < 0` to `recomputed <= 0`, re-run, and confirm **only**
`LegacyPick_scoringZeroOnlyForLackOfADurationBonus_isKept` fails. Then revert. If any
other test moves, the corpus is not isolating what it claims to.

- [ ] **Step 6: Write the fingerprint test**

Create `tests/Themearr.API.Tests/RejectionReasonFingerprintTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Makes ThemeMatch.RulesVersion honest. The version only triggers a re-check if someone
/// remembers to bump it when the rules change — and a forgotten bump fails nothing: the
/// release ships, no library is revisited, and the improvement reaches only future
/// downloads. That is the exact failure this whole feature exists to undo.
///
/// So the verdicts over a fixed corpus are hashed and pinned beside the version. Change
/// any vocabulary list, any score weight, or any branch of RejectionReason and this goes
/// red; the only way back to green is updating the hash AND the version together.
///
/// Hashing verdicts rather than the rule arrays is what makes it cover the weights and
/// the logic, not just the vocabulary.
/// </summary>
public class RejectionReasonFingerprintTests
{
    private static readonly (string VideoTitle, string? MediaTitle, int? Year, int? StoredScore)[] Corpus =
    {
        // ── Promo markers ────────────────────────────────────────────────────
        ("Interstellar - Official Trailer",              "Interstellar",      2014, 45),
        ("The Score (2001) Official Trailer",            "The Score",         2001, 38),
        ("Suite Francaise Official Trailer #1 (2015)",   "Suite Francaise",   2015, 40),
        ("Dune - Behind The Scenes",                     "Dune",              2021, 30),
        ("Arrival Featurette",                           "Arrival",           2016, 25),
        ("Blade Runner 2049 Cast Interview",             "Blade Runner 2049", 2017, 20),
        ("Alien - Chestburster clip",                    "Alien",             1979, 20),
        ("Heat - Diner scene",                           "Heat",              1995, 20),
        // ── Each MusicPhrase, as evidence ────────────────────────────────────
        ("Interstellar Main Theme - Hans Zimmer",        "Interstellar",      2014, 65),
        ("Dune (Original Soundtrack)",                   "Dune",              2021, 57),
        ("Arrival - Original Score",                     "Arrival",           2016, 54),
        ("Lawrence of Arabia Main Title",                "Lawrence of Arabia",1962, null),
        ("Holst - Jupiter Suite",                        "Jupiter",           1914, null),
        ("Overture",                                     "Fantasia",          1940, null),
        ("Twin Peaks Title Sequence",                    "Twin Peaks",        1990, null),
        ("Opening Credits [OST]",                        "Dune",              2021, null),
        ("End Credits",                                  "Se7en",             1995, null),
        // ── Short words, boundary-matched ────────────────────────────────────
        ("Akira (1988) OST",                             "Akira",             1988, null),
        ("Ghostbusters (1984)",                          "Ghostbusters",      1984, null),
        ("Serial Experiments Lain - Intro",              "Serial Experiments Lain", 1998, null),
        ("An Introduction to Dune",                      "Dune",              2021, null),
        // ── Negative markers ─────────────────────────────────────────────────
        ("Every Star Wars Theme Ranked",                 "Star Wars",         1977, null),
        ("Top 10 Movie Themes",                          "Star Wars",         1977, null),
        ("Star Wars Theme Compilation",                  "Star Wars",         1977, null),
        ("Star Wars Theme REACTION",                     "Star Wars",         1977, null),
        ("Star Wars Theme - Piano Cover",                "Star Wars",         1977, null),
        ("Star Wars Theme (Guitar Remix)",               "Star Wars",         1977, null),
        ("A Tribute to John Williams' Themes",           "Star Wars",         1977, null),
        ("Star Wars Theme Parody",                       "Star Wars",         1977, null),
        // ── Stored-score branch ──────────────────────────────────────────────
        ("Some Theme",                                   "Some Film",         2001, 0),
        ("Some Theme",                                   "Some Film",         2001, -5),
        ("Some Theme",                                   "Some Film",         2001, 1),
        // ── No evidence at all ───────────────────────────────────────────────
        ("Blade Runner 2049 - Mesa",                     "Blade Runner 2049", 2017, 45),
    };

    [Fact]
    public void Verdicts_matchThePinnedFingerprintForThisRulesVersion()
    {
        var verdicts = string.Join("\n", Corpus.Select(c =>
            ThemeMatch.RejectionReason(c.VideoTitle, c.MediaTitle, c.Year, c.StoredScore) ?? "KEEP"));

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(verdicts)))[..16];

        Assert.Equal(1, ThemeMatch.RulesVersion);

        // Measured, not predicted: run the test, read the actual value from the failure
        // message, and paste it here. If you are changing the rules, this number moving
        // is the signal to bump RulesVersion in the same commit.
        Assert.Equal("PASTE_ACTUAL_HERE", fingerprint);
    }
}
```

- [ ] **Step 7: Measure the fingerprint and pin it**

Run the test. It fails with `Assert.Equal() Failure: Expected: PASTE_ACTUAL_HERE, Actual: <16 hex chars>`. Copy the actual value into the assertion. Re-run; expect `Passed: 1`.

This is a measurement, not a placeholder — the value cannot be known without executing the rules.

- [ ] **Step 8: Run the full API suite and commit**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add src/Themearr.API/Services/ThemeMatch.cs tests/Themearr.API.Tests/RejectionReasonTests.cs tests/Themearr.API.Tests/RejectionReasonFingerprintTests.cs
git commit -m "feat: judge a stored theme pick against today's matching rules"
```

Expected: `Failed: 0, Skipped: 0`, total 400+.

---

## Task 2: Schema migrations

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (add to `Init`, lines 66-72)
- Test: `tests/Themearr.API.Tests/RecheckSchemaTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: columns `theme_history.picked_by TEXT`, `theme_history.match_score INTEGER`, `movies.recheck_state TEXT`, `movies.recheck_reason TEXT`, `shows.recheck_state TEXT`, `shows.recheck_reason TEXT`.

- [ ] **Step 1: Write the failing test**

Create `tests/Themearr.API.Tests/RecheckSchemaTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Themearr.API.Data;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the re-check columns on both a fresh database and one upgraded from a schema
/// that predates them — the second case is every existing install.
/// </summary>
public class RecheckSchemaTests
{
    private static HashSet<string> Columns(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var cols = new HashSet<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    [Fact]
    public void Init_onAFreshDatabase_addsEveryRecheckColumn()
    {
        using var dir = new TempDir();
        var path = dir.File("fresh.db");
        new Database(path).Init();

        Assert.Contains("picked_by",   Columns(path, "theme_history"));
        Assert.Contains("match_score", Columns(path, "theme_history"));
        foreach (var table in new[] { "movies", "shows" })
        {
            Assert.Contains("recheck_state",  Columns(path, table));
            Assert.Contains("recheck_reason", Columns(path, table));
        }
    }

    [Fact]
    public void Init_isIdempotent_soASecondStartupDoesNotThrow()
    {
        using var dir = new TempDir();
        var path = dir.File("twice.db");
        new Database(path).Init();
        new Database(path).Init();   // would throw "duplicate column name" if unguarded

        Assert.Contains("match_score", Columns(path, "theme_history"));
    }

    [Fact]
    public void Init_migratesADatabaseThatPredatesTheColumns_preservingItsRows()
    {
        // The only case that can lose data, and therefore the only one worth this much
        // setup: a database built by the OLD schema, migrated for the first time. Calling
        // Init() before inserting would prove nothing — the columns would already exist,
        // and the test would pass even if the migration were deleted entirely.
        using var dir = new TempDir();
        var path = dir.File("upgrade.db");

        // Hand-build the pre-V3 theme_history: read the CREATE TABLE in Database.cs plus
        // MigrateHistoryTable / MigrateHistoryTableV2 for the exact pre-migration shape.
        using (var old = new SqliteConnection($"Data Source={path}"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE theme_history (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    movie_id      TEXT NOT NULL,
                    movie_title   TEXT NOT NULL,
                    movie_year    INTEGER,
                    theme_title   TEXT,
                    source_url    TEXT,
                    downloaded_at TEXT NOT NULL,
                    media_type    TEXT NOT NULL DEFAULT 'movie'
                )
                """;
            create.ExecuteNonQuery();

            using var insert = old.CreateCommand();
            insert.CommandText =
                "INSERT INTO theme_history (movie_id, movie_title, theme_title, downloaded_at, media_type) " +
                "VALUES ('m1', 'Dune', 'Dune Main Theme', '2026-01-01T00:00:00Z', 'movie')";
            insert.ExecuteNonQuery();
        }

        new Database(path).Init();   // first migration this database has ever seen

        Assert.Contains("picked_by",   Columns(path, "theme_history"));
        Assert.Contains("match_score", Columns(path, "theme_history"));

        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT theme_title, picked_by, match_score FROM theme_history WHERE movie_id = 'm1'";
        using var reader = q.ExecuteReader();
        Assert.True(reader.Read(), "the pre-existing row must survive the migration");
        Assert.Equal("Dune Main Theme", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
    }
}
```

- [ ] **Step 2: Run and verify it fails**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter RecheckSchemaTests
```

Expected: `Assert.Contains() Failure` — `picked_by` not in the column set. (Not a compile error: `Database.Init()` already exists.)

- [ ] **Step 3: Implement the migrations**

In `src/Themearr.API/Data/Database.cs`, add two calls to `Init` after `MigrateMoviesTableV4(conn)` (line 71):

```csharp
        MigrateHistoryTableV3(conn);
        MigrateRecheckColumns(conn);
```

Then add the two methods beside the existing migrations:

```csharp
    /// <summary>
    /// Provenance for the re-check: which mechanism chose this theme, and the score
    /// behind the pick. Both are null on rows written before the re-check shipped —
    /// ThemeRecheckService resolves those from source_url instead, and takes a weaker
    /// score test (see ThemeMatch.RejectionReason).
    /// </summary>
    private static void MigrateHistoryTableV3(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(theme_history)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        if (!columns.Contains("picked_by"))
            conn.Execute("ALTER TABLE theme_history ADD COLUMN picked_by TEXT");
        if (!columns.Contains("match_score"))
            conn.Execute("ALTER TABLE theme_history ADD COLUMN match_score INTEGER");
    }

    /// <summary>
    /// "Is the theme any good" is orthogonal to "do I have a theme", so it gets its own
    /// column rather than a new `status` value. Packing them together produces a
    /// specific bug: the auto-download worker reconciles any 'pending' row that has a
    /// theme file on disk straight back to 'downloaded' (AutoDownloadService.cs:176-180),
    /// so a flagged item would leave the queue on the very next tick.
    /// </summary>
    private static void MigrateRecheckColumns(SqliteConnection conn)
    {
        foreach (var table in new[] { "movies", "shows" })
        {
            using var cmd = conn.CreateCommand();
            // nosemgrep: csharp-sqli — `table` comes from the literal array above, never a caller
            cmd.CommandText = $"PRAGMA table_info({table})";
            var columns = new HashSet<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) columns.Add(r.GetString(1));

            if (!columns.Contains("recheck_state"))
                conn.Execute($"ALTER TABLE {table} ADD COLUMN recheck_state TEXT");   // nosemgrep: csharp-sqli
            if (!columns.Contains("recheck_reason"))
                conn.Execute($"ALTER TABLE {table} ADD COLUMN recheck_reason TEXT");  // nosemgrep: csharp-sqli
        }
    }
```

- [ ] **Step 4: Run and verify it passes**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter RecheckSchemaTests
```

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 5: Run the full API suite and commit**

The existing `MoviesMigrationTests` and `ShowsSchemaTests` must still pass — the V4 movies rebuild renames and recreates the table, so a new column added before it would be dropped. Ordering the new migration *after* `MigrateMoviesTableV4` is what prevents that.

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/RecheckSchemaTests.cs
git commit -m "feat: add theme provenance and re-check state columns"
```

---

## Task 3: Data access

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs`
- Test: `tests/Themearr.API.Tests/RecheckStoreTests.cs` (create)

This task owns **every** `Database.cs` change the feature needs, so no wave-1 task touches this file.

**Interfaces:**
- Consumes: Task 2's columns.
- Produces, on `Database`:

```csharp
public record ThemeProvenance(string? ThemeTitle, string? SourceUrl, string? PickedBy, int? MatchScore);
public record RecheckItem(string Id, string MediaType, string Title, int? Year,
                          string? ThemeTitle, string? State, string? Reason);

ThemeProvenance? GetLatestThemeProvenance(string mediaId, string mediaType);
void AddThemeHistory(string movieId, string movieTitle, int? movieYear, string? themeTitle,
                     string? sourceUrl, string mediaType = "movie",
                     string? pickedBy = null, int? matchScore = null);   // extended
List<Dictionary<string, object?>> GetRecheckCandidates(string mediaType);
List<Dictionary<string, object?>> GetRecheckQueued(string mediaType);
List<RecheckItem> GetFlaggedItems();
void SetRecheck(string id, string mediaType, string? state, string? reason);
void ClearRecheck(string id, string mediaType);
int CountNeedsAttention(string mediaType);
```

`StatsResult` and `ShowStatsResult` each gain a trailing `int NeedsAttention` property.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/RecheckStoreTests.cs`. Cover, one `[Fact]` each:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class RecheckStoreTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(dir.File("t.db"));
        db.Init();
        return db;
    }

    // Seeds one movie row with a theme already 'downloaded'.
    private static void SeedMovie(Database db, string id, string title, int? year = 2021)
    {
        db.UpsertMovies(new[] { new MovieRecord($"/lib/{title}", "plex", id, title, year, $"/lib/{title}") });
        db.SetMovieStatus(db.GetAllMovies().Single(m => m["title"]?.ToString() == title)["id"]!.ToString()!, "downloaded");
    }

    [Fact]
    public void GetLatestThemeProvenance_returnsTheMostRecentRow_notTheFirst()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.AddThemeHistory("m1", "Dune", 2021, "Dune Trailer", "https://youtube.com/watch?v=a", "movie", "auto", 45);
        db.AddThemeHistory("m1", "Dune", 2021, "Dune Main Theme", "https://youtube.com/watch?v=b", "movie", "manual", null);

        var p = db.GetLatestThemeProvenance("m1", "movie");

        Assert.NotNull(p);
        Assert.Equal("Dune Main Theme", p!.ThemeTitle);
        Assert.Equal("manual", p.PickedBy);
        Assert.Null(p.MatchScore);
    }

    [Fact]
    public void GetLatestThemeProvenance_doesNotCrossMediaTypes()
    {
        // Movie and show ids come from the same MediaFolderId hash space, so a show and
        // a movie can share an id — the same reason DownloadService namespaces job keys.
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.AddThemeHistory("x1", "Dune", 2021, "Movie Theme", "https://youtube.com/watch?v=a", "movie", "auto", 60);
        db.AddThemeHistory("x1", "Dune", 2021, "Show Theme",  "https://youtube.com/watch?v=b", "show",  "auto", 61);

        Assert.Equal("Movie Theme", db.GetLatestThemeProvenance("x1", "movie")!.ThemeTitle);
        Assert.Equal("Show Theme",  db.GetLatestThemeProvenance("x1", "show")!.ThemeTitle);
    }

    [Fact]
    public void GetLatestThemeProvenance_withNoHistory_returnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(NewDb(dir).GetLatestThemeProvenance("nope", "movie"));
    }

    [Fact]
    public void SetRecheck_thenClearRecheck_roundTrips()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, "p1", "Dune");
        var id = db.GetAllMovies().Single()["id"]!.ToString()!;

        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");
        var queued = db.GetRecheckQueued("movie");
        Assert.Single(queued);
        Assert.Equal(id, queued[0]["id"]?.ToString());

        db.ClearRecheck(id, "movie");
        Assert.Empty(db.GetRecheckQueued("movie"));
    }

    [Fact]
    public void GetRecheckQueued_excludesReviewAndKept()
    {
        // 'review' and 'kept' are both waiting on a human; only 'queued' is work the
        // auto-download worker may pick up.
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, "a", "Alpha");
        SeedMovie(db, "b", "Bravo");
        var ids = db.GetAllMovies().ToDictionary(m => m["title"]!.ToString()!, m => m["id"]!.ToString()!);

        db.SetRecheck(ids["Alpha"], "movie", "review", "r");
        db.SetRecheck(ids["Bravo"], "movie", "kept",   "r");

        Assert.Empty(db.GetRecheckQueued("movie"));
    }

    [Fact]
    public void GetRecheckCandidates_excludesIgnoredAndPendingRows()
    {
        // The scan only judges themes that exist. A pending row has none, and an ignored
        // row is not the app's business.
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, "a", "Downloaded");
        db.UpsertMovies(new[] { new MovieRecord("/lib/Pending", "plex", "b", "Pending", 2020, "/lib/Pending") });
        SeedMovie(db, "c", "Ignored");
        var ids = db.GetAllMovies().ToDictionary(m => m["title"]!.ToString()!, m => m["id"]!.ToString()!);
        db.SetMovieIgnored(ids["Ignored"], true);

        var candidates = db.GetRecheckCandidates("movie");

        Assert.Single(candidates);
        Assert.Equal("Downloaded", candidates[0]["title"]?.ToString());
    }

    [Fact]
    public void GetFlaggedItems_returnsMoviesAndShowsTogether_eachCarryingItsMediaType()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, "a", "Dune");
        var movieId = db.GetAllMovies().Single()["id"]!.ToString()!;
        db.SetRecheck(movieId, "movie", "review", "looks like a trailer, clip or promo");

        var flagged = db.GetFlaggedItems();

        Assert.Single(flagged);
        Assert.Equal("movie", flagged[0].MediaType);
        Assert.Equal("looks like a trailer, clip or promo", flagged[0].Reason);
    }

    [Fact]
    public void CountNeedsAttention_countsQueuedAndReview_butNotKept()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, "a", "Alpha"); SeedMovie(db, "b", "Bravo"); SeedMovie(db, "c", "Charlie");
        var ids = db.GetAllMovies().ToDictionary(m => m["title"]!.ToString()!, m => m["id"]!.ToString()!);
        db.SetRecheck(ids["Alpha"],   "movie", "queued", "r");
        db.SetRecheck(ids["Bravo"],   "movie", "review", "r");
        db.SetRecheck(ids["Charlie"], "movie", "kept",   "r");

        Assert.Equal(2, db.CountNeedsAttention("movie"));
    }

    [Fact]
    public void AddThemeHistory_withoutProvenance_storesNulls_soExistingCallersAreUnchanged()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.AddThemeHistory("m1", "Dune", 2021, "Dune Theme", "https://example.com/a.mp3");

        var p = db.GetLatestThemeProvenance("m1", "movie");
        Assert.Null(p!.PickedBy);
        Assert.Null(p.MatchScore);
    }
}
```

Adjust `SeedMovie`'s `MovieRecord` construction to the real positional shape of that record — read it at the bottom of `Database.cs` before writing this file, and match it exactly.

- [ ] **Step 2: Run and verify it fails**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter RecheckStoreTests
```

Expected: compile error — `GetLatestThemeProvenance` is not defined.

- [ ] **Step 3: Implement**

Add a table-name whitelist helper near the top of the class, then the methods. The whitelist exists because `mediaType` reaches SQL as an identifier, which cannot be parameterised:

```csharp
    /// <summary>
    /// Maps a media type to its table. A whitelist rather than interpolation because a
    /// table name cannot be bound as a parameter, so this is the only thing standing
    /// between a caller-supplied string and the SQL text.
    /// </summary>
    private static string TableFor(string mediaType) => mediaType switch
    {
        "movie" => "movies",
        "show"  => "shows",
        _       => throw new ArgumentException($"Unknown media type: {mediaType}", nameof(mediaType)),
    };
```

Implement each method with `conn.Query` / `conn.Execute` in the existing style (callback form, ordinal reads, `IsDBNull` guards). `GetLatestThemeProvenance` orders by `id DESC LIMIT 1`. `GetRecheckCandidates` selects `status = 'downloaded' AND ignored = 0`. `GetRecheckQueued` selects `recheck_state = 'queued'`. `CountNeedsAttention` counts `recheck_state IN ('queued','review')`. `GetFlaggedItems` runs one query per table and concatenates, stamping `MediaType`.

Extend `AddThemeHistory` with the two trailing optional parameters, defaulting to `null`, and add both columns to the `INSERT`.

Add `NeedsAttention` to both stats records and populate it in `GetStats`/`GetShowStats` via `CountNeedsAttention`.

- [ ] **Step 4: Run and verify it passes**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter RecheckStoreTests
```

Expected: `Passed: 9, Failed: 0`.

- [ ] **Step 5: Full suite and commit**

`ShowStatsTests` and any test constructing `StatsResult` positionally will need the new field — that is a **signature** change, not an assertion change, so updating those constructor calls is allowed. Do not weaken any existing assertion.

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/RecheckStoreTests.cs
git commit -m "feat: add re-check queries and theme provenance reads"
```

---

## Task 4: Record provenance on every download

**Files:**
- Modify: `src/Themearr.API/Services/DownloadService.cs`
- Modify: `src/Themearr.API/Controllers/MoviesController.cs`, `src/Themearr.API/Controllers/ShowsController.cs`
- Modify: `src/Themearr.API/Services/AutoDownloadService.cs`, `src/Themearr.API/Services/ShowAutoDownloadService.cs`
- Test: `tests/Themearr.API.Tests/DownloadServiceTests.cs`, `DownloadServiceShowTests.cs` (extend)

**Interfaces:**
- Consumes: Task 3's `AddThemeHistory` and `ClearRecheck`.
- Produces: `bool DownloadService.Start(string id, string youtubeUrl, string mediaType = "movie", string pickedBy = "auto", int? matchScore = null)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Themearr.API.Tests/DownloadServiceTests.cs`:

```csharp
    [Fact]
    public async Task Start_recordsProvenanceOnTheHistoryRow()
    {
        // Whether a human or the matcher chose the video is the fact the re-check needs
        // and nothing recorded before now — every path funnelled through one Start call.
        using var dir = new TempDir();
        var db = NewDb(dir);      // reuse this file's existing helper
        // ... seed a movie whose folder is dir.Path, run a direct-URL download to
        // completion against a stub, then:
        var p = db.GetLatestThemeProvenance(movieId, "movie");
        Assert.Equal("manual", p!.PickedBy);
    }

    [Fact]
    public async Task Start_onSuccess_clearsAnyRecheckFlag()
    {
        // The flag must not outlive the problem. Clearing here rather than in the worker
        // means a manual replacement from the movies page clears it too, for free.
        // ... seed a movie flagged 'queued', download successfully, then:
        Assert.Empty(db.GetRecheckQueued("movie"));
    }

    [Fact]
    public async Task Start_onFailure_leavesTheRecheckFlagInPlace()
    {
        // Combined with WriteAtomicAsync (ThemeFiles.cs:93), this is the promise that a
        // failed replacement costs nothing: old theme still on disk, still flagged.
        // ... seed a flagged movie, force the download to fail, then:
        Assert.Single(db.GetRecheckQueued("movie"));
    }
```

Fill these in against the fixture style already in that file — read its existing tests first and reuse its helpers rather than inventing new ones. Mirror all three into `DownloadServiceShowTests.cs` with `mediaType: "show"`.

- [ ] **Step 2: Run and verify they fail**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter "DownloadService"
```

Expected: compile error on `Start(..., pickedBy:)`.

- [ ] **Step 3: Implement**

In `DownloadService.cs`:
- Widen `Start` to `Start(string id, string youtubeUrl, string mediaType = "movie", string pickedBy = "auto", int? matchScore = null)` and pass both through to `RunAsync`.
- In `RunAsync`'s success block (currently lines 209-214), pass provenance to `AddThemeHistory` and clear the flag:

```csharp
            var title = item["title"]?.ToString() ?? "";
            var year  = item["year"] is int y ? y : (int?)null;
            if (mediaType == "show") db.SetShowStatus(id, "downloaded");
            else                     db.SetMovieStatus(id, "downloaded");
            db.AddThemeHistory(id, title, year, themeTitle, url, mediaType, pickedBy, matchScore);

            // A replacement that lands clears the flag that asked for it. Doing this here
            // rather than in the auto-download worker means a manual replacement from the
            // movies page clears it too — the flag cannot outlive the problem.
            db.ClearRecheck(id, mediaType);
            _jobs[key] = new JobState(false, true, null);
```

Update call sites:

| Call site | `pickedBy` | `matchScore` |
|---|---|---|
| `MoviesController.Download` (`:180`) | `"manual"` | `null` |
| `MoviesController.DownloadUrl` (`:209`) | `"manual"` | `null` |
| `MoviesController.AutoDownload` (`:161`) | `"auto"` | `best["score"] as int?` |
| `AutoDownloadService` (`:230`) | `"auto"` | `best["score"] as int?` |
| `ShowsController` download / download-url | `"manual"` | `null` |
| `ShowAutoDownloadService` | `"auto"` | best result's `score` |

`score` is present on every search result — `RankAndMark` writes it back (`YoutubeService.cs:68`).

- [ ] **Step 4: Run and verify they pass, then commit**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add -A src/Themearr.API tests/Themearr.API.Tests
git commit -m "feat: record how each theme was chosen, and clear re-check on success"
```

---

## Task 5: The scan

**Files:**
- Create: `src/Themearr.API/Services/ThemeRecheckService.cs`
- Modify: `src/Themearr.API/Program.cs` (singleton registration only)
- Test: `tests/Themearr.API.Tests/ThemeRecheckServiceTests.cs` (create)

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:

```csharp
public class ThemeRecheckService(Database db, ILogger<ThemeRecheckService> log)
{
    public bool InProgress { get; }
    public Task Current { get; }
    public Task<bool> StartAsync();       // false when one is already running
    public object GetStatus();            // { inProgress, finished, scanned, flagged, error }
    public static string ResolveProvenance(string? pickedBy, string? sourceUrl);
}
```

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/ThemeRecheckServiceTests.cs` covering:

1. `ResolveProvenance("manual", anything)` → `"manual"`; `ResolveProvenance("auto", …)` → `"auto"` — an explicit value always wins.
2. `ResolveProvenance(null, "https://www.youtube.com/watch?v=abc")` → `"auto"`.
3. `ResolveProvenance(null, "https://example.com/song.mp3")` → `"manual"` — only `POST /api/download-url` (`MoviesController.cs:184`) can produce a non-provider URL.
4. `ResolveProvenance(null, null)` → `"auto"` — nothing says a human was involved.
5. An auto-picked trailer with a theme file on disk → `recheck_state = 'queued'`, reason `"looks like a trailer, clip or promo"`.
6. A **manual**-picked trailer → `'review'`, never `'queued'`.
7. A passing pick → `recheck_state` stays null, and `GetStatus().flagged` is 0.
8. An item whose stored `theme_title` is null → untouched (nothing to judge).
9. An item with no theme file on disk → untouched.
10. An ignored item → untouched.
11. An item already `'kept'` → untouched, even though its title still fails.
12. Shows are scanned too: a show with a trailer theme is flagged.
13. `StartAsync` twice concurrently → the second returns `false`.

Use `TempDir` for real theme files (`ThemeFiles.HasUsableThemeInExistingFolder` stats the disk), and `NullLogger<ThemeRecheckService>.Instance`.

- [ ] **Step 2: Run and verify it fails**

Expected: compile error — `ThemeRecheckService` does not exist.

- [ ] **Step 3: Implement**

Mirror `SyncService`'s concurrency shape exactly (`SyncService.cs:9-48`) — `SemaphoreSlim(1,1)`, `volatile` counters, `_current = Task.Run(RunAsync).ContinueWith(_ => _lock.Release())`, and a `GetStatus()` object. The scan body, per media type:

```csharp
    private void ScanMediaType(string mediaType)
    {
        foreach (var item in db.GetRecheckCandidates(mediaType))
        {
            var id     = item["id"]?.ToString() ?? "";
            var folder = item["folderName"]?.ToString() ?? "";

            // 'kept' is the user's answer, and it is permanent. Deleting the theme is the
            // way back — DeleteTheme clears the flag alongside the status reset it already
            // does (MoviesController.cs:84).
            if (item["recheckState"]?.ToString() == "kept") continue;

            // Judge only what is actually there. A row can say 'downloaded' while the file
            // was removed on disk out-of-band.
            if (string.IsNullOrEmpty(folder) || !ThemeFiles.HasUsableThemeInExistingFolder(folder)) continue;

            var prov = db.GetLatestThemeProvenance(id, mediaType);

            // No recorded title means no evidence to judge — a direct-URL download never
            // gets one (DownloadService.cs:147). Flagging these would be noise about
            // something that cannot be assessed.
            if (prov?.ThemeTitle is not { } themeTitle) continue;

            _scanned++;

            var reason = ThemeMatch.RejectionReason(
                themeTitle, item["title"]?.ToString(), item["year"] as int?, prov.MatchScore);
            if (reason == null) continue;

            // A hand-picked theme is never replaced without asking: manual picks fail the
            // rules more often than they should, because a real track's title often carries
            // no music word at all ("Blade Runner 2049 - Mesa").
            var state = ResolveProvenance(prov.PickedBy, prov.SourceUrl) == "manual" ? "review" : "queued";
            db.SetRecheck(id, mediaType, state, reason);
            _flagged++;

            log.LogInformation("Re-check flagged {MediaType} '{Title}' as {State}: {Reason}",
                mediaType, LogSanitizer.Clean(item["title"]?.ToString() ?? ""), state, reason);
        }
    }
```

`ResolveProvenance`:

```csharp
    /// <summary>
    /// How a theme was chosen. An explicit <c>picked_by</c> always wins; rows written
    /// before this feature have none, so provenance falls back to the one signal the old
    /// data actually carries — a non-provider URL can only have come from
    /// POST /api/download-url (MoviesController.cs:184), which a human typed.
    ///
    /// The residual gap is known and accepted: a legacy theme hand-picked from the search
    /// list is indistinguishable from an auto-picked one. Treating every legacy row as
    /// manual instead would make the first re-check — the one that clears the backlog this
    /// feature exists for — entirely manual.
    /// </summary>
    public static string ResolveProvenance(string? pickedBy, string? sourceUrl) =>
        pickedBy ?? (string.IsNullOrEmpty(sourceUrl) || DownloadService.IsProviderUrl(sourceUrl)
            ? "auto" : "manual");
```

`GetRecheckCandidates` must return `recheckState` in its row dictionary for the `'kept'` check — confirm Task 3's SELECT includes it, and add it there if not.

Register in `Program.cs` beside the other singletons (near line 46):

```csharp
builder.Services.AddSingleton<ThemeRecheckService>();
```

- [ ] **Step 4: Run and verify it passes, then commit**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add src/Themearr.API/Services/ThemeRecheckService.cs src/Themearr.API/Program.cs tests/Themearr.API.Tests/ThemeRecheckServiceTests.cs
git commit -m "feat: scan a library for themes that no longer pass the matching rules"
```

**Wave 0 gate:** the full API suite is green and `dotnet build` succeeds before any wave-1 task starts. Wave 1 tasks compile against everything above.

---

# WAVE 1 — PARALLEL (Tasks 6–9 run concurrently)

## Task 6: Run one scan after a rules change

**Files:**
- Create: `src/Themearr.API/Services/AutoRecheckService.cs`
- Modify: `src/Themearr.API/Program.cs` (hosted-service registration only — **the only wave-1 task that may touch this file**)
- Test: `tests/Themearr.API.Tests/AutoRecheckServiceTests.cs` (create)

**Interfaces:**
- Consumes: `ThemeRecheckService.StartAsync()`, `ThemeMatch.RulesVersion`, `Database.GetSetting`/`SetSetting`.
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Write the failing tests**

Cover, with the version-gate logic extracted so it is testable without a host or a clock:

1. Stored version `0` (setting absent) and `RulesVersion` 1 → a scan runs, and `match_rules_version` is then `"1"`.
2. Stored version equals `RulesVersion` → no scan, and nothing is written.
3. Stored version *newer* than the code (a downgrade) → no scan; the stored value is left alone rather than moved backwards.
4. A non-numeric stored value → treated as 0, so a corrupted setting recovers rather than wedging.

Extract the decision as `internal static bool ShouldRecheck(string storedRaw, int codeVersion)` so tests bind to the rule, not to `ExecuteAsync`.

- [ ] **Step 2: Run and verify it fails** — compile error, `AutoRecheckService` does not exist.

- [ ] **Step 3: Implement**

A `BackgroundService` with a warm-up delay, mirroring `AutoDownloadService`'s 45s pattern (`AutoDownloadService.cs:88`) so DB init and the first sync land first. Read the setting, call `ShouldRecheck`, run one scan via `ThemeRecheckService.StartAsync()`, `await` its `Current`, then store the new version. Wrap the whole body in try/catch and log — a failure here must never take the host down.

Register:

```csharp
builder.Services.AddHostedService<AutoRecheckService>();
```

- [ ] **Step 4: Run, verify green, commit**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter AutoRecheckServiceTests
git add src/Themearr.API/Services/AutoRecheckService.cs src/Themearr.API/Program.cs tests/Themearr.API.Tests/AutoRecheckServiceTests.cs
git commit -m "feat: re-check themes once after the matching rules change"
```

---

## Task 7: Replacement work in the auto-download workers

**Files:**
- Modify: `src/Themearr.API/Services/AutoDownloadService.cs`, `src/Themearr.API/Services/ShowAutoDownloadService.cs`
- Test: `tests/Themearr.API.Tests/RecheckWorkerTests.cs` (create)

**Interfaces:**
- Consumes: `Database.GetRecheckQueued`, `SetRecheck`; `DownloadService.Start(..., pickedBy, matchScore)`.
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Write the failing tests — the hazard first**

The first test is the one that matters:

```csharp
    [Fact]
    public void FailedReplacement_isCooledDown_notRetriedEveryTick()
    {
        // THE hazard. The loop decides whether the last download worked by testing
        // `status != "downloaded"` (AutoDownloadService.cs:151-155). A re-check item is
        // ALREADY 'downloaded', so a failed replacement reads as success, no cooldown is
        // applied, and the item is retried every 30 seconds forever. Re-check items must
        // be judged by whether recheck_state is still 'queued'.
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");

        var worker = Build(db);
        worker.NoteStartedForTest(id, wasRecheck: true);   // stand-in for _lastStartedMovieId
        worker.RollLastOutcomeForTest();                   // the block at :148-156

        // The download never landed, so the flag is untouched — and THAT, not `status`,
        // is what must drive the cooldown.
        Assert.Single(db.GetRecheckQueued("movie"));
        Assert.True(worker.IsCoolingDownForTest(id),
            "a failed replacement must cool down; judging it by `status` reports success "
            + "for an item that was already 'downloaded' and retries it every tick");
    }
```

The three `…ForTest` members are `internal` seams you add in Step 3 — the outcome
block is private and has no other entry point. Add
`[assembly: InternalsVisibleTo("Themearr.API.Tests")]` if the project does not already
have it. If you find a way to drive this through the public surface instead, prefer that
and say so in your report.

Then:

2. Pending items are served before queued re-check items — a library with both starts the pending one.
3. A queued item is picked up when the pending queue is dry.
4. A queued item whose search yields no confident match becomes `'review'` — **not** a 6h cooldown and retry (`AutoDownloadService.cs:19`).
5. An item in `'review'` is never picked up by the worker.
6. All of the above for `ShowAutoDownloadService`.

Follow `ShowAutoDownloadServiceTests`' existing fixture style — `ReadyProvider`/`UnconfiguredProvider` stubs, `ServiceCollection`, `NullLogger`. `YoutubeService` has no interface, so tests that would need a live search stop at the guards, exactly as the existing worker tests do; assert on state transitions and candidate selection rather than on search results.

- [ ] **Step 2: Run and verify the hazard test fails** — for the right reason: the item is retried / no cooldown recorded, **not** a compile error.

- [ ] **Step 3: Implement**

In both workers:
- After the existing pending-candidate selection finds nothing, fall through to `db.GetRecheckQueued(mediaType)`, applying the same `_cooldownUntil` filter.
- Track whether the last-started item was a re-check (e.g. `_lastStartedWasRecheck`), and in the outcome block judge a re-check item by `recheck_state == "queued"` rather than by `status`.
- On no confident match for a re-check item, move it to review with the **compare-and-set** form — `db.TrySetRecheck(id, mediaType, "review", "no better match found", expectedState: "queued")` — never the unconditional `SetRecheck`, and never `NoMatchCooldown`.

  The guard is not defensive padding; it protects the feature's central promise. Between the worker reading an item as `'queued'` and its search returning, the user can click "Keep this" and move that row to `'kept'`. An unconditional write then discards an explicit human decision about their own library. The same guard also stops a stale tick re-flagging an item whose replacement already landed and cleared the flag. `TrySetRecheck` returns `false` in both cases; treat that as "someone else resolved this item" and move on without logging an error.

- **Do not mark a re-check item's own progress with `SetRecheck`.** A row stays `'queued'` for the whole download, and `DownloadService` clears it on success (`DownloadService.cs`, the `AddThemeHistory` success block). Re-entry during that window is already prevented by the existing one-download-at-a-time guard — but note that guard has a watchdog that stops counting a job as in-flight after `DownloadTimeout` plus grace (`DownloadService.cs:85-89`), so a pathologically long download can let a later tick see the row as `'queued'` again. `TrySetRecheck`'s guard is what makes that harmless rather than a lost update.
- Pass `pickedBy: "auto"` and the best result's `score` to `Start`.
- Add `recheckQueuedCount` to `GetDiagnostics()` so `/api/auto-download/debug` shows the new candidate set.

- [ ] **Step 4: Run, verify green, commit**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add src/Themearr.API/Services/AutoDownloadService.cs src/Themearr.API/Services/ShowAutoDownloadService.cs tests/Themearr.API.Tests/RecheckWorkerTests.cs
git commit -m "feat: let the auto-download workers replace flagged themes"
```

---

## Task 8: API surface

**Files:**
- Create: `src/Themearr.API/Controllers/RecheckController.cs`
- Modify: `src/Themearr.API/Controllers/MoviesController.cs`, `ShowsController.cs`, `StatsController.cs`
- Test: `tests/Themearr.API.Tests/RecheckEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `ThemeRecheckService`, `Database.GetFlaggedItems/SetRecheck/CountNeedsAttention`.
- Produces: the routes below.

| Route | Behaviour |
|---|---|
| `POST /api/themes/recheck` | `{ started, detail }`; `detail` is `"Re-check already in progress"` when `started` is false |
| `GET /api/themes/recheck/status` | `ThemeRecheckService.GetStatus()` |
| `GET /api/themes/recheck/flagged` | `GetFlaggedItems()`, each carrying `mediaType` |
| `POST /api/movies/{id}/recheck/keep` | → `'kept'`; 404 for an unknown id |
| `POST /api/movies/{id}/recheck/replace` | → `'queued'`; 404 for an unknown id |
| `POST /api/shows/{id}/recheck/keep`, `.../replace` | the show mirrors |
| `POST /api/shows/auto-download/{showId}` | **new** — mirrors `MoviesController.AutoDownload` (`:127-164`) |

**Why the new show endpoint** (a scoped amendment to the spec): the shows adapter composes auto-download client-side out of search + the *manual* download endpoint (`media/adapter.ts`), so a show theme the matcher chose would be recorded `picked_by = 'manual'` and could never be auto-replaced. A server-side endpoint makes the provenance honest rather than trusting a client flag, and closes a movie/show parity gap that already existed.

- [ ] **Step 1: Write the failing tests**

Cover: each route's happy path; 404 for unknown ids; `keep`/`replace` actually move `recheck_state`; the flagged list carries `mediaType`; the show auto-download endpoint records `picked_by = 'auto'`; and **auth** — every new route returns 401 without a bearer token. Follow `ShowsControllerTests` / `SyncControllerTests` for the fixture style.

Add the new prefixes to `AuthBoundaryTests`' route table if it enumerates routes explicitly — read it first. `ApiAuthMiddleware.RequiresAuth` itself must **not** change (`ApiAuthMiddleware.cs:31-34`); widening a prefix there is the change no other test catches.

- [ ] **Step 2: Run and verify it fails** — 404s from routes that do not exist yet.

- [ ] **Step 3: Implement**

`RecheckController` mirrors `SyncController`'s shape. The per-item actions validate the id via `db.GetMovie` / `db.GetShow` before writing, exactly as the existing ignore/unignore endpoints do (`MoviesController.cs:89-103`). Add `needsAttention` to both stats payloads in `StatsController`. Route the shows adapter's auto-download at the new endpoint in Task 9.

Log any media title with `LogSanitizer.Clean`.

- [ ] **Step 4: Run, verify green, commit**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
git add src/Themearr.API/Controllers tests/Themearr.API.Tests/RecheckEndpointTests.cs
git commit -m "feat: expose re-check start, status, flagged list and per-item actions"
```

---

## Task 9: Frontend

**Files:**
- Modify: `src/Themearr.Web/src/lib/api.ts`, `src/lib/types.ts`, `src/lib/media/adapter.ts`
- Modify: `src/Themearr.Web/src/app/queue/page.tsx`, `src/app/dashboard/page.tsx`
- Test: `src/Themearr.Web/src/app/queue/recheck.test.tsx`, `src/app/dashboard-needs-attention.test.tsx` (create)

**Interfaces:**
- Consumes: Task 8's routes. Until Task 8 lands, mock them in tests — this task is written against the route table above, not against running code.
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Write the failing tests**

`recheck.test.tsx`:
1. Flagged items render with their title and reason.
2. "Keep this" calls `POST /api/movies/{id}/recheck/keep` and the row leaves the list.
3. "Find replacement" calls `.../recheck/replace`.
4. A show row routes to the `shows` endpoints — proving `mediaType` is honoured, not assumed.
5. The re-check button is disabled while `inProgress` is true.
6. The status poll **stops** once `inProgress` goes false — assert the fetch count stops climbing after the terminal response. Mirror `polls-stay-silent.test.tsx`.
7. A slow status response that resolves after a newer one does not overwrite it. Mirror `movies-refresh-race.test.tsx`.
8. A failed flagged-list request shows an error, not an empty "all caught up" state — route it through `useResource` (`useResource.ts:3-22` documents exactly this bug).

`dashboard-needs-attention.test.tsx`: the tile renders the count for movies and for shows, and renders nothing/zero cleanly when the field is absent.

- [ ] **Step 2: Run and verify they fail**

```bash
cd src/Themearr.Web && npm test
```

Expected: the new files fail on missing exports (`recheckApi`) and missing UI text.

- [ ] **Step 3: Implement**

- `types.ts`: add `FlaggedItem { id, mediaType: 'movie' | 'show', title, year, themeTitle, reason, state }`, `RecheckStatus`, and `needsAttention: number` on `DashboardStats` and `ShowStats`.
- `api.ts`: add `recheckApi` with `start()`, `status()`, `flagged()`, `keep(mediaType, id)`, `replace(mediaType, id)` following the existing `request<T>` style.
- `adapter.ts`: point `showsAdapter.autoDownload` at the new `POST /api/shows/auto-download/{id}` endpoint and delete the client-side composition, keeping its error copy identical.
- `queue/page.tsx`: a "Needs attention" section and the re-check button. Reuse the page's existing in-flight-guard pattern (`STATUS_TIMEOUT_MS` / `STATUS_MAX_FAILURES`, lines 10-25) for the status poll rather than writing a new one.
- `dashboard/page.tsx`: the count tile.

- [ ] **Step 4: Run the frontend gate and commit**

```bash
cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint
```

Expected: vitest green; `tsc` clean; eslint at **0 errors, 3 warnings** — a fourth is yours.

```bash
git add src/Themearr.Web
git commit -m "feat: surface flagged themes in the queue and on the dashboard"
```

---

# WAVE 2 — SERIAL VERIFICATION

## Task 10: End-to-end and the full gate

**Files:**
- Modify: `tests/e2e/` (add a spec following the existing smoke suite's shape)

- [ ] **Step 1: Write the E2E spec**

Seed a movie with a theme whose recorded title is a trailer, start a re-check via the UI, and assert the item appears under "Needs attention" with its reason. E2E boots the real API on :5099 against a throwaway DB, so seed through the API rather than by writing SQLite directly.

- [ ] **Step 2: Build the frontend, then run E2E**

```bash
cd src/Themearr.Web && npm run build
cd ../../tests/e2e && npm test
```

E2E serves the built bundle — a stale `out/` tests the old UI and passes for the wrong reason.

- [ ] **Step 3: Run the complete gate**

```bash
dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo
cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build
cd ../../tests/e2e && npm test
```

Every suite must report `Failed: 0, Skipped: 0`. The API suite is the big one — if it reports two digits, you filtered by accident.

- [ ] **Step 4: Commit**

```bash
git add tests/e2e
git commit -m "test: end-to-end coverage for flagging a stale theme"
```

## Task 11: Review

- [ ] **Step 1: Run `concurrency-reviewer`** over the full diff. It covers exactly this change's risk surface: two background services modified, one added, and a new React poll.

- [ ] **Step 2: Run `security-invariant-reviewer`** over the full diff — new `/api` routes and media titles reaching logs. Confirm `ApiAuthMiddleware.RequiresAuth` is untouched (`ApiAuthMiddleware.cs:31-34`).

  This agent is defined at `.claude/agents/security-invariant-reviewer.md`, which landed in `d50dc08` — it exists only on branches based on that commit. If it appears unavailable, check the branch base before concluding it is gone.

- [ ] **Step 3: Re-read the spec against the diff.** Every "Explicitly out of scope" item must still be out of scope. The one deliberate addition is the show auto-download endpoint in Task 8 — it should be the only thing in the diff the spec does not describe.

- [ ] **Step 4: Run `/check-citations`** over both the spec and this plan.

- [ ] **Step 5: Open the PR.** `feat:` — merging to `main` publishes a minor release.

---

## Self-Review Notes

**Spec coverage:** every spec section maps to a task — data model → 2, 3; judging → 1; state machine → 4 (clear), 5 (flag), 7 (queued→review), 8 (user actions); services → 5, 6, 7; API → 8; frontend → 9; `RulesVersion` honesty → 1 (fingerprint); testing → each task plus 10.

**Deliberate spec amendment:** the show auto-download endpoint (Task 8). Without it, `picked_by` for shows is wrong, and half the feature silently degrades to review-only.

**Known deferrals, all recorded in the spec's out-of-scope section:** no per-item re-check from the media pages, no recurring schedule, `'kept'` is permanent, replacements are not labelled distinctly in history.
