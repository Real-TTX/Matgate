namespace Matgate.Agent.Setup;

// What the setup decides - in its window or on the command line, on every system. A system reads what
// applies to it: the firewall, the user/service choice and the account are Windows', the user that
// programs run as is Linux' and macOS'.
public sealed class SetupOptions
{
    public int Port { get; set; } = 7488;

    // Windows: a rule in the firewall for the port.
    public bool OpenFirewall { get; set; } = true;

    // Windows: "user" starts at logon as the signed-in user, "service" is a Windows service under Account.
    // Linux and macOS: always the system's service.
    public string Mode { get; set; } = OperatingSystem.IsWindows() ? "user" : "service";

    // Windows, for "service": DOMAIN\name, .\name or LocalSystem. The password goes to Windows, nowhere else.
    public string Account { get; set; } = "";

    public string Password { get; set; } = "";

    // Linux and macOS: the user programs in a terminal run as (the service itself is root). Null: whoever
    // called sudo, else whatever the settings already say.
    public string? RunAs { get; set; }

    public List<AgentRoot> Roots { get; set; } = [];

    public bool Claude { get; set; } = true;

    // Windows: PowerShell as a second program.
    public bool PowerShell { get; set; }

    // Linux and macOS: a login shell as a second program.
    public bool Shell { get; set; }

    // Says what would be done, and does none of it.
    public bool DryRun { get; set; }

    // The icon in the notification area, started when someone signs in.
    public bool Tray { get; set; } = true;

    // Matgate may bring new versions on its own.
    public bool AutoUpdate { get; set; } = true;

    // The screen in Matgate (desktop, sound, clipboard).
    public bool Screen { get; set; } = true;

    // The tunnel into this machine's network (see AgentConfig.Tunnel).
    public bool Tunnel { get; set; }

    // Matgate's address, for the agent to connect to Matgate itself. Null: whatever is there stays.
    public string? MatgateUrl { get; set; }

    // The picture for this machine's start page in Matgate. A path: that picture is taken (a copy of it).
    // Empty: no logo any more. Null: whatever is there stays.
    public string? Logo { get; set; }

    // What is installed: the logo's file, for the setup to show. Not an instruction.
    public string CurrentLogo { get; set; } = "";

    // Filled from an existing installation only.
    public string Version { get; set; } = "";
}

// What a finished setup hands back: what Matgate needs to connect, and whether the agent answers.
public sealed record SetupResult(string Token, string Fingerprint, int Port, bool Listening);
