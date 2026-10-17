using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themearr.API.Services;

public sealed record AudioProcessingOptions(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("trimSeconds")] int? TrimSeconds = null,
    [property: JsonPropertyName("normalize")] bool Normalize = false,
    [property: JsonPropertyName("bitrateKbps")] int BitrateKbps = 192)
{
    public static readonly AudioProcessingOptions Default = new();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));
    public string? ValidationError() => TrimSeconds is < 5 or > 900
        ? "Trim duration must be empty or between 5 and 900 seconds."
        : BitrateKbps is not (128 or 192 or 256 or 320) ? "Bitrate must be 128, 192, 256 or 320 kbps." : null;
}
