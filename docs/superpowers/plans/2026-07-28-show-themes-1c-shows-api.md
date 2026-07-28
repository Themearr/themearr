# Show Themes — Phase 1c (Shows API) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the show themes engine (1a discovery, 1b downloads) over HTTP so the 1d frontend can list, search, download, ignore, delete, preview and count show themes.

**Architecture:** A parallel `ShowsController` mirroring `MoviesController`'s shape, with the two genuinely risky pieces (theme-file resolution + the delete-within-roots guard) extracted into `ThemeFiles` so there is one implementation, not two. Show posters live at `/api/poster/show` — inside the already-public `/api/poster` prefix — so no auth-exemption change is needed. Show status gains a fourth value, `plexTheme`.

**Tech Stack:** .NET 10 Web API, `Microsoft.Data.Sqlite`, xUnit, ASP.NET controllers instantiated directly in tests (no `TestHost`).

## Global Constraints

- **.NET 10**; xUnit tests in `tests/Themearr.API.Tests/`. Follow existing style.
- **Movie behavior must not change.** `ReadMediaRow` (shared 3-state derivation) is NOT modified. Movie routes keep their existing un-namespaced paths (`/api/search/{id}`, `/api/download`, `/api/download/status/{id}`). Run the FULL suite after each task.
- **Never add an auth exemption for `/api/shows`.** The public prefix is `/api/poster` and stays exactly that. Show posters live at `/api/poster/show`.
- **Show status derivation order (load-bearing):** `ignored` → local non-empty `theme.*` → `plex_has_theme` → `pending`. A local file always beats a Plex theme.
- **Shows are Plex-only.** Show posters resolve via `PlexLibrarySource`, never `LibrarySourceResolver.Active`.
- **Show search query is year-free:** reuse `ShowAutoDownloadService.BuildQuery(title)`.
- The lean test project has **no `TestHost` dependency** — do not add one. Route-level auth is tested via an extracted pure predicate (Task 8).

---

### Task 1: Extract shared theme-file helpers into `ThemeFiles`

`MoviesController` inlines the `theme.*` lookup, the extension→MIME map, and the delete loop. Shows need all three; two copies of a path-handling routine is how they drift.

**Files:**
- Modify: `src/Themearr.API/Services/ThemeFiles.cs`
- Modify: `src/Themearr.API/Controllers/MoviesController.cs` (`GetThemeAudio`, `DeleteTheme`)
- Test: `tests/Themearr.API.Tests/ThemeFilesLookupTests.cs` (create)

**Interfaces:**
- Produces: `static string? ThemeFiles.FindThemeFile(string folder)` — first `theme.*` that is not `.part`/`.ytdl`, or null.
- Produces: `static string ThemeFiles.ContentTypeFor(string path)` — MIME from extension, `audio/mpeg` fallback.
- Produces: `static bool ThemeFiles.DeleteThemes(string folder)` — deletes every `theme.*` except `.part`/`.ytdl`; true if any were deleted.

- [ ] **Step 1: Write the failing test** (`ThemeFilesLookupTests.cs`)

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeFilesLookupTests
{
    [Fact]
    public void FindThemeFile_skips_partial_downloads()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "theme.mp3.part"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "theme.ytdl"), "x");
        Assert.Null(ThemeFiles.FindThemeFile(dir.Path));

        File.WriteAllBytes(Path.Combine(dir.Path, "theme.mp3"), [0x49, 0x44, 0x33]);
        Assert.Equal("theme.mp3", Path.GetFileName(ThemeFiles.FindThemeFile(dir.Path)));
    }

    [Theory]
    [InlineData("theme.mp3",  "audio/mpeg")]
    [InlineData("theme.m4a",  "audio/mp4")]
    [InlineData("theme.ogg",  "audio/ogg")]
    [InlineData("theme.opus", "audio/opus")]
    [InlineData("theme.webm", "audio/webm")]
    [InlineData("theme.flac", "audio/flac")]
    [InlineData("theme.wav",  "audio/mpeg")]   // unknown → safe default
    public void ContentTypeFor_maps_known_extensions(string name, string expected) =>
        Assert.Equal(expected, ThemeFiles.ContentTypeFor(name));

    [Fact]
    public void DeleteThemes_removes_themes_but_leaves_partials_and_other_files()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "theme.mp3"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "theme.m4a"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "theme.mp3.part"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "movie.mkv"), "x");

        Assert.True(ThemeFiles.DeleteThemes(dir.Path));

        Assert.False(File.Exists(Path.Combine(dir.Path, "theme.mp3")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "theme.m4a")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "theme.mp3.part")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "movie.mkv")));

        Assert.False(ThemeFiles.DeleteThemes(dir.Path));   // nothing left to delete
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ThemeFilesLookupTests"`
Expected: FAIL to compile — `FindThemeFile` / `ContentTypeFor` / `DeleteThemes` do not exist.

- [ ] **Step 3: Add the three helpers to `ThemeFiles`**

Append inside the `ThemeFiles` class in `src/Themearr.API/Services/ThemeFiles.cs`:

