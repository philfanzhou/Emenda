using System.Data.Common;

namespace Emenda.Database;

internal static class LegacySql
{
    internal static async Task<int> ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        Configure(command, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<HashSet<string>> ReadStringsAsync(DbConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        Configure(command, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (!reader.IsDBNull(0))
                rows.Add(reader.GetString(0));
        return rows;
    }

    private static void Configure(DbCommand command, string sql, (string Name, object Value)[] parameters)
    {
        command.CommandText = sql;
        command.CommandTimeout = 0;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
