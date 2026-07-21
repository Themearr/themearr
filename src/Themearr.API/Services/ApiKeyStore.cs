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

    private readonly object _lock = new();

    // In-memory cache so a per-request read is a field access, not a database hit — an
    // anonymous caller sending a junk X-Api-Key on every request must not be able to force
    // a connection each time. Regenerate is the only supported way the key legitimately
    // changes, and it updates this field as it writes, so a regenerated key still takes
    // effect immediately with no restart. The trade-off: a key changed directly in the
    // database, out of band, will not be noticed until restart. That's acceptable because
    // Regenerate is the supported path.
    private string? _cached;

    public string Current
    {
        get
        {
            // Current can generate, so read-then-maybe-write must be atomic.
            // Regenerate must not interleave with reads.
            lock (_lock)
            {
                if (_cached is not null) return _cached;

                var existing = db.GetSetting(SettingKey, "");
                if (!string.IsNullOrEmpty(existing))
                {
                    _cached = existing;
                    return existing;
                }
                return RegenerateInternal();
            }
        }
    }

    public string Regenerate()
    {
        lock (_lock)
        {
            return RegenerateInternal();
        }
    }

    private string RegenerateInternal()
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.SetSetting(SettingKey, key);
        _cached = key;
        return key;
    }
}
