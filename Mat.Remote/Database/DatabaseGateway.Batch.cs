using System.Data.Common;
using System.Diagnostics;
using System.Globalization;


namespace Matgate.Services.Database;

// Everything a batch of statements gave back - each result that has rows as one result, and how many rows were
// changed by the ones that have none - as SQL Server Management Studio shows it, one result after the other.
public sealed record DbBatch(IReadOnlyList<DbResult> Results, int? Affected, long ElapsedMs);

public sealed partial class DatabaseGateway
{
    // At most this many results of one batch are read; the rest are left.
    public const int MaxResults = 20;

    // A batch typed into the browser: every result it gives, each up to MaxRows. Read-only as QueryAsync.
    public async Task<DbBatch> BatchAsync(DatabaseTarget server, string user, string password, string? database, string sql, bool readOnly, bool german, CancellationToken cancellationToken)
    {
        return await WithConnectionAsync(server, user, password, database, sql, readOnly, german, async connection =>
        {
            var watch = Stopwatch.StartNew();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            var results = new List<DbResult>();
            int? affected = null;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    if (reader.FieldCount == 0)
                    {
                        continue;
                    }

                    var started = watch.ElapsedMilliseconds;
                    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                    var rows = new List<string?[]>();
                    var truncated = false;
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (rows.Count >= MaxRows)
                        {
                            truncated = true;
                            break;
                        }

                        var row = new string?[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row[i] = await reader.IsDBNullAsync(i, cancellationToken) ? null : Text(reader.GetValue(i));
                        }

                        rows.Add(row);
                    }

                    results.Add(new DbResult(columns, rows, null, truncated, watch.ElapsedMilliseconds - started));
                }
                while (results.Count < MaxResults && await reader.NextResultAsync(cancellationToken));

                await reader.CloseAsync();
                affected = reader.RecordsAffected >= 0 ? reader.RecordsAffected : null;
            }
            catch (DbException exception)
            {
                throw new DatabaseException(exception.Message, exception);
            }
            catch (OperationCanceledException)
            {
                throw new DatabaseException(german ? "Abgebrochen." : "Cancelled.");
            }

            return new DbBatch(results, affected, watch.ElapsedMilliseconds);
        }, cancellationToken);
    }

    // The rows of a statement's first result, all of them (up to maxRows), handed out one by one - for an export
    // that does not fit into a page. Values are whole here: no cut after MaxCellLength, binary data fully as hex.
    public async Task ExportAsync(
        DatabaseTarget server, string user, string password, string? database, string sql, bool readOnly, bool german, long maxRows,
        Func<IReadOnlyList<string>, Task> columns, Func<string?[], Task> row, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(server, user, password, database, sql, readOnly, german, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken))
                {
                }

                await columns([.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)]);
                long count = 0;
                while (reader.FieldCount > 0 && count < maxRows && await reader.ReadAsync(cancellationToken))
                {
                    var values = new string?[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        values[i] = await reader.IsDBNullAsync(i, cancellationToken) ? null : WholeText(reader.GetValue(i));
                    }

                    await row(values);
                    count++;
                }
            }
            catch (DbException exception)
            {
                throw new DatabaseException(exception.Message, exception);
            }

            return true;
        }, cancellationToken);
    }

    // A connection for a statement typed in - read-only where the user may only read (see ReadOnlySql).
    private async Task<T> WithConnectionAsync<T>(DatabaseTarget server, string user, string password, string? database, string sql, bool readOnly, bool german, Func<DbConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        if (readOnly && ReadOnlySql.Refusal(sql, server.Protocol, german) is { } refusal)
        {
            throw new DatabaseException(refusal);
        }

        var dialect = DialectOf(server.Protocol);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        if (!readOnly)
        {
            return await work(connection);
        }

        await ExecuteAsync(connection, ReadOnlySql.Begin(server.Protocol), cancellationToken);
        try
        {
            return await work(connection);
        }
        finally
        {
            try
            {
                await ExecuteAsync(connection, ReadOnlySql.End(server.Protocol), CancellationToken.None);
            }
            catch (DatabaseException)
            {
                // The connection closes next, and with it whatever is left of the transaction.
            }
        }
    }

    private static string WholeText(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime date => date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

public sealed partial class DatabaseGateway
{
    // The designer's statements, one after the other. PostgreSQL and SQL Server change a table's structure inside a
    // transaction: all of them or none. MySQL ends a transaction with every such statement - there they run one by
    // one, and a failure says how many had already run.
    public async Task<int> ApplyAsync(DatabaseTarget server, string user, string password, string? database, IReadOnlyList<string> statements, bool german, CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        var transactional = server.Protocol != DatabaseEngine.MySql;
        if (transactional)
        {
            await ExecuteAsync(connection, server.Protocol == DatabaseEngine.MsSql ? "BEGIN TRANSACTION" : "BEGIN", cancellationToken);
        }

        var ran = 0;
        try
        {
            foreach (var statement in statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.CommandTimeout = 120;
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (System.Data.Common.DbException exception)
                {
                    throw new DatabaseException(
                        (german ? $"Anweisung {ran + 1} von {statements.Count}: " : $"Statement {ran + 1} of {statements.Count}: ") + exception.Message
                        + (transactional ? (german ? " - nichts wurde geändert." : " - nothing was changed.")
                            : ran > 0 ? (german ? $" - die {ran} davor sind gelaufen." : $" - the {ran} before it have run.") : ""),
                        exception);
                }

                ran++;
            }

            if (transactional)
            {
                await ExecuteAsync(connection, server.Protocol == DatabaseEngine.MsSql ? "COMMIT TRANSACTION" : "COMMIT", cancellationToken);
            }

            return ran;
        }
        catch
        {
            if (transactional)
            {
                try
                {
                    await ExecuteAsync(connection, server.Protocol == DatabaseEngine.MsSql ? "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION" : "ROLLBACK", CancellationToken.None);
                }
                catch (DatabaseException)
                {
                    // The connection closes next, and with it the transaction.
                }
            }

            throw;
        }
    }
}
