namespace Matgate.Models;

public enum ServerProtocol
{
    Rdp,
    Ssh,
    LegacyBrowser,
    Sftp,
    Ftp,
    Smb,
    Website,
    Vnc,
    // The gateway's own file areas (Global / User / Connection). Appended last on purpose: the
    // values are stored as numbers, so inserting anywhere else would renumber existing servers.
    Local,
    WebDav,
    // A Matgate agent on a machine: Matgate connects to it like to an RDP server (host, port, token)
    // and uses what it offers - files, terminals.
    Agent,
    // Databases: Matgate connects with the stored login and shows them in its database browser - one
    // browser for all three, one driver each (see Services/Database).
    MsSql,
    MySql,
    Postgres,
    // A WireGuard tunnel into another network: not opened itself, chosen as the way in by other connections
    // (see TunnelService). Its configuration is kept where a password is, encrypted.
    WireGuard
}
