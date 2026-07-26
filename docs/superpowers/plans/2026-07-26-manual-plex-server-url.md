# Manual Plex Server URL — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator set and test a manual Plex server URL in Settings, so a Plex server on a bridged/unreachable-advertised network can be pointed at directly.

**Architecture:** The manual URL overwrites `url` (and collapses `urls` to `[url]`) inside the existing `plex_selected_servers` record, so health/sync/poster read paths are untouched. Two new **bearer-only** endpoints in `SettingsController` — a **test** (probe a supplied URL with the stored token) and a **save** (persist + re-bind the token) — mirror the existing Radarr test/save pair. The Settings "Plex Connection" panel becomes editable.

**Tech Stack:** .NET 10 Web API (xUnit), React 19 + Vite (Vitest + Testing Library).

## Global Constraints

- **.NET 10** backend; **React 19 + Vite** frontend. Backend tests xUnit, frontend Vitest.
- **Both new endpoints are bearer-only** — gate on `HttpContext.AuthenticatedWithBearerToken()`, return `403` for the API-key scheme. Each sends/binds the Plex token to an operator-supplied host, so the externally-held API key must not reach them.
- **Never leak the Plex token** in a response body or a request URI (token goes in the `X-Plex-Token` header only).
- **Save must NOT call `Database.SetPlexServersMergingTokens`** — its URL-match guard drops the token on a URL change (correct for the unauthenticated `/health` path). Save re-binds directly via `GetPlexServers` → mutate → `SetPlexServers`.
- **Private/loopback hosts are allowed** for the Plex URL — Plex servers are private, exactly like the discovered URLs. Do NOT apply `HostGuard` here.
- Commit prefixes drive semver: use `feat:` for user-facing capability, `test:`/`refactor:` where apt.

---

### Task 1: `PlexLibrarySource.ProbeAsync` — probe a supplied URL + token

**Files:**
- Modify: `src/Themearr.API/Services/Sources/PlexLibrarySource.cs` (add `ProbeAsync`; delegate `CheckAsync` to it)
- Test: `tests/Themearr.API.Tests/PlexLibrarySourceTests.cs` (add ProbeAsync cases)

**Interfaces:**
- Produces: `public Task<string?> PlexLibrarySource.ProbeAsync(string url, string token, CancellationToken ct)` — returns `null` when reachable, else a user-facing message. Reuses the `plex-health` named client.

- [ ] **Step 1: Write the failing test** (append to `PlexLibrarySourceTests.cs`)

```csharp
[Fact]
public async Task ProbeAsync_returns_null_for_a_reachable_supplied_url()
{
    using var dir = new TempDir();
    var db = NewDb(dir, withServer: true);
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

    var reason = await NewSource(db, handler)
        .ProbeAsync("http://192.168.1.50:32400", Token, CancellationToken.None);

    Assert.Null(reason);
}

[Fact]
public async Task ProbeAsync_reports_the_rejected_token_on_401_and_never_leaks_it()
{
    using var dir = new TempDir();
    var db = NewDb(dir, withServer: true);
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

    var reason = await NewSource(db, handler)
        .ProbeAsync("http://192.168.1.50:32400", Token, CancellationToken.None);

    Assert.NotNull(reason);
    Assert.Contains("token", reason, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(Token, reason);
    Assert.DoesNotContain(Token, handler.LastRequest?.RequestUri?.ToString() ?? "");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ProbeAsync"`
Expected: FAIL to compile — `PlexLibrarySource` has no `ProbeAsync`.

- [ ] **Step 3: Add `ProbeAsync` and delegate `CheckAsync` to it**

In `PlexLibrarySource.cs`, replace the body of `CheckAsync` and add `ProbeAsync`:

```csharp
public async Task<string?> CheckAsync(CancellationToken ct)
{
    var servers = db.GetPlexServersDict();
    if (servers.Count == 0) return null;   // nothing configured is not a fault

    var (url, token) = servers.First().Value;
    return await ProbeAsync(url, token, ct);
}

/// <summary>
/// Probes an arbitrary Plex <paramref name="url"/> with <paramref name="token"/> without
/// touching stored settings — used by CheckAsync (stored config) and the Settings Plex
/// "Test" endpoint (the URL the operator just typed). The token travels in the
/// X-Plex-Token header only, never the URI, and never appears in a returned message.
/// </summary>
public async Task<string?> ProbeAsync(string url, string token, CancellationToken ct)
{
    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;

    var http = factory.CreateClient(ClientName);
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/identity");
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return "Plex rejected the stored token (401). Sign in to Plex again in Settings.";
        if (!response.IsSuccessStatusCode)
            return $"The Plex server returned HTTP {(int)response.StatusCode}.";
        return null;
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return $"The Plex server did not respond within {http.Timeout.TotalSeconds:0} seconds.";
    }
    catch (HttpRequestException)
    {
        return "The Plex server is unreachable. Check it is running and the URL in Settings is correct.";
    }
}
```

