using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using ScomMcp;

static class CertificatePublisherChecks
{
    public static async Task Run()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var request = new CertificateRequest("CN=synthetic.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
        var settings = new PublisherIdentitySettings
        {
            TenantId = "11111111-1111-4111-8111-111111111111",
            ClientId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            Resource = "api://synthetic-worker", CertificateThumbprint = certificate.Thumbprint
        };
        var handler = new Handler(rsa, settings, Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256)));
        using var provider = new CertificatePublisherToken(settings, certificate, handler, TimeProvider.System);
        var count = 0;
        if (await provider.AcquireAsync(CancellationToken.None) != "synthetic-token")
            throw new InvalidOperationException("Certificate token response differs.");
        count++;
        await provider.AcquireAsync(CancellationToken.None);
        if (handler.Assertions.Count != 2 || handler.Assertions.Distinct().Count() != 2)
            throw new InvalidOperationException("Client assertion must have a fresh replay identity per attempt.");
        count++;
        foreach (var mode in new[] { "reject", "redirect", "missing", "duplicate", "invalidExpiry", "shortExpiry", "wrongType", "overflow", "malformed" })
        {
            handler.Mode = mode;
            try { await provider.AcquireAsync(CancellationToken.None); throw new Exception("Invalid token response accepted: " + mode); }
            catch (IngestionPublicationException) { count++; }
        }
        handler.Mode = "cancel";
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await provider.AcquireAsync(cancellation.Token); throw new Exception("Token cancellation ignored."); }
            catch (OperationCanceledException) { count++; }
        }
        foreach (var resource in new[] { "http://synthetic.invalid", "https://user:password@synthetic.invalid",
            "api://synthetic-worker/.default/", "api://synthetic-worker/.default" })
        {
            settings.Resource = resource;
            try { settings.Validate(); throw new Exception("Unsafe token resource accepted."); }
            catch (InvalidOperationException) { count++; }
        }
        settings.Resource = "api://synthetic-worker";
        var expired = request.CreateSelfSigned(now.AddDays(-2), now.AddDays(-1));
        settings.CertificateThumbprint = expired.Thumbprint;
        using (var expiredProvider = new CertificatePublisherToken(settings, expired,
            new Handler(rsa, settings, ""), TimeProvider.System))
        {
            try { await expiredProvider.AcquireAsync(CancellationToken.None); throw new Exception("Expired certificate used."); }
            catch (IngestionPublicationException error) when (error.Code == "PUBLISHER_CERTIFICATE_EXPIRED_OR_NOT_YET_VALID") { count++; }
        }
        using var publicOnly = new X509Certificate2(certificate.Export(X509ContentType.Cert));
        settings.CertificateThumbprint = publicOnly.Thumbprint;
        using var invalidHandler = new Handler(rsa, settings, "");
        try
        {
            using var invalidProvider = new CertificatePublisherToken(settings, publicOnly, invalidHandler, TimeProvider.System);
            throw new Exception("Public-only certificate accepted.");
        }
        catch (InvalidOperationException) { count++; }
        Console.WriteLine($"PASS: {count} certificate publisher assertions; signed app-only claims, fresh assertions, strict responses and cancellation.");
    }

    private sealed class Handler(RSA rsa, PublisherIdentitySettings settings, string certificateHash) : HttpMessageHandler
    {
        public string Mode = "valid";
        public readonly List<string> Assertions = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = $"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/token";
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsoluteUri != endpoint)
                throw new InvalidOperationException("Unexpected identity endpoint.");
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace("+", " ")));
            if (form["grant_type"] != "client_credentials" || form["client_id"] != settings.ClientId ||
                form["scope"] != "api://synthetic-worker/.default" ||
                form["client_assertion_type"] != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
                throw new InvalidOperationException("Unexpected app-only grant form.");
            var handler = new JwtSecurityTokenHandler();
            handler.ValidateToken(form["client_assertion"], new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(rsa), ValidIssuer = settings.ClientId, ValidAudience = endpoint,
                ValidAlgorithms = [SecurityAlgorithms.RsaSsaPssSha256], ClockSkew = TimeSpan.Zero
            }, out var validated);
            var assertion = (JwtSecurityToken)validated;
            if (assertion.Subject != settings.ClientId || string.IsNullOrWhiteSpace(assertion.Id) ||
                assertion.Header["x5t#S256"]?.ToString() != certificateHash ||
                assertion.ValidTo - assertion.ValidFrom > TimeSpan.FromMinutes(6))
                throw new InvalidOperationException("Invalid certificate assertion claims/header.");
            Assertions.Add(assertion.Id);
            if (Mode == "cancel") await Task.Delay(Timeout.Infinite, cancellationToken);
            var status = Mode == "reject" ? HttpStatusCode.Unauthorized : Mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK;
            var body = Mode switch
            {
                "missing" => "{}",
                "duplicate" => "{\"access_token\":\"a\",\"access_token\":\"b\"}",
                "invalidExpiry" => "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":\"3600\"}",
                "shortExpiry" => "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":1}",
                "wrongType" => "{\"access_token\":\"a\",\"token_type\":\"Basic\",\"expires_in\":3600}",
                "overflow" => new string('x', 70000),
                "malformed" => "not-json",
                _ => "{\"access_token\":\"synthetic-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}"
            };
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
