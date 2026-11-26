using System.Data.Common;
using System.Globalization;

using Npgsql;
using NpgsqlTypes;

namespace Matgate.Services.Database;

// One change made in a table's data view: a cell changed ("update": the row by its key, the new values), a row
// added ("insert": its values; a column left out takes its default) or a row removed ("delete": by its key).
// Values are text as the data view shows them; null is NULL.
public sealed record RowChange(string Op, Dictionary<string, string?>? Key, Dictionary<string, string?>? Values);

// The statement of a change twice: as it is shown ("UPDATE ... SET x = 'a' WHERE id = 7") and as it runs - the
// same with parameters, so no value ever becomes part of the statement.
public sealed record RowStatement(string Op, string Preview, string Sql, IReadOnlyList<(string Name, string Column, string? Value)> Parameters);

// Editing a table's rows in the data view. A row is found by its primary key - a table without one is not
// edited (an UPDATE by all its values could hit several rows). Every name is checked against the table, every
// value goes in as a parameter. All changes run in one transaction: an UPDATE or DELETE that does not hit exactly
// one row (somebody changed or removed it meanwhile) undoes them all.
public sealed partial class DatabaseGateway
{
    public const int MaxRowChanges = 500;

    // Columns whose values the data view does not show whole (binary data as cut hex) or that cannot be written.
    public static bool IsEditableType(DatabaseEngine protocol, string type)
    {
        var lower = type.ToLowerInvariant();
        string[] binary = ["binary", "blob", "bytea", "image", "geometry", "geography", "hierarchyid", "rowversion", "sql_variant"];
        if (binary.Any(lower.Contains))
        {
            return false;
        }

        // In SQL Server "timestamp" is the row's version number, set by the server.
        return !(protocol == DatabaseEngine.MsSql && lower.StartsWith("timestamp", StringComparison.Ordinal));
    }

    public static List<RowStatement> RowStatements(DatabaseEngine protocol, string schema, string table, DbTableInfo info, IReadOnlyList<RowChange> changes, bool german)
    {
        if (info.Columns.Count == 0)
        {
            throw new DatabaseException(german ? "Die Tabelle gibt es nicht (mehr)." : "The table does not exist (any more).");
        }

        var keys = info.Columns.Where(column => column.PrimaryKey).ToList();
        if (keys.Count == 0)
        {
            throw new DatabaseException(german
                ? "Diese Tabelle hat keinen Primärschlüssel - ohne ihn lässt sich eine Zeile nicht sicher finden. Ändern geht hier nur über eine Abfrage."
                : "This table has no primary key - without one a row cannot be found safely. Change it through a query instead.");
        }

        if (changes.Count > MaxRowChanges)
        {
            throw new DatabaseException(german ? $"Höchstens {MaxRowChanges} Änderungen auf einmal." : $"At most {MaxRowChanges} changes at once.");
        }

        var dialect = DialectOf(protocol);
        var name = dialect.Table(schema, table);
        var byName = info.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        DbColumn Column(string column)
        {
            if (!byName.TryGetValue(column, out var found))
            {
                throw new DatabaseException(german ? $"Die Spalte „{column}“ gibt es in der Tabelle nicht." : $"The table has no column \"{column}\".");
            }

            return found;
        }

        var statements = new List<RowStatement>();
        foreach (var change in changes)
        {
            var parameters = new List<(string Name, string Column, string? Value)>();
            var preview = new List<string>();
            string Value(DbColumn column, string? value, bool forPreview)
            {
                if (forPreview)
                {
                    return Literal(protocol, value);
                }

                var parameter = "@p" + parameters.Count;
                parameters.Add((parameter, column.Name, value));
                return parameter;
            }

            (string Run, string Shown) Where()
            {
                var key = change.Key ?? [];
                foreach (var column in keys)
                {
                    if (!key.ContainsKey(column.Name) || key[column.Name] is null)
                    {
                        throw new DatabaseException(german ? $"Der Schlüssel „{column.Name}“ fehlt." : $"The key \"{column.Name}\" is missing.");
                    }
                }

                var run = string.Join(" AND ", keys.Select(column => dialect.Quote(column.Name) + " = " + Value(column, key[column.Name], false)));
                var shown = string.Join(" AND ", keys.Select(column => dialect.Quote(column.Name) + " = " + Value(column, key[column.Name], true)));
                return (run, shown);
            }

            void Writable(DbColumn column)
            {
                if (!IsEditableType(protocol, column.Type))
                {
                    throw new DatabaseException(german ? $"Die Spalte „{column.Name}“ ({column.Type}) lässt sich hier nicht bearbeiten." : $"The column \"{column.Name}\" ({column.Type}) cannot be edited here.");
                }
            }

            switch ((change.Op ?? "").ToLowerInvariant())
            {
                case "update":
                {
                    var values = (change.Values ?? []).Select(pair => (Column: Column(pair.Key), pair.Value)).ToList();
                    if (values.Count == 0)
                    {
                        continue;
                    }

                    values.ForEach(entry => Writable(entry.Column));
                    var set = string.Join(", ", values.Select(entry => dialect.Quote(entry.Column.Name) + " = " + Value(entry.Column, entry.Value, false)));
                    var setShown = string.Join(", ", values.Select(entry => dialect.Quote(entry.Column.Name) + " = " + Value(entry.Column, entry.Value, true)));
                    var (where, whereShown) = Where();
                    statements.Add(new RowStatement("update", $"UPDATE {name} SET {setShown} WHERE {whereShown};", $"UPDATE {name} SET {set} WHERE {where}", parameters));
                    break;
                }

                case "insert":
                {
                    var values = (change.Values ?? []).Select(pair => (Column: Column(pair.Key), pair.Value)).ToList();
                    values.ForEach(entry => Writable(entry.Column));
                    if (values.Count == 0)
                    {
                        var empty = protocol == DatabaseEngine.MySql ? $"INSERT INTO {name} () VALUES ()" : $"INSERT INTO {name} DEFAULT VALUES";
                        statements.Add(new RowStatement("insert", empty + ";", empty, parameters));
                        break;
                    }

                    var columns = string.Join(", ", values.Select(entry => dialect.Quote(entry.Column.Name)));
                    var run = string.Join(", ", values.Select(entry => Value(entry.Column, entry.Value, false)));
                    var shown = string.Join(", ", values.Select(entry => Value(entry.Column, entry.Value, true)));
                    statements.Add(new RowStatement("insert", $"INSERT INTO {name} ({columns}) VALUES ({shown});", $"INSERT INTO {name} ({columns}) VALUES ({run})", parameters));
                    break;
                }

                case "delete":
                {
                    var (where, whereShown) = Where();
                    statements.Add(new RowStatement("delete", $"DELETE FROM {name} WHERE {whereShown};", $"DELETE FROM {name} WHERE {where}", parameters));
                    break;
                }

                default:
                    throw new DatabaseException(german ? "Unbekannte Änderung." : "Unknown change.");
            }
        }

        return statements;
    }

