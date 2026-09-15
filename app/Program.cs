using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Npgsql;
using TicketsDashboard;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<LakebaseOptions>()
    .Bind(builder.Configuration.GetSection("Lakebase"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<IdentityOptions>()
    .Bind(builder.Configuration.GetSection("Identity"))
    .ValidateDataAnnotations()
    .Validate(options => !options.UseDeveloperCredential || builder.Environment.IsDevelopment(),
        "Developer credentials are permitted only in a local Development environment.")
    .ValidateOnStart();

builder.Services.AddSingleton<TokenCredential>(services =>
{
    var identity = services.GetRequiredService<IOptions<IdentityOptions>>().Value;
    if (identity.UseDeveloperCredential)
    {
        return new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = string.IsNullOrEmpty(identity.TenantId) ? null : identity.TenantId,
            ProcessTimeout = TimeSpan.FromSeconds(90)
        });
    }
    return new ManagedIdentityCredential(string.IsNullOrEmpty(identity.ManagedIdentityClientId)
        ? ManagedIdentityId.SystemAssigned
        : ManagedIdentityId.FromUserAssignedClientId(identity.ManagedIdentityClientId));
});

builder.Services.AddHttpClient<LakebaseCredentialProvider>((services, client) =>
{
    client.BaseAddress = new Uri(services.GetRequiredService<IOptions<LakebaseOptions>>().Value.WorkspaceUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton(LakebaseDataSourceFactory.Create);
builder.Services.AddScoped<TicketRepository>();
builder.Services.AddHostedService<ConnectionWarmup>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; base-uri 'none'; form-action 'none'";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/range", async (TicketRepository repository, CancellationToken cancellation) =>
{
    var range = await repository.GetDataRangeAsync(cancellation);
    return range is null
        ? Results.Ok(new { min = (string?)null, max = (string?)null })
        : Results.Ok(new { min = range.Value.Min.ToString("yyyy-MM-dd"), max = range.Value.Max.ToString("yyyy-MM-dd") });
});

app.MapGet("/api/dashboard", async (
    DateOnly? from, DateOnly? to, string? region, string? priority,
    TicketRepository repository, CancellationToken cancellation) =>
{
    var range = await repository.GetDataRangeAsync(cancellation);
    if (range is null) return Results.Ok(new DashboardData(new Kpis(0, null, null, null, null), [], [], []));

    var filter = new TicketFilter(
        from ?? range.Value.Min,
        to ?? range.Value.Max.AddDays(1),
        string.IsNullOrWhiteSpace(region) ? null : region,
        string.IsNullOrWhiteSpace(priority) ? null : priority);

    if (filter.Validate() is { } error)
        return Results.Problem(title: "InvalidFilter", detail: error, statusCode: StatusCodes.Status400BadRequest);

    return Results.Ok(await repository.GetDashboardAsync(filter, cancellation));
});

// Container Apps EasyAuth injects the signed-in principal; the app never validates tokens itself.
app.MapGet("/api/me", (HttpContext context) => Results.Ok(new
{
    name = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault()
}));

// Liveness must not depend on Lakebase: the compute may be resuming from scale-to-zero.
app.MapGet("/alive", () => Results.Ok(new { status = "alive" }));

app.MapGet("/healthz", async (NpgsqlDataSource dataSource, CancellationToken cancellation) =>
{
    await using var connection = await dataSource.OpenConnectionAsync(cancellation);
    await using var command = new NpgsqlCommand("SELECT 1", connection);
    await command.ExecuteScalarAsync(cancellation);
    return Results.Ok(new { status = "healthy" });
});

app.Run();
