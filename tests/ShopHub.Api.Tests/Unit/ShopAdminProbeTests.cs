using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShopHub.Api.Shops;

namespace ShopHub.Api.Tests.Unit;

/// <summary>The shop's "password changed" check over its in-cluster Service (FR-016, R10).</summary>
public sealed class ShopAdminProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return answer(request, ct);
        }
    }

    private static Stub Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    private static async Task<bool?> AskAsync(Stub stub)
    {
        using var http = new HttpClient(stub);
        return await new HttpShopAdminProbe(http, "shops", NullLogger<HttpShopAdminProbe>.Instance)
            .AdminPasswordChangedAsync("s7k2m4qxab", Ct);
    }

    [Fact]
    public async Task AsksTheShopsServiceByName()
    {
        var stub = Json("""{"name":"Acme","adminPasswordChanged":false}""");

        Assert.False(await AskAsync(stub));
        Assert.Equal([new Uri("http://s7k2m4qxab.shops.svc.cluster.local/api/shop")], stub.Requests);
    }

    [Fact]
    public async Task ReadsAChangedPassword() =>
        Assert.True(await AskAsync(Json("""{"name":"Acme","adminPasswordChanged":true}""")));

    [Theory]
    [InlineData("""{"name":"Acme"}""", HttpStatusCode.OK)]
    [InlineData("not json", HttpStatusCode.OK)]
    [InlineData("""{"title":"down"}""", HttpStatusCode.ServiceUnavailable)]
    public async Task AnythingButAClearAnswerIsNoAnswer(string body, HttpStatusCode status) =>
        Assert.Null(await AskAsync(Json(body, status)));

    [Fact]
    public async Task AnUnreachableShopIsNoAnswer() =>
        Assert.Null(await AskAsync(new Stub((_, _) => throw new HttpRequestException("connection refused"))));

    [Fact]
    public async Task AShopThatHangsIsNoAnswerAfterTheTimeout()
    {
        var started = DateTimeOffset.UtcNow;

        Assert.Null(await AskAsync(new Stub(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        Assert.InRange(DateTimeOffset.UtcNow - started, HttpShopAdminProbe.Timeout - TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OnlyShopIdsBecomeHostNames() =>
        Assert.Throws<ArgumentException>(() => HttpShopAdminProbe.ShopInfoUri("evil.example.com#", "shops"));
}
