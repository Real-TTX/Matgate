using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace Matgate.Services.Database;

// A table or view of a database, as the browser lists it.
public sealed record DbObject(string Schema, string Name, string Kind);

// What a statement gave back: the columns and rows of its first result (as text, as the browser shows them),
// or how many rows it changed. Truncated says there were more rows than were read.
public sealed record DbResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string?[]> Rows,
    int? Affected,
    bool Truncated,
    long ElapsedMs,
    long? Total = null);

public sealed class DatabaseException(string message, Exception? inner = null) : Exception(message, inner);

// What differs between the database servers: how to connect, how to list, how to quote, how to page. All
// the rest - running a statement, reading its rows, turning values into text - is the same for every one of
// them and lives in DatabaseGateway, as the file connections share FileGatewayService.
public interface IDatabaseDialect
{
    DbConnection Create(DatabaseTarget server, string user, string password, string? database);

    string ListDatabases { get; }

    // The tables and views of one database (the database's name is the parameter @db where it is needed).
    string ListObjects { get; }

    string Quote(string identifier);

    // The schema part of a table's name: MySQL has none of its own (the database is the schema).
    string Table(string schema, string name) => Quote(schema) + "." + Quote(name);

    string Page(string table, string? orderColumn, bool descending, int offset, int limit);
}

public sealed class SqlServerDialect : IDatabaseDialect
{
    public DbConnection Create(DatabaseTarget server, string user, string password, string? database) =>
        new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = $"{server.ConnectHost},{server.ConnectPort}",
            HostNameInCertificate = server.DialHost.Length > 0 ? server.Host : "",
            UserID = user,
            Password = password,
            InitialCatalog = database ?? "",
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = server.IgnoreCertificate,
            ConnectTimeout = 15,
            ApplicationName = server.ApplicationName,
            Pooling = false,
        }.ConnectionString);

    public string ListDatabases => "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 ORDER BY name";

    public string ListObjects => "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME";

    public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public string Page(string table, string? orderColumn, bool descending, int offset, int limit) =>
        $"SELECT * FROM {table} ORDER BY {(orderColumn is null ? "(SELECT NULL)" : Quote(orderColumn) + (descending ? " DESC" : ""))} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
}

public sealed class MySqlDialect : IDatabaseDialect
{
    public DbConnection Create(DatabaseTarget server, string user, string password, string? database) =>
        new MySqlConnection(new MySqlConnectionStringBuilder
        {
            Server = server.ConnectHost,
            Port = (uint)server.ConnectPort,
            UserID = user,
            Password = password,
            Database = database ?? "",
            SslMode = MySqlSslMode.Preferred,
            AllowPublicKeyRetrieval = true,
            ConnectionTimeout = 15,
            Pooling = false,
        }.ConnectionString);

    public string ListDatabases => "SHOW DATABASES";

    public string ListObjects => "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME";

    public string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public string Page(string table, string? orderColumn, bool descending, int offset, int limit) =>
        $"SELECT * FROM {table}{(orderColumn is null ? "" : " ORDER BY " + Quote(orderColumn) + (descending ? " DESC" : ""))} LIMIT {limit} OFFSET {offset}";
}

public sealed class PostgresDialect : IDatabaseDialect
{
    public DbConnection Create(DatabaseTarget server, string user, string password, string? database) =>
        new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = server.ConnectHost,
            Port = server.ConnectPort,
            Username = user,
            Password = password,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            SslMode = SslMode.Prefer,
            Timeout = 15,
            ApplicationName = server.ApplicationName,
            Pooling = false,
        }.ConnectionString);

    public string ListDatabases => "SELECT datname FROM pg_database WHERE NOT datistemplate AND has_database_privilege(datname, 'CONNECT') ORDER BY datname";

    public string ListObjects => "SELECT table_schema, table_name, table_type FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY table_schema, table_name";

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string Page(string table, string? orderColumn, bool descending, int offset, int limit) =>
        $"SELECT * FROM {table}{(orderColumn is null ? "" : " ORDER BY " + Quote(orderColumn) + (descending ? " DESC" : ""))} LIMIT {limit} OFFSET {offset}";
}

public sealed partial class DatabaseGateway
{
    public const int MaxRows = 1000;
    public const int MaxCellLength = 4000;

    public static IDatabaseDialect DialectOf(DatabaseEngine protocol) => protocol switch
    {
        DatabaseEngine.MsSql => new SqlServerDialect(),
        DatabaseEngine.MySql => new MySqlDialect(),
        DatabaseEngine.Postgres => new PostgresDialect(),
        _ => throw new DatabaseException("Not a database connection."),
    };