```csharp
    /// <summary>Extensions written mid-download; never a playable theme.</summary>
    private static bool IsPartial(string path) =>
        Path.GetExtension(path) is ".part" or ".ytdl";

    /// <summary>
    /// The playable theme file in <paramref name="folder"/>, or null. Shared by the movie
    /// and show theme-audio endpoints so the two can never disagree about which file is
    /// "the theme".
    /// </summary>
    public static string? FindThemeFile(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "theme.*").FirstOrDefault(f => !IsPartial(f))
            : null;

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3"  => "audio/mpeg",
        ".m4a"  => "audio/mp4",
        ".ogg"  => "audio/ogg",
        ".opus" => "audio/opus",
        ".webm" => "audio/webm",
        ".flac" => "audio/flac",
        _       => "audio/mpeg",
    };

    /// <summary>
    /// Deletes every theme file in <paramref name="folder"/>, leaving partial downloads
    /// alone. Returns true if anything was deleted. Callers MUST have already confirmed
    /// the folder is inside the configured library roots (see <see cref="IsWithinRoots"/>).
    /// </summary>
    public static bool DeleteThemes(string folder)
    {
        var deleted = false;
        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
        {
            if (IsPartial(f)) continue;
            File.Delete(f);
            deleted = true;
        }
        return deleted;
    }
```

- [ ] **Step 4: Point `MoviesController` at the helpers**

In `DeleteTheme`, replace the delete loop:

```csharp
        var deleted = ThemeFiles.DeleteThemes(folder);
```

In `GetThemeAudio`, replace the lookup and the content-type switch:

```csharp
        var themeFile = ThemeFiles.FindThemeFile(folder);
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        var contentType = ThemeFiles.ContentTypeFor(themeFile);
```

