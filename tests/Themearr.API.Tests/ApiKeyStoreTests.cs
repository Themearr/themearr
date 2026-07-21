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
    public void Regenerate_invalidates_the_cached_current_value_immediately()
    {
        using var dir = new TempDir();
        var (store, _) = New(dir);
        var before = store.Current; // populates the in-memory cache

        var after = store.Regenerate();

        // Current must reflect the regenerated key right away, not the value
        // that was cached before Regenerate ran — no restart required.
        Assert.NotEqual(before, after);
        Assert.Equal(after, store.Current);
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
