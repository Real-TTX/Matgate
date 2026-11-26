using Matgate.Models;
using Matgate.Services.Database;
using Xunit;

namespace Matgate.Access.Tests;

// Editing rows in the data view: the statements a change becomes, and the values turned into the column's type;
// and the check that the execution plan gets exactly one statement.
public sealed class RowEditTests
{
    private static readonly DbTableInfo Customers = new(
        [
            new DbColumn("id", "int", false, null, true, true),
            new DbColumn("name", "nvarchar(80)", false, null, false, false),
            new DbColumn("city", "nvarchar(80)", true, null, false, false),
            new DbColumn("photo", "varbinary(max)", true, null, false, false),
        ],
        [],
        []);

    [Fact]
    public void A_changed_cell_is_an_update_by_the_key_with_parameters()
    {
        var statements = DatabaseGateway.RowStatements(DatabaseEngine.MsSql, "dbo", "customers", Customers,
            [new RowChange("update", new() { ["id"] = "7" }, new() { ["city"] = "Köln's", ["name"] = "Anna" })], true);
        var update = Assert.Single(statements);
        Assert.Equal("UPDATE [dbo].[customers] SET [city] = N'Köln''s', [name] = N'Anna' WHERE [id] = N'7';", update.Preview);
        Assert.Equal("UPDATE [dbo].[customers] SET [city] = @p0, [name] = @p1 WHERE [id] = @p2", update.Sql);
        Assert.Equal(["Köln's", "Anna", "7"], update.Parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void Insert_delete_and_null()
    {
        var statements = DatabaseGateway.RowStatements(DatabaseEngine.Postgres, "public", "customers", Customers,
            [
                new RowChange("insert", null, new() { ["name"] = "Bernd", ["city"] = null }),
                new RowChange("delete", new() { ["id"] = "3" }, null),
                new RowChange("insert", null, []),
            ], true);
        Assert.Equal("INSERT INTO \"public\".\"customers\" (\"name\", \"city\") VALUES ('Bernd', NULL);", statements[0].Preview);
        Assert.Equal("DELETE FROM \"public\".\"customers\" WHERE \"id\" = '3';", statements[1].Preview);
        Assert.Equal("INSERT INTO \"public\".\"customers\" DEFAULT VALUES;", statements[2].Preview);
        Assert.Null(statements[0].Parameters[1].Value);
    }

    [Fact]
    public void Refused_without_a_key_with_unknown_columns_and_binary_data()
    {
        var noKey = new DbTableInfo([new DbColumn("name", "text", true, null, false, false)], [], []);
        Assert.Throws<DatabaseException>(() => DatabaseGateway.RowStatements(DatabaseEngine.Postgres, "public", "log", noKey, [new RowChange("delete", new() { ["name"] = "x" }, null)], true));
        Assert.Throws<DatabaseException>(() => DatabaseGateway.RowStatements(DatabaseEngine.MsSql, "dbo", "customers", Customers, [new RowChange("update", new() { ["id"] = "1" }, new() { ["nope"] = "x" })], true));
        Assert.Throws<DatabaseException>(() => DatabaseGateway.RowStatements(DatabaseEngine.MsSql, "dbo", "customers", Customers, [new RowChange("update", new() { ["id"] = "1" }, new() { ["photo"] = "0x00" })], true));
        Assert.Throws<DatabaseException>(() => DatabaseGateway.RowStatements(DatabaseEngine.MsSql, "dbo", "customers", Customers, [new RowChange("update", new() { ["city"] = "x" }, new() { ["name"] = "y" })], true));
    }

    [Fact]
    public void Values_take_the_columns_type()
    {
        Assert.Equal(true, DatabaseGateway.Typed(DatabaseEngine.MsSql, "bit", "on", "true", true));
        Assert.Equal(false, DatabaseGateway.Typed(DatabaseEngine.MySql, "tinyint(1)", "on", "0", true));
        Assert.Equal(42L, DatabaseGateway.Typed(DatabaseEngine.MySql, "int(11)", "n", "42", true));
        Assert.Equal(12.5m, DatabaseGateway.Typed(DatabaseEngine.MsSql, "decimal(10,2)", "n", "12.5", true));
        Assert.Equal(new DateTime(2026, 11, 25, 10, 30, 0), DatabaseGateway.Typed(DatabaseEngine.MsSql, "datetime2(7)", "d", "2026-11-25 10:30:00", true));
        Assert.Equal("text", DatabaseGateway.Typed(DatabaseEngine.MsSql, "nvarchar(10)", "t", "text", true));
        Assert.Throws<DatabaseException>(() => DatabaseGateway.Typed(DatabaseEngine.MsSql, "int", "n", "zwölf", true));
    }

    [Theory]
    [InlineData("SELECT * FROM t", true)]
    [InlineData("SELECT * FROM t;", true)]
    [InlineData("SELECT ';' FROM t; ", true)]
    [InlineData("SELECT 1; DROP TABLE t", false)]
    [InlineData("  ", false)]
    public void The_plan_gets_exactly_one_statement(string sql, bool single) =>
        Assert.Equal(single, ReadOnlySql.IsSingleStatement(sql, DatabaseEngine.Postgres));
}