Leave everything else in both methods (roots guard, status reset, ETag/caching) untouched.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS — new tests green AND every existing movie test (`DeleteThemeTests`, etc.) still green.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/ThemeFiles.cs src/Themearr.API/Controllers/MoviesController.cs tests/Themearr.API.Tests/ThemeFilesLookupTests.cs
git commit -m "refactor: extract theme-file lookup, MIME map and delete into ThemeFiles"
```

---

### Task 2: 4-state show status + `plexHasTheme`

Shows need their own row reader. The shared `ReadMediaRow` stays exactly as it is — movies depend on its 3-state derivation.

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (`GetAllShows`, `GetShow`, new `ReadShowRow`)
- Test: `tests/Themearr.API.Tests/ShowStatusDerivationTests.cs` (create)

**Interfaces:**
- Produces: `GetAllShows()` / `GetShow(id)` rows gain `["plexHasTheme"] = bool` and `["status"]` of `"ignored" | "downloaded" | "plexTheme" | "pending"`.

- [ ] **Step 1: Write the failing test** (`ShowStatusDerivationTests.cs`)

```csharp
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class ShowStatusDerivationTests
{
    private static (Database db, string id, string folder) NewShow(
        TempDir dir, string title, bool plexHasTheme)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        var db = new Database(Path.Combine(dir.Path, $"{title}.db"));
        db.Init();
        db.UpsertShows([new ShowRecord(folder, "plex", "srv1:1", title, 2010, folder, plexHasTheme)]);
        return (db, MediaFolderId.For(folder), folder);
    }

    [Fact]
    public void No_theme_anywhere_is_pending()
    {
        using var dir = new TempDir();
        var (db, id, _) = NewShow(dir, "Plain", plexHasTheme: false);
        Assert.Equal("pending", db.GetShow(id)!["status"]);
        Assert.Equal(false, db.GetShow(id)!["plexHasTheme"]);
    }

    [Fact]
    public void Plex_theme_without_a_local_file_is_plexTheme()
    {
        using var dir = new TempDir();
        var (db, id, _) = NewShow(dir, "PlexThemed", plexHasTheme: true);
        Assert.Equal("plexTheme", db.GetShow(id)!["status"]);
        Assert.Equal(true, db.GetShow(id)!["plexHasTheme"]);
    }

    /// <summary>
    /// Rule 2 before rule 3 — the load-bearing ordering. Without it, "Download anyway" on
    /// a Plex-themed show would write a real theme.mp3 and the row would still claim
    /// plexTheme, so the UI could never show the download succeeded.
    /// </summary>
    [Fact]
    public void A_local_theme_beats_a_plex_theme()
    {
        using var dir = new TempDir();
        var (db, id, folder) = NewShow(dir, "Both", plexHasTheme: true);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);

        Assert.Equal("downloaded", db.GetShow(id)!["status"]);
        Assert.Equal(true, db.GetShow(id)!["plexHasTheme"]);   // flag still reported
    }

    [Fact]
    public void An_empty_local_theme_does_not_count_as_downloaded()
    {
        using var dir = new TempDir();
        var (db, id, folder) = NewShow(dir, "Truncated", plexHasTheme: false);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), []);
        Assert.Equal("pending", db.GetShow(id)!["status"]);
    }

    [Fact]
    public void Ignored_beats_everything()
    {
        using var dir = new TempDir();
        var (db, id, folder) = NewShow(dir, "Ignored", plexHasTheme: true);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        db.SetShowIgnored(id, true);

        Assert.Equal("ignored", db.GetShow(id)!["status"]);
    }

    [Fact]
    public void GetAllShows_uses_the_same_derivation()
    {
        using var dir = new TempDir();
        var (db, id, _) = NewShow(dir, "Listed", plexHasTheme: true);
        var row = db.GetAllShows().Single(s => (string)s["id"]! == id);
        Assert.Equal("plexTheme", row["status"]);
        Assert.Equal(true, row["plexHasTheme"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowStatusDerivationTests"`
Expected: FAIL — `plexHasTheme` key missing; `plexTheme` status never returned.

- [ ] **Step 3: Add `ReadShowRow` and use it**

In `src/Themearr.API/Data/Database.cs`, add next to `ReadMediaRow`:

```csharp
    /// <summary>
    /// Show rows carry a fourth status, 'plexTheme', for a show Plex already themes but
    /// that has no local theme file. Deliberately separate from <see cref="ReadMediaRow"/>:
    /// movies have no equivalent state, and widening the shared reader would change movie
    /// behaviour. Expects the SELECT to end with ..., ignored, plex_has_theme.
    /// </summary>
    private static Dictionary<string, object?>? ReadShowRow(SqliteDataReader r)
    {
        var ignored      = !r.IsDBNull(8) && r.GetInt32(8) == 1;
        var plexHasTheme = !r.IsDBNull(9) && r.GetInt32(9) == 1;
        var folder       = r.IsDBNull(1) ? "" : r.GetString(1);

        if (!ignored && (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)))
            return null;

        // Order matters: a local file is a fact on disk and always wins over Plex's own
        // theme, so downloading one for a plexTheme show visibly moves it to 'downloaded'.
        string status;
        if (ignored)                                            status = "ignored";
        else if (ThemeFiles.HasUsableThemeInExistingFolder(folder)) status = "downloaded";
        else if (plexHasTheme)                                  status = "plexTheme";
        else                                                    status = "pending";

        return new Dictionary<string, object?>
        {
            ["id"]           = r.GetString(0),
            ["folderName"]   = folder,
            ["source"]       = r.GetString(2),
            ["sourceRef"]    = r.IsDBNull(3) ? null : r.GetString(3),
            ["title"]        = r.GetString(4),
            ["year"]         = r.IsDBNull(5) ? null : r.GetInt32(5),
            ["sourcePath"]   = r.IsDBNull(6) ? null : r.GetString(6),
            ["status"]       = status,
            ["ignored"]      = ignored,
            ["plexHasTheme"] = plexHasTheme,
        };
    }
```

Then change both show queries to select `plex_has_theme` and use the new reader:

```csharp
    public List<Dictionary<string, object?>> GetAllShows()
    {
        using var conn = Open();
        var result = new List<Dictionary<string, object?>>();
        conn.Query("SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored, plex_has_theme FROM shows ORDER BY status, title",
            r => { while (r.Read()) { var row = ReadShowRow(r); if (row != null) result.Add(row); } });
        return result;
    }

    public Dictionary<string, object?>? GetShow(string id)
    {
        using var conn = Open();
        Dictionary<string, object?>? result = null;
        conn.Query("SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored, plex_has_theme FROM shows WHERE id = @id",
            r => { if (r.Read()) result = ReadShowRow(r); }, ("@id", id));
        return result;
    }
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS. Note `ShowAutoDownloadService.GetDiagnostics` counts `status == "pending"` from `GetAllShows` — plexTheme shows correctly stop being counted as pending, which is the intended behaviour, not a regression.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/ShowStatusDerivationTests.cs
git commit -m "feat: 4-state show status (plexTheme) and plexHasTheme in show rows"
```

---

### Task 3: `GetShowStats()` + `GET /api/stats/shows`

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (add `GetShowStats`, add `ShowStatsResult` record)
- Modify: `src/Themearr.API/Controllers/StatsController.cs`
- Test: `tests/Themearr.API.Tests/ShowStatsTests.cs` (create)

**Interfaces:**
- Produces: `ShowStatsResult GetShowStats()` with `(int Total, int Downloaded, int PlexTheme, int Pending, int Ignored, double Coverage)`.
- Produces: `GET /api/stats/shows` → `{ total, downloaded, plexTheme, pending, ignored, coverage }`.

- [ ] **Step 1: Write the failing test** (`ShowStatsTests.cs`)

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowStatsTests
{
    private static string Add(Database db, TempDir dir, string title, bool plexHasTheme, bool localTheme)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", $"srv1:{title}", title, 2010, folder, plexHasTheme)]);
        if (localTheme) File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        return MediaFolderId.For(folder);
    }

    [Fact]
    public void Counts_each_state_and_treats_plexTheme_as_covered()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();

        Add(db, dir, "Downloaded", plexHasTheme: false, localTheme: true);
        Add(db, dir, "PlexThemed", plexHasTheme: true,  localTheme: false);
        Add(db, dir, "Pending",    plexHasTheme: false, localTheme: false);
        var ignoredId = Add(db, dir, "Ignored", plexHasTheme: false, localTheme: false);
        db.SetShowIgnored(ignoredId, true);

        var stats = db.GetShowStats();

        Assert.Equal(4, stats.Total);
        Assert.Equal(1, stats.Downloaded);
        Assert.Equal(1, stats.PlexTheme);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Ignored);
        // (downloaded + plexTheme) / total — a Plex-themed show is covered.
        Assert.Equal(50.0, stats.Coverage);
    }

    [Fact]
    public void Empty_library_reports_zero_coverage_without_dividing_by_zero()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();

        var stats = db.GetShowStats();

        Assert.Equal(0, stats.Total);
        Assert.Equal(0.0, stats.Coverage);
    }

    [Fact]
    public void Endpoint_returns_the_counts()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        Add(db, dir, "PlexThemed", plexHasTheme: true, localTheme: false);

        var controller = new StatsController(db, new PosterUrlSigner([1, 2, 3]), null!);
        var result = Assert.IsType<OkObjectResult>(controller.GetShowStats());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("plexTheme").GetInt32());
        Assert.Equal(100.0, body.GetProperty("coverage").GetDouble());
    }
}
```

Note: `StatsController`'s `sources` argument is passed `null!` because `GetShowStats` never touches it — shows are Plex-only and this endpoint returns no poster URLs. If that ever changes, this test will `NullReferenceException` loudly rather than pass silently.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowStatsTests"`
Expected: FAIL to compile — `GetShowStats` does not exist on `Database` or `StatsController`.

- [ ] **Step 3: Add `GetShowStats` to `Database`**

Add near `GetStats()`:

