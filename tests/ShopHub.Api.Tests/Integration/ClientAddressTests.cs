using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Tests.Integration;

/// <summary>
/// Research R7 through the middleware: forwarded headers count only from Traefik, and the
/// registration limit is per resulting client address (ported from shop 0.2.0's API tests).
/// </summary>
public sealed class ClientAddressTests
{
    private const string TraefikPod = "10.244.0.64";

    /// <summary>A pod in the pod network that is not Traefik.</summary>
    private const string OtherPod = "10.244.0.99";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestHost> StartAsync(IHostResolver resolver, int registrationsPerHour = 2) =>
        TestHost.StartAsync(app =>
        {
            app.MapGet("/whoami", (HttpContext ctx) => Results.Ok(new { address = ClientAddress.Of(ctx), scheme = ctx.Request.Scheme }));
            app.MapPost("/api/register", () => Results.NoContent()).RequireRateLimiting(RateLimits.Registration);
        }, new Dictionary<string, string?>
        {
            ["TRUSTED_PROXY_HOST"] = "traefik-proxies.traefik.svc.cluster.local",
            [RateLimits.RegistrationSetting] = registrationsPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, resolver);

    private sealed record WhoAmI(string Address, string Scheme);

    private static async Task<WhoAmI> WhoAmIAsync(TestHost host, string peer, string? forwardedFor, string? proto = null)
    {
        using var client = host.Browser(scheme: "http", peer: peer).Http;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/whoami", UriKind.Relative));
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (proto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", proto);
        }

        using var response = await client.SendAsync(request, Ct);
        return (await response.Content.ReadFromJsonAsync<WhoAmI>(Ct))!;
    }

    private static async Task<HttpStatusCode> RegisterAsync(TestHost host, string peer, string? forwardedFor)
    {
        using var client = host.Browser(peer: peer).Http;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/register", UriKind.Relative));
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task TraefikForwardsTheClientAndScheme()
    {
        await using var host = await StartAsync(new StaticResolver(TraefikPod));

        Assert.Equal(new WhoAmI("203.0.113.7", "https"), await WhoAmIAsync(host, TraefikPod, "203.0.113.7", "https"));
        // Only the last value counts: the one Traefik appended.
        Assert.Equal("203.0.113.7", (await WhoAmIAsync(host, TraefikPod, "192.0.2.1, 203.0.113.7")).Address);
    }

    [Fact]
    public async Task OtherPodsCannotChooseTheirAddress()
    {
        await using var host = await StartAsync(new StaticResolver(TraefikPod));

        Assert.Equal(new WhoAmI(OtherPod, "http"), await WhoAmIAsync(host, OtherPod, "203.0.113.7", "https"));
    }

    [Fact]
    public async Task AFailedLookupTrustsNobody()
    {
        await using var host = await StartAsync(new StaticResolver(TraefikPod) { Fail = true });

        Assert.Equal(TraefikPod, (await WhoAmIAsync(host, TraefikPod, "203.0.113.7")).Address);
    }

    /// <summary>Registration is limited per client address; forged headers from other pods don't help.</summary>
    [Fact]
    public async Task RegistrationIsLimitedPerClientAddress()
    {
        await using var host = await StartAsync(new StaticResolver(TraefikPod), registrationsPerHour: 2);

        Assert.Equal(HttpStatusCode.NoContent, await RegisterAsync(host, TraefikPod, "203.0.113.7"));
        Assert.Equal(HttpStatusCode.NoContent, await RegisterAsync(host, TraefikPod, "203.0.113.7"));
        Assert.Equal((HttpStatusCode)429, await RegisterAsync(host, TraefikPod, "203.0.113.7"));
        Assert.Equal(HttpStatusCode.NoContent, await RegisterAsync(host, TraefikPod, "198.51.100.9"));

        Assert.Equal(HttpStatusCode.NoContent, await RegisterAsync(host, OtherPod, "192.0.2.1"));
        Assert.Equal(HttpStatusCode.NoContent, await RegisterAsync(host, OtherPod, "192.0.2.2"));
        Assert.Equal((HttpStatusCode)429, await RegisterAsync(host, OtherPod, "192.0.2.3"));
    }

    [Fact]
    public async Task OverTheLimitIsProblemWithRetryAfter()
    {
        await using var host = await StartAsync(new StaticResolver(), registrationsPerHour: 1);
        using var client = host.Browser(peer: "203.0.113.7").Http;
        using (await client.PostAsync(new Uri("/api/register", UriKind.Relative), null, Ct))
        {
        }

        using var limited = await client.PostAsync(new Uri("/api/register", UriKind.Relative), null, Ct);

        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 3600);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
    }
}

/// <summary>Stands in for DNS: <c>TRUSTED_PROXY_HOST</c> resolves to the given addresses, or fails.</summary>
public sealed class StaticResolver(params string[] addresses) : IHostResolver
{
    public bool Fail { get; init; }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Fail
        ? throw new System.Net.Sockets.SocketException(11001)
        : Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
}
