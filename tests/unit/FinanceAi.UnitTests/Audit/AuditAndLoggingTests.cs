using System.Net;
using FinanceAi.Api.Logging;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;

namespace FinanceAi.UnitTests.Audit;

/// <summary>AC-37 / SEC-53, at the hash level. The database half is proved by the security suite.</summary>
public sealed class AuditHashTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var e = SampleEvent();
        Assert.Equal(AuditHash.Compute(e, "prev"), AuditHash.Compute(e, "prev"));
    }

    [Fact]
    public void Compute_ChangesWhenAnyAuditedFieldChanges()
    {
        var baseline = AuditHash.Compute(SampleEvent(), "prev");

        var mutations = new List<(string Field, Action<AuditEvent> Mutate)>
        {
            ("eventType", e => e.EventType = "auth.login_failed"),
            ("entityId", e => e.EntityId = Guid.NewGuid()),
            ("entityType", e => e.EntityType = "tenant"),
            ("actorUserId", e => e.ActorUserId = Guid.NewGuid()),
            ("actorKind", e => e.ActorKind = ActorKinds.System),
            ("actorIp", e => e.ActorIp = IPAddress.Parse("10.0.0.1")),
            ("tenantId", e => e.TenantId = Guid.NewGuid()),
            ("occurredAt", e => e.OccurredAt = e.OccurredAt.AddSeconds(1)),
            ("fromState", e => e.FromState = "Open"),
            ("toState", e => e.ToState = "Closed"),
            ("reasonCode", e => e.ReasonCode = "changed"),
            ("note", e => e.Note = "changed"),
            ("changes", e => e.Changes = """{"name":{"old":"a","new":"b"}}"""),
            ("requestId", e => e.RequestId = "changed"),
        };

        foreach (var (field, mutate) in mutations)
        {
            var mutated = SampleEvent();
            mutate(mutated);

            Assert.True(
                baseline != AuditHash.Compute(mutated, "prev"),
                $"Changing '{field}' did not change the hash — it is outside the tamper-evident chain.");
        }
    }

    [Fact]
    public void Compute_ChainsOnThePreviousHash()
    {
        var e = SampleEvent();
        Assert.NotEqual(AuditHash.Compute(e, "prev-a"), AuditHash.Compute(e, "prev-b"));
        Assert.NotEqual(AuditHash.Compute(e, null), AuditHash.Compute(e, "prev-a"));
    }

    [Fact]
    public void TruncateToStorage_MatchesPostgresMicrosecondPrecision()
    {
        // 100ns ticks in .NET, microseconds in timestamptz. Without truncation, every chain would
        // "fail" verification for a rounding reason rather than a tampering one.
        var value = new DateTimeOffset(2026, 9, 10, 7, 30, 0, TimeSpan.Zero).AddTicks(1234567);
        var truncated = AuditHash.TruncateToStorage(value);

        Assert.Equal(0, truncated.Ticks % 10);
        Assert.Equal(truncated, AuditHash.TruncateToStorage(truncated));
    }

    private static AuditEvent SampleEvent() => new()
    {
        TenantId = Guid.Parse("018f0000-0000-7000-8000-000000000001"),
        OccurredAt = new DateTimeOffset(2026, 9, 10, 7, 30, 0, TimeSpan.Zero),
        ActorUserId = Guid.Parse("018f0000-0000-7000-8000-000000000002"),
        ActorKind = ActorKinds.User,
        EventType = AuditEventTypes.LoginSucceeded,
        EntityType = "user",
        EntityId = Guid.Parse("018f0000-0000-7000-8000-000000000002"),
        RequestId = "request-1",
    };
}

/// <summary>AC-42 / SEC-41 / T-82.</summary>
public sealed class SensitiveDataTests
{
    [Fact]
    public void LogPipeline_RedactsSecrets()
    {
        const string Password = "correct horse battery staple";
        const string Iban = "JO94CBJO0010000000000131000302";
        const string Card = "4111111111111111";
        const string NationalId = "9901234567";
        const string Argon = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdHNhbHRzYWx0c2E=$aGFzaGhhc2hoYXNoaGFzaA==";

        var payload = $$"""
            {"password":"{{Password}}","iban":"{{Iban}}","card":"{{Card}}",
             "nationalId":"{{NationalId}}","passwordHash":"{{Argon}}",
             "authorization":"Bearer eyJhbGciOiJSUzI1NiJ9.payload.signature"}
            """;

        var redacted = SensitiveData.Redact(payload);

        Assert.DoesNotContain(Password, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Iban, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(Card, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(NationalId, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2id", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("passwordHash")]
    [InlineData("password_hash")]
    [InlineData("refreshToken")]
    [InlineData("Authorization")]
    [InlineData("smtp_password")]
    [InlineData("mfaSecret")]
    public void IsSensitiveKey_RecognizesCredentialFieldNames(string key) =>
        Assert.True(SensitiveData.IsSensitiveKey(key));

    [Theory]
    [InlineData("fullName")]
    [InlineData("invoiceNumber")]
    [InlineData("tenantId")]
    public void IsSensitiveKey_LeavesOrdinaryFieldsAlone(string key) =>
        Assert.False(SensitiveData.IsSensitiveKey(key));

    [Fact]
    public void Redact_LeavesNonSensitiveTextIntact()
    {
        const string Message = "Registration processed for organization Al Amal Trading Co.";
        Assert.Equal(Message, SensitiveData.Redact(Message));
    }
}

/// <summary>AC-40 / API-13: the specified default is what ships when nothing is configured.</summary>
public sealed class RateLimitDefaultTests
{
    [Fact]
    public void AuthRateLimit_DefaultsToTenPerMinute()
    {
        Assert.Equal(10, RateLimitPolicies.DefaultAuthPermitsPerMinute);
        Assert.Equal(600, RateLimitPolicies.DefaultGeneralPermitsPerMinute);
    }
}
