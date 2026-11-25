using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using ShopHub.Api.Accounts;

namespace ShopHub.Api.Tests.Integration.Postgres;

/// <summary>
/// The app with its environment variables, as a pod gets them. Log output goes to
/// <paramref name="logs"/> when given, for leak checks.
/// </summary>
public sealed class ShopHubApp(
    IReadOnlyDictionary<string, string?> env,
    LogSink? logs = null,
    TimeSpan? startupTimeout = null,
    IPasswordHasher<OwnerUser>? hasher = null) : WebApplicationFactory<Program>
{
    /// <summary>TestServer has no peer address; this test-only header simulates the TCP peer.</summary>
    public const string PeerHeader = "X-Test-Peer";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, PeerFilter>());
        if (hasher is not null)
        {
            builder.ConfigureServices(s => s.AddSingleton(hasher));
        }

        foreach (var (key, value) in env)
        {
            builder.UseSetting(key, value);
        }

        if (logs is not null)
        {
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        }

        if (startupTimeout is { } timeout)
        {
            builder.ConfigureServices(s => s.Configure<HostOptions>(o => o.StartupTimeout = timeout));
        }
    }
}

internal sealed class PeerFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (System.Net.IPAddress.TryParse(context.Request.Headers[ShopHubApp.PeerHeader], out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}

/// <summary>Collects every log message and exception text written by the app.</summary>
public sealed class LogSink : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    private sealed class Logger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Add($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>T013 checkpoint: the app migrates its database at start and answers on PostgreSQL.</summary>
public sealed class StartupTests(PostgresContainer db) : IClassFixture<PostgresContainer>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Dictionary<string, string?>> FreshEnvAsync() => new()
    {
        ["DATABASE_URL"] = await db.CreateDatabaseUrlAsync("app_" + Guid.NewGuid().ToString("N")),
    };

    private static async Task<List<int>> AppliedVersionsAsync(ShopHubApp app)
    {
        var source = app.Services.GetRequiredService<NpgsqlDataSource>();
        await using var cmd = source.CreateCommand("SELECT version FROM schema_version ORDER BY version");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var versions = new List<int>();
        while (await reader.ReadAsync(Ct))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    [Fact]
    public async Task StartsMigratesAndAnswersHealth()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        using var client = app.CreateClient();

        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Ct);
        using var unknown = await client.GetAsync(new Uri("/api/nope", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2], await AppliedVersionsAsync(app));
    }

    [Fact]
    public async Task ThreeReplicasStartingTogetherMigrateOnce()
    {
        var env = await FreshEnvAsync();
        var apps = Enumerable.Range(0, 3).Select(_ => new ShopHubApp(env)).ToList();
        try
        {
            await Task.WhenAll(apps.Select(a => Task.Run(() => a.CreateClient().Dispose(), Ct)));
            Assert.Equal([1, 2], await AppliedVersionsAsync(apps[0]));
        }
        finally
        {
            foreach (var a in apps)
            {
                await a.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task EveryResponseCarriesSecurityHeaders()
    {
        await using var app = new ShopHubApp(await FreshEnvAsync());
        using var client = app.CreateClient();
        foreach (var path in new[] { "/healthz", "/api/nope" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        }
    }

    [Fact]
    public void MissingDatabaseUrlStopsTheStartAndNamesIt()
    {
        using var app = new ShopHubApp(new Dictionary<string, string?>());

        var ex = Assert.Throws<SettingsException>(() => app.CreateClient());

        Assert.Contains("DATABASE_URL is not set", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A wrong database password keeps the app from opening its port; the logs name the problem, never the password.</summary>
    [Fact]
    public async Task UnreachableDatabaseKeepsThePortClosedAndLogsNoSecret()
    {
        const string wrong = "wrong-pw-1f6d9c";
        var logs = new LogSink();
        var env = new Dictionary<string, string?> { ["DATABASE_URL"] = db.DatabaseUrl("postgres", wrong) };
        await using var app = new ShopHubApp(env, logs, startupTimeout: TimeSpan.FromSeconds(3));

        Assert.ThrowsAny<Exception>(() => app.CreateClient());

        var all = string.Join('\n', logs.Lines);
        Assert.Contains("Waiting for the database", all, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, all, StringComparison.Ordinal);
        Assert.DoesNotContain(db.Password, all, StringComparison.Ordinal);
    }
}
