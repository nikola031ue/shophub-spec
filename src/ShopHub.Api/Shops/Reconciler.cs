using Npgsql;
using ShopHub.Api.Storage.Postgres;

namespace ShopHub.Api.Shops;

/// <summary>Wakes the reconciler early; API writes call <see cref="Wake"/> after committing (R8).</summary>
public sealed class ReconcileTrigger : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Requests a pass soon. Several wakes before the next pass give one pass.</summary>
    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already requested.
        }
    }

    /// <summary>True if woken, false when the interval passed.</summary>
    public Task<bool> WaitAsync(TimeSpan interval, CancellationToken ct) => _signal.WaitAsync(interval, ct);

    public void Dispose() => _signal.Dispose();
}

/// <summary>
/// What one pass did. <see cref="Ran"/> is false when another replica held the lock.
/// <see cref="Removed"/>: rows of deleted shops removed; <see cref="Waiting"/>: shops being deleted
/// whose objects still exist (finalizers), checked again soon.
/// </summary>
public sealed record ReconcilePass(bool Ran, int Applied, int Failed, int Removed = 0, int Waiting = 0);

/// <summary>
/// Makes the cluster match the <c>shops</c> table (research R8, FR-014, FR-015). Every 30 s and
/// after each API write, one replica at a time (transaction-level advisory lock) server-side
/// applies the DiscordChannel, current Wallet and Shop of every <c>Active</c> record, in that order.
/// That one step creates, finishes an interrupted create and repairs drift alike.
/// <para>
/// After a wallet change (FR-009) the same step creates Wallet <c>-w&lt;new&gt;</c> and points the
/// Shop at it; only then are the older generations still recorded as possibly present deleted, each
/// by name, and the record notes them as gone. The operator keeps a Wallet while a Shop still uses
/// it (002 <c>InUse</c>), so the delete can't take a Wallet from under a running shop.
/// </para>
/// <para>
/// <para>
/// A <c>Deleting</c> record (FR-013, T022) is never applied. Each pass deletes its Shop, then its
/// DiscordChannel and Wallets <c>-w1</c> … <c>-w&lt;generation&gt;</c>, each by name (missing is
/// fine), and removes the row once none of them exists any more. Finalizers (the operator
/// deleting the Discord channel, the database, a Wallet still in use) can keep objects for a
/// while; until then passes come every <see cref="DeletingInterval"/> instead of every 30 s.
/// </para>
/// <para>
/// It works only from records and by exact names: it never lists or selects cluster objects, so
/// an object without a record is never touched. One shop failing doesn't stop the others; it is
/// retried on the next pass.
/// </para>
/// </summary>
public sealed partial class Reconciler(
    NpgsqlDataSource db,
    IShopStore shops,
    Func<IKubeGateway> gateway,
    ReconcileTrigger trigger,
    ILogger<Reconciler> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>While a deleted shop's objects still exist, so it leaves the list soon after (SC-003).</summary>
    public static readonly TimeSpan DeletingInterval = TimeSpan.FromSeconds(5);

    /// <summary>"SHUB" + 2; the schema migrator uses + 1.</summary>
    public const long LockKey = 0x5348_5542_0002;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pass = await RunOnceAsync(stoppingToken);
                await trigger.WaitAsync(pass.Waiting > 0 ? DeletingInterval : Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Database or cluster unreachable: try again next interval; the host keeps running.
                LogPassFailed(e);
                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One pass. Public so tests (and nothing else) can drive passes one at a time.</summary>
    public async Task<ReconcilePass> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        // The lock lives as long as this transaction: released on commit, rollback or a lost connection.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var tryLock = Pg.Command(conn, "SELECT pg_try_advisory_xact_lock(@key)", ("key", LockKey)))
        {
            if (!(bool)(await tryLock.ExecuteScalarAsync(ct))!)
            {
                return new ReconcilePass(false, 0, 0);
            }
        }

        var records = await shops.ListAllForReconcilerAsync(ct);
        var active = records.Where(r => r.State == RecordState.Active).ToList();
        var deleting = records.Where(r => r.State == RecordState.Deleting).ToList();
        int applied = 0, failed = 0, removed = 0, waiting = 0;
        if (records.Count > 0)
        {
            // Resolved only when there is work, so a host without cluster access starts and idles quietly.
            var kube = gateway();
            foreach (var record in active)
            {
                try
                {
                    await kube.ApplyAsync(Resources(record), ct);
                    await DeleteOldWalletsAsync(kube, record, ct);
                    applied++;
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failed++;
                    LogApplyFailed(record.Id, e);
                }
            }

            foreach (var record in deleting)
            {
                try
                {
                    await kube.DeleteShopAsync(record.Id, record.WalletGeneration, ct);
                    if ((await kube.RemainingAsync(record.Id, record.WalletGeneration, ct)).Count > 0)
                    {
                        waiting++;
                    }
                    else if (await shops.RemoveDeletedAsync(record.Id, ct))
                    {
                        removed++;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Counted as failed only: an unreachable API server is retried at the normal interval.
                    failed++;
                    LogDeleteFailed(record.Id, e);
                }
            }
        }

        await tx.CommitAsync(ct);
        return new ReconcilePass(true, applied, failed, removed, waiting);
    }

    /// <summary>
    /// Generations <c>oldest</c> … <c>current-1</c>, after the Shop already points at the current one.
    /// Missing is fine (an interrupted pass may have deleted some); the record is updated last, so a
    /// crash in between only repeats deletes of names that are already gone.
    /// </summary>
    private async Task DeleteOldWalletsAsync(IKubeGateway kube, ShopRecord record, CancellationToken ct)
    {
        if (record.OldestWalletGeneration >= record.WalletGeneration)
        {
            return;
        }

        for (var g = record.OldestWalletGeneration; g < record.WalletGeneration; g++)
        {
            await kube.DeleteWalletAsync(record.Id, g, ct);
        }

        await shops.MarkOldWalletsDeletedAsync(record.Id, record.WalletGeneration, ct);
    }

    public static ShopResources Resources(ShopRecord r) =>
        new(r.Id, r.OwnerId, r.DisplayName, r.Availability, r.Database, r.WalletAddress, r.WalletGeneration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying shop {ShopId} failed; retrying on the next pass.")]
    private partial void LogApplyFailed(string shopId, Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting shop {ShopId} failed; retrying on the next pass.")]
    private partial void LogDeleteFailed(string shopId, Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconcile pass failed; retrying in 30 s.")]
    private partial void LogPassFailed(Exception e);
}
