using NpgsqlTypes;
using Npgsql;

namespace TicketsDashboard;

public sealed record TicketFilter(DateOnly From, DateOnly To, string? Region, string? Priority)
{
    public const int MaxSpanDays = 1000;

    public string? Validate()
    {
        if (To <= From) return "'to' must be after 'from'.";
        if (From.Year < 2000) return "'from' must be in year 2000 or later.";
        if (To.DayNumber - From.DayNumber > MaxSpanDays) return $"Range must not exceed {MaxSpanDays} days.";
        if (Region is { Length: > 64 } || Priority is { Length: > 64 }) return "Filter values are too long.";
        if (Region?.Any(char.IsControl) == true || Priority?.Any(char.IsControl) == true)
            return "Filter values contain invalid characters.";
        return null;
    }
}

public sealed record Kpis(
    long Tickets, double? AvgCost, double? AvgCsat, double? AvgSentiment, double? FirstTimeResolutionRate);

public sealed record DailyPoint(DateOnly Date, long Tickets, double? AvgCost);

public sealed record Slice(string Label, long Tickets, double? AvgCost);

public sealed record DashboardData(
    Kpis Kpis, IReadOnlyList<DailyPoint> Daily, IReadOnlyList<Slice> ByRegion, IReadOnlyList<Slice> ByPriority);

public sealed class TicketRepository(NpgsqlDataSource dataSource, Microsoft.Extensions.Options.IOptions<LakebaseOptions> options)
{
    // Safe to interpolate: both parts are regex-validated as bare lowercase identifiers at startup.
    private readonly string _table = $"\"{options.Value.Schema}\".\"{options.Value.Table}\"";

    private const string Predicate = """
        WHERE created_time >= @from AND created_time < @to
          AND (@region IS NULL OR continental_region = @region)
          AND (@priority IS NULL OR priority = @priority)
        """;

    public async Task<DashboardData> GetDashboardAsync(TicketFilter filter, CancellationToken cancellation)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellation);
        await using var batch = new NpgsqlBatch(connection);

        batch.BatchCommands.Add(Command($"""
            SELECT COUNT(*), AVG(operational_cost), AVG(csat_score), AVG(call_sentiment_score),
                   AVG(CASE WHEN first_time_resolution THEN 1.0 ELSE 0.0 END)
            FROM {_table}
            {Predicate}
            """, filter));

        batch.BatchCommands.Add(Command($"""
            SELECT created_time::date AS day, COUNT(*), AVG(operational_cost)
            FROM {_table}
            {Predicate}
            GROUP BY day
            ORDER BY day
            LIMIT {TicketFilter.MaxSpanDays}
            """, filter));

        batch.BatchCommands.Add(Command($"""
            SELECT COALESCE(continental_region, 'Unknown'), COUNT(*), AVG(operational_cost)
            FROM {_table}
            {Predicate}
            GROUP BY 1
            ORDER BY 2 DESC
            LIMIT 50
            """, filter));

        batch.BatchCommands.Add(Command($"""
            SELECT COALESCE(priority, 'Unknown'), COUNT(*), AVG(operational_cost)
            FROM {_table}
            {Predicate}
            GROUP BY 1
            ORDER BY 2 DESC
            LIMIT 50
            """, filter));

        await using var reader = await batch.ExecuteReaderAsync(cancellation);

        await reader.ReadAsync(cancellation);
        var kpis = new Kpis(
            reader.GetInt64(0), await NullableDouble(reader, 1), await NullableDouble(reader, 2),
            await NullableDouble(reader, 3), await NullableDouble(reader, 4));

        await reader.NextResultAsync(cancellation);
        var daily = new List<DailyPoint>();
        while (await reader.ReadAsync(cancellation))
        {
            daily.Add(new DailyPoint(
                DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetInt64(1), await NullableDouble(reader, 2)));
        }

        await reader.NextResultAsync(cancellation);
        var byRegion = await ReadSlices(reader, cancellation);

        await reader.NextResultAsync(cancellation);
        var byPriority = await ReadSlices(reader, cancellation);

        return new DashboardData(kpis, daily, byRegion, byPriority);
    }

    public async Task<(DateOnly Min, DateOnly Max)?> GetDataRangeAsync(CancellationToken cancellation)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellation);
        await using var command = new NpgsqlCommand(
            $"SELECT MIN(created_time)::date, MAX(created_time)::date FROM {_table}", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellation);

        if (!await reader.ReadAsync(cancellation) || await reader.IsDBNullAsync(0)) return null;
        return (DateOnly.FromDateTime(reader.GetDateTime(0)), DateOnly.FromDateTime(reader.GetDateTime(1)));
    }

    private static async Task<List<Slice>> ReadSlices(NpgsqlDataReader reader, CancellationToken cancellation)
    {
        var slices = new List<Slice>();
        while (await reader.ReadAsync(cancellation))
            slices.Add(new Slice(reader.GetString(0), reader.GetInt64(1), await NullableDouble(reader, 2)));
        return slices;
    }

    private static async Task<double?> NullableDouble(NpgsqlDataReader reader, int ordinal) =>
        await reader.IsDBNullAsync(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal));

    private static NpgsqlBatchCommand Command(string sql, TicketFilter filter)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz)
        {
            Value = filter.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz)
        {
            Value = filter.To.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        });
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Text)
        {
            Value = (object?)filter.Region ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("priority", NpgsqlDbType.Text)
        {
            Value = (object?)filter.Priority ?? DBNull.Value
        });
        return command;
    }
}
