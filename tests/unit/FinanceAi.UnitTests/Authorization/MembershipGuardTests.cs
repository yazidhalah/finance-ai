using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Security;

namespace FinanceAi.UnitTests.Authorization;

/// <summary>
/// Doc 10 slice 1 §7: the last Owner cannot be demoted or deactivated.
/// <para>
/// The database guarantees <i>at most</i> one Owner per tenant (the <c>one_owner_per_tenant</c>
/// index, proved by AC-03). This guard is the other half — <i>at least</i> one — which no index can
/// express. An organization with no Owner cannot transfer ownership, invite anyone, or be deleted:
/// it is unrecoverable without operator intervention.
/// </para>
/// </summary>
public sealed class MembershipGuardTests
{
    [Fact]
    public void TheLastOwner_CannotBeDemoted() =>
        Assert.True(TenantMembership.WouldRemoveLastOwner(
            TenantRole.Owner, MembershipStatus.Active,
            TenantRole.Admin, MembershipStatus.Active,
            otherActiveOwnerCount: 0));

    [Fact]
    public void TheLastOwner_CannotBeDeactivated() =>
        Assert.True(TenantMembership.WouldRemoveLastOwner(
            TenantRole.Owner, MembershipStatus.Active,
            TenantRole.Owner, MembershipStatus.Disabled,
            otherActiveOwnerCount: 0));

    [Fact]
    public void AnOwner_MayBeDemotedWhenAnotherOwnerRemains() =>
        Assert.False(TenantMembership.WouldRemoveLastOwner(
            TenantRole.Owner, MembershipStatus.Active,
            TenantRole.Admin, MembershipStatus.Active,
            otherActiveOwnerCount: 1));

    [Fact]
    public void ANonOwner_MayBeDemotedOrDeactivatedFreely()
    {
        Assert.False(TenantMembership.WouldRemoveLastOwner(
            TenantRole.Admin, MembershipStatus.Active,
            TenantRole.Viewer, MembershipStatus.Active,
            otherActiveOwnerCount: 0));

        Assert.False(TenantMembership.WouldRemoveLastOwner(
            TenantRole.Collector, MembershipStatus.Active,
            TenantRole.Collector, MembershipStatus.Disabled,
            otherActiveOwnerCount: 0));
    }

    [Fact]
    public void AnOwnerStayingAnOwner_IsNeverBlocked() =>
        Assert.False(TenantMembership.WouldRemoveLastOwner(
            TenantRole.Owner, MembershipStatus.Active,
            TenantRole.Owner, MembershipStatus.Active,
            otherActiveOwnerCount: 0));
}

/// <summary>AC-16: the claims and lifetime of the access token (SEC-03).</summary>
public sealed class AccessTokenTests
{
    [Fact]
    public void Issue_CarriesSubjectTenantRoleAndPermissionSetVersion()
    {
        using var issuer = new RsaAccessTokenIssuer(TestKeys.RsaPrivateKeyPem);

        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var (token, expiresAt) = issuer.Issue(userId, tenantId, TenantRole.Accountant);

        var parsed = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);

        Assert.Equal(userId.ToString(), parsed.Subject);
        Assert.Equal(tenantId.ToString(), parsed.GetClaim(FinanceAiClaims.TenantId).Value);
        Assert.Equal(nameof(TenantRole.Accountant), parsed.GetClaim(FinanceAiClaims.Role).Value);
        Assert.Equal("1", parsed.GetClaim(FinanceAiClaims.PermissionSetVersion).Value);
        Assert.False(string.IsNullOrEmpty(parsed.Id));
        Assert.Equal("RS256", parsed.Alg);

        // SEC-03: 15 minutes.
        Assert.Equal(TimeSpan.FromMinutes(15), issuer.Lifetime);
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Issue_ProducesADistinctJtiPerToken()
    {
        using var issuer = new RsaAccessTokenIssuer(TestKeys.RsaPrivateKeyPem);
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        var first = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(
            issuer.Issue(userId, tenantId, TenantRole.Owner).Token);
        var second = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(
            issuer.Issue(userId, tenantId, TenantRole.Owner).Token);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void AKeyBelowTwoThousandFortyEightBits_IsRefused()
    {
        using var weak = System.Security.Cryptography.RSA.Create(1024);
        var pem = weak.ExportPkcs8PrivateKeyPem();

        var ex = Assert.Throws<InvalidOperationException>(() => new RsaAccessTokenIssuer(pem));
        Assert.Contains("2048", ex.Message, StringComparison.Ordinal);
    }
}

internal static class TestKeys
{
    public static string RsaPrivateKeyPem { get; } = Create();

    private static string Create()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
