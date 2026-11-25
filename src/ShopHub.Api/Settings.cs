namespace ShopHub.Api;

/// <summary>
/// Configuration from environment variables (contracts/delivery.md). Errors name the variable and
/// never include its value. T013 adds <c>DATABASE_URL</c>, <c>SHOPS_NAMESPACE</c>,
/// <c>SHOP_DOMAIN</c> and <c>MAX_SHOPS_PER_OWNER</c>.
/// </summary>
public sealed class Settings
{
    /// <summary>
    /// Optional <c>TRUSTED_PROXY_HOST</c>: DNS name whose addresses are the only trusted proxies
    /// (research R7). Null means forwarded headers are ignored.
    /// </summary>
    public string? TrustedProxyHost { get; init; }

    public static Settings Load(IConfiguration configuration) => Load(key => configuration[key]);

    public static Settings Load(Func<string, string?> get)
    {
        var problems = new List<string>();

        var proxyHost = get("TRUSTED_PROXY_HOST")?.Trim();
        if (string.IsNullOrEmpty(proxyHost))
        {
            proxyHost = null;
        }
        else if (Uri.CheckHostName(proxyHost) == UriHostNameType.Unknown)
        {
            problems.Add("TRUSTED_PROXY_HOST is not a DNS name or address");
        }

        if (problems.Count > 0)
        {
            throw new SettingsException("Invalid configuration: " + string.Join("; ", problems) + ".");
        }

        return new Settings { TrustedProxyHost = proxyHost };
    }
}

/// <summary>Missing or invalid configuration. The message names variables only.</summary>
public sealed class SettingsException(string message) : Exception(message);
