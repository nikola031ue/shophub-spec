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

/// <summary>What one pass did. <see cref="Ran"/> is false when another replica held the lock.</summary>
public sealed record ReconcilePass(bool Ran, int Applied, int Failed);

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
/// It works only from records and by exact names: it never lists or selects cluster objects, so
/// an object without a record is never touched. One shop failing doesn't stop the others; it is
/// retried on the next pass. Records in state <c>Deleting</c> are not applied (deletion is T022).
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

    /// <summary>"SHUB" + 2; the schema migrator uses + 1.</summary>
    public const long LockKey = 0x5348_5542_0002;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await trigger.WaitAsync(Interval, stoppingToken);
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

        var active = (await shops.ListAllForReconcilerAsync(ct)).Where(r => r.State == RecordState.Active).ToList();
        int applied = 0, failed = 0;
        if (active.Count > 0)
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
        }

        await tx.CommitAsync(ct);
        return new ReconcilePass(true, applied, failed);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconcile pass failed; retrying in 30 s.")]
    private partial void LogPassFailed(Exception e);
}