    public async Task<List<string>> DatabasesAsync(DatabaseTarget server, string user, string password, CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        var result = await RunAsync(server, user, password, null, dialect.ListDatabases, MaxRows, null, cancellationToken);
        return [.. result.Rows.Select(row => row[0] ?? "").Where(name => name.Length > 0)];
    }

    public async Task<List<DbObject>> ObjectsAsync(DatabaseTarget server, string user, string password, string database, CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        var result = await RunAsync(server, user, password, database, dialect.ListObjects, 20000, new() { ["db"] = database }, cancellationToken);
        return [.. result.Rows.Select(row => new DbObject(
            row[0] ?? "",
            row[1] ?? "",
            (row[2] ?? "").Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table"))];
    }

    // One page of a table, ordered by one of its columns. The column is taken only when the table has it - a
    // name from the browser is never written into a statement unchecked.
    public async Task<DbResult> RowsAsync(
        DatabaseTarget server, string user, string password, string database,
        string schema, string table, int offset, int limit, string? orderColumn, bool descending,
        CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        var name = dialect.Table(schema, table);
        limit = Math.Clamp(limit, 1, MaxRows);
        offset = Math.Max(0, offset);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        var columns = await ColumnsAsync(connection, name, cancellationToken);
        var order = orderColumn is not null && columns.Contains(orderColumn, StringComparer.Ordinal) ? orderColumn : null;
        var page = await ReadAsync(connection, dialect.Page(name, order, descending, offset, limit), limit, null, cancellationToken);
        long? total = null;
        try
        {
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM " + name;
            count.CommandTimeout = 10;
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        catch (DbException)
        {
            // Too big to count in time: the browser does without the number.
        }

        return page with { Total = total };
    }

    // A statement typed into the browser: run as written, with the connection's login - which may do what that
    // login may do. The rows of the first result are read, up to MaxRows.
    // Read-only (the user may only read this connection): see ReadOnlySql - checked before, and run inside a
    // read-only transaction that is rolled back after.
    public async Task<DbResult> QueryAsync(DatabaseTarget server, string user, string password, string? database, string sql, bool readOnly, bool german, CancellationToken cancellationToken)
    {
        if (!readOnly)
        {
            return await RunAsync(server, user, password, database, sql, MaxRows, null, cancellationToken);
        }

        if (ReadOnlySql.Refusal(sql, server.Protocol, german) is { } refusal)
        {
            throw new DatabaseException(refusal);
        }

        var dialect = DialectOf(server.Protocol);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        await ExecuteAsync(connection, ReadOnlySql.Begin(server.Protocol), cancellationToken);
        try
        {
            return await ReadAsync(connection, sql, MaxRows, null, cancellationToken);
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

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException exception)
        {
            throw new DatabaseException(exception.Message, exception);
        }
    }

    private static async Task<DbResult> RunAsync(
        DatabaseTarget server, string user, string password, string? database, string sql, int maxRows,
        Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        return await ReadAsync(connection, sql, maxRows, parameters, cancellationToken);
    }

    private static async Task<DbConnection> OpenAsync(IDatabaseDialect dialect, DatabaseTarget server, string user, string password, string? database, CancellationToken cancellationToken)
    {
        var chosen = string.IsNullOrWhiteSpace(database) ? (string.IsNullOrWhiteSpace(server.DatabaseName) ? null : server.DatabaseName) : database;
        var connection = dialect.Create(server, user, password, chosen);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException or ArgumentException)
        {
            await connection.DisposeAsync();
            throw new DatabaseException(exception.Message, exception);
        }
    }

    private static async Task<List<string>> ColumnsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM " + table + " WHERE 1 = 0";
        command.CommandTimeout = 15;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
        }
        catch (DbException exception)
        {
            throw new DatabaseException(exception.Message, exception);
        }
    }

    private static async Task<DbResult> ReadAsync(DbConnection connection, string sql, int maxRows, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        foreach (var (key, value) in parameters ?? [])
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            // The first result that has columns; a batch of statements may begin with ones that have none.
            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken))
            {
            }

            if (reader.FieldCount == 0)
            {
                return new DbResult([], [], reader.RecordsAffected, false, watch.ElapsedMilliseconds);
            }

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<string?[]>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= maxRows)
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

            return new DbResult(columns, rows, null, truncated, watch.ElapsedMilliseconds);
        }
        catch (DbException exception)
        {
            throw new DatabaseException(exception.Message, exception);
        }
    }

    // A value as the browser shows it: dates as ISO, numbers without the server's culture, binary data as hex.
    private static string Text(object value)
    {
        var text = value switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))) + (bytes.Length > 64 ? "…" : ""),
            DateTime date => date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return text.Length > MaxCellLength ? text[..MaxCellLength] + "…" : text;
    }
}
