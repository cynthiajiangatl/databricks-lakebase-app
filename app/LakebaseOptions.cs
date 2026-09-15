using System.ComponentModel.DataAnnotations;

namespace TicketsDashboard;

public sealed class LakebaseOptions : IValidatableObject
{
    [Required] public string WorkspaceUrl { get; set; } = "";

    /// <summary>Fully qualified endpoint, e.g. projects/p1/branches/production/endpoints/primary.</summary>
    [Required, RegularExpression(@"^projects/[\w-]{1,64}/branches/[\w-]{1,64}/endpoints/[\w-]{1,64}$")]
    public string EndpointName { get; set; } = "";

    [Required] public string Host { get; set; } = "";

    [Required, RegularExpression("^[a-zA-Z_][a-zA-Z0-9_]{0,62}$")]
    public string Database { get; set; } = "databricks_postgres";

    /// <summary>Postgres role. For a managed identity this is its client ID.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Role { get; set; } = "";

    [Required, RegularExpression("^[a-z_][a-z0-9_]{0,62}$")]
    public string Schema { get; set; } = "";

    [Required, RegularExpression("^[a-z_][a-z0-9_]{0,62}$")]
    public string Table { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(WorkspaceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !uri.Host.EndsWith(".azuredatabricks.net", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
        {
            yield return new ValidationResult(
                "WorkspaceUrl must be an Azure public-cloud Databricks HTTPS workspace origin.",
                [nameof(WorkspaceUrl)]);
        }

        if (!Host.EndsWith(".azuredatabricks.net", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                "Host must be an Azure Databricks Lakebase endpoint host.", [nameof(Host)]);
        }

        // The PgBouncer pooler rejects Lakebase OAuth credentials with "SASL authentication failed".
        if (Host.Contains("-pooler.", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                "Use the direct endpoint host, not the pooled host: the pooler does not accept OAuth credentials. " +
                "Connections are pooled in-process by NpgsqlDataSource instead.",
                [nameof(Host)]);
        }
    }
}

public sealed class IdentityOptions
{
    public bool UseDeveloperCredential { get; set; }

    [RegularExpression("^$|^[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}$")]
    public string ManagedIdentityClientId { get; set; } = "";

    [RegularExpression("^$|^[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}$")]
    public string TenantId { get; set; } = "";
}
