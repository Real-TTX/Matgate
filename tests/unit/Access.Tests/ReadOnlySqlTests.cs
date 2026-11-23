using Matgate.Models;
using Matgate.Services.Database;
using Xunit;

namespace Matgate.Access.Tests;

public sealed class ReadOnlySqlTests
{
    [Theory]
    [InlineData("SELECT * FROM customers")]
    [InlineData("  select 1;  ")]
    [InlineData("-- a comment\nSELECT 1")]
    [InlineData("/* DROP TABLE x; */ SELECT 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SELECT 'a; DELETE FROM b' AS text")]
    [InlineData("SELECT \"weird;name\" FROM t")]
    public void Reading_statements_pass(string sql) =>
        Assert.Null(ReadOnlySql.Refusal(sql, ServerProtocol.Postgres, german: false));

    [Theory]
    [InlineData("DELETE FROM customers")]
    [InlineData("insert into t values (1)")]
    [InlineData("DROP TABLE customers")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("SELECT 1; DROP TABLE customers")]
    [InlineData("SELECT 1; COMMIT; DELETE FROM t")]
    [InlineData("SELECT 1;;(DELETE FROM t)")]
    [InlineData("")]
    [InlineData("-- only a comment")]
    [InlineData("SET transaction_read_only = off")]
    public void Writing_or_several_statements_are_refused(string sql) =>
        Assert.NotNull(ReadOnlySql.Refusal(sql, ServerProtocol.Postgres, german: false));

    [Fact]
    public void A_dollar_quoted_body_hides_nothing()
    {
        Assert.Null(ReadOnlySql.Refusal("SELECT $$; DROP TABLE x$$", ServerProtocol.Postgres, false));
        Assert.NotNull(ReadOnlySql.Refusal("SELECT $tag$x$tag$; DROP TABLE x", ServerProtocol.Postgres, false));
    }

    [Fact]
    public void MySql_select_into_outfile_is_refused()
    {
        Assert.NotNull(ReadOnlySql.Refusal("SELECT * FROM t INTO OUTFILE '/tmp/x'", ServerProtocol.MySql, false));
        Assert.Null(ReadOnlySql.Refusal("SELECT 'INTO OUTFILE' FROM t", ServerProtocol.MySql, false));
    }

    [Fact]
    public void MySql_hash_comments_and_backslash_escapes()
    {
        Assert.Null(ReadOnlySql.Refusal("SELECT 1 # ; DROP TABLE x", ServerProtocol.MySql, false));
        Assert.Null(ReadOnlySql.Refusal("SELECT 'it\\'s; DROP' FROM t", ServerProtocol.MySql, false));
    }

    [Fact]
    public void SqlServer_brackets_are_names()
    {
        Assert.Null(ReadOnlySql.Refusal("SELECT [a;b] FROM [dbo].[t]", ServerProtocol.MsSql, false));
        Assert.Null(ReadOnlySql.Refusal("SELECT [delete] FROM t", ServerProtocol.MsSql, false));
    }

    [Theory]
    [InlineData("SELECT 1 DELETE FROM t")]
    [InlineData("SELECT 1 COMMIT DELETE FROM t")]
    [InlineData("SELECT 1 EXEC sp_who")]
    [InlineData("WITH x AS (SELECT 1 AS a) DELETE FROM t")]
    [InlineData("SELECT * INTO copy FROM t; SELECT 1")]
    public void SqlServer_statements_without_semicolons_are_seen(string sql) =>
        Assert.NotNull(ReadOnlySql.Refusal(sql, ServerProtocol.MsSql, false));

    [Fact]
    public void Show_create_table_reads()
    {
        Assert.Null(ReadOnlySql.Refusal("SHOW CREATE TABLE customers", ServerProtocol.MySql, false));
        Assert.NotNull(ReadOnlySql.Refusal("SELECT * FROM t FOR UPDATE", ServerProtocol.Postgres, false));
    }
}
