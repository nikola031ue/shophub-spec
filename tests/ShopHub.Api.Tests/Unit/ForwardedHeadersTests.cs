using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace ShopHub.Api.Tests.Unit;

/// <summary>Spec 004 FR-022: settings, header parsing and the TRUSTED_PROXY_HOST refresh.</summary>
public sealed class ForwardedHeadersTests
{
    private static Dictionary<string, string?> Env(string? proxyHost) => new()
    {
        ["DATABASE_URL"] = "postgresql://app:pw@shophub-db-rw.shophub.svc:5432/app",
        ["TRUSTED_PROXY_HOST"] = proxyHost,
    };

    private static Settings Load(string? proxyHost) => Settings.Load(k => Env(proxyHost).GetValueOrDefault(k));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("traefik-proxies.traefik.svc.cluster.local", "traefik-proxies.traefik.svc.cluster.local")]
    [InlineData(" 10.244.0.64 ", "10.244.0.64")]
    public void ProxyHostIsOptional(string? value, string? expected) =>
        Assert.Equal(expected, Load(value).TrustedProxyHost);

    [Fact]
    public void InvalidProxyHostStopsTheStart()
    {
        var ex = Assert.Throws<SettingsException>(() => Load("not a host/"));
        Assert.Contains("TRUSTED_PROXY_HOST", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "203.0.113.7" }, "203.0.113.7")]
    [InlineData(new[] { "198.51.100.1, 203.0.113.7" }, "203.0.113.7")]
    [InlineData(new[] { "198.51.100.1", "203.0.113.7 , " }, "203.0.113.7")]
    [InlineData(new[] { "203.0.113.7", "" }, "203.0.113.7")]
    public void OnlyTheLastForwardedValueCounts(string[] lines, string expected) =>
        Assert.Equal(expected, TrustedForwardedHeaders.Last(new StringValues(lines)));

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("203.0.113.7:51234", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    public void ForwardedAddressesAreParsed(string value, string? expected) =>
        Assert.Equal(expected, TrustedForwardedHeaders.ParseAddress(value)?.ToString());

    [Fact]
    public void MappedPeersMatchTheirIPv4Address()
    {
        var proxies = new TrustedProxies();
        proxies.Set([IPAddress.Parse("10.244.0.64")]);

        Assert.True(proxies.IsTrusted(IPAddress.Parse("::ffff:10.244.0.64")));
        Assert.False(proxies.IsTrusted(IPAddress.Parse("10.244.0.65")));
        Assert.False(proxies.IsTrusted(null));
    }

    [Fact]
    public async Task RefreshFollowsDnsAndFailsClosed()
    {
        var resolver = new StubResolver { Result = [IPAddress.Parse("10.244.0.64")] };
        var proxies = new TrustedProxies();
        var refresher = new TrustedProxyRefresher(Load("traefik-proxies.test"), resolver, proxies,
            new FakeTimeProvider(), NullLogger<TrustedProxyRefresher>.Instance);
        var ct = TestContext.Current.CancellationToken;

        await refresher.RefreshAsync(ct);
        Assert.True(proxies.IsTrusted(IPAddress.Parse("10.244.0.64")));

        // Traefik moved to a new pod.
        resolver.Result = [IPAddress.Parse("10.244.0.70")];
        await refresher.RefreshAsync(ct);
        Assert.False(proxies.IsTrusted(IPAddress.Parse("10.244.0.64")));
        Assert.True(proxies.IsTrusted(IPAddress.Parse("10.244.0.70")));

        // A failed lookup trusts nobody.
        resolver.Fail = true;
        await refresher.RefreshAsync(ct);
        Assert.Empty(proxies.Addresses);
        Assert.Equal(["traefik-proxies.test", "traefik-proxies.test", "traefik-proxies.test"], resolver.Hosts);
    }

    [Fact]
    public async Task NoProxyHostMeansNoLookupAndNoTrust()
    {
        var resolver = new StubResolver { Result = [IPAddress.Parse("10.244.0.64")] };
        var proxies = new TrustedProxies();
        var refresher = new TrustedProxyRefresher(Load(null), resolver, proxies,
            new FakeTimeProvider(), NullLogger<TrustedProxyRefresher>.Instance);

        await refresher.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(resolver.Hosts);
        Assert.Empty(proxies.Addresses);
    }

    private sealed class StubResolver : IHostResolver
    {
        public IPAddress[] Result { get; set; } = [];

        public bool Fail { get; set; }

        public List<string> Hosts { get; } = [];

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Hosts.Add(host);
            return Fail ? throw new System.Net.Sockets.SocketException(11001) : Task.FromResult(Result);
        }
    }
}
