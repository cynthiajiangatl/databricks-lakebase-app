using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TicketsDashboard;

/// <summary>
/// Exchanges an Entra ID token for a short-lived Lakebase database credential.
/// No Postgres password is ever stored: the project has native login disabled.
/// </summary>
public sealed class LakebaseCredentialProvider(
    HttpClient http, TokenCredential credential, IOptions<LakebaseOptions> options)
{
    /// <summary>Resource ID of the Azure Databricks first-party application.</summary>
    internal const string DatabricksScope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

    public async ValueTask<string> GetDatabaseCredentialAsync(CancellationToken cancellation)
    {
        var entraToken = await credential.GetTokenAsync(
            new TokenRequestContext([DatabricksScope]), cancellation);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/2.0/postgres/credentials")
        {
            Content = new StringContent(
                $$"""{"endpoint":{{JsonSerializer.Serialize(options.Value.EndpointName)}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entraToken.Token);

        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new LakebaseException(
                $"Databricks rejected the database credential request ({(int)response.StatusCode}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        if (!document.RootElement.TryGetProperty("token", out var token) ||
            token.GetString() is not { Length: > 0 } value)
        {
            throw new LakebaseException("The database credential response did not contain a token.");
        }
        return value;
    }
}

public sealed class LakebaseException(string message) : Exception(message);

public static class LakebaseDataSourceFactory
{
    /// <summary>Credentials last 60 minutes; refresh well before expiry so no request sees an expired one.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    public static NpgsqlDataSource Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<LakebaseOptions>>().Value;
        var provider = services.GetRequiredService<LakebaseCredentialProvider>();

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = 5432,
            Database = options.Database,
            Username = options.Role,
            SslMode = SslMode.VerifyFull,
            Timeout = 30,
            CommandTimeout = 30,
            MaxPoolSize = 20,
            // The endpoint scales to zero after inactivity; let idle connections lapse rather than fail.
            ConnectionIdleLifetime = 240,
            ApplicationName = "tickets-dashboard"
        }.ConnectionString;

        return new NpgsqlDataSourceBuilder(connectionString)
            .UsePeriodicPasswordProvider(
                (_, cancellation) => provider.GetDatabaseCredentialAsync(cancellation),
                RefreshInterval,
                RetryInterval)
            .Build();
    }
}
