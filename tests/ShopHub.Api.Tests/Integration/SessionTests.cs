using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Tests.Integration;

/// <summary>
/// FR-002/FR-004 session plumbing (ported from shop's session-version checks, adapted to per-session
/// ids): sign-out ends only that session, a password change ends every other session of the owner,
/// copied cookies stop working, and the cookies carry Secure, HttpOnly and SameSite=Strict.
/// </summary>
public sealed class SessionTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("0193a5e0-0000-7000-8000-000000000001");
    private static readonly Guid OtherOwner = Guid.Parse("0193a5e0-0000-7000-8000-000000000002");

    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(app =>
    {
        // Test-only stand-ins for the T015 endpoints: sign-in creates a session row for ?owner=.
        app.MapPost("/signin", async (Guid owner, HttpContext ctx, InMemorySessionStore store) =>
        {
            await Sessions.SignInAsync(ctx, owner, store.Create(owner));
            return Results.NoContent();
        });
        var api = app.MapGroup("/api").RequireAuthorization(Sessions.Policy);
        api.MapGet("/me", (HttpContext ctx) => Results.Ok(new { owner = Sessions.OwnerId(ctx.User) }));
        api.MapPost("/signout", async (HttpContext ctx, CancellationToken ct) =>
        {
            await Sessions.SignOutAsync(ctx, ct);
            return Results.NoContent();
        }).RequireXsrf();
        api.MapPost("/password", async (HttpContext ctx, CancellationToken ct) =>
        {
            await Sessions.EndOtherSessionsAsync(ctx, ct);
            return Results.NoContent();
        }).RequireXsrf();
    });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<BrowserClient> SignedInAsync(Guid owner)
    {
        var client = _host.Browser();
        using var response = await client.SendAsync(HttpMethod.Post, $"/signin?owner={owner}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    private static async Task<HttpStatusCode> MeAsync(BrowserClient client)
    {
        using var response = await client.SendAsync(HttpMethod.Get, "/api/me");
        return response.StatusCode;
    }

    /// <summary>Sends only a copied session cookie, as an attacker holding it would.</summary>
    private async Task<HttpStatusCode> MeWithCookieAsync(string? cookie)
    {
        using var raw = _host.Browser().Http;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/me", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        using var response = await raw.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task AnonymousRequestsAreRefused()
    {
        using var anonymous = _host.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(anonymous));
    }

    /// <summary>FR-004: the three flags on the session cookie, and Secure on every cookie, even over plain HTTP.</summary>
    [Fact]
    public async Task CookiesAreSecureHttpOnlyAndStrict()
    {
        using var client = _host.Browser(scheme: "http");
        using var response = await client.SendAsync(HttpMethod.Post, $"/signin?owner={Owner}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        string Find(string name) =>
            Assert.Single(client.LastSetCookies, c => c.StartsWith(name + "=", StringComparison.Ordinal)).ToLowerInvariant();
        var session = Find(Sessions.Cookie);
        Assert.Contains("secure", session, StringComparison.Ordinal);
        Assert.Contains("httponly", session, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", session, StringComparison.Ordinal);
        Assert.Contains("secure", Find("shophub.af"), StringComparison.Ordinal);
        Assert.Contains("secure", Find(Sessions.XsrfCookie), StringComparison.Ordinal);
        Assert.DoesNotContain("httponly", Find(Sessions.XsrfCookie), StringComparison.Ordinal);
    }

    /// <summary>FR-002: sign-out ends that session only; a replayed copy of its cookie stops working.</summary>
    [Fact]
    public async Task SignOutEndsOnlyThatSession()
    {
        using var laptop = await SignedInAsync(Owner);
        using var phone = await SignedInAsync(Owner);
        var copied = laptop.Cookie(Sessions.Cookie);
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(copied));

        using var signOut = await laptop.SendAsync(HttpMethod.Post, "/api/signout");
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(laptop));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(copied));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(phone));
    }

    /// <summary>
    /// A password change ends every other session of that owner, including one whose cookie was
    /// stolen; the session that changed the password and other owners' sessions stay.
    /// </summary>
    [Fact]
    public async Task PasswordChangeEndsTheOwnersOtherSessionsIncludingAStolenCookie()
    {
        using var current = await SignedInAsync(Owner);
        using var other = await SignedInAsync(Owner);
        using var someoneElse = await SignedInAsync(OtherOwner);
        var stolen = other.Cookie(Sessions.Cookie);
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(stolen));

        using var change = await current.SendAsync(HttpMethod.Post, "/api/password");
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(stolen));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(other));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(current));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(someoneElse));
        Assert.Equal(1, _host.Services.GetRequiredService<InMemorySessionStore>().Count(Owner));
    }

    [Fact]
    public async Task StateChangesWithoutXsrfHeaderAreRefused()
    {
        using var client = await SignedInAsync(Owner);
        using var raw = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/password", UriKind.Relative));
        raw.Headers.Add("Cookie", client.Cookie(Sessions.Cookie) + "; " + client.Cookie("shophub.af"));

        using var response = await client.Http.SendAsync(raw, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(
            TestContext.Current.CancellationToken)).GetProperty("status").GetInt32());
    }
}
