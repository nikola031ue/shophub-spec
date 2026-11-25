using System.Net.Http.Json;

namespace ShopHub.Api.Shops;

/// <summary>
/// Asks a shop whether its admin has replaced the generated password (FR-016, research R10), over
/// the shop's in-cluster Service. The shop NetworkPolicy lets only ShopHub's pods in from the
/// <c>shophub</c> namespace (contracts/platform-phase1.md).
/// </summary>
public interface IShopAdminProbe
{
    /// <summary>The shop's <c>adminPasswordChanged</c>, or null when the shop doesn't answer.</summary>
    Task<bool?> AdminPasswordChangedAsync(string id, CancellationToken ct);
}

/// <summary><c>GET http://&lt;id&gt;.&lt;namespace&gt;.svc.cluster.local/api/shop</c> (shop 0.2.0: <c>{name, adminPasswordChanged}</c>).</summary>
public sealed partial class HttpShopAdminProbe(HttpClient http, string shopsNamespace, ILogger<HttpShopAdminProbe> log) : IShopAdminProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed record ShopInfo(string? Name, bool? AdminPasswordChanged);

    public static Uri ShopInfoUri(string id, string shopsNamespace) =>
        new($"http://{ShopNames.Checked(id)}.{shopsNamespace}.svc.cluster.local/api/shop");

    public async Task<bool?> AdminPasswordChangedAsync(string id, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var info = await http.GetFromJsonAsync<ShopInfo>(ShopInfoUri(id, shopsNamespace), timeout.Token);
            return info?.AdminPasswordChanged;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            LogUnreachable(id, e.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shop {ShopId} did not answer the admin-password check ({Error}).")]
    private partial void LogUnreachable(string shopId, string error);
}
