

namespace Matgate.Services.Database;

// Whoever may only read a database connection may still type a statement - one that reads. Two locks, because
// neither is enough alone:
//
// 1. Before it runs, the statement is looked at: exactly one, beginning with a word that reads (SELECT, WITH,
//    SHOW, ...), and none of the ways of writing a file from a SELECT. One statement only, because a second
//    one after a COMMIT would run outside the transaction of (2).
// 2. It runs inside a read-only transaction that is rolled back afterwards (PostgreSQL and MySQL refuse to
//    write in one; SQL Server has none, there the rollback undoes it - SELECT ... INTO included).
//
// What a login may do at all is still the database's business: for a connection that must never write, a
// login that cannot write is the real answer. This is what Matgate adds on top.
public static class ReadOnlySql
{
    private static readonly HashSet<string> ReadingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "SHOW", "EXPLAIN", "DESCRIBE", "DESC", "VALUES", "TABLE",
    };

    private static readonly HashSet<string> ChangingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "DROP", "ALTER", "CREATE", "TRUNCATE", "RENAME",
        "GRANT", "REVOKE", "DENY", "COMMIT", "ROLLBACK", "SAVEPOINT", "BEGIN", "EXEC", "EXECUTE", "CALL",
        "LOCK", "UNLOCK", "KILL", "SHUTDOWN", "DBCC", "BACKUP", "RESTORE", "RECONFIGURE", "BULK",
        "OPENROWSET", "OPENDATASOURCE", "OPENQUERY", "USE", "GO", "LOAD", "HANDLER", "COPY",
    };
    // Exactly one statement (a ";" at its end allowed): what EXPLAIN may be put in front of. Strings, quoted
    // names and comments are skipped as in Refusal. SQL Server needs no ";" between statements - there the
    // plan is only shown, nothing runs, so it does not matter.
    public static bool IsSingleStatement(string sql, DatabaseEngine protocol)
    {
        var (words, several) = Scan(sql, protocol);
        return words.Count > 0 && !several;
    }

    // Null when the statement may run read-only; otherwise why not.
    public static string? Refusal(string sql, DatabaseEngine protocol, bool german)
    {
        var (words, several) = Scan(sql, protocol);
        if (several)
        {
            // Anything after a finished statement: a second one.
            return german ? "Nur lesen: bitte nur eine Anweisung auf einmal." : "Read-only: one statement at a time, please.";
        }

        if (words.Count == 0)
        {
            return german ? "Keine Abfrage." : "No statement.";
        }

        if (!ReadingWords.Contains(words[0]))
        {
            return german
                ? $"Nur lesen: „{words[0].ToUpperInvariant()}“ ändert etwas. Erlaubt sind Abfragen wie SELECT, WITH, SHOW oder EXPLAIN."
                : $"Read-only: \"{words[0].ToUpperInvariant()}\" changes something. Allowed are statements such as SELECT, WITH, SHOW or EXPLAIN.";
        }

        // SQL Server needs no ";" between two statements ("SELECT 1 COMMIT DELETE FROM t" is three), and a
        // COMMIT would end the transaction that undoes the rest. So no word that changes, ends a transaction or
        // runs something else may appear anywhere - outside strings and comments, which are skipped above.
        for (var w = 0; w < words.Count; w++)
        {
            if (ChangingWords.Contains(words[w]) && !(w > 0 && string.Equals(words[w - 1], "SHOW", StringComparison.OrdinalIgnoreCase)))
            {
                return german
                    ? $"Nur lesen: „{words[w].ToUpperInvariant()}“ ist hier nicht erlaubt."
                    : $"Read-only: \"{words[w].ToUpperInvariant()}\" is not allowed here.";
            }
        }

        for (var w = 0; w + 1 < words.Count; w++)
        {
            if (string.Equals(words[w], "INTO", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(words[w + 1], "OUTFILE", StringComparison.OrdinalIgnoreCase) || string.Equals(words[w + 1], "DUMPFILE", StringComparison.OrdinalIgnoreCase)))
            {
                return german ? "Nur lesen: SELECT ... INTO OUTFILE schreibt eine Datei." : "Read-only: SELECT ... INTO OUTFILE writes a file.";
            }
        }

        return null;
    }

    // What opens the read-only transaction on each server.
    public static string Begin(DatabaseEngine protocol) => protocol switch
    {
        DatabaseEngine.Postgres => "BEGIN TRANSACTION READ ONLY",
        DatabaseEngine.MySql => "START TRANSACTION READ ONLY",
        _ => "BEGIN TRANSACTION",
    };

    public static string End(DatabaseEngine protocol) => protocol == DatabaseEngine.MsSql ? "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION" : "ROLLBACK";

    // The words of a statement outside strings and comments, and whether a second statement follows the first.
    private static (List<string> Words, bool Several) Scan(string sql, DatabaseEngine protocol)
    {
        var words = new List<string>();
        var statements = 0;
        var pendingStatement = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '-' && next == '-' || c == '#' && protocol == DatabaseEngine.MySql)
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            if (statements > 0 && !char.IsWhiteSpace(c) && c != ';')
            {
                // Anything after a finished statement: a second one.
                return (words, true);
            }

            if (c is '\'' or '"' or '`' || c == '[' && protocol == DatabaseEngine.MsSql)
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        // Doubled: the quote itself, inside.
                        if (i + 1 < sql.Length && sql[i + 1] == close)
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    if (sql[i] == '\\' && protocol == DatabaseEngine.MySql && c != '`')
                    {
                        i++;
                    }

                    i++;
                }

                i++;
                pendingStatement = true;
                continue;
            }

            if (c == '$' && protocol == DatabaseEngine.Postgres)
            {
                // A dollar-quoted string: $$...$$ or $tag$...$tag$.
                var tagEnd = sql.IndexOf('$', i + 1);
                if (tagEnd > i && sql[(i + 1)..tagEnd].All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    var tag = sql[i..(tagEnd + 1)];
                    var end = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + tag.Length;
                    pendingStatement = true;
                    continue;
                }
            }

            if (c == ';')
            {
                if (pendingStatement)
                {
                    statements++;
                    pendingStatement = false;
                }

                i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                {
                    i++;
                }

                words.Add(sql[start..i]);
                pendingStatement = true;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                pendingStatement = true;
            }

            i++;
        }

        return (words, false);
    }
}
