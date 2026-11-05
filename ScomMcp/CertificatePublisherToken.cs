using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace ScomMcp;

public sealed class PublisherIdentitySettings
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Resource { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public void Validate()
    {
        if (!ReadModelSettings.ValidId(TenantId) || !ReadModelSettings.ValidId(ClientId) ||
            !Uri.TryCreate(Resource, UriKind.Absolute, out var resource) ||
            resource.Scheme is not ("api" or "https") || resource.UserInfo.Length != 0 ||
            resource.Query.Length != 0 || resource.Fragment.Length != 0 || Resource.EndsWith("/", StringComparison.Ordinal) ||
            Resource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase) ||
            CertificateThumbprint is null || CertificateThumbprint.Length != 40 ||
            CertificateThumbprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Publisher identity requires tenant/client IDs, an application resource and a certificate thumbprint.");
    }
}

// App-only identity is distinct from delegated MCP readers. The certificate is
// provisioned outside this application; no private keys or tokens are persisted.
public sealed class CertificatePublisherToken : IDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly HttpClient client;
    private readonly string clientId;
    private readonly string scope;
    private readonly Uri endpoint;
    private readonly TimeProvider clock;

    public CertificatePublisherToken(PublisherIdentitySettings settings)
        : this(settings, LoadCertificate(settings),
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, TimeProvider.System) { }

    // Injected certificate and transport are owned by this instance.
    public CertificatePublisherToken(PublisherIdentitySettings settings, X509Certificate2 certificate,
        HttpMessageHandler handler, TimeProvider clock)
    {
        settings.Validate();
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        if (!certificate.HasPrivateKey ||
            !string.Equals(certificate.Thumbprint, settings.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Publisher certificate must match the configured private-key certificate.");
        this.certificate = certificate;
        this.clock = clock;
        clientId = Guid.Parse(settings.ClientId).ToString("D");
        scope = settings.Resource + "/.default";
        endpoint = new($"https://login.microsoftonline.com/{Guid.Parse(settings.TenantId):D}/oauth2/v2.0/token");
        client = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<string> AcquireAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        var now = clock.GetUtcNow();
        if (now.UtcDateTime < certificate.NotBefore.ToUniversalTime() ||
            now.UtcDateTime >= certificate.NotAfter.ToUniversalTime())
            throw new IngestionPublicationException("PUBLISHER_CERTIFICATE_EXPIRED_OR_NOT_YET_VALID");
        using var rsa = certificate.GetRSAPrivateKey() ??
            throw new IngestionPublicationException("PUBLISHER_CERTIFICATE_RSA_REQUIRED");
        if (rsa.KeySize < 2048)
            throw new IngestionPublicationException("PUBLISHER_CERTIFICATE_RSA_KEY_TOO_SMALL");
        var signingKey = new RsaSecurityKey(rsa)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.RsaSsaPssSha256))
        {
            ["x5t#S256"] = Base64UrlEncoder.Encode(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        };
        var payload = new JwtPayload(clientId, endpoint.AbsoluteUri,
            [new Claim("sub", clientId), new Claim("jti", Guid.NewGuid().ToString("D"))],
            now.UtcDateTime.AddSeconds(-30), now.UtcDateTime.AddMinutes(5), now.UtcDateTime);
        var assertion = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["scope"] = scope, ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new IngestionPublicationException("PUBLISHER_TOKEN_REQUEST_REJECTED");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > 64 * 1024)
                throw new IngestionPublicationException("PUBLISHER_TOKEN_RESPONSE_BUDGET");
            output.Write(buffer, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(output.ToArray());
            SnapshotReader.RejectDuplicateProperties(json.RootElement);
            var result = json.RootElement;
            if (!result.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
                access.GetString() is not { Length: > 0 and <= 32768 } token || token.Any(char.IsWhiteSpace) ||
                !result.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !result.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number ||
                !expires.TryGetInt32(out var seconds) || seconds < 60)
                throw new IngestionPublicationException("PUBLISHER_TOKEN_RESPONSE_INVALID");
            return token;
        }
        catch (JsonException) { throw new IngestionPublicationException("PUBLISHER_TOKEN_RESPONSE_INVALID"); }
        catch (QueryFault) { throw new IngestionPublicationException("PUBLISHER_TOKEN_RESPONSE_INVALID"); }
    }

    private static X509Certificate2 LoadCertificate(PublisherIdentitySettings settings)
    {
        settings.Validate();
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Publisher certificate store identity requires Windows.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, settings.CertificateThumbprint, false);
        if (matches.Count != 1)
        {
            foreach (var match in matches) match.Dispose();
            throw new InvalidOperationException("Publisher certificate must uniquely exist in LocalMachine/My.");
        }
        return matches[0];
    }

    public void Dispose()
    {
        client.Dispose();
        certificate.Dispose();
    }
}
