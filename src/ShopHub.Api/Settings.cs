using Npgsql;

namespace ShopHub.Api;

/// <summary>
/// Configuration from environment variables (contracts/delivery.md "Settings"). Errors name the
/// variable and never include its value.
/// </summary>
public sealed class Settings
{
    public const string DefaultShopsNamespace = "shops";
    public const string DefaultShopDomain = "shops.localhost";
    public const int DefaultMaxShopsPerOwner = 5;

    /// <summary>Npgsql connection string converted from <c>DATABASE_URL</c> (CNPG Secret <c>shophub-db-app</c>, key <c>uri</c>).</summary>
    public required string PostgresConnectionString { get; init; }

    /// <summary><c>SHOPS_NAMESPACE</c>: the only namespace ShopHub writes Shops, Wallets and DiscordChannels to.</summary>
    public required string ShopsNamespace { get; init; }

    /// <summary><c>SHOP_DOMAIN</c>: for display only; shop URLs come from the Shop status.</summary>
    public required string ShopDomain { get; init; }

    /// <summary><c>MAX_SHOPS_PER_OWNER</c> (FR-011), default 5.</summary>
    public required int MaxShopsPerOwner { get; init; }

    /// <summary>
    /// Optional <c>TRUSTED_PROXY_HOST</c>: DNS name whose addresses are the only trusted proxies
    /// (research R7). Null means forwarded headers are ignored.
    /// </summary>
    public string? TrustedProxyHost { get; init; }

    public static Settings Load(IConfiguration configuration) => Load(key => configuration[key]);

    public static Settings Load(Func<string, string?> get)
    {
        var problems = new List<string>();
        string? Optional(string name) => get(name)?.Trim() is { Length: > 0 } v ? v : null;

        string? postgres = null;
        if (Optional("DATABASE_URL") is not { } databaseUrl)
        {
            problems.Add("DATABASE_URL is not set");
        }
        else if ((postgres = ToNpgsql(databaseUrl)) is null)
        {
            problems.Add("DATABASE_URL is not a valid postgresql:// URI with host and database");
        }

        var shopsNamespace = Optional("SHOPS_NAMESPACE") ?? DefaultShopsNamespace;
        if (!IsDnsLabel(shopsNamespace))
        {
            problems.Add("SHOPS_NAMESPACE is not a namespace name");
        }

        var shopDomain = Optional("SHOP_DOMAIN") ?? DefaultShopDomain;
        if (Uri.CheckHostName(shopDomain) != UriHostNameType.Dns)
        {
            problems.Add("SHOP_DOMAIN is not a DNS name");
        }

        var maxShops = DefaultMaxShopsPerOwner;
        if (Optional("MAX_SHOPS_PER_OWNER") is { } max
            && (!int.TryParse(max, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxShops)
                || maxShops < 1))
        {
            problems.Add("MAX_SHOPS_PER_OWNER must be a whole number of 1 or more");
        }

        var proxyHost = Optional("TRUSTED_PROXY_HOST");
        if (proxyHost is not null && Uri.CheckHostName(proxyHost) == UriHostNameType.Unknown)
        {
            problems.Add("TRUSTED_PROXY_HOST is not a DNS name or address");
        }

        if (problems.Count > 0)
        {
            throw new SettingsException("Invalid configuration: " + string.Join("; ", problems) + ".");
        }

        return new Settings
        {
            PostgresConnectionString = postgres!,
            ShopsNamespace = shopsNamespace,
            ShopDomain = shopDomain,
            MaxShopsPerOwner = maxShops,
            TrustedProxyHost = proxyHost,
        };
    }

    private static bool IsDnsLabel(string s) =>
        s.Length is >= 1 and <= 63 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && s[0] != '-' && s[^1] != '-';

    /// <summary>Converts a CNPG <c>uri</c> (<c>postgresql://user:pw@host:5432/db</c>) for Npgsql (from shop).</summary>
    internal static string? ToNpgsql(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)
            || u.Scheme is not ("postgresql" or "postgres")
            || string.IsNullOrEmpty(u.DnsSafeHost))
        {
            return null;
        }

        var database = Uri.UnescapeDataString(u.AbsolutePath.Trim('/'));
        if (database.Length == 0)
        {
            return null;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = u.DnsSafeHost,
            Port = u.IsDefaultPort || u.Port < 0 ? 5432 : u.Port,
            Database = database,
            ApplicationName = "shophub",
            // CNPG doesn't use GSSAPI, and the chiseled image has no libgssapi_krb5.
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        var userInfo = u.UserInfo.Split(':', 2);
        if (userInfo[0].Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length == 2)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<SslMode>(kv[1].Replace("-", "", StringComparison.Ordinal), true, out var mode))
                {
                    return null;
                }

                builder.SslMode = mode;
            }
        }

        return builder.ConnectionString;
    }
}

/// <summary>Missing or invalid configuration. The message names variables only.</summary>
public sealed class SettingsException(string message) : Exception(message);
