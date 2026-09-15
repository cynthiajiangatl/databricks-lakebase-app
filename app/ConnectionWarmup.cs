using Npgsql;

namespace TicketsDashboard;

/// <summary>
/// Opens one connection at startup so the first real request does not pay for the
/// Entra token exchange mid-handshake, which Postgres cuts off as an auth timeout.
/// </summary>
public sealed class ConnectionWarmup(
    NpgsqlDataSource dataSource, ILogger<ConnectionWarmup> logger) : IHostedService
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Budget);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
            logger.LogInformation("Lakebase connection warmed.");
        }
        catch (Exception ex)
        {
            // Never block startup: the compute may be resuming from scale-to-zero.
            logger.LogWarning(ex, "Lakebase warmup did not complete; the first request may be slower.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