```csharp
    /// <summary>
    /// Aggregate show counts for the shows dashboard. Coverage counts a Plex-themed show
    /// as covered — it has a theme, just not one Themearr wrote — so the number matches
    /// what the user actually hears. The separate counts keep that explainable.
    /// </summary>
    public ShowStatsResult GetShowStats()
    {
        var all        = GetAllShows();
        var downloaded = all.Count(s => s["status"]?.ToString() == "downloaded");
        var plexTheme  = all.Count(s => s["status"]?.ToString() == "plexTheme");
        var pending    = all.Count(s => s["status"]?.ToString() == "pending");
        var ignored    = all.Count(s => s["status"]?.ToString() == "ignored");

        var total    = all.Count;
        var coverage = total > 0 ? Math.Round((downloaded + plexTheme) * 100.0 / total, 1) : 0.0;

        return new ShowStatsResult(total, downloaded, plexTheme, pending, ignored, coverage);
    }
```

And next to `StatsResult` at the bottom of the file:

```csharp
public record ShowStatsResult(
    int Total, int Downloaded, int PlexTheme, int Pending, int Ignored, double Coverage);
```

- [ ] **Step 4: Add the endpoint to `StatsController`**

```csharp
    [HttpGet("shows")]
    public IActionResult GetShowStats()
    {
        var s = db.GetShowStats();
        return Ok(new
        {
            total      = s.Total,
            downloaded = s.Downloaded,
            plexTheme  = s.PlexTheme,
            pending    = s.Pending,
            ignored    = s.Ignored,
            coverage   = s.Coverage,
        });
    }
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Data/Database.cs src/Themearr.API/Controllers/StatsController.cs tests/Themearr.API.Tests/ShowStatsTests.cs
git commit -m "feat: show stats (GetShowStats + GET /api/stats/shows)"
```

---

### Task 4: Show posters at `/api/poster/show`

**Files:**
- Modify: `src/Themearr.API/Services/PosterUrlSigner.cs`
- Modify: `src/Themearr.API/Controllers/PosterController.cs`
- Test: `tests/Themearr.API.Tests/ShowPosterTests.cs` (create)

**Interfaces:**
- Produces: `string PosterUrlSigner.ShowPosterPath(string id, DateTimeOffset expiry)` → `/api/poster/show?id=…&exp=…&sig=…`
- Produces: `bool PosterUrlSigner.VerifyShow(string id, long expUnix, string? sig, DateTimeOffset now)`
- Produces: `GET /api/poster/show` on `PosterController`.

