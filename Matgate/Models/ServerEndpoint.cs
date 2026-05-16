namespace Matgate.Models;

public sealed class ServerEndpoint
{
    public const string DefaultKeyboardLayout = "de-de-qwertz";
    public const int DefaultTerminalFontSize = 12;

    public static readonly IReadOnlyList<string> IconKeys =
    [
        "rdp",
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
        "home"
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

    public string KeyboardLayout { get; set; } = DefaultKeyboardLayout;

    public int TerminalFontSize { get; set; } = DefaultTerminalFontSize;

    public bool IgnoreCertificate { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public string Notes { get; set; } = "";

    public Guid? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPrivate => OwnerUserId.HasValue;

    public static int NormalizeTerminalFontSize(int fontSize)
    {
        return Math.Clamp(fontSize <= 0 ? DefaultTerminalFontSize : fontSize, 8, 24);
    }

    public static bool IsGuacamoleProtocol(ServerProtocol protocol)
    {
        return protocol is ServerProtocol.Rdp or ServerProtocol.Ssh;
    }

    public static bool IsFileProtocol(ServerProtocol protocol)
    {
        return protocol is ServerProtocol.Sftp or ServerProtocol.Ftp or ServerProtocol.Smb;
    }

    public static string DefaultIconKey(ServerProtocol protocol)
    {
        return protocol switch
        {
            ServerProtocol.Rdp => "rdp",
            ServerProtocol.Ssh => "ssh",
            ServerProtocol.Sftp => "sftp",
            ServerProtocol.Ftp => "ftp",
            ServerProtocol.Smb => "smb",
            _ => "server"
        };
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
