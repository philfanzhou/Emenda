using System.Data.Common;

namespace Ruoyu.Study.Mistake.Database;

/// <summary>
/// Transaction-scoped staging for data the upgrade removes or rewrites. Rows are captured before the change and
/// copied into <c>legacy_archive</c> once the target schema exists; the staging table disappears on commit or rollback.
/// </summary>
internal sealed class LegacyArchiveStage(DbConnection connection)
{
    private const string Table = "pg_temp.mistake_legacy_stage";
    private bool _created;

    public long Count { get; private set; }

    /// <summary>Stages the rows of a query returning (source_table, source_key, kind, payload, reason).</summary>
    public async Task<long> AddAsync(string select, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var added = await ExecuteAsync(connection,
            $"INSERT INTO {Table} (source_table,source_key,kind,payload,reason) {select}", cancellationToken, parameters)
            .ConfigureAwait(false);
        Count += added;
        return added;
    }

    /// <summary>Copies every staged row into <c>legacy_archive</c> and verifies the count.</summary>
    public async Task<long> FlushAsync(CancellationToken cancellationToken)
    {
        if (!_created || Count == 0)
            return 0;
        var copied = await ExecuteAsync(connection, $"""
            INSERT INTO public.legacy_archive (source_table,source_key,kind,payload,reason,archived_at)
            SELECT source_table,source_key,kind,payload,reason,now() FROM {Table} ORDER BY seq
            """, cancellationToken).ConfigureAwait(false);
        if (copied != Count)
            throw new UpgradeStepException($"Mistake legacy archive copied {copied} of {Count} staged rows.");
        return copied;
    }

    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_created)
            return;
        await ExecuteAsync(connection, $"""
            CREATE TEMP TABLE mistake_legacy_stage (
              seq bigint GENERATED ALWAYS AS IDENTITY,
              source_table text NOT NULL, source_key text NOT NULL, kind text NOT NULL,
              payload jsonb NOT NULL, reason text NOT NULL) ON COMMIT DROP
            """, cancellationToken).ConfigureAwait(false);
        _created = true;
    }

    internal static async Task<int> ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
