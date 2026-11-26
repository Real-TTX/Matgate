using Matgate.Files;
using Matgate.Models;

namespace Matgate.Services;

// A file connection of Matgate as the target of Mat.Remote's RemoteFiles.
public static class FileTargets
{
    public static FileTarget Of(ServerEndpoint server) => new()
    {
        Protocol = server.Protocol switch
        {
            ServerProtocol.Sftp => FileProtocol.Sftp,
            ServerProtocol.Ftp => FileProtocol.Ftp,
            ServerProtocol.Smb => FileProtocol.Smb,
            ServerProtocol.WebDav => FileProtocol.WebDav,
            _ => throw new InvalidOperationException("Dieser Server ist keine Dateiverbindung."),
        },
        Host = server.Host ?? "",
        Port = server.Port,
        UserName = server.UserName ?? "",
        Password = server.Password ?? "",
        Domain = server.Domain ?? "",
        PrivateKey = server.PrivateKey ?? "",
        FileRootPath = server.FileRootPath ?? "",
        IgnoreCertificate = server.IgnoreCertificate,
    };
}