    // A value as it is shown in the preview: NULL, or the text in quotes (N'...' for SQL Server's Unicode).
    private static string Literal(DatabaseEngine protocol, string? value) =>
        value is null ? "NULL" : (protocol == DatabaseEngine.MsSql ? "N'" : "'") + (protocol == DatabaseEngine.MySql ? value.Replace("\\", "\\\\") : value).Replace("'", "''") + "'";

    // All changes in one transaction. An UPDATE or DELETE that does not hit exactly one row undoes everything.
    public async Task<int> ApplyRowsAsync(DatabaseTarget server, string user, string password, string? database, DbTableInfo info, IReadOnlyList<RowStatement> statements, bool german, CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        var types = info.Columns.ToDictionary(column => column.Name, column => column.Type, StringComparer.Ordinal);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var number = 0;
        try
        {
            foreach (var statement in statements)
            {
                number++;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement.Sql;
                command.CommandTimeout = 60;
                foreach (var (name, column, value) in statement.Parameters)
                {
                    command.Parameters.Add(Parameter(command, server.Protocol, name, types[column], column, value, german));
                }

                int affected;
                try
                {
                    affected = await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (DbException exception)
                {
                    throw new DatabaseException((german ? $"Änderung {number} von {statements.Count}: " : $"Change {number} of {statements.Count}: ") + exception.Message + (german ? " - nichts wurde gespeichert." : " - nothing was saved."), exception);
                }

                if (statement.Op != "insert" && affected != 1)
                {
                    throw new DatabaseException(german
                        ? $"Änderung {number} von {statements.Count} traf {affected} Zeilen statt einer - die Zeile wurde inzwischen geändert oder gelöscht. Nichts wurde gespeichert; bitte neu laden."
                        : $"Change {number} of {statements.Count} hit {affected} rows instead of one - the row was changed or removed meanwhile. Nothing was saved; please reload.");
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return statements.Count;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollback) when (rollback is DbException or InvalidOperationException)
            {
                // The connection closes next, and with it the transaction.
            }

            throw;
        }
    }

    // PostgreSQL: the text as an untyped value, so the server reads it as it reads a literal - for every type,
    // its own ones included. SQL Server and MySQL: the text turned into the column's type first.
    private static DbParameter Parameter(DbCommand command, DatabaseEngine protocol, string name, string type, string column, string? value, bool german)
    {
        if (protocol == DatabaseEngine.Postgres)
        {
            return new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = (object?)value ?? DBNull.Value };
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is null ? DBNull.Value : Typed(protocol, type, column, value, german);
        return parameter;
    }

    public static object Typed(DatabaseEngine protocol, string type, string column, string value, bool german)
    {
        var lower = type.ToLowerInvariant();
        var invariant = CultureInfo.InvariantCulture;
        bool Is(params string[] words) => words.Any(word => lower.StartsWith(word, StringComparison.Ordinal));
        try
        {
            if (protocol == DatabaseEngine.MsSql && Is("bit") || protocol == DatabaseEngine.MySql && (lower.StartsWith("tinyint(1)", StringComparison.Ordinal) || Is("bool")))
            {
                return value.Trim().ToLowerInvariant() switch
                {
                    "1" or "true" or "wahr" => true,
                    "0" or "false" or "falsch" => false,
                    _ => throw new FormatException(),
                };
            }

            if (Is("tinyint", "smallint", "mediumint", "int", "bigint", "integer"))
            {
                return long.Parse(value.Trim(), NumberStyles.Integer, invariant);
            }

            if (Is("decimal", "numeric", "money", "smallmoney"))
            {
                return decimal.Parse(value.Trim(), NumberStyles.Number, invariant);
            }

            if (Is("float", "real", "double"))
            {
                return double.Parse(value.Trim(), NumberStyles.Float, invariant);
            }

            if (Is("datetimeoffset"))
            {
                return DateTimeOffset.Parse(value.Trim(), invariant);
            }

            if (Is("datetime", "smalldatetime", "date", "timestamp"))
            {
                return DateTime.Parse(value.Trim(), invariant, DateTimeStyles.None);
            }

            if (Is("time"))
            {
                return TimeSpan.Parse(value.Trim(), invariant);
            }

            if (Is("uniqueidentifier"))
            {
                return Guid.Parse(value.Trim());
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new DatabaseException(german ? $"Spalte „{column}“: „{value}“ ist kein Wert für {type}." : $"Column \"{column}\": \"{value}\" is no value for {type}.");
        }

        return value;
    }
}
