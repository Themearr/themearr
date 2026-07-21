# API Key & Radarr Webhook Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Themearr an API key external tools can authenticate with, and a Radarr webhook that fetches a theme the moment a movie is imported.

**Architecture:** A generated key lives in the `api_key` setting. `ApiAuthMiddleware` accepts it as an `X-Api-Key` header alongside the existing bearer token, reading it **only when that header is present** so the browser's hot path never touches the database. A `WebhookController` filters Radarr's event types and, on an import, signals the existing sync task — whose trigger channel already coalesces a burst of webhooks into one sync.

**Tech Stack:** .NET 10 (ASP.NET Core), `Microsoft.Data.Sqlite`, xUnit, React 19 + Vite.

**Spec:** `docs/superpowers/specs/2026-07-21-api-key-and-radarr-webhook-design.md`

## Global Constraints

- **The API key must never appear in a log line or a URL.** It travels only in the `X-Api-Key` header.
- **The browser's hot path must not read the database.** `ApiAuthMiddleware` reads the stored key only when an `X-Api-Key` header is present; a request carrying `Authorization: Bearer …` takes the existing in-memory comparison. This is a tested property, not an aspiration.
- **The existing frontend must keep working with no change to how it authenticates.** Bearer stays exactly as it is.
- **Both credentials grant identical access.** There is no scoping or permission concept.
- Both are compared with `CryptographicOperations.FixedTimeEquals`.
- The existing carve-outs are unchanged: `/api/auth/*` stays unauthenticated and `/api/poster` keeps self-authenticating via its signed query string. Neither accepts an API key.
- **The webhook returns 200 for every recognised request**, including event types it ignores. A 400 on an unrecognised `eventType` would make Radarr report the connection as failing and possibly disable it. Only a body that is not valid JSON, or has no `eventType`, returns 400.
- The key is 32 random bytes rendered as 64 lowercase hex characters, from `RandomNumberGenerator`, stored in the `api_key` setting.
- Target framework `net10.0`, nullable reference types enabled, primary constructors, style matching `src/Themearr.API/Services/`.
- Backend tests: `dotnet test` from the repository root. The suite currently has **226** tests, all passing.
- Frontend checks from `src/Themearr.Web`: `npx tsc --noEmit`, `npm run lint` (expect 0 errors and 3 pre-existing warnings — 1 in `src/app/login/page.tsx`, 2 in `src/lib/auth.tsx`), `npm run build`.

---

### Task 1: The API key store

**Files:**
- Create: `src/Themearr.API/Services/ApiKeyStore.cs`
- Modify: `src/Themearr.API/Program.cs`
- Test: `tests/Themearr.API.Tests/ApiKeyStoreTests.cs`

**Interfaces:**
- Consumes: `Database.GetSetting(key, default)`, `Database.SetSetting(key, value)`
- Produces:
  - `IApiKeyStore` with `string Current { get; }` and `string Regenerate()`
  - `ApiKeyStore(Database db) : IApiKeyStore`
  - `ApiKeyStore.SettingKey` (const `"api_key"`)

The interface exists so the middleware in Task 2 can be tested for the "does not read the database" property with a counting fake. `Database.GetSetting` is not virtual, so a fake needs a seam.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/ApiKeyStoreTests.cs`:

```csharp
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ApiKeyStoreTests
{
    private static (ApiKeyStore Store, Database Db) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return (new ApiKeyStore(db), db);
    }

    [Fact]
    public void Current_generates_a_key_on_first_use()
    {
        using var dir = new TempDir();
        var (store, _) = New(dir);

        Assert.Matches("^[0-9a-f]{64}$", store.Current);
    }

    [Fact]
    public void Current_is_stable_across_calls_and_instances()
    {
        using var dir = new TempDir();
        var (store, db) = New(dir);

        var first = store.Current;

        Assert.Equal(first, store.Current);
        Assert.Equal(first, new ApiKeyStore(db).Current);
    }

    [Fact]
    public void Regenerate_produces_a_different_key_and_persists_it()
    {
        using var dir = new TempDir();
        var (store, db) = New(dir);
        var before = store.Current;

        var after = store.Regenerate();

        Assert.NotEqual(before, after);
        Assert.Matches("^[0-9a-f]{64}$", after);
        Assert.Equal(after, new ApiKeyStore(db).Current);
    }

    [Fact]
    public void An_existing_key_is_not_overwritten()
    {
        using var dir = new TempDir();
        var (store, db) = New(dir);
        db.SetSetting(ApiKeyStore.SettingKey, new string('a', 64));

        Assert.Equal(new string('a', 64), store.Current);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ApiKeyStoreTests`
Expected: FAIL — `The type or namespace name 'ApiKeyStore' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Themearr.API/Services/ApiKeyStore.cs`:

```csharp
using System.Security.Cryptography;
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// The key an external tool — Radarr, or a script — authenticates with.
///
/// Deliberately separate from THEMEARR_AUTH_TOKEN: that is the master credential
/// every browser session holds, set in the environment and immutable for the
/// process lifetime. This one can be regenerated without editing a file,
/// restarting, or logging anyone out, so Radarr's access can be revoked on its own.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>The current key, generated on first access if none exists.</summary>
    string Current { get; }

    /// <summary>Replaces the key and returns the new one. The old one stops working immediately.</summary>
    string Regenerate();
}