- [ ] **Step 1: Write the failing test** (`ShowPosterTests.cs`)

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowPosterTests
{
    private static readonly byte[] Key = [1, 2, 3, 4, 5];

    [Fact]
    public void ShowPosterPath_targets_the_public_poster_prefix()
    {
        var signer = new PosterUrlSigner(Key);
        var path = signer.ShowPosterPath("abc", DateTimeOffset.UtcNow.AddHours(1));

        // Must live under /api/poster so the existing auth exemption covers it. Anything
        // under /api/shows would 401 for an <img> tag.
        Assert.StartsWith("/api/poster/show?", path);
    }

    /// <summary>
    /// MediaFolderId is a pure function of the folder path, so a show and a movie on one
    /// folder share an id. Domain-separating the signature means a movie poster URL can
    /// never be replayed against the show route, or vice versa.
    /// </summary>
    [Fact]
    public void A_movie_signature_does_not_verify_as_a_show_signature()
    {
        var signer = new PosterUrlSigner(Key);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var movieSig = signer.Sign("shared-id", exp);

        Assert.True(signer.Verify("shared-id", exp, movieSig, DateTimeOffset.UtcNow));
        Assert.False(signer.VerifyShow("shared-id", exp, movieSig, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_show_signature_verifies_on_the_show_route_only()
    {
        var signer = new PosterUrlSigner(Key);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var path = signer.ShowPosterPath("s1", DateTimeOffset.FromUnixTimeSeconds(exp));
        var sig  = path.Split("&sig=")[1];

        Assert.True(signer.VerifyShow("s1", exp, sig, DateTimeOffset.UtcNow));
        Assert.False(signer.Verify("s1", exp, sig, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void An_expired_show_signature_is_rejected()
    {
        var signer = new PosterUrlSigner(Key);
        var exp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var sig = signer.Sign("show:s1", exp);

        Assert.False(signer.VerifyShow("s1", exp, sig, DateTimeOffset.UtcNow));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowPosterTests"`
Expected: FAIL to compile — `ShowPosterPath` / `VerifyShow` do not exist.

- [ ] **Step 3: Add the show scope to `PosterUrlSigner`**

```csharp
    // Domain separation: movie and show ids come from the same MediaFolderId hash space,
    // so without a scope prefix one media type's signed poster URL would validate against
    // the other's route.
    private const string ShowScope = "show:";

    public string ShowPosterPath(string id, DateTimeOffset expiry)
    {
        var exp = expiry.ToUnixTimeSeconds();
        return $"/api/poster/show?id={Uri.EscapeDataString(id)}&exp={exp}&sig={Sign(ShowScope + id, exp)}";
    }

    public bool VerifyShow(string id, long expUnix, string? sig, DateTimeOffset now) =>
        Verify(ShowScope + id, expUnix, sig, now);
```

- [ ] **Step 4: Add the route to `PosterController`**

Add `PlexLibrarySource plexSource` to the primary constructor parameter list, then:

```csharp
    // Show posters. Deliberately under /api/poster (not /api/shows) so the existing auth
    // exemption in Program.cs covers it without widening — see the 1c design doc. Shows
    // only ever come from Plex, so this resolves through PlexLibrarySource directly rather
    // than LibrarySourceResolver.Active, which would be Radarr for a Radarr user.
    [HttpGet("poster/show")]
    public async Task<IActionResult> GetShow(
        [FromQuery] string id, [FromQuery] long exp, [FromQuery] string sig, [FromQuery] int? w = null)
    {
        if (string.IsNullOrEmpty(id) || !signer.VerifyShow(id, exp, sig, DateTimeOffset.UtcNow))
            return Unauthorized();

        var show = db.GetShow(id);
        var sourceRef = show?.GetValueOrDefault("sourceRef")?.ToString() ?? "";
        if (string.IsNullOrEmpty(sourceRef)) return NotFound();

        var width = Math.Clamp(w ?? DefaultWidth, 40, MaxWidth);

        try
        {
            await using var stream = await plexSource.FetchPosterAsync(sourceRef, width, HttpContext.RequestAborted);
            if (stream is null) return NotFound();

            using var buffer = new MemoryStream();
            await StreamLimits.CopyWithLimitAsync(stream, buffer, StreamLimits.MaxPosterBytes);
            buffer.Position = 0;

            Response.Headers.CacheControl = "private, max-age=86400";
            return File(buffer.ToArray(), "image/jpeg");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Show poster fetch failed for {Id}", LogSanitizer.Clean(id));
            return NotFound();
        }
    }
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS. `PosterController` gained a constructor parameter, but no test constructs it directly (verified: no `new PosterController` in `tests/`), and `PlexLibrarySource` is already a registered singleton (`Program.cs:37`), so DI resolves it.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/PosterUrlSigner.cs src/Themearr.API/Controllers/PosterController.cs tests/Themearr.API.Tests/ShowPosterTests.cs
git commit -m "feat: show posters at /api/poster/show (scoped signature, Plex-only source)"
```

---

### Task 5: `ShowsController` — list and search

**Files:**
- Create: `src/Themearr.API/Controllers/ShowsController.cs`
- Test: `tests/Themearr.API.Tests/ShowsControllerTests.cs` (create)

**Interfaces:**
- Consumes: `Database.GetAllShows`/`GetShow` (Task 2), `PosterUrlSigner.ShowPosterPath` (Task 4), `ShowAutoDownloadService.BuildQuery` (1b), `YoutubeService.SearchAsync` (1b).
- Produces: `GET /api/shows`, `GET /api/shows/{showId}/search?q=`.

- [ ] **Step 1: Write the failing test** (`ShowsControllerTests.cs`)

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowsControllerTests
{
    private sealed class NullProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    internal static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    internal static string AddShow(Database db, TempDir dir, string title, bool plexHasTheme = false)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", $"srv1:{title}", title, 2010, folder, plexHasTheme)]);
        return MediaFolderId.For(folder);
    }

    internal static ShowsController New(Database db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var download = new DownloadService(new NullProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);
        return new ShowsController(db, new YoutubeService(), download, new PosterUrlSigner([1, 2, 3]),
            NullLogger<ShowsController>.Instance);
    }

    [Fact]
    public void ListShows_returns_status_plexHasTheme_and_a_show_poster_url()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddShow(db, dir, "PlexThemed", plexHasTheme: true);

        var result = Assert.IsType<OkObjectResult>(New(db).ListShows());
        var shows = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(result.Value);
        var row = shows.Single(s => (string)s["id"]! == id);

        Assert.Equal("plexTheme", row["status"]);
        Assert.Equal(true, row["plexHasTheme"]);
        Assert.StartsWith("/api/poster/show?", (string)row["posterUrl"]!);
    }

    [Fact]
    public void ListShows_gives_no_poster_url_when_the_show_has_no_source_ref()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var folder = Path.Combine(dir.Path, "NoRef"); Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", "", "NoRef", 2010, folder, false)]);

        var result = Assert.IsType<OkObjectResult>(New(db).ListShows());
        var shows = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(result.Value);

        Assert.Null(shows.Single(s => (string)s["title"]! == "NoRef")["posterUrl"]);
    }

    [Fact]
    public async Task Search_404s_for_an_unknown_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        var result = await New(db).SearchYoutube("nope");

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowsControllerTests"`
Expected: FAIL to compile — `ShowsController` does not exist.

- [ ] **Step 3: Create `ShowsController` with list + search**

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// The shows API. A deliberate parallel of <see cref="MoviesController"/> rather than a
/// media-type-generic controller: branching every movie route by media type risks changing
/// movie behaviour, and the genuinely shared logic already lives in ThemeFiles and
/// DownloadService. Unlike the movie routes' legacy shape, everything here is namespaced
/// under /api/shows — except posters, which must sit under the public /api/poster prefix.
/// </summary>
[ApiController]
[Route("api/shows")]
public class ShowsController(
    Database db, YoutubeService youtube, DownloadService download, PosterUrlSigner posterSigner,
    ILogger<ShowsController> log) : ControllerBase
{
    [HttpGet]
    public IActionResult ListShows()
    {
        var shows = db.GetAllShows();
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        foreach (var show in shows)
        {
            var id = show.GetValueOrDefault("id")?.ToString() ?? "";
            var hasPoster = !string.IsNullOrEmpty(show.GetValueOrDefault("sourceRef")?.ToString());

            show["posterUrl"] = (!string.IsNullOrEmpty(id) && hasPoster)
                ? posterSigner.ShowPosterPath(id, posterExpiry)
                : null;
        }
        return Ok(shows);
    }

    [HttpGet("{showId}/search")]
    public async Task<IActionResult> SearchYoutube(string showId, [FromQuery] string? q = null)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var title = show["title"]?.ToString() ?? "";
        // Year-free by default: a show spans years, so a year biases the search toward one
        // season's upload. Same query the auto-download worker uses.
        var query = !string.IsNullOrWhiteSpace(q) ? q : ShowAutoDownloadService.BuildQuery(title);

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, title: title);
            return Ok(new { show, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Controllers/ShowsController.cs tests/Themearr.API.Tests/ShowsControllerTests.cs
git commit -m "feat: ShowsController list + search (year-free show query)"
```

---

### Task 6: `ShowsController` — download, download-url, status

**Files:**
- Modify: `src/Themearr.API/Controllers/ShowsController.cs`
- Test: `tests/Themearr.API.Tests/ShowsDownloadEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `DownloadService.Start(id, url, "show")`, `GetStatus(id, "show")`, `DownloadBlockedReason`, `IsProviderUrl` (1b).
- Produces: `POST /api/shows/{showId}/download`, `POST /api/shows/{showId}/download-url`, `GET /api/shows/{showId}/download/status`.
- Produces: `record ShowDownloadRequest(string VideoId)`, `record ShowDownloadUrlRequest(string Url)`.

- [ ] **Step 1: Write the failing test** (`ShowsDownloadEndpointTests.cs`)

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;

namespace Themearr.API.Tests;

public class ShowsDownloadEndpointTests
{
    [Fact]
    public void Download_404s_for_an_unknown_show()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);

        var result = ShowsControllerTests.New(db).Download("nope", new ShowDownloadRequest("vid123"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void DownloadUrl_rejects_a_private_address()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        var result = ShowsControllerTests.New(db)
            .DownloadUrl(id, new ShowDownloadUrlRequest("http://169.254.169.254/latest/meta-data"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadUrl_rejects_a_non_http_scheme()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        var result = ShowsControllerTests.New(db)
            .DownloadUrl(id, new ShowDownloadUrlRequest("file:///etc/passwd"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// A Plex-themed show is informational, not blocked — the UI gates it behind an
    /// explicit "download anyway", but the API must accept it.
    /// </summary>
    [Fact]
    public void Download_is_accepted_for_a_plexTheme_show()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "PlexThemed", plexHasTheme: true);

        var result = ShowsControllerTests.New(db).Download(id, new ShowDownloadRequest("vid123"));

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public void Status_reports_not_started_for_an_untouched_show()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        var result = Assert.IsType<OkObjectResult>(ShowsControllerTests.New(db).DownloadStatus(id));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.False(body.GetProperty("inProgress").GetBoolean());
        Assert.False(body.GetProperty("finished").GetBoolean());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowsDownloadEndpointTests"`
Expected: FAIL to compile — `Download` / `DownloadUrl` / `DownloadStatus` and the request records do not exist.

- [ ] **Step 3: Add the three endpoints to `ShowsController`**

```csharp
    [HttpPost("{showId}/download")]
    [Consumes("application/json")]
    public IActionResult Download(string showId, [FromBody] ShowDownloadRequest req)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });

        if (download.DownloadBlockedReason(isProviderUrl: true) is { } notReady)
        {
            log.LogWarning("Show download for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        download.Start(showId, $"https://www.youtube.com/watch?v={req.VideoId}", "show");
        return Accepted(new { started = true, showId });
    }

    [HttpPost("{showId}/download-url")]
    [Consumes("application/json")]
    public IActionResult DownloadUrl(string showId, [FromBody] ShowDownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) || !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return BadRequest(new { detail = "Invalid URL" });

        if (uri.Scheme is not ("http" or "https"))
            return BadRequest(new { detail = "Only http and https URLs are supported." });

        if (HostGuard.IsPrivateOrLoopback(uri.Host))
            return BadRequest(new { detail = "Refusing to download from a private or loopback address." });

        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });

        // A pasted YouTube URL still goes through the provider, so pre-flight it
        // (config + quota cooldown). Direct URLs are not gated.
        if (download.DownloadBlockedReason(DownloadService.IsProviderUrl(req.Url)) is { } notReady)
        {
            log.LogWarning("Show download-url for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        download.Start(showId, req.Url, "show");
        return Accepted(new { started = true, showId });
    }

    [HttpGet("{showId}/download/status")]
    public IActionResult DownloadStatus(string showId) => Ok(download.GetStatus(showId, "show"));
```

At the bottom of the file, next to the class:

```csharp
public record ShowDownloadRequest(string VideoId);
public record ShowDownloadUrlRequest(string Url);
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Controllers/ShowsController.cs tests/Themearr.API.Tests/ShowsDownloadEndpointTests.cs
git commit -m "feat: ShowsController download, download-url and status endpoints"
```

---

### Task 7: `ShowsController` — ignore, unignore, delete theme, theme audio

**Files:**
- Modify: `src/Themearr.API/Controllers/ShowsController.cs`
- Test: `tests/Themearr.API.Tests/ShowsThemeEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `Database.SetShowIgnored`/`SetShowStatus`/`GetLibraryPaths`, `ThemeFiles.IsWithinRoots`/`DeleteThemes`/`FindThemeFile`/`ContentTypeFor` (Task 1).
- Produces: `POST /api/shows/{showId}/ignore`, `POST /api/shows/{showId}/unignore`, `DELETE /api/shows/{showId}/theme`, `GET /api/shows/{showId}/theme/audio`.

- [ ] **Step 1: Write the failing test** (`ShowsThemeEndpointTests.cs`)

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class ShowsThemeEndpointTests
{
    [Fact]
    public void Ignore_then_unignore_round_trips()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var controller = ShowsControllerTests.New(db);

        Assert.IsType<OkObjectResult>(controller.IgnoreShow(id));
        Assert.Equal("ignored", db.GetShow(id)!["status"]);

        Assert.IsType<OkObjectResult>(controller.UnignoreShow(id));
        Assert.Equal("pending", db.GetShow(id)!["status"]);
    }

    /// <summary>
    /// Mirrors the movie contract: an in-app delete must reset stored status to 'pending'
    /// so the auto-download worker's stored-status pre-filter re-adopts the show.
    /// </summary>
    [Fact]
    public void DeleteTheme_removes_the_file_and_resets_stored_status()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var folder = db.GetShow(id)!["folderName"]!.ToString()!;
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        db.SetShowStatus(id, "downloaded");

        var result = Assert.IsType<OkObjectResult>(ShowsControllerTests.New(db).DeleteTheme(id));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.True(body.GetProperty("deleted").GetBoolean());
        Assert.False(File.Exists(Path.Combine(folder, "theme.mp3")));
        Assert.Single(db.GetPendingShows());   // stored column reset, not just the disk
    }

    [Fact]
    public void DeleteTheme_refuses_a_folder_outside_the_configured_roots()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var folder = db.GetShow(id)!["folderName"]!.ToString()!;
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        // A root that does not contain the show folder.
        db.SetLibraryPaths([Path.Combine(dir.Path, "elsewhere")]);

        var result = ShowsControllerTests.New(db).DeleteTheme(id);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(File.Exists(Path.Combine(folder, "theme.mp3")));   // untouched
    }

    [Fact]
    public void ThemeAudio_404s_when_there_is_no_theme()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        Assert.IsType<NotFoundObjectResult>(ShowsControllerTests.New(db).GetThemeAudio(id));
    }

    [Fact]
    public void ThemeAudio_serves_the_file_with_the_right_content_type()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var folder = db.GetShow(id)!["folderName"]!.ToString()!;
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);

        var result = Assert.IsType<PhysicalFileResult>(ShowsControllerTests.New(db).GetThemeAudio(id));

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.True(result.EnableRangeProcessing);
    }
}
```

(`Database.SetLibraryPaths(List<string>)` and `GetLibraryPaths()` already exist — `Database.cs:444` and `:456`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowsThemeEndpointTests"`
Expected: FAIL to compile — the four methods do not exist.

- [ ] **Step 3: Add the four endpoints to `ShowsController`**

Add `using Microsoft.Net.Http.Headers;` at the top of the file, then:

```csharp
    [HttpPost("{showId}/ignore")]
    public IActionResult IgnoreShow(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        db.SetShowIgnored(showId, true);
        return Ok(new { ignored = true });
    }

    [HttpPost("{showId}/unignore")]
    public IActionResult UnignoreShow(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        db.SetShowIgnored(showId, false);
        return Ok(new { ignored = false });
    }

    [HttpDelete("{showId}/theme")]
    public IActionResult DeleteTheme(string showId)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var folder = show["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return BadRequest(new { detail = "Show has no folder" });

        // Confine deletes to the configured library roots (see DownloadService).
        var roots = db.GetLibraryPaths();
        if (roots.Count > 0 && !ThemeFiles.IsWithinRoots(folder, roots))
            return BadRequest(new { detail = "Refusing to delete outside the configured library roots." });

        var deleted = ThemeFiles.DeleteThemes(folder);

        // Reset the stored status so the auto-download worker's stored-status pre-filter
        // re-adopts this show — same contract as the movie endpoint.
        if (deleted) db.SetShowStatus(showId, "pending");

        return Ok(new { deleted });
    }

    [HttpGet("{showId}/theme/audio")]
    public IActionResult GetThemeAudio(string showId)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var folder = show["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return NotFound(new { detail = "No folder" });

        var themeFile = ThemeFiles.FindThemeFile(folder);
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        // ETag + Last-Modified so repeated visits don't re-download the same theme file.
        var info = new FileInfo(themeFile);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, ThemeFiles.ContentTypeFor(themeFile),
            info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Controllers/ShowsController.cs tests/Themearr.API.Tests/ShowsThemeEndpointTests.cs
git commit -m "feat: ShowsController ignore, delete-theme and theme-audio endpoints"
```

---

### Task 8: Pin the auth boundary

The poster exemption is a path prefix in `Program.cs`. Nothing currently tests it, and a future edit that widened it to `/api/shows` would silently unauthenticate the whole shows API. Extract the predicate so it can be tested without adding a `TestHost` dependency.

**Files:**
- Modify: `src/Themearr.API/Services/ApiAuthMiddleware.cs` (add `RequiresAuth`)
- Modify: `src/Themearr.API/Program.cs` (call it)
- Test: `tests/Themearr.API.Tests/AuthBoundaryTests.cs` (create)

**Interfaces:**
- Produces: `static bool ApiAuthMiddleware.RequiresAuth(PathString path)`.

- [ ] **Step 1: Write the failing test** (`AuthBoundaryTests.cs`)

```csharp
using Microsoft.AspNetCore.Http;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// The public surface is exactly /api/auth and /api/poster. Everything else under /api
/// must require a credential. This is a prefix match, so it is one careless edit away from
/// exposing a whole namespace — hence the explicit table.
/// </summary>
public class AuthBoundaryTests
{
    [Theory]
    [InlineData("/api/shows")]
    [InlineData("/api/shows/abc123/download")]
    [InlineData("/api/shows/abc123/theme/audio")]
    [InlineData("/api/stats/shows")]
    [InlineData("/api/movies")]
    [InlineData("/api/settings")]
    public void Protected_routes_require_auth(string path) =>
        Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString(path)));

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/poster")]
    [InlineData("/api/poster/show")]
    public void Public_routes_do_not(string path) =>
        Assert.False(ApiAuthMiddleware.RequiresAuth(new PathString(path)));

    [Fact]
    public void Non_api_paths_are_not_guarded_here()
    {
        Assert.False(ApiAuthMiddleware.RequiresAuth(new PathString("/index.html")));
        Assert.False(ApiAuthMiddleware.RequiresAuth(new PathString("/")));
    }

    /// <summary>A segment-boundary check: /api/posterX must NOT inherit the exemption.</summary>
    [Fact]
    public void A_route_that_merely_starts_with_poster_is_still_protected() =>
        Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString("/api/posterize")));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~AuthBoundaryTests"`
Expected: FAIL to compile — `RequiresAuth` does not exist.

- [ ] **Step 3: Extract the predicate**

Add to `ApiAuthMiddleware`:

```csharp
    /// <summary>
    /// Which paths this middleware guards. Everything under /api except the two public
    /// prefixes: /api/auth (you have no credential yet) and /api/poster (an &lt;img&gt; tag
    /// cannot send an Authorization header, so poster URLs self-authenticate via a signed,
    /// expiring query string instead).
    ///
    /// Extracted from Program.cs so the boundary is testable — a widened prefix here would
    /// silently expose a whole namespace. StartsWithSegments matches on segment boundaries,
    /// so "/api/posterize" is NOT covered by the "/api/poster" exemption.
    /// </summary>
    public static bool RequiresAuth(PathString path) =>
        path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/api/auth")
        && !path.StartsWithSegments("/api/poster");
```

- [ ] **Step 4: Point `Program.cs` at it**

Replace the `UseWhen` block (currently lines ~150-157):

```csharp
// Bearer-token auth for every /api/* route except the public prefixes — see
// ApiAuthMiddleware.RequiresAuth, which is unit-tested in AuthBoundaryTests.
app.UseWhen(
    ctx => Themearr.API.Services.ApiAuthMiddleware.RequiresAuth(ctx.Request.Path),
    branch => branch.UseMiddleware<Themearr.API.Services.ApiAuthMiddleware>());
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 6: Verify the boundary against a running server**

Unit tests pin the predicate; this confirms the wiring. Start the API and check three paths:

```bash
SCRATCH=$(mktemp -d)
THEMEARR_AUTH_TOKEN=plan-check-0123456789abcdef DB_PATH=$SCRATCH/t.db \
  ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project src/Themearr.API --no-launch-profile &
sleep 22
curl -s -o /dev/null -w "shows(no auth)  = %{http_code}\n" http://127.0.0.1:5199/api/shows
curl -s -o /dev/null -w "shows(bearer)   = %{http_code}\n" -H "Authorization: Bearer plan-check-0123456789abcdef" http://127.0.0.1:5199/api/shows
curl -s -o /dev/null -w "poster/show     = %{http_code}\n" "http://127.0.0.1:5199/api/poster/show?id=x&exp=1&sig=y"
pkill -f "Themearr.API"; rm -rf $SCRATCH
```

Expected: `shows(no auth) = 401`, `shows(bearer) = 200`, `poster/show = 401` (reached the controller and failed *signature* verification, not middleware auth — the distinction that proves the exemption applies).

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Services/ApiAuthMiddleware.cs src/Themearr.API/Program.cs tests/Themearr.API.Tests/AuthBoundaryTests.cs
git commit -m "test: pin the /api auth boundary via an extracted RequiresAuth predicate"
```

---

## Final verification

- [ ] `dotnet test tests/Themearr.API.Tests` — whole suite green, no warnings.
- [ ] `dotnet build src/Themearr.API` — 0 warnings, 0 errors.
- [ ] Boot the API (see Task 8 Step 6) and confirm `GET /api/shows` returns `200` with a bearer token and `GET /api/stats/shows` returns the counts.
- [ ] Confirm no auth exemption was added for `/api/shows` (`grep -n "api/shows" src/Themearr.API/Program.cs` → no matches).
- [ ] Manual (maintainer's box, live Plex, once 1d exposes it): select a show library, sync, confirm a Plex-Pass-themed show reports `plexTheme` and a themeless one reports `pending`; download a theme for the themeless one and watch it flip to `downloaded`.

## Self-review notes

- **Spec coverage:** shared helper extraction (Task 1); 4-state status + `plexHasTheme` (Task 2); `GetShowStats` + `/api/stats/shows` (Task 3); posters at `/api/poster/show` with scoped signature and Plex-only source (Task 4); list + search (Task 5); download/download-url/status (Task 6); ignore/unignore/delete/theme-audio (Task 7); auth-boundary regression guard (Task 8). Every endpoint in the spec's table has a task.
- **Type consistency:** `ShowsController.New(db)` test factory, `ShowDownloadRequest`/`ShowDownloadUrlRequest`, `ShowStatsResult`, `ShowPosterPath`/`VerifyShow`, `FindThemeFile`/`ContentTypeFor`/`DeleteThemes`, `RequiresAuth` are used identically across tasks. `ShowsControllerTests.NewDb`/`AddShow`/`New` are declared `internal static` in Task 5 precisely because Tasks 6 and 7 reuse them.
- **Movie behavior preserved:** `ReadMediaRow` untouched; movie routes untouched; Task 1 refactors movie internals to call the extracted helpers with identical behaviour, gated by the existing movie tests.
- **Known deferral:** `ShowAutoDownloadService.GetDiagnostics()` remains unreferenced after this slice (the debug endpoint was explicitly deferred). Expose or delete it in 1d.
- **Not in this plan:** webhook show-sync trigger; show auto-download debug endpoint; all UI (1d); Sonarr and ThemerrDB (Phase 2).
