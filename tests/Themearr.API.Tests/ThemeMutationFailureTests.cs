using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ThemeMutationFailureTests
{
    private sealed class Provider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct) => Task.FromResult<string?>(null);
    }
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0folder")]
    public void An_unrelated_malformed_folder_does_not_prevent_deleting_a_valid_theme(string malformed)
    {
        using var dir = new TempDir();
        var folder = dir.File("Movie"); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [1, 2, 3]);
        var db = new Database(dir.File("test.db")); db.Init();
        db.UpsertMovies([new(folder, "plex", "1", "Movie", 2020, folder)]);
        var id = MediaFolderId.For(folder); db.SetMovieStatus(id, "downloaded");
        using var conn = new SqliteConnection($"Data Source={dir.File("test.db")}"); conn.Open();
        using (var bad = conn.CreateCommand())
        {
            bad.CommandText = "INSERT INTO movies (id, folderName, title) VALUES ('malformed', @folder, 'Old broken row')";
            bad.Parameters.AddWithValue("@folder", malformed); bad.ExecuteNonQuery();
        }
        var download = new DownloadService(new Provider(), db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
        var controller = new MoviesController(db, new YoutubeService(), download, new PosterUrlSigner(new byte[32]), null!, NullLogger<MoviesController>.Instance);
        Assert.IsType<OkObjectResult>(controller.DeleteTheme(id));
        Assert.False(ThemeFiles.HasUsableTheme(folder));
        using var status = conn.CreateCommand(); status.CommandText = "SELECT status FROM movies WHERE id = @id"; status.Parameters.AddWithValue("@id", id);
        Assert.Equal("pending", status.ExecuteScalar());
    }
}
