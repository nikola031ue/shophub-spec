using Npgsql;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Tests.Unit;

/// <summary>T013 settings (contracts/delivery.md "Settings"): defaults, validation, no values in errors.</summary>
public sealed class SettingsTests
{
    private const string DbSecret = "db-S3cret!pw";

    private static Dictionary<string, string?> Env() => new()
    {
        ["DATABASE_URL"] = $"postgresql://app:{Uri.EscapeDataString(DbSecret)}@shophub-db-rw.shophub.svc:5432/app",
    };

    private static Settings Load(Dictionary<string, string?> env) => Settings.Load(k => env.GetValueOrDefault(k));

    [Fact]
    public void DefaultsApplyWhenOnlyTheDatabaseIsSet()
    {
        var s = Load(Env());

        var cs = new NpgsqlConnectionStringBuilder(s.PostgresConnectionString);
        Assert.Equal("shophub-db-rw.shophub.svc", cs.Host);
        Assert.Equal(5432, cs.Port);
        Assert.Equal("app", cs.Database);
        Assert.Equal("app", cs.Username);
        Assert.Equal(DbSecret, cs.Password);
        Assert.Equal("shophub", cs.ApplicationName);
        Assert.Equal("shops", s.ShopsNamespace);
        Assert.Equal("shops.localhost", s.ShopDomain);
        Assert.Equal(5, s.MaxShopsPerOwner);
        Assert.Null(s.TrustedProxyHost);
    }

    [Fact]
    public void EverySettingIsRead()
    {
        var env = Env();
        env["SHOPS_NAMESPACE"] = "tenant-shops";
        env["SHOP_DOMAIN"] = "shops.example.test";
        env["MAX_SHOPS_PER_OWNER"] = "3";
        env["TRUSTED_PROXY_HOST"] = "traefik-proxies.traefik.svc.cluster.local";

        var s = Load(env);

        Assert.Equal("tenant-shops", s.ShopsNamespace);
        Assert.Equal("shops.example.test", s.ShopDomain);
        Assert.Equal(3, s.MaxShopsPerOwner);
        Assert.Equal("traefik-proxies.traefik.svc.cluster.local", s.TrustedProxyHost);
    }

    [Theory]
    [InlineData("postgresql://u:p@h/db?sslmode=require", SslMode.Require)]
    [InlineData("postgres://u:p@h:6543/db?sslmode=verify-full", SslMode.VerifyFull)]
    public void PostgresUriOptions(string uri, SslMode mode) =>
        Assert.Equal(mode, new NpgsqlConnectionStringBuilder(Settings.ToNpgsql(uri)).SslMode);

    [Theory]
    [InlineData("DATABASE_URL", null, "DATABASE_URL is not set")]
    [InlineData("DATABASE_URL", "mysql://u:db-S3cret!pw@h/db", "DATABASE_URL is not a valid")]
    [InlineData("DATABASE_URL", "postgresql://u:db-S3cret!pw@h", "DATABASE_URL is not a valid")]
    [InlineData("SHOPS_NAMESPACE", "Shops_NS", "SHOPS_NAMESPACE")]
    [InlineData("SHOP_DOMAIN", "not a domain", "SHOP_DOMAIN")]
    [InlineData("MAX_SHOPS_PER_OWNER", "0", "MAX_SHOPS_PER_OWNER")]
    [InlineData("MAX_SHOPS_PER_OWNER", "-1", "MAX_SHOPS_PER_OWNER")]
    [InlineData("MAX_SHOPS_PER_OWNER", "five", "MAX_SHOPS_PER_OWNER")]
    [InlineData("TRUSTED_PROXY_HOST", "not a host/", "TRUSTED_PROXY_HOST")]
    public void InvalidSettingsAreNamedWithoutTheirValue(string name, string? value, string message)
    {
        var env = Env();
        env[name] = value;

        var ex = Assert.Throws<SettingsException>(() => Load(env));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DbSecret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllProblemsAreReportedAtOnce()
    {
        var ex = Assert.Throws<SettingsException>(() => Load(new() { ["MAX_SHOPS_PER_OWNER"] = "0" }));

        Assert.Contains("DATABASE_URL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MAX_SHOPS_PER_OWNER", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryBackoffIsCappedAt10Seconds()
    {
        var delays = new List<TimeSpan>();
        var d = DatabaseInitializer.FirstDelay;
        for (var i = 0; i < 10; i++)
        {
            delays.Add(d);
            d = DatabaseInitializer.NextDelay(d);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[0]);
        Assert.All(delays, x => Assert.True(x <= TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), delays[^1]);
    }
}
