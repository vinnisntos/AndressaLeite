using System.Security.Claims;
using AndressaLeite.Services;

namespace AndressaLeite.Tests;

/// <summary>
/// AuthorizationService centraliza checagens usadas em todo o app (proteção
/// contra open-redirect, leitura de claims de identidade/tenant, mapeamento
/// de role → landing page). São funções puras, sem dependência do Supabase,
/// então cobrir aqui evita regressão silenciosa em algo que protege login e
/// isolamento entre tenants.
/// </summary>
public class AuthorizationServiceTests
{
    [Theory]
    [InlineData("/Cliente/DashCliente")]
    [InlineData("/Admin/DashAdmin?tab=metrics")]
    [InlineData("/")]
    public void IsLocalSafeUrl_AcceptsRelativePaths(string url)
    {
        Assert.True(AuthorizationService.IsLocalSafeUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//evil.com")]
    [InlineData("//evil.com/path")]
    [InlineData("/\\evil.com")]
    [InlineData("\\evil.com")]
    [InlineData("https://evil.com")]
    [InlineData("http://evil.com/Admin/DashAdmin")]
    public void IsLocalSafeUrl_RejectsOpenRedirectVectors(string? url)
    {
        Assert.False(AuthorizationService.IsLocalSafeUrl(url));
    }

    [Theory]
    [InlineData("admin", "/Admin/DashAdmin")]
    [InlineData("ADMIN", "/Admin/DashAdmin")]
    [InlineData("employee", "/Profissional/DashProfissional")]
    [InlineData("client", "/Cliente/DashCliente")]
    [InlineData("inactive", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("algo-desconhecido", "/")]
    public void GetDefaultLandingForRole_MapsKnownRolesCaseInsensitively(string? role, string expected)
    {
        Assert.Equal(expected, AuthorizationService.GetDefaultLandingForRole(role));
    }

    [Fact]
    public void TryGetUserId_ReturnsTrueForValidGuidClaim()
    {
        var guid = Guid.NewGuid();
        var user = BuildPrincipal((ClaimTypes.NameIdentifier, guid.ToString()));

        var found = AuthorizationService.TryGetUserId(user, out var id);

        Assert.True(found);
        Assert.Equal(guid, id);
    }

    [Fact]
    public void TryGetUserId_ReturnsFalseWhenClaimMissing()
    {
        var user = BuildPrincipal();

        var found = AuthorizationService.TryGetUserId(user, out var id);

        Assert.False(found);
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void TryGetUserId_ReturnsFalseWhenClaimIsNotAGuid()
    {
        var user = BuildPrincipal((ClaimTypes.NameIdentifier, "não-é-um-guid"));

        var found = AuthorizationService.TryGetUserId(user, out var id);

        Assert.False(found);
    }

    [Fact]
    public void TryGetUserId_ReturnsFalseForNullPrincipal()
    {
        var found = AuthorizationService.TryGetUserId(null, out var id);

        Assert.False(found);
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void TryGetTenantId_ReturnsTrueAndValueWhenClaimPresent()
    {
        var user = BuildPrincipal((AuthorizationService.TenantClaimType, "tenant-123"));

        var found = AuthorizationService.TryGetTenantId(user, out var tenantId);

        Assert.True(found);
        Assert.Equal("tenant-123", tenantId);
    }

    [Fact]
    public void TryGetTenantId_ReturnsFalseWhenClaimMissing()
    {
        var user = BuildPrincipal();

        var found = AuthorizationService.TryGetTenantId(user, out var tenantId);

        Assert.False(found);
        Assert.Equal(string.Empty, tenantId);
    }

    [Fact]
    public void GetRole_ReturnsEmptyStringWhenNoRoleClaim()
    {
        var user = BuildPrincipal();

        Assert.Equal(string.Empty, AuthorizationService.GetRole(user));
    }

    [Fact]
    public void GetRole_ReturnsEmptyStringForNullPrincipal()
    {
        Assert.Equal(string.Empty, AuthorizationService.GetRole(null));
    }

    [Fact]
    public void GetRole_ReturnsClaimValueWhenPresent()
    {
        var user = BuildPrincipal((ClaimTypes.Role, "employee"));

        Assert.Equal("employee", AuthorizationService.GetRole(user));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("EMPLOYEE")]
    [InlineData("client")]
    [InlineData("inactive")]
    public void KnownRoles_ContainsExpectedRolesCaseInsensitively(string role)
    {
        Assert.Contains(role, AuthorizationService.KnownRoles);
    }

    [Fact]
    public void KnownRoles_DoesNotContainUnknownRole()
    {
        Assert.DoesNotContain("superadmin", AuthorizationService.KnownRoles);
    }

    private static ClaimsPrincipal BuildPrincipal(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "TestAuth");
        return new ClaimsPrincipal(identity);
    }
}
