using Npgsql;

namespace ShopHub.Api.Storage.Postgres;

/// <summary>
/// Applies the schema before Kestrel listens (hosted services start first), retrying with backoff
/// capped at 10 s, as shop's <c>StoreInitializer</c>. Until it succeeds the port stays closed, so
/// the readiness probe fails instead of the app answering without a database.
/// </summary>
public sealed partial class DatabaseInitializer(
    IServiceProvider services,
    TimeProvider time,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving these validates the configuration; a SettingsException stops the start.
        services.GetRequiredService<Settings>();
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();

        var delay = FirstDelay;
        string? lastError = null;
        while (true)
        {
            try
            {
                await SchemaMigrator.MigrateAsync(dataSource, cancellationToken);
                LogReady(logger);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Logged even when the start is being cancelled, so the reason is never lost.
                var error = ex.GetType().Name + ": " + Redaction.Redact(ex.Message);
                if (error != lastError)
                {
                    LogWaiting(logger, error);
                    lastError = error;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(delay, time, cancellationToken);
                delay = NextDelay(delay);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static TimeSpan NextDelay(TimeSpan delay) => delay * 2 < MaxDelay ? delay * 2 : MaxDelay;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database ready")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for the database, retrying: {Error}")]
    private static partial void LogWaiting(ILogger logger, string error);
}
