namespace Matgate.Services.Database;

/// <summary>The database servers <see cref="DatabaseGateway"/> speaks.</summary>
public enum DatabaseEngine
{
    MsSql,
    MySql,
    Postgres,
}

/// <summary>
/// Where a database connection goes - what Matgate's ServerEndpoint and ThixGate's connection have in common. The login is passed to each
/// call of <see cref="DatabaseGateway"/> on its own (it may be typed in for one session only).
/// </summary>
public sealed record DatabaseTarget
{
    public DatabaseEngine Protocol { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; }

    /// <summary>Empty: every database the login may see.</summary>
    public string DatabaseName { get; init; } = "";

    /// <summary>SQL Server: trust the server's certificate (TrustServerCertificate).</summary>
    public bool IgnoreCertificate { get; init; }

    /// <summary>How the connection names itself to the server (SQL Server's program name).</summary>
    public string ApplicationName { get; init; } = "Matgate";

    /// <summary>
    /// Where the TCP connection really goes when not to Host and Port - a relay or tunnel on this machine (ThixGate über Matgate). SQL
    /// Server still checks its certificate against Host. Empty / 0: Host and Port.
    /// </summary>
    public string DialHost { get; init; } = "";

    public int DialPort { get; init; }

    internal string ConnectHost => DialHost.Length > 0 ? DialHost : Host;

    internal int ConnectPort => DialPort > 0 ? DialPort : Port > 0 ? Port : DefaultPort(Protocol);

    public static int DefaultPort(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.MsSql => 1433,
        DatabaseEngine.MySql => 3306,
        _ => 5432,
    };
}
