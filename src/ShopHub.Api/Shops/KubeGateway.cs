using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace ShopHub.Api.Shops;

/// <summary>What ShopHub writes for one shop record (data-model.md "Cluster resources written by ShopHub").</summary>
public sealed record ShopResources(
    string Id,
    Guid OwnerId,
    string DisplayName,
    string Availability,
    string Database,
    string WalletAddress,
    int WalletGeneration);

/// <summary>The parts of a Shop's status ShopHub reads.</summary>
public sealed record ShopStatus(bool Ready, string? Reason, string? Message, string? Url, int ReadyReplicas);

/// <summary>A write the API server refused as invalid (422), with the server's message.</summary>
public sealed class KubeValidationException(string message) : Exception(message);

/// <summary>Resource names, all derived from a shop id from the database (data-model.md).</summary>
public static partial class ShopNames
{
    public static bool IsValidId(string? id) => id is not null && IdFormat().IsMatch(id);

    /// <summary>Rejects anything that isn't a generated shop id before a name is built from it.</summary>
    public static string Checked(string id) =>
        IsValidId(id) ? id : throw new ArgumentException("Not a shop id.", nameof(id));

    public static string Shop(string id) => Checked(id);

    public static string Channel(string id) => Checked(id);

    public static string Wallet(string id, int generation) =>
        generation >= 1 ? $"{Checked(id)}-w{generation}" : throw new ArgumentOutOfRangeException(nameof(generation));

    /// <summary>The only Secret ShopHub reads (FR-016).</summary>
    public static string AdminSecret(string id) => $"{Checked(id)}-admin";

    [GeneratedRegex("^s[a-z2-7]{9}$")]
    private static partial Regex IdFormat();
}

/// <summary>
/// The Kubernetes seam (T014). Every call names its objects exactly, from the shop record: there is
/// no list-and-delete and no label selector anywhere, so a mistake can't reach another shop.
/// </summary>
public interface IKubeGateway
{
    /// <summary>Server-side applies the DiscordChannel, the current Wallet and the Shop (field manager <c>shophub</c>).</summary>
    Task ApplyAsync(ShopResources shop, CancellationToken ct);

    /// <summary>Deletes one Wallet generation by name (after an address change). Missing is fine.</summary>
    Task DeleteWalletAsync(string id, int generation, CancellationToken ct);

    /// <summary>Deletes the Shop, its DiscordChannel and Wallets <c>-w1</c>…<c>-w&lt;generation&gt;</c>, each by name.</summary>
    Task DeleteShopAsync(string id, int walletGeneration, CancellationToken ct);

    /// <summary>The names of those objects that still exist (finalizers may hold them for a while).</summary>
    Task<IReadOnlyList<string>> RemainingAsync(string id, int walletGeneration, CancellationToken ct);

    /// <summary>The Shop's status, or null if the Shop doesn't exist.</summary>
    Task<ShopStatus?> GetShopStatusAsync(string id, CancellationToken ct);

    /// <summary>Key <c>password</c> of Secret <c>&lt;id&gt;-admin</c>, or null if it doesn't exist (FR-016).</summary>
    Task<string?> ReadAdminPasswordAsync(string id, CancellationToken ct);
}

public sealed class KubeGateway(IKubernetes client, string shopsNamespace) : IKubeGateway
{
    public const string FieldManager = "shophub";
    public const string Group = "shophub.io";
    public const string Version = "v1alpha1";

    // Plural names; RemainingAsync answers "<plural>/<name>".
    public const string Shops = "shops";
    public const string Wallets = "wallets";
    public const string Channels = "discordchannels";

    public async Task ApplyAsync(ShopResources shop, CancellationToken ct)
    {
        var id = ShopNames.Checked(shop.Id);
        var labels = new Dictionary<string, string>
        {
            ["shophub.io/owner"] = shop.OwnerId.ToString("D"),
            ["shophub.io/shop"] = id,
            ["app.kubernetes.io/managed-by"] = FieldManager,
        };
        var wallet = ShopNames.Wallet(id, shop.WalletGeneration);

        // Referenced objects first, so the Shop never points at something that isn't there yet.
        await ApplyObjectAsync(Channels, "DiscordChannel", ShopNames.Channel(id), labels,
            new Dictionary<string, object> { ["channelName"] = id }, ct);
        await ApplyObjectAsync(Wallets, "Wallet", wallet, labels,
            new Dictionary<string, object> { ["address"] = shop.WalletAddress }, ct);
        await ApplyObjectAsync(Shops, "Shop", ShopNames.Shop(id), labels, new Dictionary<string, object>
        {
            ["shopName"] = shop.DisplayName,
            ["availability"] = shop.Availability,
            ["database"] = shop.Database,
            ["walletRef"] = new Dictionary<string, object> { ["name"] = wallet },
            ["discordChannelRef"] = new Dictionary<string, object> { ["name"] = id },
        }, ct);
    }

    public Task DeleteWalletAsync(string id, int generation, CancellationToken ct) =>
        DeleteObjectAsync(Wallets, ShopNames.Wallet(id, generation), ct);

    public async Task DeleteShopAsync(string id, int walletGeneration, CancellationToken ct)
    {
        // Spec 005 D-4: mark the Shop as deleted by ShopHub first; the shop-operator chart's
        // admission policy refuses any Shop DELETE without this annotation.
        await AnnotateDeleteRequestedAsync(ShopNames.Shop(id), ct);

        // The Shop first: the operator keeps a Wallet or DiscordChannel while a Shop uses it.
        await DeleteObjectAsync(Shops, ShopNames.Shop(id), ct);
        await DeleteObjectAsync(Channels, ShopNames.Channel(id), ct);
        for (var g = 1; g <= walletGeneration; g++)
        {
            await DeleteObjectAsync(Wallets, ShopNames.Wallet(id, g), ct);
        }
    }

