using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ShopHub.Api.Tests.Integration;

/// <summary>The scaffold (T011): health, API 404s as problem details, security headers, settings errors.</summary>
public sealed class AppTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class App(Dictionary<string, string?>? env = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in env ?? [])
            {
                builder.UseSetting(key, value);
            }
        }
    }

    [Fact]
    public async Task AnswersHealthAndUnknownApiPaths()
    {
        await using var app = new App();
        using var client = app.CreateClient();

        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Ct);
        using var unknown = await client.GetAsync(new Uri("/api/nope", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EveryResponseCarriesSecurityHeaders()
    {
        await using var app = new App();
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
    public void InvalidSettingsStopTheStartAndNameTheVariable()
    {
        using var app = new App(new() { ["TRUSTED_PROXY_HOST"] = "not a host/" });

        var ex = Assert.Throws<SettingsException>(() => app.CreateClient());

        Assert.Contains("TRUSTED_PROXY_HOST", ex.Message, StringComparison.Ordinal);
    }
}
