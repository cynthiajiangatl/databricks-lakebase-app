using System.ComponentModel.DataAnnotations;
using TicketsDashboard;
using Xunit;

namespace TicketsDashboard.Tests;

public class LakebaseOptionsTests
{
    private static LakebaseOptions Valid() => new()
    {
        WorkspaceUrl = "https://adb-7405619192018422.2.azuredatabricks.net",
        EndpointName = "projects/lakebaseproject1/branches/production/endpoints/primary",
        Host = "ep-twilight-rice-e1ipdt0o.database.eastus2.azuredatabricks.net",
        Database = "databricks_postgres",
        Role = "admin@example.com",
        Schema = "dbdemos_aibi_customer_support",
        Table = "lb_tickets_clean"
    };

    private static List<ValidationResult> Validate(LakebaseOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void RealConfigurationIsAccepted()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Fact]
    public void PooledHostIsRejectedBecauseItCannotAcceptOAuthCredentials()
    {
        var options = Valid();
        options.Host = "ep-twilight-rice-e1ipdt0o-pooler.database.eastus2.azuredatabricks.net";

        var errors = Validate(options);

        Assert.Contains(errors, e => e.ErrorMessage!.Contains("pooler", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("http://adb-123.azuredatabricks.net")]
    [InlineData("https://evil.example.com")]
    [InlineData("https://adb-123.azuredatabricks.net:8443")]
    public void OnlyAzureDatabricksHttpsOriginsAreAccepted(string url)
    {
        var options = Valid();
        options.WorkspaceUrl = url;

        Assert.NotEmpty(Validate(options));
    }

    [Theory]
    [InlineData("lb_tickets_clean; DROP TABLE users")]
    [InlineData("Tickets")]
    [InlineData("\"quoted\"")]
    public void TableNamesAreRestrictedToBareIdentifiers(string table)
    {
        // The table name is interpolated into SQL, so the regex is the injection boundary.
        var options = Valid();
        options.Table = table;

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void EndpointNameMustBeFullyQualified()
    {
        var options = Valid();
        options.EndpointName = "lakebaseproject1";

        Assert.NotEmpty(Validate(options));
    }
}