    public async Task<IReadOnlyList<string>> RemainingAsync(string id, int walletGeneration, CancellationToken ct)
    {
        var names = new List<(string Plural, string Name)> { (Shops, ShopNames.Shop(id)), (Channels, ShopNames.Channel(id)) };
        names.AddRange(Enumerable.Range(1, walletGeneration).Select(g => (Wallets, ShopNames.Wallet(id, g))));
        var remaining = new List<string>();
        foreach (var (plural, name) in names)
        {
            if (await GetObjectAsync(plural, name, ct) is not null)
            {
                remaining.Add($"{plural}/{name}");
            }
        }

        return remaining;
    }

    public async Task<ShopStatus?> GetShopStatusAsync(string id, CancellationToken ct)
    {
        if (await GetObjectAsync(Shops, ShopNames.Shop(id), ct) is not { } shop)
        {
            return null;
        }

        if (!shop.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            return new ShopStatus(false, null, null, null, 0);
        }

        JsonElement? ready = null;
        if (status.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
        {
            ready = conditions.EnumerateArray()
                .Cast<JsonElement?>()
                .FirstOrDefault(c => c!.Value.TryGetProperty("type", out var t) && t.GetString() == "Ready");
        }

        string? Text(JsonElement? e, string name) =>
            e is { } v && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        // Spec 005 D-6 (research R12): a status older than the spec says nothing about the change
        // just made, so the Shop is not ready until the operator has seen it. A status without
        // observedGeneration can't be dated and is taken as it is.
        var generation = Number(shop.TryGetProperty("metadata", out var metadata) ? metadata : null, "generation");
        if (generation is { } current && (Number(status, "observedGeneration") < current || Number(ready, "observedGeneration") < current))
        {
            return new ShopStatus(false, ChangePendingReason, null, Text(status, "url"),
                status.TryGetProperty("readyReplicas", out var stale) && stale.TryGetInt32(out var staleCount) ? staleCount : 0);
        }

        return new ShopStatus(
            Text(ready, "status") == "True",
            Text(ready, "reason"),
            Text(ready, "message"),
            Text(status, "url"),
            status.TryGetProperty("readyReplicas", out var r) && r.TryGetInt32(out var n) ? n : 0);
    }

    /// <summary>The reason ShopHub reports while the operator hasn't seen the latest change (D-6).</summary>
    public const string ChangePendingReason = "ChangePending";

    /// <summary>Spec 005 D-4: the annotation ShopHub writes on a Shop right before deleting it.</summary>
    public const string DeleteRequestedAnnotation = "shophub.io/delete-requested";

    private static long? Number(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : null;

    /// <summary>A merge patch on the Shop's annotations; a Shop already gone needs nothing.</summary>
    private async Task AnnotateDeleteRequestedAsync(string name, CancellationToken ct)
    {
        var patch = new Dictionary<string, object>
        {
            ["metadata"] = new Dictionary<string, object>
            {
                ["annotations"] = new Dictionary<string, string> { [DeleteRequestedAnnotation] = "true" },
            },
        };
        try
        {
            await client.CustomObjects.PatchNamespacedCustomObjectAsync(
                new V1Patch(patch, V1Patch.PatchType.MergePatch), Group, Version, shopsNamespace, Shops, name,
                fieldManager: FieldManager, cancellationToken: ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    public async Task<string?> ReadAdminPasswordAsync(string id, CancellationToken ct)
    {
        try
        {
            var secret = await client.CoreV1.ReadNamespacedSecretAsync(ShopNames.AdminSecret(id), shopsNamespace, cancellationToken: ct);
            return secret.Data is { } data && data.TryGetValue("password", out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task ApplyObjectAsync(
        string plural, string kind, string name, Dictionary<string, string> labels, Dictionary<string, object> spec, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["apiVersion"] = $"{Group}/{Version}",
            ["kind"] = kind,
            ["metadata"] = new Dictionary<string, object> { ["name"] = name, ["namespace"] = shopsNamespace, ["labels"] = labels },
            ["spec"] = spec,
        };
        try
        {
            await client.CustomObjects.PatchNamespacedCustomObjectAsync(
                new V1Patch(body, V1Patch.PatchType.ApplyPatch), Group, Version, shopsNamespace, plural, name,
                fieldManager: FieldManager, force: true, cancellationToken: ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            throw new KubeValidationException(ServerMessage(e) ?? $"{kind} {name} was refused as invalid.");
        }
    }

    private async Task DeleteObjectAsync(string plural, string name, CancellationToken ct)
    {
        try
        {
            await client.CustomObjects.DeleteNamespacedCustomObjectAsync(Group, Version, shopsNamespace, plural, name, cancellationToken: ct);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    private async Task<JsonElement?> GetObjectAsync(string plural, string name, CancellationToken ct)
    {
        try
        {
            var obj = await client.CustomObjects.GetNamespacedCustomObjectAsync(Group, Version, shopsNamespace, plural, name, ct);
            return obj is JsonElement e ? e : JsonSerializer.SerializeToElement(obj);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string? ServerMessage(HttpOperationException e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.Response.Content ?? "");
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
