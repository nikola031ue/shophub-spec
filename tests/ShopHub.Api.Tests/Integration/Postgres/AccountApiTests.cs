using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShopHub.Api.Accounts;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>Counts PBKDF2 operations, so "same cost on every path" is asserted without a clock.</summary>
public sealed class CountingHasher : IPasswordHasher<OwnerUser>
{
    private readonly PasswordHasher<OwnerUser> _inner = new();
    private int _hashes;
    private int _verifies;

    public int Hashes => _hashes;

    public int Verifies => _verifies;

    public void Reset()
    {
        Interlocked.Exchange(ref _hashes, 0);
        Interlocked.Exchange(ref _verifies, 0);
    }

    public string HashPassword(OwnerUser user, string password)
    {
        Interlocked.Increment(ref _hashes);
        return _inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(OwnerUser user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _verifies);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

/// <summary>
/// T015 accounts API on PostgreSQL (contracts/panel-api.md "Accounts", FR-001 to FR-004): sessions,
/// cookies, lockout per (account, address) with the per-account ceiling, password change ending the
/// owner's other sessions, and no account enumeration.
/// </summary>
public sealed class AccountApiTests(PostgresContainer db) : IClassFixture<PostgresContainer>
{
    private const string Password = "correct-horse-battery";
    private const string AddrA = "203.0.113.7";
    private const string AddrB = "198.51.100.9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Dictionary<string, string?>> FreshEnvAsync(int registrationsPerHour = 1000) => new()
    {
        ["DATABASE_URL"] = await db.CreateDatabaseUrlAsync("acct_" + Guid.NewGuid().ToString("N")),
        [RateLimits.RegistrationSetting] = registrationsPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>A browser at <paramref name="peer"/> that has loaded the panel (so it holds an XSRF token).</summary>
    private static async Task<BrowserClient> BrowserAsync(ShopHubApp app, string peer = AddrA, string scheme = "https")
    {
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri($"{scheme}://localhost") });
        http.DefaultRequestHeaders.Add(ShopHubApp.PeerHeader, peer);
        var client = new BrowserClient(http);
        using var session = await client.SendAsync(HttpMethod.Get, "/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        return client;
    }

    private static async Task<HttpStatusCode> RegisterAsync(ShopHubApp app, string email, string password = Password, string peer = AddrA)
    {
        using var client = await BrowserAsync(app, peer);
        using var response = await client.SendAsync(HttpMethod.Post, "/api/register", new { email, password });
        return response.StatusCode;
    }

    private static async Task<(HttpStatusCode Status, BrowserClient Client)> SignInAsync(
        ShopHubApp app, string email, string password = Password, string peer = AddrA)
    {
        var client = await BrowserAsync(app, peer);
        using var response = await client.SendAsync(HttpMethod.Post, "/api/signin", new { email, password });
        return (response.StatusCode, client);
    }

    private static async Task<BrowserClient> SignedInAsync(ShopHubApp app, string email, string password = Password, string peer = AddrA)
    {
        var (status, client) = await SignInAsync(app, email, password, peer);
        Assert.Equal(HttpStatusCode.NoContent, status);
        return client;
    }

    private sealed record SessionJson(bool SignedIn, string? Email);

    private static async Task<SessionJson> SessionAsync(BrowserClient client)
    {
        using var response = await client.SendAsync(HttpMethod.Get, "/api/session");
        return (await response.Content.ReadFromJsonAsync<SessionJson>(Ct))!;
    }

    /// <summary>Session state seen by someone holding only <paramref name="cookie"/>.</summary>
    private static async Task<SessionJson> SessionWithCookieAsync(ShopHubApp app, string? cookie)
    {
        using var raw = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/session", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        using var response = await raw.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync<SessionJson>(Ct))!;
    }

    /// <summary>Response body without the per-request <c>traceId</c>, for comparing two responses.</summary>
    private static async Task<string> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (text.Length == 0)
        {
            return "";
        }

        var node = JsonNode.Parse(text)!.AsObject();
        node.Remove("traceId");
        return node.ToJsonString();
    }

    private static bool SetsSessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(Sessions.Cookie + "=", StringComparison.Ordinal));

    [Fact]
    public async Task RegisterThenSignInGivesASession()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        using var client = await BrowserAsync(app);

        using var register = await client.SendAsync(HttpMethod.Post, "/api/register", new { email = " Ana@Example.test ", password = Password });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        Assert.Equal("", await register.Content.ReadAsStringAsync(Ct));
        Assert.False(SetsSessionCookie(register));
        Assert.False((await SessionAsync(client)).SignedIn);

