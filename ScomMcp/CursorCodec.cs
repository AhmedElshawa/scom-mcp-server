using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScomMcp;

public sealed class CursorCodec(TimeProvider clock, SourceSettings settings)
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private sealed record Payload(string Caller, string Scope, string Dataset, string Fingerprint,
        string Filter, int Offset, long Expires);

    public string Create(Caller caller, string scope, Dataset dataset, string filter, int offset)
    {
        var payload = new Payload(Hash(caller.Binding), scope, dataset.Snapshot.DatasetId, dataset.Fingerprint,
            Hash(filter), offset, clock.GetUtcNow().AddSeconds(settings.CursorLifetimeSeconds).ToUnixTimeSeconds());
        var body = Encoding.UTF8.GetBytes(Wire.Encode(payload));
        return Base64(body) + "." + Base64(HMACSHA256.HashData(key, body));
    }

    public int Read(string? cursor, Caller caller, string scope, Dataset dataset, string filter)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 4096) throw new FormatException();
            var parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var body = Convert.FromBase64String(parts[0]);
            var signature = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, body)))
                throw new FormatException();
            var payload = JsonSerializer.Deserialize<Payload>(body, Wire.Json) ?? throw new FormatException();
            if (payload.Caller != Hash(caller.Binding) || payload.Scope != scope || payload.Filter != Hash(filter) ||
                payload.Expires <= clock.GetUtcNow().ToUnixTimeSeconds() || payload.Offset < 0)
                throw new FormatException();
            if (payload.Dataset != dataset.Snapshot.DatasetId || payload.Fingerprint != dataset.Fingerprint)
                throw new QueryFault("CURSOR_DATASET_CHANGED", "Dataset changed; restart paging without a cursor.");
            return payload.Offset;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new QueryFault("INVALID_CURSOR", "Cursor is invalid, expired, or belongs to another request.");
        }
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
