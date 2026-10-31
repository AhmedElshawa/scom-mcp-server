using System.Collections.Immutable;
using System.Security.Claims;

namespace ScomMcp;

public sealed class AuthSettings
{
    public string TenantId { get; set; } = "";
    public string[] Audiences { get; set; } = [];
    public string RequiredScope { get; set; } = "";
    public CallerGrant[] Callers { get; set; } = [];

    public void Validate()
    {
        if (!Guid.TryParseExact(TenantId, "D", out var tenantId) || tenantId == Guid.Empty ||
            Audiences.Length == 0 ||
            Audiences.Any(a => string.IsNullOrWhiteSpace(a) || a.Contains('*') || a.Any(char.IsWhiteSpace)) ||
            string.IsNullOrWhiteSpace(RequiredScope) || RequiredScope.Any(char.IsWhiteSpace) ||
            RequiredScope.Contains('*') || Callers.Length == 0 ||
            Callers.Any(c => (string.IsNullOrWhiteSpace(c.ObjectId) == string.IsNullOrWhiteSpace(c.UserPrincipalName)) ||
                (!string.IsNullOrEmpty(c.ObjectId) && !Guid.TryParse(c.ObjectId, out _)) ||
                (!string.IsNullOrEmpty(c.UserPrincipalName) &&
                    (!c.UserPrincipalName.Contains('@') || c.UserPrincipalName.Contains('*') ||
                     c.UserPrincipalName.Any(char.IsWhiteSpace))) ||
                c.Scopes.Length == 0 || c.Scopes.Any(s => s != Wire.ScopeId)))
            throw new InvalidOperationException("Auth configuration is missing or invalid. Refusing to start.");
        TenantId = tenantId.ToString("D");
    }
}

public sealed class CallerGrant
{
    public string? ObjectId { get; set; }
    public string? UserPrincipalName { get; set; }
    public string[] Scopes { get; set; } = [];
}

public sealed record Caller(string Binding, ImmutableHashSet<string> Scopes);

public static class CallerAuthorization
{
    public static Caller? Resolve(ClaimsPrincipal principal, AuthSettings settings)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            principal.FindFirst("tid")?.Value != settings.TenantId ||
            !principal.FindAll("scp").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(settings.RequiredScope, StringComparer.Ordinal))
            return null;
        var oid = principal.FindFirst("oid")?.Value;
        if (Guid.TryParse(oid, out var id)) oid = id.ToString("D");
        else oid = null;
        var upns = new[] { "preferred_username", "upn", "unique_name" }
            .SelectMany(principal.FindAll).Select(c => c.Value.ToLowerInvariant()).Distinct().ToArray();
        // Ambiguous identity claims fail closed; UPN grants are only a transitional option.
        if (upns.Length > 1) return null;
        var scopes = settings.Callers.Where(g =>
            !string.IsNullOrWhiteSpace(g.ObjectId)
                ? oid is not null && string.Equals(g.ObjectId, oid, StringComparison.OrdinalIgnoreCase)
                : upns.Length == 1 && string.Equals(g.UserPrincipalName, upns[0], StringComparison.OrdinalIgnoreCase))
            .SelectMany(g => g.Scopes).ToImmutableHashSet(StringComparer.Ordinal);
        if (scopes.Count == 0) return null;
        return new Caller(oid is not null ? $"oid:{oid}" : $"upn:{upns.Single()}", scopes);
    }

    public static string AuthorizeScope(Caller caller, string? scopeId)
    {
        var scope = scopeId ?? Wire.ScopeId;
        if (scope != Wire.ScopeId || !caller.Scopes.Contains(scope))
            throw new QueryFault("FORBIDDEN", "Scope is not authorized.");
        return scope;
    }
}
