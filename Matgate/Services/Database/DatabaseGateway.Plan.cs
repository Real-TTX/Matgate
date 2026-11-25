using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Matgate.Models;

namespace Matgate.Services.Database;

// One step of an execution plan: what the server does (a scan, a seek, a join, a sort ...), on what, how many
// rows it expects and what it costs - its own cost besides the total with everything below it. After a run
// ("with run times", PostgreSQL) also the real rows and milliseconds.
public sealed record PlanNode(
    string Operation,
    string Target,
    string Detail,
    double? Rows,
    double? Cost,
    double? SelfCost,
    double? ActualRows,
    double? ActualMs,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PlanNode> Children);

// The plan of a statement: a tree (PostgreSQL, SQL Server) or MySQL's table of steps; notes on the whole
// (planning time, a missing index SQL Server suggests); the server's own text of it, to copy.
public sealed record DbPlan(string Shape, IReadOnlyList<PlanNode> Nodes, DbResult? Table, IReadOnlyList<string> Notes, string Raw, bool Analyzed);

// "Execution plan" in the query editor: how the server would run a statement, without running it. Only with
// run times (PostgreSQL's EXPLAIN ANALYZE) does it run - inside a transaction that is always rolled back, so an
// UPDATE explained this way changes nothing. One statement only: EXPLAIN in front of the first of several would
// let the others run as they are.
public sealed partial class DatabaseGateway
{
    public async Task<DbPlan> PlanAsync(ServerEndpoint server, string user, string password, string? database, string sql, bool analyze, bool readOnly, bool german, CancellationToken cancellationToken)
    {
        sql = sql.Trim().TrimEnd(';').Trim();
        if (!ReadOnlySql.IsSingleStatement(sql, server.Protocol))
        {
            throw new DatabaseException(german ? "Für den Ausführungsplan bitte genau eine Anweisung (oder sie markieren)." : "For the execution plan, exactly one statement please (or select it).");
        }

        if (readOnly && ReadOnlySql.Refusal(sql, server.Protocol, german) is { } refusal)
        {
            throw new DatabaseException(refusal);
        }

        var dialect = DialectOf(server.Protocol);
        await using var connection = await OpenAsync(dialect, server, user, password, database, cancellationToken);
        return server.Protocol switch
        {
            ServerProtocol.Postgres => await PostgresPlanAsync(connection, sql, analyze, readOnly, german, cancellationToken),
            ServerProtocol.MsSql => await SqlServerPlanAsync(connection, sql, german, cancellationToken),
            _ => await MySqlPlanAsync(connection, sql, cancellationToken),
        };
    }

