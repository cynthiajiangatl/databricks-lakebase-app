using Microsoft.Extensions.Options;
using Azure.Identity;
using TicketsDashboard;
using Xunit;

namespace TicketsDashboard.Tests;

/// <summary>
/// Runs only when Lakebase connection details are present, so the suite stays
/// green on machines and build agents without access to the database.
/// </summary>
public sealed class LakebaseFactAttribute : FactAttribute
{
    public LakebaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAKEBASE_HOST")))
            Skip = "Set LAKEBASE_HOST, LAKEBASE_ROLE and LAKEBASE_TENANT_ID to run Lakebase integration tests.";
    }
}

public class LakebaseIntegrationTests
{
    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} not set.");

    private static (TicketRepository Repository, IDisposable Scope) Build()
    {
        var options = Options.Create(new LakebaseOptions
        {
            WorkspaceUrl = Env("LAKEBASE_WORKSPACE_URL"),
            EndpointName = Env("LAKEBASE_ENDPOINT"),
            Host = Env("LAKEBASE_HOST"),
            Database = "databricks_postgres",
            Role = Env("LAKEBASE_ROLE"),
            Schema = "dbdemos_aibi_customer_support",
            Table = "lb_tickets_clean"
        });

        var http = new HttpClient { BaseAddress = new Uri(options.Value.WorkspaceUrl) };
        var credential = new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = Env("LAKEBASE_TENANT_ID"),
            ProcessTimeout = TimeSpan.FromSeconds(90)
        });

        var services = new ServiceCollectionStub(options, new LakebaseCredentialProvider(http, credential, options));
        var dataSource = LakebaseDataSourceFactory.Create(services);
        return (new TicketRepository(dataSource, options), dataSource);
    }

    [LakebaseFact]
    public async Task DataRangeCoversTheSyncedDataset()
    {
        var (repository, scope) = Build();
        using (scope)
        {
            var range = await repository.GetDataRangeAsync(default);

            Assert.NotNull(range);
            Assert.True(range!.Value.Min < range.Value.Max);
            Assert.True(range.Value.Min.Year >= 2024);
        }
    }

    [LakebaseFact]
    public async Task DashboardAggregatesAreInternallyConsistent()
    {
        var (repository, scope) = Build();
        using (scope)
        {
            var range = await repository.GetDataRangeAsync(default);
            var filter = new TicketFilter(range!.Value.Min, range.Value.Max.AddDays(1), null, null);

            var data = await repository.GetDashboardAsync(filter, default);

            Assert.True(data.Kpis.Tickets > 0);
            // Every ticket must land in exactly one region bucket and one priority bucket.
            Assert.Equal(data.Kpis.Tickets, data.ByRegion.Sum(s => s.Tickets));
            Assert.Equal(data.Kpis.Tickets, data.ByPriority.Sum(s => s.Tickets));
            Assert.Equal(data.Kpis.Tickets, data.Daily.Sum(d => d.Tickets));
        }
    }

    [LakebaseFact]
    public async Task RegionFilterNarrowsTheResultSet()
    {
        var (repository, scope) = Build();
        using (scope)
        {
            var range = await repository.GetDataRangeAsync(default);
            var from = range!.Value.Min;
            var to = range.Value.Max.AddDays(1);

            var all = await repository.GetDashboardAsync(new TicketFilter(from, to, null, null), default);
            var emea = await repository.GetDashboardAsync(new TicketFilter(from, to, "EMEA", null), default);

            Assert.True(emea.Kpis.Tickets < all.Kpis.Tickets);
            Assert.Single(emea.ByRegion);
            Assert.Equal("EMEA", emea.ByRegion[0].Label);
        }
    }

    [LakebaseFact]
    public async Task UnknownRegionYieldsEmptyResultRatherThanError()
    {
        var (repository, scope) = Build();
        using (scope)
        {
            var range = await repository.GetDataRangeAsync(default);
            var filter = new TicketFilter(range!.Value.Min, range.Value.Max.AddDays(1), "NO_SUCH_REGION", null);

            var data = await repository.GetDashboardAsync(filter, default);

            Assert.Equal(0, data.Kpis.Tickets);
            Assert.Empty(data.Daily);
        }
    }

    /// <summary>Minimal resolver so the factory can be exercised without a full host.</summary>
    private sealed class ServiceCollectionStub(
        IOptions<LakebaseOptions> options, LakebaseCredentialProvider provider) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IOptions<LakebaseOptions>) ? options
            : serviceType == typeof(LakebaseCredentialProvider) ? provider
            : null;
    }
}
