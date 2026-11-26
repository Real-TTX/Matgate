using Matgate.Models;
using Matgate.Services.Database;

namespace Matgate.Services;

// A database connection of Matgate as the target of Mat.Remote's DatabaseGateway.
public static class DatabaseTargets
{
    public static DatabaseTarget Of(ServerEndpoint server) => new()
    {
        Protocol = server.Protocol switch
        {
            ServerProtocol.MsSql => DatabaseEngine.MsSql,
            ServerProtocol.MySql => DatabaseEngine.MySql,
            ServerProtocol.Postgres => DatabaseEngine.Postgres,
            _ => throw new DatabaseException("Not a database connection."),
        },
        Host = server.Host ?? "",
        Port = server.Port,
        DatabaseName = server.DatabaseName ?? "",
        IgnoreCertificate = server.IgnoreCertificate,
    };
}
