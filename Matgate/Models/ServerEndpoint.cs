namespace Matgate.Models;

public sealed class ServerEndpoint
{
    public const string DefaultKeyboardLayout = "de-de-qwertz";
    public const int DefaultTerminalFontSize = 12;

    public static readonly IReadOnlyList<string> IconKeys =
    [
        "rdp",
        "vnc",
        "ssh",
        "sftp",
        "ftp",
        "smb",
        "server",
        "desktop",
        "terminal",
        "folder",
        "database",
        "cloud",
        "shield",
        "home",
        "globe"
    ];

    private static readonly HashSet<string> AllowedIconKeys = new(IconKeys, StringComparer.OrdinalIgnoreCase);

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public ServerProtocol Protocol { get; set; } = ServerProtocol.Rdp;

    public string IconKey { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 3389;

    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    public string Domain { get; set; } = "";

    public string FileRootPath { get; set; } = "";

    // Only set for the gateway's own places ("global" | "user" | "connection" | "session") so the file
    // manager can group them without guessing from the name. Not persisted - a place is built fresh
    // from the permissions on every request.
    [System.Text.Json.Serialization.JsonIgnore]
    public string AreaKind { get; set; } = "";

    // Read-only: set for places that must not be written to - today the folder of a workspace whose
    // uploads are switched off. Enforced in FileGatewayService, not in the UI; the UI only hides the
    // button on top of that.
    public bool IsReadOnly { get; set; }

    // What the place was created from - today the workspace's id. A place's own id is derived from its
    // name and cannot be computed back; without this there would be no way from the file manager to the
    // workspace's settings and its link.
    public Guid? AreaSourceId { get; set; }

    public string WebsiteUrl { get; set; } = "";

    // For Website servers: how it is opened (native proxy vs. a browser-farm VNC session).
    public WebsiteRenderMode WebsiteRenderMode { get; set; } = WebsiteRenderMode.Native;

    // For a natively shown website: always under a path of Matgate's own address, even where websites can
    // have a host name of their own (MATGATE_WEBSITE_HOST). For the rare page that worked the old way only.
    public bool WebsitePathMode { get; set; }

    // A website tab that is a browser: any address may be typed into it and followed from it. Only ever
    // set on quick-connect endpoints (which exist in memory, for someone who may quick-connect) - never read
    // from a form or a file.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WebsiteBrowse { get; set; }

    // For browser-farm (Chromium/Firefox VNC) websites: show the browser's own toolbar (address bar,
    // tabs) instead of the default clean full-screen kiosk view. Only relevant when WebsiteRenderMode
    // is a VNC mode.
    public bool WebsiteFarmToolbar { get; set; }

    public string KeyboardLayout { get; set; } = DefaultKeyboardLayout;

    public int TerminalFontSize { get; set; } = DefaultTerminalFontSize;

    public bool IgnoreCertificate { get; set; } = true;

    // How a remote desktop or a terminal is opened - the connection's sub type, like a website's render mode:
    // "" = Matgate's own engine (its own RDP and VNC clients and SSH, all inside Matgate itself),
    // "guacamole" = Apache Guacamole.
    public string Engine { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public string Notes { get; set; } = "";

    public string FolderName { get; set; } = "";

    // A database connection: the database it opens in (empty: the server's list of databases).
    public string DatabaseName { get; set; } = "";

    // The agent this connection goes through into another network (see TunnelService); null: directly.
    public Guid? TunnelId { get; set; }

    // Which protocols can go through a tunnel: those that open one TCP connection to the host and its port.
    // (FTP opens further ones to addresses the server names; WebDAV and websites are addressed by name.)
    public static bool CanTunnel(ServerProtocol protocol) =>
        protocol is ServerProtocol.Rdp or ServerProtocol.Vnc or ServerProtocol.Ssh or ServerProtocol.Sftp or ServerProtocol.Smb
            or ServerProtocol.MsSql or ServerProtocol.MySql or ServerProtocol.Postgres;

    // Stored credentials this connection signs in with (see Credential); null: its own login below.
    public Guid? CredentialId { get; set; }

    // The name of those credentials, filled in when the connection is read - never stored.
    [System.Text.Json.Serialization.JsonIgnore]
    public string CredentialName { get; set; } = "";

    public string FolderIconKey { get; set; } = "";

    public Guid? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPrivate => OwnerUserId.HasValue;

    public ServerEndpoint Clone() => (ServerEndpoint)MemberwiseClone();

    // Shallow copy with a different file root - used to confine a user to a subfolder without
    // mutating the shared/cached endpoint instance.
    public ServerEndpoint WithFileRootPath(string fileRootPath)
    {
        var clone = (ServerEndpoint)MemberwiseClone();
        clone.FileRootPath = fileRootPath;
        return clone;
    }

    public static int NormalizeTerminalFontSize(int fontSize)
    {
        return Math.Clamp(fontSize <= 0 ? DefaultTerminalFontSize : fontSize, 8, 24);
    }

    // What really opens this connection here: "matgate", "guacamole" or "" (neither is set up). It is the one
    // the connection asks for; only when that one is not part of this installation does the other one step in
    // (RDP and VNC of Matgate's own need the H.264 encoder library the image brings along, Guacamole needs
    // its containers).
    public string EffectiveEngine(bool displayEngineAvailable, bool guacamoleAvailable)
    {
        if (!IsGuacamoleProtocol(Protocol))
        {
            return "";
        }

        var ownAvailable = Protocol == ServerProtocol.Ssh || displayEngineAvailable;
        // Through a tunnel only Matgate itself can go: Guacamole's guacd connects on its own, from elsewhere.
        if (TunnelId is not null)
        {
            return ownAvailable ? "matgate" : "";
        }

        if (NormalizeEngine(Engine) == "guacamole")
        {
            return guacamoleAvailable ? "guacamole" : ownAvailable ? "matgate" : "";
        }

        return ownAvailable ? "matgate" : guacamoleAvailable ? "guacamole" : "";
    }

    public static string NormalizeEngine(string? value) =>
        string.Equals((value ?? "").Trim(), "guacamole", StringComparison.OrdinalIgnoreCase) ? "guacamole" : "";

    public static bool IsGuacamoleProtocol(ServerProtocol protocol)
    {
        return protocol is ServerProtocol.Rdp or ServerProtocol.Ssh or ServerProtocol.Vnc;
    }

    public static bool IsFileProtocol(ServerProtocol protocol)
    {
        return protocol is ServerProtocol.Sftp or ServerProtocol.Ftp or ServerProtocol.Smb or ServerProtocol.Local or ServerProtocol.WebDav or ServerProtocol.Agent;
    }

    public static bool IsDatabaseProtocol(ServerProtocol protocol)
    {
        return protocol is ServerProtocol.MsSql or ServerProtocol.MySql or ServerProtocol.Postgres;
    }

    public static bool IsWebsiteProtocol(ServerProtocol protocol)
    {
        return protocol == ServerProtocol.Website;
    }

    public static string DefaultIconKey(ServerProtocol protocol)
    {
        return protocol switch
        {
            ServerProtocol.Rdp => "rdp",
            ServerProtocol.Vnc => "vnc",
            ServerProtocol.Ssh => "ssh",
            ServerProtocol.Sftp => "sftp",
            ServerProtocol.Ftp => "ftp",
            ServerProtocol.Smb => "smb",
            ServerProtocol.WebDav => "cloud",
            ServerProtocol.Website => "globe",
            ServerProtocol.Agent => "desktop",
            ServerProtocol.MsSql or ServerProtocol.MySql or ServerProtocol.Postgres => "database",
            ServerProtocol.WireGuard => "shield",
            _ => "server"
        };
    }

    // A website's address as it is stored: what was written, with https in front where no scheme was
    // given - and nothing else. The path stays as it is; "/admin" is not "/admin/" to every website.
    public static string CleanWebsiteUrl(string? value, string? fallback = "")
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = (fallback ?? "").Trim();
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "";
        }

        if (!cleaned.Contains("://", StringComparison.Ordinal))
        {
            cleaned = $"https://{cleaned}";
        }

        return Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) ? uri.ToString() : "";
    }

    // The address as the proxy under a path needs it: a directory to resolve the page's links against,
    // so a path without a file name gets its slash.
    public static string NormalizeWebsiteUrl(string? value, string? fallback = "")
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = (fallback ?? "").Trim();
        }

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "";
        }

        if (!cleaned.Contains("://", StringComparison.Ordinal))
        {
            cleaned = $"https://{cleaned}";
        }

        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
        {
            return "";
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/') && !Path.HasExtension(builder.Path))
        {
            builder.Path += "/";
        }

        return builder.Uri.ToString();
    }

    public static string NormalizeIconKey(string? iconKey)
    {
        var cleaned = (iconKey ?? "").Trim().ToLowerInvariant();
        return AllowedIconKeys.Contains(cleaned) ? cleaned : "";
    }

    public static string EffectiveIconKey(ServerProtocol protocol, string? iconKey)
    {
        var normalized = NormalizeIconKey(iconKey);
        return string.IsNullOrWhiteSpace(normalized) ? DefaultIconKey(protocol) : normalized;
    }
}
