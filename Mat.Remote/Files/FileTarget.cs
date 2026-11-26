namespace Matgate.Files;

/// <summary>The kinds of file connection <see cref="RemoteFiles"/> speaks.</summary>
public enum FileProtocol
{
    Sftp,
    Ftp,
    Smb,
    WebDav,
}

/// <summary>
/// Where a file connection goes and how it signs in - what Matgate's ServerEndpoint and ThixGate's connection have in common. Host may be a
/// full URL for WebDAV (https://cloud.example/remote.php/dav); FileRootPath is the connection's root ("/home/user", "share/folder").
/// </summary>
public sealed record FileTarget
{
    public FileProtocol Protocol { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; }

    public string UserName { get; init; } = "";

    public string Password { get; init; } = "";

    /// <summary>SMB: the Windows domain.</summary>
    public string Domain { get; init; } = "";

    /// <summary>SFTP: a private key as text (OpenSSH, PEM, PuTTY); the password is then its passphrase.</summary>
    public string PrivateKey { get; init; } = "";

    public string FileRootPath { get; init; } = "";

    /// <summary>WebDAV: accept any certificate of the server.</summary>
    public bool IgnoreCertificate { get; init; }

    /// <summary>
    /// Where the TCP connection really goes when not to Host and Port - a relay or tunnel on this machine (ThixGate über Matgate). The
    /// protocol still speaks to Host: WebDAV keeps its URL and certificate name. Empty / 0: Host and Port.
    /// </summary>
    public string DialHost { get; init; } = "";

    public int DialPort { get; init; }

    internal string ConnectHost => DialHost.Length > 0 ? DialHost : Host;

    internal int ConnectPort => DialPort > 0 ? DialPort : Port > 0 ? Port : DefaultPort(Protocol);

    public static int DefaultPort(FileProtocol protocol) => protocol switch
    {
        FileProtocol.Sftp => 22,
        FileProtocol.Ftp => 21,
        FileProtocol.Smb => 445,
        _ => 443,
    };
}
