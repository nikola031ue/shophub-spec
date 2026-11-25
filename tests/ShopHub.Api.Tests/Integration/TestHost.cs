using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Tests.Integration;

/// <summary>
/// A minimal app on TestServer with ShopHub's middleware (forwarded headers, cookie policy,
/// sessions, rate limits) and test-only endpoints, for testing the reusable pieces before the real
/// endpoints exist (T015 wires them into the panel API).
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    /// <summary>TestServer has no peer address; this test-only header simulates the TCP peer.</summary>
    public const string PeerHeader = "X-Test-Peer";

    private readonly WebApplication _app;

    private TestHost(WebApplication app) => _app = app;

    public IServiceProvider Services => _app.Services;

    public static async Task<TestHost> StartAsync(
        Action<WebApplication> endpoints,
        IDictionary<string, string?>? config = null,
        IHostResolver? resolver = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config ?? new Dictionary<string, string?>());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => Settings.Load(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddProblemDetails();
        builder.Services.AddTrustedProxies();
        if (resolver is not null)
        {
            builder.Services.AddSingleton(resolver);
        }

        builder.Services.AddRateLimits();
        builder.Services.AddSessions();
        builder.Services.AddSingleton<InMemorySessionStore>();
        builder.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<InMemorySessionStore>());

        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[PeerHeader], out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            return next(context);
        });
        app.UseTrustedForwardedHeaders();
        app.UseCookiePolicy();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        endpoints(app);
        await app.StartAsync();
        return new TestHost(app);
    }

    /// <summary>A browser-like client: keeps cookies and sends the XSRF header from the XSRF-TOKEN cookie.</summary>
    public BrowserClient Browser(string scheme = "https", string? peer = null)
    {
        var http = _app.GetTestClient();
        http.BaseAddress = new Uri($"{scheme}://localhost");
        if (peer is not null)
        {
            http.DefaultRequestHeaders.Add(PeerHeader, peer);
        }

        return new BrowserClient(http);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>Cookie jar by hand (TestServer clients have none), plus the XSRF header.</summary>
public sealed class BrowserClient(HttpClient http) : IDisposable
{
    private readonly Dictionary<string, string> _cookies = [];

    public HttpClient Http => http;

    /// <summary>Every Set-Cookie header of the last response.</summary>
    public IReadOnlyList<string> LastSetCookies { get; private set; } = [];

    public string? Cookie(string name) => _cookies.TryGetValue(name, out var v) ? $"{name}={v}" : null;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (_cookies.TryGetValue("XSRF-TOKEN", out var xsrf))
        {
            request.Headers.Add(Sessions.XsrfHeader, Uri.UnescapeDataString(xsrf));
        }

        var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var set) ? [.. set] : [];
        foreach (var cookie in LastSetCookies)
        {
            var pair = cookie.Split(';')[0].Split('=', 2);
            if (pair[1].Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
            {
                _cookies.Remove(pair[0]);
            }
            else
            {
                _cookies[pair[0]] = pair[1];
            }
        }

        return response;
    }

    public void Dispose() => http.Dispose();
}

/// <summary>In-memory <see cref="ISessionStore"/> with the semantics of <c>owner_sessions</c>.</summary>
public sealed class InMemorySessionStore(TimeProvider time) : ISessionStore
{
    private readonly ConcurrentDictionary<Guid, (Guid Owner, DateTimeOffset ExpiresAt)> _sessions = new();

    public Guid Create(Guid owner)
    {
        var id = Guid.NewGuid();
        _sessions[id] = (owner, time.GetUtcNow() + Sessions.Lifetime);
        return id;
    }

    public int Count(Guid owner) => _sessions.Count(s => s.Value.Owner == owner);

    public Task<bool> IsActiveAsync(Guid ownerId, Guid sessionId, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(_sessions.TryGetValue(sessionId, out var s) && s.Owner == ownerId && s.ExpiresAt > now);

    public Task EndAsync(Guid sessionId, CancellationToken ct)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task EndOthersAsync(Guid ownerId, Guid keep, CancellationToken ct)
    {
        foreach (var (id, s) in _sessions)
        {
            if (s.Owner == ownerId && id != keep)
            {
                _sessions.TryRemove(id, out _);
            }
        }

        return Task.CompletedTask;
    }
}
