using System.Threading.RateLimiting;
using Themearr.API.Data;
using Themearr.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Rate-limit the unauthenticated token-verify oracle (per client IP) so it can't be
// used for unbounded brute-force / token-probing.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth-verify", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10,
                QueueLimit = 0,
            }));
});

// Config
var config = builder.Configuration.GetSection("Themearr");
var dbPath = Environment.GetEnvironmentVariable("DB_PATH")
    ?? config["DbPath"]
    ?? "/opt/themearr/data/themearr.db";

// Services
builder.Services.AddSingleton<Database>(_ => new Database(dbPath));
// Shared by every library source: the tool reporting paths sees a different
// filesystem than Themearr does.
builder.Services.AddSingleton<LocalFolderResolver>();
builder.Services.AddSingleton<Themearr.API.Services.Sources.PlexLibrarySource>();
builder.Services.AddSingleton<Themearr.API.Services.Sources.ILibrarySource>(
    sp => sp.GetRequiredService<Themearr.API.Services.Sources.PlexLibrarySource>());
builder.Services.AddSingleton<Themearr.API.Services.Sources.RadarrLibrarySource>();
builder.Services.AddSingleton<Themearr.API.Services.Sources.ILibrarySource>(
    sp => sp.GetRequiredService<Themearr.API.Services.Sources.RadarrLibrarySource>());
builder.Services.AddHttpClient(Themearr.API.Services.Sources.RadarrLibrarySource.ClientName,
    c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<Themearr.API.Services.Sources.LibrarySourceResolver>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHttpClient<PlexService>();
builder.Services.AddTransient<YoutubeService>();
// A client that does NOT auto-follow redirects, so the direct-URL download can
// re-validate every redirect Location against the SSRF guard before following it.
builder.Services.AddHttpClient("no-redirect")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// Theme-audio download backend behind an interface so the provider can be swapped
// without touching DownloadService (stateless — reads its config from the DB per call).
builder.Services.AddSingleton<IThemeAudioProvider, RapidApiThemeAudioProvider>();
builder.Services.AddSingleton<DownloadService>();
// Signs short-lived poster URLs so the Plex token never appears in a client-visible URL.
builder.Services.AddSingleton<PosterUrlSigner>();
builder.Services.AddSingleton<IApiKeyStore, ApiKeyStore>();
builder.Services.AddHostedService<AutoSyncService>();
// Register AutoDownloadService as a singleton AND wire its hosted-service lifecycle
// off the same instance so a controller can ask it for diagnostics.
builder.Services.AddSingleton<AutoDownloadService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoDownloadService>());

// ── System page: health checks + scheduled tasks ──────────────────────────────
builder.Services.AddSingleton<TaskRegistry>();
builder.Services.AddSingleton<Themearr.API.Services.Health.HealthCache>();
// The download worker's status is read by DownloadWorkerCheck through a narrow
// interface; resolve it from the same singleton the hosted service uses.
builder.Services.AddSingleton<Themearr.API.Services.Health.IDownloadWorkerStatus>(
    sp => sp.GetRequiredService<AutoDownloadService>());
builder.Services.AddSingleton<Themearr.API.Services.Health.IQuotaStatus>(
    sp => sp.GetRequiredService<DownloadService>());
// A short timeout matters: an unreachable Plex server is the expected case here,
// and without it the whole health page waits on a TCP hang.
builder.Services.AddHttpClient(Themearr.API.Services.Sources.PlexLibrarySource.ClientName,
    c => c.Timeout = TimeSpan.FromSeconds(3));
// Same reasoning, same timeout, for Radarr's health probe — kept separate from the
// "radarr" client above (10s) so an unreachable Radarr fails fast instead of racing
// HealthCache's own 10s refresh budget and losing to its generic timeout message.
builder.Services.AddHttpClient(Themearr.API.Services.Sources.RadarrLibrarySource.HealthClientName,
    c => c.Timeout = TimeSpan.FromSeconds(3));

builder.Services.AddHealthChecks()
    .AddCheck<Themearr.API.Services.Health.LibraryPathsCheck>("libraryPaths")
    .AddCheck<Themearr.API.Services.Health.LibrarySourceCheck>("librarySource")
    .AddCheck<Themearr.API.Services.Health.RapidApiCheck>("rapidapi")
    .AddCheck<Themearr.API.Services.Health.DownloadWorkerCheck>("autoDownload");

// CORS for dev (Vite dev server on :3000) — only in Development
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:3000")
         .AllowAnyHeader()
         .AllowAnyMethod()));
}

var app = builder.Build();

// Fail-closed: require a token at startup so an unauth'd deploy can't happen by accident.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<Themearr.API.Services.ApiAuthMiddleware>();
    Themearr.API.Services.ApiAuthMiddleware.LoadToken(builder.Configuration, logger);
}

// Initialise DB
var db = app.Services.GetRequiredService<Database>();
db.Init();

// Seed app version
var versionFile = Environment.GetEnvironmentVariable("THEMEARR_VERSION_FILE")
    ?? config["VersionFile"]
    ?? "/opt/themearr/VERSION";
var appVersion = Environment.GetEnvironmentVariable("APP_VERSION")?.Trim()
    ?? (File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "dev");
db.SetSetting("app_version", appVersion);

// Generate the external API key on first run, so it exists before anything asks for it.
_ = app.Services.GetRequiredService<IApiKeyStore>().Current;

// Security headers on every response (static SPA + API). The policy lives in
// SecurityHeaders so each allowance is pinned to the feature needing it — see
// SecurityHeadersTests.
app.Use(async (ctx, next) =>
{
    Themearr.API.Services.SecurityHeaders.Apply(ctx.Response.Headers);
    await next();
});

app.UseRateLimiter();

if (app.Environment.IsDevelopment()) app.UseCors();

// Bearer-token auth for every /api/* route except /api/auth/*
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api")
           && !ctx.Request.Path.StartsWithSegments("/api/auth")
           // Poster URLs self-authenticate via a signed, expiring query string so an
           // <img> tag (which can't send a bearer header) can still load them.
           && !ctx.Request.Path.StartsWithSegments("/api/poster"),
    branch => branch.UseMiddleware<Themearr.API.Services.ApiAuthMiddleware>());

app.UseDefaultFiles();
// Prevent browsers from caching index.html so updated JS bundles are loaded after deploys
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
        }
    }
});

// Unauthenticated monitoring endpoint for Uptime Kuma / Gatus. Deliberately
// detail-free: a single status word, no check names, no messages, no version.
// ApiAuthMiddleware guards only /api/*, so this needs no allowlist entry.
//
// It MUST go through HealthCache rather than MapHealthChecks: the built-in
// middleware re-runs every check on each request, which would let an anonymous
// caller drive unbounded outbound probes of the user's Plex server.
app.MapGet("/health", async (Themearr.API.Services.Health.HealthCache cache, CancellationToken ct) =>
    Results.Json(new { status = (await cache.GetAsync(ct)).Status.ToString() }));

app.MapControllers();

// SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Run();
