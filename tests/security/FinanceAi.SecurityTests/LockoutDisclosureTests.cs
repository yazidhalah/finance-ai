using System.Diagnostics;
using System.Net.Http.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace FinanceAi.SecurityTests;

/// <summary>
/// Slice 1 review, fixed late (SEC-06): a lockout must never be judged before the password hash is
/// compared. The original <c>AuthenticateAsync</c> answered <c>Locked</c> for a locked address in
/// microseconds, before any Argon2id work, which made "this address exists and is locked" readable
/// from the response time and cheaper to probe than any other outcome.
/// <para>
/// The tests drive the store directly with the real hasher behind a counting decorator, so "a hash
/// comparison happened in this request" is a deterministic fact rather than a timing inference; the
/// timing assertion sits on top of it.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LockoutDisclosureTests(ApiTestFixture fixture, ITestOutputHelper output)
{
    private const string WrongPassword = "not the password, and long enough";

    /// <summary>1. A locked account with a wrong password and an unknown email take comparable time.</summary>
    [Fact]
    public async Task LockedAccount_WrongPassword_AndUnknownEmail_TakeComparableTime()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        await LockAsync(organization.OwnerUserId);
        var unknownEmail = $"nobody-{Guid.NewGuid():N}@example.jo";

        using var scope = fixture.Api.Services.CreateScope();
        var hasher = new CountingPasswordHasher();
        var identity = Store(scope, hasher);

        // Warm-up: connection pool, EF model, JIT — none of which is what is being measured.
        await identity.AuthenticateAsync(unknownEmail, WrongPassword, null, null);
        await identity.AuthenticateAsync(organization.OwnerEmail, WrongPassword, null, null);

        const int rounds = 7;
        var locked = new List<TimeSpan>(rounds);
        var unknown = new List<TimeSpan>(rounds);

        // Interleaved so a drift in the machine's load lands on both series alike.
        for (var round = 0; round < rounds; round++)
        {
            var (lockedResult, lockedElapsed) = await Timed(() => identity.AuthenticateAsync(organization.OwnerEmail, WrongPassword, null, null));
            var (unknownResult, unknownElapsed) = await Timed(() => identity.AuthenticateAsync(unknownEmail, WrongPassword, null, null));

            Assert.Equal(PlatformIdentityStore.AuthenticationOutcome.Locked, lockedResult.Outcome);
            Assert.Equal(PlatformIdentityStore.AuthenticationOutcome.Failed, unknownResult.Outcome);
            locked.Add(lockedElapsed);
            unknown.Add(unknownElapsed);
        }

        var lockedMedian = Median(locked);
        var unknownMedian = Median(unknown);
        output.WriteLine($"locked + wrong password: median {lockedMedian.TotalMilliseconds:F1} ms; unknown email: median {unknownMedian.TotalMilliseconds:F1} ms");

        // Both branches performed exactly one hash comparison per request: the locked branch against
        // the stored hash, the unknown-email branch against the fixed dummy hash.
        Assert.Equal(2 * (rounds + 1), hasher.Verifications);

        // Before the fix the locked branch skipped Argon2id entirely and answered an order of
        // magnitude faster than the unknown-email branch. With the hash comparison restored the two
        // differ only by the lookups around it; a factor of two is generous for the noise of a shared
        // runner and still far below the gap that was the oracle.
        Assert.True(
            lockedMedian >= unknownMedian / 2 && lockedMedian <= unknownMedian * 2,
            $"a locked account answered in {lockedMedian.TotalMilliseconds:F1} ms against {unknownMedian.TotalMilliseconds:F1} ms for an unknown email");
    }

    /// <summary>2. A locked account with the correct password still returns Locked, never Succeeded (AC-09).</summary>
    [Fact]
    public async Task LockedAccount_CorrectPassword_StillReturnsLocked()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        await LockAsync(organization.OwnerUserId);

        using var scope = fixture.Api.Services.CreateScope();
        var hasher = new CountingPasswordHasher();
        var identity = Store(scope, hasher);

        var refreshTokensBefore = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens WHERE user_id = @u", ("u", organization.OwnerUserId));
        var successesBefore = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = @e", ("t", organization.TenantId), ("e", AuditEventTypes.LoginSucceeded));

        var result = await identity.AuthenticateAsync(organization.OwnerEmail, ApiScenario.ValidPassword, null, null);

        Assert.Equal(PlatformIdentityStore.AuthenticationOutcome.Locked, result.Outcome);
        Assert.Null(result.Session);
        Assert.NotNull(result.LockedUntil);
        Assert.Equal(1, hasher.Verifications);   // the password was compared — and matched — and the lock still won

        // No session was issued and no login.succeeded row was written: the lock is a hard stop.
        Assert.Equal(refreshTokensBefore, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens WHERE user_id = @u", ("u", organization.OwnerUserId)));
        Assert.Equal(successesBefore, await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = @e", ("t", organization.TenantId), ("e", AuditEventTypes.LoginSucceeded)));

        // And over HTTP the same request is 423, the same as the ten-failure path LoginTests proves.
        using var client = fixture.Api.CreateCookieClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = organization.OwnerEmail, password = ApiScenario.ValidPassword }, ApiScenario.Json);
        Assert.Equal(System.Net.HttpStatusCode.Locked, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    /// <summary>3. LockedUntil is never returned unless a real password hash comparison occurred in this request.</summary>
    [Fact]
    public async Task LockedUntil_IsOnlyReturnedAfterAHashComparison()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        var unknownEmail = $"nobody-{Guid.NewGuid():N}@example.jo";

        using var scope = fixture.Api.Services.CreateScope();
        var hasher = new CountingPasswordHasher();
        var identity = Store(scope, hasher);

        // Every way of reaching the store, before and after the lock, with a right and a wrong
        // password and with no account at all. Whatever the outcome, a LockedUntil in the result
        // means this very call compared a hash.
        var attempts = new List<(string Email, string Password)>
        {
            (organization.OwnerEmail, WrongPassword),
            (unknownEmail, WrongPassword),
            (unknownEmail, ApiScenario.ValidPassword),
        };

        await LockAsync(organization.OwnerUserId);
        attempts.Add((organization.OwnerEmail, WrongPassword));
        attempts.Add((organization.OwnerEmail, ApiScenario.ValidPassword));
        attempts.Add((unknownEmail, WrongPassword));

        var sawLockedUntil = false;
        foreach (var (email, password) in attempts)
        {
            var before = hasher.Verifications;
            var result = await identity.AuthenticateAsync(email, password, null, null);
            var comparisons = hasher.Verifications - before;

            Assert.Equal(1, comparisons);   // one per request, on every path — that is what makes them indistinguishable
            if (result.LockedUntil is not null)
            {
                sawLockedUntil = true;
                Assert.Equal(PlatformIdentityStore.AuthenticationOutcome.Locked, result.Outcome);
                Assert.Equal(1, comparisons);
            }
            else
            {
                Assert.NotEqual(PlatformIdentityStore.AuthenticationOutcome.Locked, result.Outcome);
            }
        }

        Assert.True(sawLockedUntil, "the locked attempts should have produced LockedUntil, or the assertion proved nothing");

        // The strongest form: a hasher that refuses to compare is a request in which no comparison
        // occurred, and such a request must not be able to see LockedUntil — it must not even reach
        // the lock. Before the fix this returned Locked with LockedUntil set without touching the hasher.
        var refusing = Store(scope, new ThrowingPasswordHasher());
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.AuthenticateAsync(organization.OwnerEmail, WrongPassword, null, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.AuthenticateAsync(organization.OwnerEmail, ApiScenario.ValidPassword, null, null));
    }

    private static PlatformIdentityStore Store(IServiceScope scope, IPasswordHasher hasher) =>
        new(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<TenantDbContext>>(),
            hasher,
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            scope.ServiceProvider.GetRequiredService<ISecretBox>());

    /// <summary>Puts the account into the state ten failures would leave it in (SEC-06), without spending ten Argon2id rounds.</summary>
    private Task LockAsync(Guid userId) =>
        fixture.Database.ExecuteAsync(
            "UPDATE users SET failed_login_count = @n, locked_until = now() + interval '15 minutes' WHERE id = @u",
            ("n", PlatformIdentityStore.MaxFailedLogins), ("u", userId));

    private static async Task<(T Result, TimeSpan Elapsed)> Timed<T>(Func<Task<T>> action)
    {
        var watch = Stopwatch.StartNew();
        var result = await action();
        return (result, watch.Elapsed);
    }

    private static TimeSpan Median(List<TimeSpan> samples)
    {
        var sorted = samples.OrderBy(s => s).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>The real hasher, counting every comparison it is asked for.</summary>
    private sealed class CountingPasswordHasher : IPasswordHasher
    {
        private readonly Argon2idPasswordHasher inner = new();
        private int verifications;

        public int Verifications => Volatile.Read(ref this.verifications);

        public string Hash(string password) => this.inner.Hash(password);

        public bool Verify(string password, string encoded)
        {
            Interlocked.Increment(ref this.verifications);
            return this.inner.Verify(password, encoded);
        }
    }

    /// <summary>A hasher that cannot compare: any outcome reached with it was reached without a comparison.</summary>
    private sealed class ThrowingPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => throw new InvalidOperationException("no hashing in this test");

        public bool Verify(string password, string encoded) => throw new InvalidOperationException("the lock was judged before the password");
    }
}