public sealed class ApiKeyStore(Database db) : IApiKeyStore
{
    public const string SettingKey = "api_key";

    public string Current
    {
        get
        {
            var existing = db.GetSetting(SettingKey, "");
            if (!string.IsNullOrEmpty(existing)) return existing;
            return Regenerate();
        }
    }

    public string Regenerate()
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.SetSetting(SettingKey, key);
        return key;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ApiKeyStoreTests`
Expected: PASS — 4 tests passed.

- [ ] **Step 5: Register it and seed the key at startup**

In `src/Themearr.API/Program.cs`, add to the service registrations (near the other `AddSingleton` calls):

```csharp
builder.Services.AddSingleton<IApiKeyStore, ApiKeyStore>();
```

Then, immediately after the existing `db.SetSetting("app_version", appVersion);` line, add:

```csharp
// Generate the external API key on first run, so it exists before anything asks for it.
app.Services.GetRequiredService<IApiKeyStore>().Current is var _;
```

If that expression reads awkwardly in context, assign it to a discard or a named local instead — the requirement is only that `Current` is touched once at startup so the key is created eagerly rather than on the first API call.

- [ ] **Step 6: Build and run the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded with 0 warnings; **230 tests passing** (226 + 4).

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Services/ApiKeyStore.cs src/Themearr.API/Program.cs \
        tests/Themearr.API.Tests/ApiKeyStoreTests.cs
git commit -m "feat(auth): add a regenerable API key for external tools"
```

---

### Task 2: Accept `X-Api-Key` in the auth middleware

**Files:**
- Modify: `src/Themearr.API/Services/ApiAuthMiddleware.cs`
- Test: `tests/Themearr.API.Tests/ApiAuthMiddlewareTests.cs`

**Interfaces:**
- Consumes: `IApiKeyStore.Current` (Task 1)
- Produces: no new public API — the middleware's behaviour widens

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/ApiAuthMiddlewareTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ApiAuthMiddlewareTests
{
    private const string Token = "test-bearer-token-at-least-16";

    /// <summary>Counts reads so the hot-path property can be asserted.</summary>
    private sealed class CountingKeyStore(string key) : IApiKeyStore
    {
        public int Reads;
        public string Current { get { Reads++; return key; } }
        public string Regenerate() => key;
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Themearr:AuthToken"] = Token })
            .Build();

    private static async Task<(int Status, bool NextCalled, int KeyReads)> Run(
        Action<HttpContext> setup, string apiKey = "the-api-key")
    {
        var store = new CountingKeyStore(apiKey);
        var nextCalled = false;
        var middleware = new ApiAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Config(), NullLogger<ApiAuthMiddleware>.Instance, store);

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        setup(ctx);

        await middleware.Invoke(ctx);
        return (ctx.Response.StatusCode, nextCalled, store.Reads);
    }

    [Fact]
    public async Task A_valid_bearer_token_is_still_accepted()
    {
        var (_, nextCalled, _) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_valid_api_key_is_accepted()
    {
        var (_, nextCalled, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "the-api-key");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_wrong_api_key_is_rejected()
    {
        var (status, nextCalled, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "wrong");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task No_credential_at_all_is_rejected()
    {
        var (status, nextCalled, _) = await Run(_ => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task The_key_store_is_not_read_when_no_api_key_header_is_present()
    {
        // The browser sends Bearer and never sets X-Api-Key. Reading the stored key
        // on that path would put a database hit on every page load and every poll.
        var (_, nextCalled, reads) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.True(nextCalled);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task The_key_store_is_not_read_when_there_is_no_credential_either()
    {
        var (status, _, reads) = await Run(_ => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(0, reads);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ApiAuthMiddlewareTests`
Expected: FAIL — the `ApiAuthMiddleware` constructor does not take an `IApiKeyStore`.

- [ ] **Step 3: Widen the middleware**

In `src/Themearr.API/Services/ApiAuthMiddleware.cs`, change the class declaration from:

```csharp
public class ApiAuthMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiAuthMiddleware> log)
```

to:

```csharp
public class ApiAuthMiddleware(
    RequestDelegate next, IConfiguration config, ILogger<ApiAuthMiddleware> log, IApiKeyStore keys)
```

Then, inside `Invoke`, after the existing bearer-token check and before the 401 response, insert:

```csharp
        // Only touch the key store when the header is actually present. The browser
        // sends Bearer and never sets this, so its hot path — every page load, the
        // health poll, the sync poll — never reads the database.
        var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrEmpty(apiKey))
        {
            var provided = Encoding.UTF8.GetBytes(apiKey);
            var expected = Encoding.UTF8.GetBytes(keys.Current);
            if (provided.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                await next(ctx);
                return;
            }
        }
```

Do not change the existing bearer branch, the 401 status, or the `WWW-Authenticate` header.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ApiAuthMiddlewareTests`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; **236 tests passing**.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/ApiAuthMiddleware.cs tests/Themearr.API.Tests/ApiAuthMiddlewareTests.cs
git commit -m "feat(auth): accept an X-Api-Key header alongside the bearer token"
```

---

### Task 3: Settings endpoints for the key

**Files:**
- Modify: `src/Themearr.API/Controllers/SettingsController.cs`
- Test: `tests/Themearr.API.Tests/ApiKeyEndpointTests.cs`

**Interfaces:**
- Consumes: `IApiKeyStore` (Task 1)
- Produces:
  - `GET  /api/settings/apikey` → `{ key }`
  - `POST /api/settings/apikey/regenerate` → `{ key }`

**Note the deliberate asymmetry with Radarr's key.** Radarr's key is write-only — Themearr holds it and never returns it. This key is the opposite: it is *issued* to the operator to paste into Radarr, so the GET returns it in full. Same class of secret, opposite treatment, because the direction of trust is reversed.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/ApiKeyEndpointTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ApiKeyEndpointTests
{
    private static (SettingsController Controller, IApiKeyStore Keys) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var keys = new ApiKeyStore(db);
        // Read SettingsController's constructor and supply whatever else it needs;
        // only the key store matters for these tests.
        return (TestControllers.NewSettingsController(db, keys), keys);
    }

    [Fact]
    public void Get_returns_the_current_key()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir);

        var result = Assert.IsType<OkObjectResult>(controller.GetApiKey());

        Assert.Contains(keys.Current, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void Regenerate_returns_a_different_key_and_the_old_one_stops_being_current()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir);
        var before = keys.Current;

        var result = Assert.IsType<OkObjectResult>(controller.RegenerateApiKey());
        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain(before, body);
        Assert.Contains(keys.Current, body);
        Assert.NotEqual(before, keys.Current);
    }
}
```

You will need a small helper to construct `SettingsController`, since its constructor takes several dependencies this test does not care about. Create `tests/Themearr.API.Tests/TestControllers.cs` with a `NewSettingsController(Database, IApiKeyStore)` factory that supplies the rest — read the real constructor first and mirror how `RadarrSettingsEndpointTests.cs` already builds it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ApiKeyEndpointTests`
Expected: FAIL — `SettingsController` has no `GetApiKey` / `RegenerateApiKey`.

- [ ] **Step 3: Add the endpoints**

In `src/Themearr.API/Controllers/SettingsController.cs`, add `IApiKeyStore keys` to the primary constructor parameters and add these two actions:

```csharp
    /// <summary>
    /// Returns the API key in full. Unlike Radarr's key — which Themearr holds and never
    /// discloses — this one is issued to the operator to paste into an external tool, so
    /// it has to be readable.
    /// </summary>
    [HttpGet("apikey")]
    public IActionResult GetApiKey() => Ok(new { key = keys.Current });

    [HttpPost("apikey/regenerate")]
    public IActionResult RegenerateApiKey() => Ok(new { key = keys.Regenerate() });
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ApiKeyEndpointTests`
Expected: PASS — 2 tests passed.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; **238 tests passing**.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Controllers/SettingsController.cs tests/Themearr.API.Tests/
git commit -m "feat(settings): expose and regenerate the API key"
```

---

### Task 4: The Radarr webhook

**Files:**
- Create: `src/Themearr.API/Controllers/WebhookController.cs`
- Test: `tests/Themearr.API.Tests/WebhookControllerTests.cs`

**Interfaces:**
- Consumes: `TaskRegistry.Trigger(string id) -> bool`, `AutoSyncService.SyncTaskId` (const `"syncLibrary"`)
- Produces: `POST /api/webhook/radarr`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/WebhookControllerTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class WebhookControllerTests
{
    private static (WebhookController Controller, TaskRegistry Registry) New()
    {
        var registry = new TaskRegistry();
        registry.Register(AutoSyncService.SyncTaskId, "Sync Library", TimeSpan.FromHours(24));
        return (new WebhookController(registry, Microsoft.Extensions.Logging.Abstractions
            .NullLogger<WebhookController>.Instance), registry);
    }

    /// <summary>True when a sync is pending — the trigger channel holds one slot.</summary>
    private static bool SyncPending(TaskRegistry r) =>
        !r.Trigger(AutoSyncService.SyncTaskId);   // a second write fails only if one is queued

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void An_import_triggers_a_sync()
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body("""{"eventType":"Download"}"""));

        Assert.IsType<OkObjectResult>(result);
        Assert.True(SyncPending(registry));
    }

    [Fact]
    public void A_test_ping_succeeds_without_triggering_a_sync()
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body("""{"eventType":"Test"}"""));

        Assert.IsType<OkObjectResult>(result);
        // Nothing queued, so this write is the first and must succeed.
        Assert.True(registry.Trigger(AutoSyncService.SyncTaskId));
    }

    [Theory]
    [InlineData("Grab")]
    [InlineData("Rename")]
    [InlineData("MovieDelete")]
    [InlineData("Health")]
    public void Other_events_succeed_without_triggering_a_sync(string eventType)
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body($$"""{"eventType":"{{eventType}}"}"""));

        // Must be 200: a 400 makes Radarr report the connection as failing.
        Assert.IsType<OkObjectResult>(result);
        Assert.True(registry.Trigger(AutoSyncService.SyncTaskId));
    }

    [Fact]
    public void A_body_with_no_event_type_is_a_bad_request()
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body("""{"movie":{"title":"Heat"}}"""));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(registry.Trigger(AutoSyncService.SyncTaskId));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~WebhookControllerTests`
Expected: FAIL — `The type or namespace name 'WebhookController' could not be found`.

- [ ] **Step 3: Write the controller**

Create `src/Themearr.API/Controllers/WebhookController.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// Receives Radarr's Connect webhooks so a newly imported movie gets its theme in
/// seconds rather than at the next scheduled sync.
///
/// Sits under /api/*, so ApiAuthMiddleware guards it and the API key works here
/// without an exemption.
/// </summary>
[ApiController]
[Route("api/webhook")]
public class WebhookController(TaskRegistry tasks, ILogger<WebhookController> log) : ControllerBase
{
    [HttpPost("radarr")]
    [Consumes("application/json")]
    public IActionResult Radarr([FromBody] JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("eventType", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return BadRequest(new { detail = "Expected a Radarr webhook payload with an eventType." });

        var eventType = typeElement.GetString() ?? "";

        // Radarr sends this when the operator presses Test. Answering it plainly is what
        // makes configuring the connection give feedback, rather than deferring the
        // discovery of a wrong URL or key to the next import.
        if (eventType == "Test")
            return Ok(new { received = "Test", detail = "Themearr is reachable." });

        // "Download" is Radarr's import event. Everything else — Grab, Rename,
        // MovieDelete, Health — is acknowledged and ignored: returning anything but 200
        // makes Radarr report the connection as failing and may disable it.
        if (eventType != "Download")
            return Ok(new { received = eventType, detail = "Ignored." });

        // Signal the existing sync rather than inserting the movie here: the sync owns
        // resolving and upserting, and a second write path into the movie table would
        // drift. The trigger channel holds one slot, so a batch import that fires many
        // webhooks still produces a single sync.
        tasks.Trigger(AutoSyncService.SyncTaskId);
        log.LogInformation("Radarr reported an import — library sync requested");
        return Ok(new { received = eventType, detail = "Sync requested." });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~WebhookControllerTests`
Expected: PASS — 7 tests passed (the `[Theory]` contributes 4).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; **245 tests passing**.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Controllers/WebhookController.cs tests/Themearr.API.Tests/WebhookControllerTests.cs
git commit -m "feat(webhook): sync when Radarr reports an import"
```

---

### Task 5: Settings UI for the key

**Files:**
- Modify: `src/Themearr.Web/src/lib/types.ts`
- Modify: `src/Themearr.Web/src/lib/api.ts`
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`

**Interfaces:**
- Consumes: `GET /api/settings/apikey` → `{ key }`, `POST /api/settings/apikey/regenerate` → `{ key }`
- Produces: `apiKeyApi.{get,regenerate}`

- [ ] **Step 1: Add the type and client**

Append to `src/Themearr.Web/src/lib/types.ts`:

```ts
export interface ApiKey {
  key: string
}
```

In `src/Themearr.Web/src/lib/api.ts`, add `ApiKey` to the existing `import type` list and append:

```ts
// ── API key (for Radarr and scripts) ──────────────────────────────────────────

export const apiKeyApi = {
  get: () => request<ApiKey>('/api/settings/apikey'),
  regenerate: () =>
    request<ApiKey>('/api/settings/apikey/regenerate', { method: 'POST' }),
}
```

- [ ] **Step 2: Add an API key section to Settings**

Read `src/Themearr.Web/src/app/settings/page.tsx` and follow the structure of an existing section. Add an **API key** section containing:

- the key displayed in a read-only field, with a **Copy** button
- the webhook URL to paste into Radarr, built client-side as `` `${window.location.origin}/api/webhook/radarr` `` with its own Copy button
- a **Regenerate** button that warns, before acting, that any Radarr connection using the old key will stop working
- a one-line explanation that this key is for Radarr and scripts, and is not the access token used to sign in

Unlike the Radarr API key field elsewhere on this page, **this key is meant to be visible** — it is issued to be copied, not held on someone else's behalf. Do not mask it.

Load it with `apiKeyApi.get()` on mount, following the page's existing state, loading and error conventions.

- [ ] **Step 3: Verify**

Run from `src/Themearr.Web`:
```bash
npx tsc --noEmit && npm run lint && npm run build
```
Expected: typecheck clean, lint 0 errors with 3 pre-existing warnings, build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.Web/src/lib/types.ts src/Themearr.Web/src/lib/api.ts \
        src/Themearr.Web/src/app/settings/page.tsx
git commit -m "feat(web): show the API key and Radarr webhook URL in settings"
```

---

### Task 6: End-to-end verification and README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Verify the whole flow against a running instance**

```bash
cd /Users/devlin/Documents/GitHub/themearr
cd src/Themearr.Web && npm run build && cd ../..
SCRATCH=$(mktemp -d)
dotnet publish src/Themearr.API/Themearr.API.csproj -c Release -o "$SCRATCH/app"
cp -r src/Themearr.Web/out "$SCRATCH/app/wwwroot"

THEMEARR_AUTH_TOKEN=d-verify-token-abcdef DB_PATH="$SCRATCH/themearr.db" \
  ASPNETCORE_URLS=http://127.0.0.1:5205 dotnet "$SCRATCH/app/Themearr.API.dll" &
sleep 8

BEARER='Authorization: Bearer d-verify-token-abcdef'
KEY=$(curl -s -H "$BEARER" http://127.0.0.1:5205/api/settings/apikey | python3 -c 'import sys,json;print(json.load(sys.stdin)["key"])')
echo "key length: ${#KEY}"

echo "--- the key authenticates ---"
curl -s -o /dev/null -w '%{http_code}\n' -H "X-Api-Key: $KEY" http://127.0.0.1:5205/api/system/tasks
echo "--- a wrong key does not ---"
curl -s -o /dev/null -w '%{http_code}\n' -H "X-Api-Key: wrong" http://127.0.0.1:5205/api/system/tasks
echo "--- no credential does not ---"
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5205/api/system/tasks
echo "--- Radarr Test ping ---"
curl -s -X POST -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"eventType":"Test"}' http://127.0.0.1:5205/api/webhook/radarr; echo
echo "--- Radarr import ---"
curl -s -X POST -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"eventType":"Download","movie":{"title":"Heat"}}' http://127.0.0.1:5205/api/webhook/radarr; echo
echo "--- regenerate invalidates the old key ---"
NEWKEY=$(curl -s -X POST -H "$BEARER" http://127.0.0.1:5205/api/settings/apikey/regenerate | python3 -c 'import sys,json;print(json.load(sys.stdin)["key"])')
curl -s -o /dev/null -w 'old key now: %{http_code}\n' -H "X-Api-Key: $KEY"    http://127.0.0.1:5205/api/system/tasks
curl -s -o /dev/null -w 'new key now: %{http_code}\n' -H "X-Api-Key: $NEWKEY" http://127.0.0.1:5205/api/system/tasks
echo "--- the key must not appear in the log ---"
kill %1; sleep 1
```

All of these must hold: the key is 64 characters; a valid key returns `200`; a wrong key and no credential both return `401`; the Test ping and the import both return `200`; after regenerating, the **old** key returns `401` and the new one `200` **without restarting the service**.

Also confirm the key appears nowhere in the server's output. If any check fails, report it — do not adjust the check.

- [ ] **Step 2: Update the README**

Add this section immediately before `## Updating`:

```markdown
## API key and Radarr webhook

**Settings → API key** shows a key that external tools can use to talk to Themearr.
Send it as an `X-Api-Key` header on any `/api/…` request:

```bash
curl -H "X-Api-Key: <your key>" http://themearr:8080/api/system/tasks
```

It is separate from the access token you sign in with, so you can regenerate it
without logging anyone out — and regenerating immediately stops the old one working.

### Fetching themes the moment Radarr imports

Instead of waiting for the next sync, have Radarr tell Themearr directly. In Radarr:
**Settings → Connect → Add → Webhook**, then set:

| Field | Value |
|---|---|
| Notification Triggers | **On Import** (also tick On Upgrade if you want) |
| URL | `http://themearr:8080/api/webhook/radarr` |
| Method | `POST` |
| Headers | `X-Api-Key` = your key from Settings → API key |

Press **Test** — Themearr answers, so a wrong URL or key shows up immediately rather
than at the next import.

Importing several movies at once is fine: Themearr collapses the burst into a single
sync rather than one per movie.

Two caveats:

- This is most useful when **Radarr is your library source**. If you use Plex as the
  source and Radarr only downloads, the webhook still fires — but Plex may not have
  scanned the new file yet, so the theme may still wait for a later sync.
- Radarr builds from before custom webhook headers were added (upstream, late 2024)
  cannot send `X-Api-Key`.
```

Do not rename, reword or re-case the headings `### Library paths & path mappings` or `## Downloads require a RapidAPI key` — the in-app health checks link to their exact GitHub anchors.

- [ ] **Step 3: Final verification**

```bash
dotnet test
cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build
```
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs(readme): document the API key and the Radarr webhook"
```

---

## Self-review notes

**Spec coverage.** API key generation and storage → Task 1; `X-Api-Key` in the middleware including the hot-path property → Task 2; settings endpoints → Task 3; the webhook with event filtering and the `Test` ping → Task 4; settings UI showing the key and webhook URL → Task 5; README and end-to-end verification → Task 6.

**The seam Task 1 introduces is there for testability.** `IApiKeyStore` exists because `Database.GetSetting` is not virtual, so the "does not read the database" property in Task 2 would otherwise be unassertable. That property is the whole reason the design is safe to put on every request, so it needs a real test rather than a claim.

**Two tests assert an absence, and they are the fragile ones.** "The key store is not read" and "a Test ping does not trigger a sync" both check that something *didn't* happen — exactly the class of behaviour that regresses invisibly, since a spurious database read or a spurious sync looks like nothing at all in normal use.

**The webhook tests probe pending state through `TaskRegistry.Trigger`'s return value**, which is `false` when a trigger is already queued. That is a slightly indirect way to ask "is a sync pending", and it relies on the channel's capacity-1 semantics. If the implementer finds it confusing to read, adding an explicit test helper is fine — but do not change `TaskRegistry` to expose pending state just for the tests.

**Type consistency.** `IApiKeyStore.Current` / `.Regenerate()` are defined in Task 1 and used in Tasks 2 and 3. `AutoSyncService.SyncTaskId` and `TaskRegistry.Trigger` are existing API, used in Task 4. The response shape `{ key }` is produced in Task 3 and consumed in Task 5.