    private static async Task<string> ScalarTextAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var text = new System.Text.StringBuilder();
            do
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.FieldCount > 0 && !await reader.IsDBNullAsync(0, cancellationToken))
                    {
                        text.Append(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture));
                    }
                }
            }
            while (text.Length == 0 && await reader.NextResultAsync(cancellationToken));
            return text.ToString();
        }
        catch (DbException exception)
        {
            throw new DatabaseException(exception.Message, exception);
        }
    }

    // ---- PostgreSQL: EXPLAIN (FORMAT JSON), with ANALYZE inside a transaction rolled back afterwards
    private static async Task<DbPlan> PostgresPlanAsync(DbConnection connection, string sql, bool analyze, bool readOnly, bool german, CancellationToken cancellationToken)
    {
        if (analyze)
        {
            await ExecuteAsync(connection, readOnly ? ReadOnlySql.Begin(ServerProtocol.Postgres) : "BEGIN", cancellationToken);
        }

        string json;
        try
        {
            json = await ScalarTextAsync(connection, "EXPLAIN (FORMAT JSON" + (analyze ? ", ANALYZE, BUFFERS" : "") + ") " + sql, cancellationToken);
        }
        finally
        {
            if (analyze)
            {
                try
                {
                    await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None);
                }
                catch (DatabaseException)
                {
                    // The connection closes next, and with it the transaction.
                }
            }
        }

        using var document = JsonDocument.Parse(json);
        var top = document.RootElement[0];
        var notes = new List<string>();
        if (top.TryGetProperty("Planning Time", out var planning))
        {
            notes.Add((german ? "Planung: " : "Planning: ") + planning.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) + " ms");
        }

        if (top.TryGetProperty("Execution Time", out var execution))
        {
            notes.Add((german ? "Ausführung: " : "Execution: ") + execution.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) + " ms" + (german ? " (zurückgerollt)" : " (rolled back)"));
        }

        return new DbPlan("tree", [PostgresNode(top.GetProperty("Plan"))], null, notes, JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }), analyze);
    }

    private static PlanNode PostgresNode(JsonElement plan)
    {
        string Text(string name) => plan.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        double? Number(string name) => plan.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        var children = plan.TryGetProperty("Plans", out var plans) ? plans.EnumerateArray().Select(PostgresNode).ToList() : [];
        var operation = Text("Node Type");
        if (Text("Join Type") is { Length: > 0 } join && (operation.Contains("Join", StringComparison.Ordinal) || operation == "Nested Loop"))
        {
            operation = join + " " + operation;
        }

        var relation = Text("Relation Name");
        var target = relation.Length > 0
            ? (Text("Schema") is { Length: > 0 } schema ? schema + "." : "") + relation + (Text("Alias") is { Length: > 0 } alias && alias != relation ? " " + alias : "")
            : Text("CTE Name") is { Length: > 0 } cte ? "CTE " + cte : Text("Function Name");
        if (Text("Index Name") is { Length: > 0 } index)
        {
            target += (target.Length > 0 ? " · " : "") + index;
        }

        var detail = string.Join(" · ", new[] { "Index Cond", "Hash Cond", "Merge Cond", "Join Filter", "Filter", "Recheck Cond" }
            .Select(name => (name, value: Text(name)))
            .Where(entry => entry.value.Length > 0)
            .Select(entry => entry.name + ": " + entry.value)
            .Concat(plan.TryGetProperty("Sort Key", out var sort) && sort.ValueKind == JsonValueKind.Array ? ["Sort: " + string.Join(", ", sort.EnumerateArray().Select(key => key.GetString()))] : []));
        var cost = Number("Total Cost");
        var loops = Number("Actual Loops") ?? 1;
        return new PlanNode(
            operation,
            target,
            detail,
            Number("Plan Rows"),
            cost,
            cost is null ? null : Math.Max(0, cost.Value - children.Sum(child => child.Cost ?? 0)),
            Number("Actual Rows") is { } actualRows ? actualRows * loops : null,
            Number("Actual Total Time") is { } time ? time * loops : null,
            [],
            children);
    }

    // ---- SQL Server: SHOWPLAN_XML - the plan instead of the result; nothing runs
    private static readonly XNamespace ShowPlan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static async Task<DbPlan> SqlServerPlanAsync(DbConnection connection, string sql, bool german, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "SET SHOWPLAN_XML ON", cancellationToken);
        string xml;
        try
        {
            xml = await ScalarTextAsync(connection, sql, cancellationToken);
        }
        finally
        {
            try
            {
                await ExecuteAsync(connection, "SET SHOWPLAN_XML OFF", CancellationToken.None);
            }
            catch (DatabaseException)
            {
                // The connection closes next.
            }
        }

        var document = XDocument.Parse(xml);
        var notes = new List<string>();
        foreach (var group in document.Descendants(ShowPlan + "MissingIndexGroup"))
        {
            foreach (var index in group.Elements(ShowPlan + "MissingIndex"))
            {
                string Columns(string usage) => string.Join(", ", index.Elements(ShowPlan + "ColumnGroup").Where(columns => (string?)columns.Attribute("Usage") == usage).SelectMany(columns => columns.Elements(ShowPlan + "Column")).Select(column => (string?)column.Attribute("Name")));
                var keys = string.Join(", ", new[] { Columns("EQUALITY"), Columns("INEQUALITY") }.Where(part => part.Length > 0));
                var include = Columns("INCLUDE");
                notes.Add((german ? $"Fehlender Index (Nutzen {(string?)group.Attribute("Impact")} %): " : $"Missing index (impact {(string?)group.Attribute("Impact")} %): ")
                    + $"CREATE INDEX ... ON {(string?)index.Attribute("Schema")}.{(string?)index.Attribute("Table")} ({keys})" + (include.Length > 0 ? $" INCLUDE ({include})" : ""));
            }
        }

        var roots = document.Descendants(ShowPlan + "QueryPlan")
            .SelectMany(plan => plan.Elements(ShowPlan + "RelOp"))
            .Select(SqlServerNode)
            .ToList();
        return new DbPlan("tree", roots, null, notes, xml, false);
    }

    private static PlanNode SqlServerNode(XElement relOp)
    {
        double? Number(string name) => double.TryParse((string?)relOp.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        // What belongs to this step and not to one below it.
        IEnumerable<XElement> Own(XName name) => relOp.Descendants(name).Where(element => element.Ancestors(ShowPlan + "RelOp").First() == relOp);
        var children = relOp.Descendants(ShowPlan + "RelOp")
            .Where(element => element.Ancestors(ShowPlan + "RelOp").First() == relOp)
            .Select(SqlServerNode)
            .ToList();
        var objects = Own(ShowPlan + "Object").Select(item =>
                string.Join(".", new[] { (string?)item.Attribute("Schema"), (string?)item.Attribute("Table") }.Where(part => !string.IsNullOrEmpty(part)))
                + ((string?)item.Attribute("Index") is { Length: > 0 } index ? " · " + index : ""))
            .Distinct()
            .ToList();
        var predicates = new[] { "SeekPredicates", "Predicate", "HashKeysProbe", "OrderBy" }
            .SelectMany(name => Own(ShowPlan + name).Select(element => element.Descendants(ShowPlan + "ScalarOperator").FirstOrDefault()?.Attribute("ScalarString")?.Value
                ?? string.Join(", ", element.Descendants(ShowPlan + "ColumnReference").Select(column => (string?)column.Attribute("Column")))))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct()
            .Take(3);
        var warnings = Own(ShowPlan + "Warnings").SelectMany(warning => warning.Elements()).Select(warning => warning.Name.LocalName).Distinct().ToList();
        var physical = (string?)relOp.Attribute("PhysicalOp") ?? "";
        var logical = (string?)relOp.Attribute("LogicalOp") ?? "";
        var cost = Number("EstimatedTotalSubtreeCost");
        return new PlanNode(
            physical + (logical.Length > 0 && logical != physical ? " (" + logical + ")" : ""),
            string.Join(", ", objects),
            string.Join(" · ", predicates),
            Number("EstimateRows"),
            cost,
            cost is null ? null : Math.Max(0, cost.Value - children.Sum(child => child.Cost ?? 0)),
            null,
            null,
            warnings,
            children);
    }

    // ---- MySQL / MariaDB: EXPLAIN as its table of steps - nothing runs
    private static async Task<DbPlan> MySqlPlanAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var table = await ReadAsync(connection, "EXPLAIN " + sql, MaxRows, null, cancellationToken);
        var raw = string.Join("\n", new[] { string.Join("\t", table.Columns) }.Concat(table.Rows.Select(row => string.Join("\t", row.Select(value => value ?? "NULL")))));
        return new DbPlan("table", [], table, [], raw, false);
    }
}
