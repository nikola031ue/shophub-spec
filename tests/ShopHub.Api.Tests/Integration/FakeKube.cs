using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopHub.Api.Shops;

namespace ShopHub.Api.Tests.Integration;

/// <summary>
/// The cluster as the API tests need it: Shop statuses, admin Secrets and the shops' own
/// "password changed" answer are set by the test; every read is recorded by shop id. The real
/// gateway is covered against k3s in <c>K3s/</c>.
/// </summary>
public sealed class FakeKube : IKubeGateway, IShopAdminProbe
{
    private readonly ConcurrentDictionary<string, ShopStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, string> _passwords = new();
    private readonly ConcurrentDictionary<string, bool?> _changed = new();
    private readonly ConcurrentQueue<string> _applied = new();
    private readonly ConcurrentQueue<string> _statusReads = new();
    private readonly ConcurrentQueue<string> _secretReads = new();
    private readonly ConcurrentQueue<string> _probes = new();
    private readonly ConcurrentQueue<string> _deleted = new();
    private readonly ConcurrentDictionary<string, string[]> _remaining = new();

    /// <summary>When true, every call fails as if the API server were unreachable.</summary>
    public bool Down { get; set; }

    public IReadOnlyList<string> Applied => [.. _applied];

    public IReadOnlyList<string> StatusReads => [.. _statusReads];

    public IReadOnlyList<string> SecretReads => [.. _secretReads];

    public IReadOnlyList<string> Probes => [.. _probes];

    /// <summary>Shop ids whose objects were deleted (each call).</summary>
    public IReadOnlyList<string> Deleted => [.. _deleted];

    /// <summary>Objects of a shop that still exist after its delete (finalizers); none by default.</summary>
    public void SetRemaining(string id, params string[] names) => _remaining[id] = names;

    public void SetStatus(string id, ShopStatus status) => _statuses[id] = status;

    public void SetReady(string id, string? url = null) => _statuses[id] = new ShopStatus(true, null, null, url ?? $"https://{id}.shops.localhost", 2);

    public void SetAdminPassword(string id, string password) => _passwords[id] = password;

    /// <summary>The shop's <c>adminPasswordChanged</c>; null = the shop doesn't answer.</summary>
    public void SetPasswordChanged(string id, bool? changed) => _changed[id] = changed;

    /// <summary>Replaces the gateway and the probe in a test host.</summary>
    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IKubeGateway>();
        services.RemoveAll<IShopAdminProbe>();
        services.AddSingleton<IKubeGateway>(this);
        services.AddSingleton<IShopAdminProbe>(this);
    }

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new HttpRequestException("API server unreachable", new SocketException((int)SocketError.ConnectionRefused));
        }
    }

    public Task ApplyAsync(ShopResources shop, CancellationToken ct)
    {
        ThrowIfDown();
        _applied.Enqueue(shop.Id);
        return Task.CompletedTask;
    }

    public Task<ShopStatus?> GetShopStatusAsync(string id, CancellationToken ct)
    {
        ThrowIfDown();
        _statusReads.Enqueue(id);
        return Task.FromResult(_statuses.TryGetValue(id, out var s) ? s : null);
    }

    public Task<string?> ReadAdminPasswordAsync(string id, CancellationToken ct)
    {
        ThrowIfDown();
        _secretReads.Enqueue(id);
        return Task.FromResult(_passwords.TryGetValue(id, out var p) ? p : null);
    }

    public Task<bool?> AdminPasswordChangedAsync(string id, CancellationToken ct)
    {
        _probes.Enqueue(id);
        return Task.FromResult(_changed.TryGetValue(id, out var c) ? c : false);
    }

    public Task DeleteWalletAsync(string id, int generation, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteShopAsync(string id, int walletGeneration, CancellationToken ct)
    {
        ThrowIfDown();
        _deleted.Enqueue(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> RemainingAsync(string id, int walletGeneration, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult<IReadOnlyList<string>>(_remaining.TryGetValue(id, out var names) ? names : []);
    }
}
