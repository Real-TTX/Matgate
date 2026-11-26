using System.Text;


namespace Matgate.Services.Database;

// A column of a table, as the object explorer and the designer show it.
public sealed record DbColumn(string Name, string Type, bool Nullable, string? Default, bool PrimaryKey, bool Identity);

public sealed record DbIndex(string Name, string Columns, bool Unique, bool Primary, string Definition);

public sealed record DbForeignKey(string Name, string Columns, string ReferencedTable, string ReferencedColumns, string OnDelete, string OnUpdate);

public sealed record DbRoutine(string Schema, string Name, string Kind);

public sealed record DbTableInfo(IReadOnlyList<DbColumn> Columns, IReadOnlyList<DbIndex> Indexes, IReadOnlyList<DbForeignKey> ForeignKeys);

// What lies in a database beyond its tables and views - their columns, keys, indexes and foreign keys, the
// procedures and functions - and each of them as the statement that makes it ("script as CREATE"). Read from each
// server's own catalog: information_schema says too little about keys and indexes on every one of them. Every name
// from the browser goes in as a parameter, never into the statement.
public sealed partial class DatabaseGateway
{
    private const int CatalogRows = 20000;

    private async Task<List<string?[]>> CatalogAsync(DatabaseTarget server, string user, string password, string database, string sql, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var result = await RunAsync(server, user, password, database, sql, CatalogRows, parameters, cancellationToken);
        return [.. result.Rows];
    }

    private static bool Flag(string? value) => value is "1" or "true" or "True" or "t" or "YES";

    public async Task<DbTableInfo> TableAsync(DatabaseTarget server, string user, string password, string database, string schema, string table, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["db"] = database, ["schema"] = schema, ["table"] = table };
        var (columnsSql, indexesSql, keysSql) = server.Protocol switch
        {
            DatabaseEngine.Postgres => (
                """
                SELECT a.attname, format_type(a.atttypid, a.atttypmod), CASE WHEN a.attnotnull THEN 0 ELSE 1 END, pg_get_expr(d.adbin, d.adrelid),
                       CASE WHEN EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY(i.indkey)) THEN 1 ELSE 0 END,
                       CASE WHEN a.attidentity <> '' OR pg_get_expr(d.adbin, d.adrelid) LIKE 'nextval(%' THEN 1 ELSE 0 END
                FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY a.attnum
                """,
                """
                SELECT i.relname,
                       array_to_string(ARRAY(SELECT pg_get_indexdef(ix.indexrelid, k + 1, true) FROM generate_subscripts(ix.indkey, 1) k ORDER BY k), ', '),
                       CASE WHEN ix.indisunique THEN 1 ELSE 0 END, CASE WHEN ix.indisprimary THEN 1 ELSE 0 END, pg_get_indexdef(ix.indexrelid)
                FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid JOIN pg_class c ON c.oid = ix.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table
                ORDER BY ix.indisprimary DESC, i.relname
                """,
                """
                SELECT con.conname,
                       array_to_string(ARRAY(SELECT a.attname FROM unnest(con.conkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.n ORDER BY k.o), ', '),
                       quote_ident(fn.nspname) || '.' || quote_ident(f.relname),
                       array_to_string(ARRAY(SELECT a.attname FROM unnest(con.confkey) WITH ORDINALITY k(n, o) JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.n ORDER BY k.o), ', '),
                       CASE con.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' WHEN 'r' THEN 'RESTRICT' ELSE 'NO ACTION' END,
                       CASE con.confupdtype WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' WHEN 'r' THEN 'RESTRICT' ELSE 'NO ACTION' END
                FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_class f ON f.oid = con.confrelid JOIN pg_namespace fn ON fn.oid = f.relnamespace
                WHERE con.contype = 'f' AND n.nspname = @schema AND c.relname = @table
                ORDER BY con.conname
                """),
            DatabaseEngine.MySql => (
                """
                SELECT COLUMN_NAME, COLUMN_TYPE, CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END, COLUMN_DEFAULT,
                       CASE WHEN COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END, CASE WHEN EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END
                FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION
                """,
                """
                SELECT INDEX_NAME, GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ', '),
                       CASE WHEN MAX(NON_UNIQUE) = 0 THEN 1 ELSE 0 END, CASE WHEN INDEX_NAME = 'PRIMARY' THEN 1 ELSE 0 END, ''
                FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
                GROUP BY INDEX_NAME ORDER BY INDEX_NAME = 'PRIMARY' DESC, INDEX_NAME
                """,
                """
                SELECT k.CONSTRAINT_NAME, GROUP_CONCAT(k.COLUMN_NAME ORDER BY k.ORDINAL_POSITION SEPARATOR ', '),
                       CONCAT('`', k.REFERENCED_TABLE_SCHEMA, '`.`', k.REFERENCED_TABLE_NAME, '`'),
                       GROUP_CONCAT(k.REFERENCED_COLUMN_NAME ORDER BY k.ORDINAL_POSITION SEPARATOR ', '),
                       MAX(r.DELETE_RULE), MAX(r.UPDATE_RULE)
                FROM information_schema.KEY_COLUMN_USAGE k
                JOIN information_schema.REFERENTIAL_CONSTRAINTS r ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.TABLE_SCHEMA = @schema AND k.TABLE_NAME = @table AND k.REFERENCED_TABLE_NAME IS NOT NULL
                GROUP BY k.CONSTRAINT_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME ORDER BY k.CONSTRAINT_NAME
                """),
            _ => (
                """
                SELECT c.name,
                       t.name + CASE
                           WHEN t.name IN ('varchar', 'char', 'varbinary', 'binary') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                           WHEN t.name IN ('nvarchar', 'nchar') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                           WHEN t.name IN ('decimal', 'numeric') THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                           WHEN t.name IN ('datetime2', 'time', 'datetimeoffset') THEN '(' + CAST(c.scale AS varchar(10)) + ')'
                           ELSE '' END,
                       CAST(c.is_nullable AS int), dc.definition,
                       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                         WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END,
                       CAST(c.is_identity AS int)
                FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
                ORDER BY c.column_id
                """,
                """
                SELECT i.name,
                       STRING_AGG(col.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal),
                       CAST(i.is_unique AS int), CAST(i.is_primary_key AS int), i.type_desc
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) AND i.name IS NOT NULL
                GROUP BY i.name, i.is_unique, i.is_primary_key, i.type_desc
                ORDER BY i.is_primary_key DESC, i.name
                """,
                """
                SELECT fk.name,
                       STRING_AGG(pc.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id),
                       QUOTENAME(OBJECT_SCHEMA_NAME(fk.referenced_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.referenced_object_id)),
                       STRING_AGG(rc.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id),
                       REPLACE(fk.delete_referential_action_desc, '_', ' '), REPLACE(fk.update_referential_action_desc, '_', ' ')
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE fk.parent_object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
                GROUP BY fk.name, fk.referenced_object_id, fk.delete_referential_action_desc, fk.update_referential_action_desc
                ORDER BY fk.name
                """),
        };

