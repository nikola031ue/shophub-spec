namespace ShopHub.Api;

/// <summary>
/// The client address used for lockouts and rate limits: after <see cref="TrustedForwardedHeaders"/>,
/// the address Traefik forwarded, otherwise the TCP peer.
/// </summary>
public static class ClientAddress
{
    /// <summary>Used when the server reports no peer (only in-process tests).</summary>
    public const string Unknown = "unknown";

    public static string Of(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip ? TrustedProxies.Normalize(ip).ToString() : Unknown;
}
