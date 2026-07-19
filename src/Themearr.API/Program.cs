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
builder.Services.AddHostedService<AutoSyncService>();
// Register AutoDownloadService as a singleton AND wire its hosted-service lifecycle
// off the same instance so a controller can ask it for diagnostics.
builder.Services.AddSingleton<AutoDownloadService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoDownloadService>());

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

// Security headers on every response (static SPA + API). Posters/themes are served
// same-origin now, so a tight CSP doesn't need any external sources.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; media-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
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

app.MapControllers();

// SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Run();
