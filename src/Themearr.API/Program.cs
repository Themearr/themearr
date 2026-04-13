using Themearr.API.Data;
using Themearr.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

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
builder.Services.AddSingleton<DownloadService>();

// CORS for dev (Next.js dev server on :3000)
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:3000")
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();

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

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// SPA fallback — serve index.html for all non-API routes
app.MapFallbackToFile("index.html");

app.Run();