        var columns = (await CatalogAsync(server, user, password, database, columnsSql, parameters, cancellationToken))
            .Select(row => new DbColumn(row[0] ?? "", row[1] ?? "", Flag(row[2]), row[3], Flag(row[4]), Flag(row[5])))
            .ToList();
        var indexes = (await CatalogAsync(server, user, password, database, indexesSql, parameters, cancellationToken))
            .Select(row => new DbIndex(row[0] ?? "", row[1] ?? "", Flag(row[2]), Flag(row[3]), row[4] ?? ""))
            .ToList();
        var keys = (await CatalogAsync(server, user, password, database, keysSql, parameters, cancellationToken))
            .Select(row => new DbForeignKey(row[0] ?? "", row[1] ?? "", row[2] ?? "", row[3] ?? "", row[4] ?? "", row[5] ?? ""))
            .ToList();
        return new DbTableInfo(columns, indexes, keys);
    }

    public async Task<List<DbRoutine>> RoutinesAsync(DatabaseTarget server, string user, string password, string database, CancellationToken cancellationToken)
    {
        var sql = server.Protocol switch
        {
            DatabaseEngine.Postgres => """
                SELECT n.nspname, p.proname, CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                LEFT JOIN pg_depend d ON d.objid = p.oid AND d.deptype = 'e'
                WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND p.prokind IN ('f', 'p') AND d.objid IS NULL
                ORDER BY n.nspname, p.proname
                """,
            DatabaseEngine.MySql => """
                SELECT ROUTINE_SCHEMA, ROUTINE_NAME, LOWER(ROUTINE_TYPE) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @db ORDER BY ROUTINE_NAME
                """,
            _ => """
                SELECT SCHEMA_NAME(o.schema_id), o.name, CASE WHEN o.type = 'P' THEN 'procedure' ELSE 'function' END
                FROM sys.objects o WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
                ORDER BY SCHEMA_NAME(o.schema_id), o.name
                """,
        };
        var rows = await CatalogAsync(server, user, password, database, sql, new() { ["db"] = database }, cancellationToken);
        return [.. rows.Select(row => new DbRoutine(row[0] ?? "", row[1] ?? "", row[2] ?? "function"))];
    }

    // An object as the statement that makes it. Views, procedures and functions as the server keeps them; a table
    // from its columns, keys and indexes (MySQL says it itself).
    public async Task<string> ScriptAsync(DatabaseTarget server, string user, string password, string database, string kind, string schema, string name, CancellationToken cancellationToken)
    {
        var dialect = DialectOf(server.Protocol);
        var parameters = new Dictionary<string, object> { ["db"] = database, ["schema"] = schema, ["name"] = name };
        if (server.Protocol == DatabaseEngine.MySql)
        {
            var what = kind switch { "view" => "VIEW", "procedure" => "PROCEDURE", "function" => "FUNCTION", _ => "TABLE" };
            var shown = await RunAsync(server, user, password, database, $"SHOW CREATE {what} {dialect.Table(schema, name)}", 1, null, cancellationToken);
            var column = kind is "procedure" or "function" ? 2 : 1;
            return shown.Rows.Count > 0 && shown.Rows[0].Length > column ? (shown.Rows[0][column] ?? "") + ";" : "";
        }

        if (kind is "view" or "procedure" or "function")
        {
            var sql = server.Protocol == DatabaseEngine.Postgres
                ? kind == "view"
                    ? "SELECT 'CREATE OR REPLACE VIEW ' || quote_ident(@schema) || '.' || quote_ident(@name) || ' AS' || chr(10) || pg_get_viewdef((quote_ident(@schema) || '.' || quote_ident(@name))::regclass, true)"
                    : "SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema AND p.proname = @name ORDER BY p.oid LIMIT 1"
                : "SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name)))";
            var result = await RunAsync(server, user, password, database, sql, 1, parameters, cancellationToken);
            var text = result.Rows.Count > 0 ? result.Rows[0][0] ?? "" : "";
            return text.TrimEnd() + (server.Protocol == DatabaseEngine.MsSql ? "\nGO" : ";");
        }

        var info = await TableAsync(server, user, password, database, schema, name, cancellationToken);
        return TableScript(server.Protocol, dialect, schema, name, info);
    }

    internal static string TableScript(DatabaseEngine protocol, IDatabaseDialect dialect, string schema, string name, DbTableInfo info)
    {
        var table = dialect.Table(schema, name);
        var lines = new List<string>();
        foreach (var column in info.Columns)
        {
            var line = "    " + dialect.Quote(column.Name) + " " + column.Type;
            var serial = column.Identity && column.Default?.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase) == true;
            if (column.Identity && protocol == DatabaseEngine.MsSql)
            {
                line += " IDENTITY(1,1)";
            }
            else if (column.Identity && protocol == DatabaseEngine.Postgres && !serial)
            {
                line += " GENERATED BY DEFAULT AS IDENTITY";
            }

            line += column.Nullable ? " NULL" : " NOT NULL";
            if (!string.IsNullOrWhiteSpace(column.Default))
            {
                line += " DEFAULT " + column.Default;
            }

            lines.Add(line);
        }

        var primary = info.Indexes.FirstOrDefault(index => index.Primary);
        if (primary is not null)
        {
            lines.Add("    CONSTRAINT " + dialect.Quote(primary.Name) + " PRIMARY KEY (" + QuotedList(dialect, primary.Columns) + ")");
        }

        var script = new StringBuilder();
        script.Append("CREATE TABLE ").Append(table).Append(" (\n").Append(string.Join(",\n", lines)).Append("\n);\n");
        foreach (var index in info.Indexes.Where(index => !index.Primary))
        {
            script.Append('\n');
            script.Append(protocol == DatabaseEngine.Postgres && index.Definition.Length > 0
                ? index.Definition + ";"
                : "CREATE " + (index.Unique ? "UNIQUE " : "") + "INDEX " + dialect.Quote(index.Name) + " ON " + table + " (" + QuotedList(dialect, index.Columns) + ");");
        }

        foreach (var key in info.ForeignKeys)
        {
            script.Append("\n\nALTER TABLE ").Append(table).Append(" ADD CONSTRAINT ").Append(dialect.Quote(key.Name))
                .Append("\n    FOREIGN KEY (").Append(QuotedList(dialect, key.Columns)).Append(") REFERENCES ").Append(key.ReferencedTable)
                .Append(" (").Append(QuotedList(dialect, key.ReferencedColumns)).Append(')');
            if (key.OnDelete.Length > 0 && !key.OnDelete.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase))
            {
                script.Append(" ON DELETE ").Append(key.OnDelete);
            }

            if (key.OnUpdate.Length > 0 && !key.OnUpdate.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase))
            {
                script.Append(" ON UPDATE ").Append(key.OnUpdate);
            }

            script.Append(';');
        }

        return script.ToString().TrimEnd() + "\n";
    }

    private static string QuotedList(IDatabaseDialect dialect, string columns) =>
        string.Join(", ", columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(column => column.StartsWith('"') || column.StartsWith('[') || column.StartsWith('`') ? column : dialect.Quote(column)));
}
