using System.Net;
using Microsoft.Extensions.Primitives;

namespace ShopHub.Api;

/// <summary>Resolves a host name to addresses; replaced in tests.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

internal sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// The current addresses of <c>TRUSTED_PROXY_HOST</c> (the Traefik pods behind the headless
/// <c>traefik-proxies</c> Service): the only peers whose forwarded headers count (spec 004 FR-022,
/// research R5). Empty means no proxy is trusted.
/// </summary>
public sealed class TrustedProxies
{
    private volatile HashSet<IPAddress> _addresses = [];

    public IReadOnlySet<IPAddress> Addresses => _addresses;

    public bool IsTrusted(IPAddress? peer) => peer is not null && _addresses.Contains(Normalize(peer));

    internal void Set(IEnumerable<IPAddress> addresses) => _addresses = addresses.Select(Normalize).ToHashSet();

    /// <summary>Kestrel may report IPv4 peers as IPv4-mapped IPv6 addresses.</summary>
    public static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>
/// Resolves <c>TRUSTED_PROXY_HOST</c> before the server starts and then every
/// <see cref="Interval"/>. A failed lookup trusts nobody until the next one succeeds (fail closed:
/// the peer address is used, which only makes lockouts and limits stricter).
/// </summary>
public sealed partial class TrustedProxyRefresher(
    Settings settings,
    IHostResolver resolver,
    TrustedProxies proxies,
    TimeProvider time,
    ILogger<TrustedProxyRefresher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.TrustedProxyHost is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (settings.TrustedProxyHost is not { } host)
        {
            proxies.Set([]);
            return;
        }

        try
        {
            proxies.Set(await resolver.ResolveAsync(host, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            proxies.Set([]);
            LogLookupFailed(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Looking up TRUSTED_PROXY_HOST failed ({Error}); no proxy is trusted until the next lookup.")]
    private static partial void LogLookupFailed(ILogger logger, string error);
}

/// <summary>
/// Honours <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only when the TCP peer is a trusted
/// proxy, and then only the last (rightmost) value, the one the proxy added (ForwardLimit = 1).
/// Headers from any other peer, ShopHub included, are ignored.
/// </summary>
public static class TrustedForwardedHeaders
{
    public const string ForwardedFor = "X-Forwarded-For";
    public const string ForwardedProto = "X-Forwarded-Proto";

    public static IServiceCollection AddTrustedProxies(this IServiceCollection services)
    {
        services.AddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<TrustedProxies>();
        services.AddHostedService<TrustedProxyRefresher>();
        return services;
    }

    public static IApplicationBuilder UseTrustedForwardedHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var proxies = context.RequestServices.GetRequiredService<TrustedProxies>();
            if (proxies.IsTrusted(context.Connection.RemoteIpAddress))
            {
                if (ParseAddress(Last(context.Request.Headers[ForwardedFor])) is { } client)
                {
                    context.Connection.RemoteIpAddress = client;
                }

                var scheme = Last(context.Request.Headers[ForwardedProto])?.ToLowerInvariant();
                if (scheme is "http" or "https")
                {
                    context.Request.Scheme = scheme;
                }
            }

            return next(context);
        });

    /// <summary>The last comma-separated entry over all header lines, or null.</summary>
    internal static string? Last(StringValues values)
    {
        for (var i = values.Count - 1; i >= 0; i--)
        {
            var parts = (values[i] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                return parts[^1];
            }
        }

        return null;
    }

    /// <summary>An address, optionally with a port (<c>1.2.3.4:5</c>, <c>[::1]:5</c>); null if neither.</summary>
    internal static IPAddress? ParseAddress(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (IPAddress.TryParse(value, out var address))
        {
            return TrustedProxies.Normalize(address);
        }

        return IPEndPoint.TryParse(value, out var endpoint) ? TrustedProxies.Normalize(endpoint.Address) : null;
    }
}