- [ ] **Step 4: Run tests to verify pass (incl. existing CheckAsync regression)**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexLibrarySourceTests"`
Expected: PASS — new ProbeAsync tests green AND all pre-existing CheckAsync tests still green (they now exercise the delegated path).

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/Sources/PlexLibrarySource.cs tests/Themearr.API.Tests/PlexLibrarySourceTests.cs
git commit -m "refactor: extract PlexLibrarySource.ProbeAsync for a supplied url+token"
```

---

### Task 2: `Database.UpdatePlexServerUrl` — rebind the token to a new URL

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (add method near `SetPlexServersMergingTokens`)
- Test: `tests/Themearr.API.Tests/PlexServerUrlStoreTests.cs` (create)

**Interfaces:**
- Produces: `public bool Database.UpdatePlexServerUrl(string serverId, string url)` — sets the matching server's `url` and `urls=[url]`, keeps its token; returns `false` if no server matched.

- [ ] **Step 1: Write the failing test** (`PlexServerUrlStoreTests.cs`)

```csharp
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class PlexServerUrlStoreTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower",
            ["url"] = "https://old.plex.direct:32400",
            ["urls"] = new List<string> { "https://old.plex.direct:32400" },
            ["token"] = "tok-123",
        }]);
        return db;
    }

    [Fact]
    public void UpdatePlexServerUrl_sets_the_url_and_collapses_urls_and_keeps_the_token()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        var ok = db.UpdatePlexServerUrl("srv1", "http://192.168.1.50:32400");

        Assert.True(ok);
        var srv = db.GetPlexServersDict()["srv1"];
        Assert.Equal("http://192.168.1.50:32400", srv.Url);
        Assert.Equal("tok-123", srv.Token);   // token stayed bound to the new url
    }

    [Fact]
    public void UpdatePlexServerUrl_returns_false_for_an_unknown_server()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        Assert.False(db.UpdatePlexServerUrl("nope", "http://192.168.1.50:32400"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexServerUrlStoreTests"`
Expected: FAIL to compile — `Database` has no `UpdatePlexServerUrl`.

- [ ] **Step 3: Implement the method** (in `Database.cs`, after `SetPlexServersMergingTokens`)

```csharp
/// <summary>
/// Points a stored Plex server at <paramref name="url"/> from an authenticated operator
/// action, keeping the existing token bound to the new address. Deliberately NOT
/// SetPlexServersMergingTokens: that path drops the token on a url change to stay safe for
/// the unauthenticated /health endpoint; this one is only reachable bearer-only, so it
/// rebinds directly. Returns false when no server has this id.
/// </summary>
public bool UpdatePlexServerUrl(string serverId, string url)
{
    var servers = GetPlexServers();
    var matched = false;
    foreach (var srv in servers)
    {
        if ((srv.GetValueOrDefault("id")?.ToString() ?? "") != serverId) continue;
        srv["url"]  = url;
        srv["urls"] = new List<string> { url };
        matched = true;
    }
    if (matched) SetPlexServers(servers);
    return matched;
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexServerUrlStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/PlexServerUrlStoreTests.cs
git commit -m "feat: Database.UpdatePlexServerUrl rebinds the token to a new address"
```

---

### Task 3: `SettingsController` plex/test + plex/server endpoints (bearer-only)

**Files:**
- Modify: `src/Themearr.API/Controllers/SettingsController.cs` (add `PlexLibrarySource` ctor dep; two endpoints; `PlexUrlPayload`; `NormalizePlexUrl`; `PlexUrlForbidden`)
- Modify: `tests/Themearr.API.Tests/TestControllers.cs` (build the controller with a `PlexLibrarySource`)
- Test: `tests/Themearr.API.Tests/PlexServerUrlEndpointTests.cs` (create)

**Interfaces:**
- Consumes: `PlexLibrarySource.ProbeAsync(url, token, ct)` (Task 1); `Database.UpdatePlexServerUrl(serverId, url)` (Task 2).
- Produces: `POST /api/settings/plex/test` → `{ ok, detail }`; `POST /api/settings/plex/server` → `{ selectedServers }` (redacted). Both bearer-only. Request body `{ serverId, url }`.

- [ ] **Step 1: Add the ctor dep + `TestControllers` wiring so the suite compiles**

In `SettingsController.cs`, change the constructor to add `PlexLibrarySource plex`:

```csharp
public class SettingsController(Database db, RadarrLibrarySource radarr, PlexLibrarySource plex, IApiKeyStore keys) : ControllerBase
```

In `TestControllers.cs`, replace `NewSettingsController` with a default + probe-injecting overload:

```csharp
public static SettingsController NewSettingsController(Database db, IApiKeyStore keys) =>
    NewSettingsController(db, keys, new UnusedHttpClientFactory());

// plexFactory supplies the HttpClient PlexLibrarySource.ProbeAsync uses — pass a stub
// returning canned responses for the /plex/test path; the default throws (probe unused).
public static SettingsController NewSettingsController(Database db, IApiKeyStore keys, IHttpClientFactory plexFactory) =>
    new(db,
        new RadarrLibrarySource(db, new LocalFolderResolver(db), new UnusedHttpClientFactory()),
        new PlexLibrarySource(new PlexService(new HttpClient(), db, new LocalFolderResolver(db)), db, plexFactory),
        keys);
```

- [ ] **Step 2: Write the failing tests** (`PlexServerUrlEndpointTests.cs`)

```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexServerUrlEndpointTests
{
    private const string Token = "tok-123";

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower",
            ["url"] = "https://old.plex.direct:32400",
            ["urls"] = new List<string> { "https://old.plex.direct:32400" },
            ["token"] = Token,
        }]);
        return db;
    }

    private static SettingsController Controller(Database db, string scheme, HttpMessageHandler? plex = null)
    {
        var factory = plex is null ? (IHttpClientFactory?)null : new StubFactory(plex);
        var controller = factory is null
            ? TestControllers.NewSettingsController(db, new ApiKeyStore(db))
            : TestControllers.NewSettingsController(db, new ApiKeyStore(db), factory);
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiAuthMiddleware.AuthSchemeItemKey] = scheme;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    // ── save ───────────────────────────────────────────────────────────────
    [Fact]
    public void SavePlexUrl_updates_the_url_and_keeps_the_token()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var controller = Controller(db, ApiAuthMiddleware.BearerScheme);

        var result = controller.SavePlexUrl(new SettingsController.PlexUrlPayload("srv1", "192.168.1.50:32400"));

        Assert.IsType<OkObjectResult>(result);
        var srv = db.GetPlexServersDict()["srv1"];
        Assert.Equal("http://192.168.1.50:32400", srv.Url);   // scheme defaulted to http
        Assert.Equal(Token, srv.Token);
    }

    [Fact]
    public void SavePlexUrl_returns_404_for_an_unknown_server()
    {
        using var dir = new TempDir();
        var result = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme)
            .SavePlexUrl(new SettingsController.PlexUrlPayload("nope", "http://192.168.1.50:32400"));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<NotFoundObjectResult>(result).StatusCode);
    }

    [Fact]
    public void SavePlexUrl_returns_400_for_a_blank_url()
    {
        using var dir = new TempDir();
        var result = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme)
            .SavePlexUrl(new SettingsController.PlexUrlPayload("srv1", "   "));
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
    }

    [Fact]
    public void SavePlexUrl_is_refused_403_under_the_api_key_and_does_not_change_the_url()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var result = Controller(db, ApiAuthMiddleware.ApiKeyScheme)
            .SavePlexUrl(new SettingsController.PlexUrlPayload("srv1", "http://attacker.example:32400"));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("https://old.plex.direct:32400", db.GetPlexServersDict()["srv1"].Url);
    }

    // ── test ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task TestPlex_returns_ok_when_the_supplied_url_is_reachable()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme, handler);

        var result = Assert.IsType<OkObjectResult>(
            await controller.TestPlex(new SettingsController.PlexUrlPayload("srv1", "http://192.168.1.50:32400"), CancellationToken.None));

        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TestPlex_reports_not_ok_on_401_without_leaking_the_token()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var controller = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme, handler);

        var result = Assert.IsType<OkObjectResult>(
            await controller.TestPlex(new SettingsController.PlexUrlPayload("srv1", "http://192.168.1.50:32400"), CancellationToken.None));

        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"ok\":false", body);
        Assert.DoesNotContain(Token, body);
    }

    [Fact]
    public async Task TestPlex_is_refused_403_under_the_api_key()
    {
        using var dir = new TempDir();
        var result = await Controller(NewDb(dir), ApiAuthMiddleware.ApiKeyScheme)
            .TestPlex(new SettingsController.PlexUrlPayload("srv1", "http://attacker.example:32400"), CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexServerUrlEndpointTests"`
Expected: FAIL to compile — `TestPlex` / `SavePlexUrl` / `PlexUrlPayload` don't exist yet.

- [ ] **Step 4: Implement the endpoints** (in `SettingsController.cs`, after `TestRadarr` / the `RadarrPayload` record)

```csharp
// ── Plex server URL (manual override) ──────────────────────────────────────
// Both endpoints are bearer-only: each sends or binds the stored Plex token to an
// operator-supplied host, so the externally-held API key must not reach them (same
// gate as apikey management above).
private IActionResult PlexUrlForbidden() => StatusCode(StatusCodes.Status403Forbidden,
    new { detail = "Changing the Plex server URL requires the access token, not the API key." });

[HttpPost("plex/test")]
[Consumes("application/json")]
public async Task<IActionResult> TestPlex([FromBody] PlexUrlPayload payload, CancellationToken ct)
{
    if (!AuthenticatedWithBearerToken) return PlexUrlForbidden();

    var url = NormalizePlexUrl(payload.Url);
    if (url is null)
        return BadRequest(new { detail = "Enter a valid server address, e.g. http://192.168.1.50:32400." });

    // Probe with the STORED token for that server — never a token from the request body.
    if (!db.GetPlexServersDict().TryGetValue(payload.ServerId ?? "", out var srv))
        return NotFound(new { detail = "That Plex server is not connected." });

    var reason = await plex.ProbeAsync(url, srv.Token, ct);
    return Ok(new { ok = reason is null, detail = reason ?? "Plex is reachable." });
}

[HttpPost("plex/server")]
[Consumes("application/json")]
public IActionResult SavePlexUrl([FromBody] PlexUrlPayload payload)
{
    if (!AuthenticatedWithBearerToken) return PlexUrlForbidden();

    var url = NormalizePlexUrl(payload.Url);
    if (url is null)
        return BadRequest(new { detail = "Enter a valid server address, e.g. http://192.168.1.50:32400." });

    if (!db.UpdatePlexServerUrl(payload.ServerId ?? "", url))
        return NotFound(new { detail = "That Plex server is not connected." });

    return Ok(new { selectedServers = db.GetPlexServersRedacted() });
}

public record PlexUrlPayload(string? ServerId, string? Url);

// Normalizes a user-entered Plex address: trims, defaults to http:// when no scheme is
// given (Plex local is http on :32400), requires an http(s) URL with a host, and strips a
// trailing slash. Returns null when the input can't be a valid server address. Private and
// loopback hosts are allowed on purpose — Plex servers are private, like the discovered URLs.
private static string? NormalizePlexUrl(string? raw)
{
    var text = (raw ?? "").Trim();
    if (text.Length == 0) return null;
    if (!text.Contains("://")) text = "http://" + text;
    if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
    if (uri.Scheme is not ("http" or "https")) return null;
    if (string.IsNullOrEmpty(uri.Host)) return null;
    return text.TrimEnd('/');
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexServerUrlEndpointTests"`
Expected: PASS (all 7).

- [ ] **Step 6: Run the FULL API suite (regression — controller ctor changed)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Controllers/SettingsController.cs tests/Themearr.API.Tests/TestControllers.cs tests/Themearr.API.Tests/PlexServerUrlEndpointTests.cs
git commit -m "feat: bearer-only Plex server URL test + save endpoints"
```

---

### Task 4: Frontend — plexApi client + editable Plex Connection panel

**Files:**
- Modify: `src/Themearr.Web/src/lib/api.ts` (add `plexApi`)
- Modify: `src/Themearr.Web/src/app/settings/page.tsx` (editable Plex Connection panel + state/handlers)
- Test: `src/Themearr.Web/src/app/settings-plex-url.test.tsx` (create)

**Interfaces:**
- Consumes: `POST /api/settings/plex/test` and `POST /api/settings/plex/server` (Task 3).
- Produces: `plexApi.test(serverId, url)` → `{ ok, detail }`; `plexApi.saveUrl(serverId, url)` → `{ selectedServers: PlexServer[] }`.

- [ ] **Step 1: Write the failing test** (`settings-plex-url.test.tsx`)

```tsx
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import SettingsPage from './settings/page'
import * as api from '@/lib/api'

// Mirror the mocking used by the neighbouring settings tests: stub the api module so the
// panel's calls are observable and the page renders without a real backend.
vi.mock('@/lib/api', async (orig) => {
  const actual = await orig<typeof import('@/lib/api')>()
  return {
    ...actual,
    settingsApi: { get: vi.fn().mockResolvedValue({
      selectedServers: [{ id: 'srv1', name: 'Tower', url: 'https://old.plex.direct:32400' }],
      selectedLibraries: {}, pathMappings: [], libraryPaths: [],
      advanced: { maxSearchDirs: 20000, searchDepth: 4 },
      autoDownload: false, autoSync: false, lastAutoSyncAt: '',
    }), save: vi.fn() },
    plexApi: {
      test: vi.fn().mockResolvedValue({ ok: false, detail: 'The Plex server is unreachable.' }),
      saveUrl: vi.fn().mockResolvedValue({ selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://192.168.1.50:32400' }] }),
    },
  }
})

describe('Plex Connection manual URL', () => {
  beforeEach(() => vi.clearAllMocks())

  it('tests and saves an edited Plex server URL, surfacing the test result', async () => {
    render(<MemoryRouter><SettingsPage /></MemoryRouter>)

    const input = await screen.findByDisplayValue('https://old.plex.direct:32400')
    await userEvent.clear(input)
    await userEvent.type(input, 'http://192.168.1.50:32400')

    await userEvent.click(screen.getByRole('button', { name: /test/i }))
    await waitFor(() => expect(api.plexApi.test).toHaveBeenCalledWith('srv1', 'http://192.168.1.50:32400'))
    expect(await screen.findByText(/unreachable/i)).toBeInTheDocument()   // failure surfaced, not swallowed

    await userEvent.click(screen.getByRole('button', { name: /^save/i }))
    await waitFor(() => expect(api.plexApi.saveUrl).toHaveBeenCalledWith('srv1', 'http://192.168.1.50:32400'))
  })
})
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd src/Themearr.Web && npx vitest run settings-plex-url`
Expected: FAIL — `plexApi` is undefined / the panel has no editable input.

- [ ] **Step 3a: Add `plexApi`** (in `src/lib/api.ts`, after `radarrApi`)

```ts
// ── Plex server (manual URL override) ─────────────────────────────────────────
export const plexApi = {
  test: (serverId: string, url: string) =>
    request<{ ok: boolean; detail: string }>('/api/settings/plex/test', {
      method: 'POST',
      body: JSON.stringify({ serverId, url }),
    }),
  saveUrl: (serverId: string, url: string) =>
    request<{ selectedServers: PlexServer[] }>('/api/settings/plex/server', {
      method: 'POST',
      body: JSON.stringify({ serverId, url }),
    }),
}
```

Ensure `PlexServer` is imported at the top of `api.ts` (it already backs `Settings`); add it to the existing type import if missing.

- [ ] **Step 3b: Make the Plex Connection panel editable** (in `settings/page.tsx`)

Add state near the other Settings state:

```tsx
const [plexUrls,   setPlexUrls]   = useState<Record<string, string>>({})
const [plexTest,   setPlexTest]   = useState<{ ok: boolean; detail: string } | null>(null)
const [plexTesting, setPlexTesting] = useState(false)
const [plexSaving, setPlexSaving] = useState(false)
const [plexSaved,  setPlexSaved]  = useState(false)
const [plexError,  setPlexError]  = useState('')
```

Seed `plexUrls` from loaded settings (where `settings` is first populated), e.g.:

```tsx
setPlexUrls(Object.fromEntries(data.selectedServers.map(s => [s.id, s.url])))
```

Add handlers:

```tsx
async function testPlexUrl(serverId: string) {
  setPlexTesting(true); setPlexTest(null); setPlexError('')
  try { setPlexTest(await plexApi.test(serverId, plexUrls[serverId] ?? '')) }
  catch (e) { setPlexError((e as Error).message) }   // surface, never swallow
  finally { setPlexTesting(false) }
}

async function savePlexUrl(serverId: string) {
  setPlexSaving(true); setPlexSaved(false); setPlexError('')
  try {
    await plexApi.saveUrl(serverId, plexUrls[serverId] ?? '')
    setPlexSaved(true); setTimeout(() => setPlexSaved(false), 2000)
  } catch (e) { setPlexError((e as Error).message) }
  finally { setPlexSaving(false) }
}
```

Replace the read-only "Plex Connection" panel body (the `settings.selectedServers.map(...)` block) with an editable one, mirroring the Radarr connect UI (`Input` + result box + Test/Save `Button`s):

```tsx
{settings.selectedServers.map(srv => (
  <div key={srv.id} className="space-y-3 rounded-lg border border-[#1D2939] px-4 py-3">
    <p className="text-sm font-medium text-[#F9FAFB]">{srv.name}</p>
    <Input
      label="Server URL"
      placeholder="http://192.168.1.50:32400"
      value={plexUrls[srv.id] ?? srv.url}
      onChange={e => { setPlexUrls(p => ({ ...p, [srv.id]: e.target.value })); setPlexTest(null) }}
      className="font-mono text-xs"
    />
    {plexTest && (
      <div className={`rounded-lg border px-3.5 py-2.5 text-sm ${
        plexTest.ok
          ? 'border-[#12B76A]/30 bg-[#12B76A]/5 text-[#D0D5DD]'
          : 'border-[#B42318]/30 bg-[#FEF3F2]/5 text-[#FDA29B]'
      }`}>{plexTest.detail}</div>
    )}
    <div className="flex gap-2">
      <Button variant="secondary" size="sm" onClick={() => testPlexUrl(srv.id)} loading={plexTesting}
        disabled={!(plexUrls[srv.id] ?? srv.url).trim()}>Test connection</Button>
      <Button size="sm" onClick={() => savePlexUrl(srv.id)} loading={plexSaving}
        disabled={!(plexUrls[srv.id] ?? srv.url).trim()}>{plexSaved ? 'Saved ✓' : 'Save'}</Button>
    </div>
    {plexError && <p className="text-xs text-[#FDA29B]">{plexError}</p>}
  </div>
))}
```

Confirm `Input` is imported in `settings/page.tsx` (add to the `@/components/ui` import if absent).

- [ ] **Step 4: Run test to verify pass**

Run: `cd src/Themearr.Web && npx vitest run settings-plex-url`
Expected: PASS.

- [ ] **Step 5: Lint + typecheck + full frontend suite**

Run: `cd src/Themearr.Web && npm run lint && npx tsc --noEmit && npm test`
Expected: all clean/green.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.Web/src/lib/api.ts src/Themearr.Web/src/app/settings/page.tsx src/Themearr.Web/src/app/settings-plex-url.test.tsx
git commit -m "feat: editable + testable Plex server URL in Settings"
```

---

## Final verification

- [ ] `dotnet test` (whole API suite) — green.
- [ ] `cd src/Themearr.Web && npm run lint && npx tsc --noEmit && npm test` — clean/green.
- [ ] Manual (maintainer's box, live Plex): edit the Plex URL to a reachable LAN address, **Test** shows reachable, **Save**, then System → Health clears the "Plex server is unreachable" error.

## Self-review notes

- **Spec coverage:** test endpoint (Task 3), save+rebind (Tasks 2–3), bearer-only both endpoints (Task 3 tests 4 & 7), token-never-leaked (Tasks 1 & 3), private hosts allowed via `NormalizePlexUrl` (Task 3), editable Settings panel + honest failure surfacing (Task 4), health message now truthful (Final/manual). Rejected `plex_manual_url` alternative not implemented (spec honored).
- **Types consistent:** `PlexUrlPayload(serverId, url)`, `ProbeAsync(url, token, ct)`, `UpdatePlexServerUrl(serverId, url)`, `plexApi.test/saveUrl(serverId, url)` used identically across tasks.
- **Not in scope (per spec):** setup-wizard manual entry, URL-only connect, changing the token.