        using var signIn = await client.SendAsync(HttpMethod.Post, "/api/signin", new { email = "ana@EXAMPLE.test", password = Password });
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        Assert.Equal(new SessionJson(true, "Ana@Example.test"), await SessionAsync(client));
    }

    [Theory]
    [InlineData("no-at-sign", Password, "email")]
    [InlineData("a@b@c", Password, "email")]
    [InlineData("@example.test", Password, "email")]
    [InlineData("a@example.test", "short-11chr", "password")]
    public async Task RegistrationIsValidated(string email, string password, string field)
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        using var client = await BrowserAsync(app);

        using var response = await client.SendAsync(HttpMethod.Post, "/api/register", new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task LongEmailAndPasswordLimitsAreEnforced()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());

        Assert.Equal(HttpStatusCode.BadRequest, await RegisterAsync(app, new string('a', 243) + "@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, await RegisterAsync(app, new string('a', 241) + "@example.test"));
        Assert.Equal(HttpStatusCode.BadRequest, await RegisterAsync(app, "b@example.test", new string('p', 201)));
        Assert.Equal(HttpStatusCode.Accepted, await RegisterAsync(app, "c@example.test", new string('p', 200)));
    }

    /// <summary>FR-004: the session cookie is Secure, HttpOnly and SameSite=Strict, even on a plain-HTTP request.</summary>
    [Fact]
    public async Task SessionCookieFlags()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "flags@example.test");
        using var client = await BrowserAsync(app, scheme: "http");

        using var signIn = await client.SendAsync(HttpMethod.Post, "/api/signin", new { email = "flags@example.test", password = Password });

        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        var session = Assert.Single(client.LastSetCookies, c => c.StartsWith(Sessions.Cookie + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("secure", session, StringComparison.Ordinal);
        Assert.Contains("httponly", session, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", session, StringComparison.Ordinal);
        Assert.All(client.LastSetCookies, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StateChangesNeedTheXsrfHeader()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        using var raw = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using var response = await raw.PostAsJsonAsync(new Uri("/api/register", UriKind.Relative),
            new { email = "x@example.test", password = Password }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>FR-002: sign-out ends that session only; a replayed copy of its cookie stops working.</summary>
    [Fact]
    public async Task SignOutEndsOnlyThatSession()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "out@example.test");
        using var laptop = await SignedInAsync(app, "out@example.test");
        using var phone = await SignedInAsync(app, "out@example.test");
        var copied = laptop.Cookie(Sessions.Cookie);

        using var signOut = await laptop.SendAsync(HttpMethod.Post, "/api/signout");

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.False((await SessionWithCookieAsync(app, copied)).SignedIn);
        Assert.True((await SessionAsync(phone)).SignedIn);
    }

    /// <summary>
    /// FR-002: a password change ends every other session of the owner, including one whose cookie
    /// was stolen; the session that made the change stays, and the new password is the one that works.
    /// </summary>
    [Fact]
    public async Task PasswordChangeEndsOtherSessionsIncludingAStolenCookie()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "pw@example.test");
        await RegisterAsync(app, "other@example.test");
        using var current = await SignedInAsync(app, "pw@example.test");
        using var victim = await SignedInAsync(app, "pw@example.test", peer: AddrB);
        using var someoneElse = await SignedInAsync(app, "other@example.test");
        var stolen = victim.Cookie(Sessions.Cookie);
        Assert.True((await SessionWithCookieAsync(app, stolen)).SignedIn);

        using var change = await current.SendAsync(HttpMethod.Post, "/api/password",
            new { currentPassword = Password, newPassword = "a-brand-new-password" });

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.False((await SessionWithCookieAsync(app, stolen)).SignedIn);
        Assert.False((await SessionAsync(victim)).SignedIn);
        Assert.True((await SessionAsync(current)).SignedIn);
        Assert.True((await SessionAsync(someoneElse)).SignedIn);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(app, "pw@example.test", Password, AddrB)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(app, "pw@example.test", "a-brand-new-password", AddrB)).Status);
    }

    [Fact]
    public async Task WrongCurrentPasswordCountsTowardsTheLockout()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "cur@example.test");
        using var client = await SignedInAsync(app, "cur@example.test");

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await client.SendAsync(HttpMethod.Post, "/api/password",
                new { currentPassword = "wrong-" + i, newPassword = "a-brand-new-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await client.SendAsync(HttpMethod.Post, "/api/password",
            new { currentPassword = Password, newPassword = "a-brand-new-password" });
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        // Same (account, address) scope as sign-in: signing in from there is locked too.
        Assert.Equal((HttpStatusCode)429, (await SignInAsync(app, "cur@example.test")).Status);
    }

    /// <summary>
    /// FR-003: 5 failures for X from A lock X from A only. X from B signs in, and Y from A signs in
    /// (failures for one account don't lock another account from the same address).
    /// </summary>
    [Fact]
    public async Task LockoutIsPerAccountAndAddress()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "x@example.test");
        await RegisterAsync(app, "y@example.test");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(app, "x@example.test", "wrong-" + i)).Status);
        }

        var client = await BrowserAsync(app);
        using var locked = await client.SendAsync(HttpMethod.Post, "/api/signin", new { email = "x@example.test", password = Password });
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.InRange(int.Parse(locked.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 890, 900);
        client.Dispose();

        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(app, "x@example.test", Password, AddrB)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(app, "y@example.test", Password, AddrA)).Status);
    }

    /// <summary>The per-account ceiling: 50 failures for X across addresses lock X everywhere, not Y.</summary>
    [Fact]
    public async Task FiftyFailuresAcrossAddressesLockThatAccountOnly()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "x@example.test");
        await RegisterAsync(app, "y@example.test");
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(app, "x@example.test", "wrong", $"192.0.2.{i / 2}")).Status);
        }

        Assert.Equal((HttpStatusCode)429, (await SignInAsync(app, "x@example.test", Password, AddrB)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(app, "y@example.test", Password, AddrB)).Status);
    }

    [Fact]
    public async Task RegistrationIsRateLimitedPerAddress()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync(registrationsPerHour: 2));

        Assert.Equal(HttpStatusCode.Accepted, await RegisterAsync(app, "r1@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, await RegisterAsync(app, "r2@example.test"));
        Assert.Equal((HttpStatusCode)429, await RegisterAsync(app, "r3@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, await RegisterAsync(app, "r3@example.test", peer: AddrB));
    }

    /// <summary>
    /// FR-001 no enumeration: registering a taken email answers exactly like a new one, leaves the
    /// account untouched, and costs the same one hash.
    /// </summary>
    [Fact]
    public async Task RegistrationDoesNotRevealExistingAccounts()
    {
        var hasher = new CountingHasher();
        await using var app = new ShopHubApp(await FreshEnvAsync(), hasher: hasher);
        Assert.Equal(HttpStatusCode.Accepted, await RegisterAsync(app, "taken@example.test"));

        async Task<(HttpResponseMessage Response, int Hashes, int Verifies)> RegisterCountedAsync(string email)
        {
            using var client = await BrowserAsync(app);
            hasher.Reset();
            var response = await client.SendAsync(HttpMethod.Post, "/api/register", new { email, password = "another-password-1" });
            return (response, hasher.Hashes, hasher.Verifies);
        }

        var (fresh, freshHashes, freshVerifies) = await RegisterCountedAsync("fresh@example.test");
        var (taken, takenHashes, takenVerifies) = await RegisterCountedAsync("TAKEN@example.test");
        using (fresh)
        using (taken)
        {
            Assert.Equal(fresh.StatusCode, taken.StatusCode);
            Assert.Equal(await BodyAsync(fresh), await BodyAsync(taken));
            Assert.False(SetsSessionCookie(fresh));
            Assert.False(SetsSessionCookie(taken));
            Assert.Equal(fresh.Content.Headers.ContentType, taken.Content.Headers.ContentType);
        }

        Assert.Equal((1, 0), (freshHashes, freshVerifies));
        Assert.Equal((1, 0), (takenHashes, takenVerifies));

        // The existing account kept its password.
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(app, "taken@example.test", Password)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(app, "taken@example.test", "another-password-1", AddrB)).Status);
    }

    /// <summary>FR-001 no enumeration: an unknown email gets the wrong-password answer, at the cost of one verify.</summary>
    [Fact]
    public async Task SignInDoesNotRevealWhetherTheEmailExists()
    {
        var hasher = new CountingHasher();
        await using var app = new ShopHubApp(await FreshEnvAsync(), hasher: hasher);
        await RegisterAsync(app, "known@example.test");

        async Task<(HttpResponseMessage Response, int Hashes, int Verifies)> SignInCountedAsync(string email, string peer)
        {
            var client = await BrowserAsync(app, peer);
            hasher.Reset();
            var response = await client.SendAsync(HttpMethod.Post, "/api/signin", new { email, password = "not-the-password" });
            client.Dispose();
            return (response, hasher.Hashes, hasher.Verifies);
        }

        var (unknown, unknownHashes, unknownVerifies) = await SignInCountedAsync("nobody@example.test", AddrA);
        var (wrong, wrongHashes, wrongVerifies) = await SignInCountedAsync("known@example.test", AddrB);
        using (unknown)
        using (wrong)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            Assert.Equal(wrong.StatusCode, unknown.StatusCode);
            Assert.Equal(await BodyAsync(wrong), await BodyAsync(unknown));
            Assert.Equal(wrong.Content.Headers.ContentType, unknown.Content.Headers.ContentType);
        }

        // The hash work (the only costly part) is identical: one verify each, no hashing.
        Assert.Equal((0, 1), (unknownHashes, unknownVerifies));
        Assert.Equal((0, 1), (wrongHashes, wrongVerifies));
    }

    /// <summary>
    /// Coarse wall-clock guard only (CI noise): over 20 requests each, the medians of the paired paths
    /// are within a factor of 5. The exact assertion is the hash count above. Distinct addresses and
    /// emails keep the lockout out of the way.
    /// </summary>
    [Fact]
    public async Task PairedPathsTakeSimilarTime()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "known@example.test");

        async Task<double> MedianMsAsync(Func<int, Task<HttpStatusCode>> call, HttpStatusCode expected)
        {
            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                Assert.Equal(expected, await call(i));
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            return (times[9] + times[10]) / 2;
        }

        // Each call includes loading the panel (GET /api/session), the same for both sides of a pair.
        var newReg = await MedianMsAsync(i => RegisterAsync(app, $"new{i}@example.test", peer: $"10.1.0.{i}"), HttpStatusCode.Accepted);
        var takenReg = await MedianMsAsync(i => RegisterAsync(app, "known@example.test", peer: $"10.2.0.{i}"), HttpStatusCode.Accepted);
        var unknown = await MedianMsAsync(async i => (await SignInAsync(app, $"nobody{i}@example.test", "not-the-password", $"10.3.0.{i}")).Status, HttpStatusCode.Unauthorized);
        var wrong = await MedianMsAsync(async i => (await SignInAsync(app, "known@example.test", "not-the-password", $"10.4.0.{i}")).Status, HttpStatusCode.Unauthorized);

        Assert.InRange(newReg / takenReg, 0.2, 5);
        Assert.InRange(unknown / wrong, 0.2, 5);
    }

    /// <summary>Sessions and Data-Protection keys are in the database: another replica accepts the cookie.</summary>
    [Fact]
    public async Task SessionWorksOnAnotherReplica()
    {
        var env = await FreshEnvAsync();
        await using var first = new ShopHubApp(env);
        await using var second = new ShopHubApp(env);
        await RegisterAsync(first, "r@example.test");
        using var client = await SignedInAsync(first, "r@example.test");

        Assert.True((await SessionWithCookieAsync(second, client.Cookie(Sessions.Cookie))).SignedIn);
    }

    [Fact]
    public async Task ExpiredSessionsAreRefused()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        await RegisterAsync(app, "exp@example.test");
        using var client = await SignedInAsync(app, "exp@example.test");

        var source = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var cmd = source.CreateCommand("UPDATE owner_sessions SET created_at = now() - interval '9 hours', expires_at = now() - interval '1 hour'"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        Assert.False((await SessionAsync(client)).SignedIn);
    }

    [Fact]
    public async Task LogsContainNoPasswordsOrHashes()
    {
        var logs = new LogSink();
        await using var app = new ShopHubApp(await FreshEnvAsync(), logs);
        await RegisterAsync(app, "log@example.test");
        await SignInAsync(app, "log@example.test", "wrong-password-xyz");
        using var client = await SignedInAsync(app, "log@example.test");
        using (await client.SendAsync(HttpMethod.Post, "/api/password", new { currentPassword = Password, newPassword = "new-password-abc" }))
        {
        }

        var hash = await HashOfAsync(app, "log@example.test");
        var all = string.Join('\n', logs.Lines);
        foreach (var secret in new[] { Password, "wrong-password-xyz", "new-password-abc", hash, db.Password })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    private static async Task<string> HashOfAsync(ShopHubApp app, string email)
    {
        var source = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using var cmd = source.CreateCommand("SELECT password_hash FROM owners WHERE email_normalized = $1");
        cmd.Parameters.AddWithValue(email);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
