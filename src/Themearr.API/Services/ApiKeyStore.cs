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
