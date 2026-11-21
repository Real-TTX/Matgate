using System.Security.Cryptography;
using System.Text.Json;

namespace Matgate.Agent;

// matgate-agent.json next to the executable (or the path given with --config). Created on the first
// start with a fresh token; the folders and terminals are then filled in by hand.
public sealed class AgentConfig
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    // The port Matgate connects to - like 3389 for RDP.
    public int Port { get; set; } = 7488;

    // The address to listen on. "0.0.0.0" = every network interface; "127.0.0.1" when Matgate runs on
    // the same machine (or reaches it through a tunnel) and nothing else should see the port.
    public string ListenAddress { get; set; } = "0.0.0.0";

    // Matgate's "connection password" for this agent. Generated on the first start.
    public string Token { get; set; } = "";

    // Shown in Matgate when set; otherwise the machine name.
    public string Name { get; set; } = "";

    // Linux and macOS, where the agent runs as the system's own service (root): the user programs in a
    // terminal run as - with that user's login, home folder and rights. Empty: as the agent itself.
    public string RunAs { get; set; } = "";

    // The picture shown on this machine's start page in Matgate (see AgentLogo): a path, relative ones start
    // at the folder of this file. Empty: a file called logo.png (.jpg, .webp, .gif, .svg) there, if any.
    public string Logo { get; set; } = "";

    // The TLS certificate (created on the first start, self-signed - tick "ignore certificate" in
    // Matgate, as for RDP). A path to an own .pfx may be put here instead.
    public string CertificatePath { get; set; } = "matgate-agent.pfx";

    public string CertificatePassword { get; set; } = "";

    // The folders the file explorer may see. Nothing outside them is reachable - not by "..", not by a
    // link. Without any, the agent offers no files.
    public List<AgentRoot> Roots { get; set; } = [];

    // Programs the agent can start in a terminal in the browser. Each one is a plugin of its own.
    public List<AgentTerminal> Terminals { get; set; } = [];

    // A terminal whose browser went away keeps running this long, so a phone that drops the connection
    // finds its session again. 0 = until the program ends by itself.
    public int DetachedSessionMinutes { get; set; }

    // Matgate may bring new versions on its own (when no terminal is open). Off: only "Jetzt aktualisieren".
    public bool AutoUpdate { get; set; } = true;

    // The screen (desktop with mouse, keyboard, sound and clipboard) in Matgate. Works when the agent runs as
    // the signed-in user, or as a LocalSystem service (then also at the sign-in screen); a service under any
    // other account sees no desktop.
    public bool Screen { get; set; } = true;

    // Where the microphone of the person in the browser is played (see Screen/Microphone.cs). Empty: into a
    // virtual audio cable, if the machine has one - its other end is a microphone programs can pick. A part
    // of a playback device's name: into that device. "speakers": on the machine's loudspeakers. "off": nowhere.
    public string Microphone { get; set; } = "";

    // Game controllers of the browser as Xbox controllers of this machine. Needs the ViGEmBus driver.
    public bool Gamepads { get; set; } = true;

    // The tunnel: Matgate reaches other machines in this machine's network through the agent - an RDP server,
    // a share, a database - as if it stood here. Off unless switched on: it opens this network to whoever may
    // use the agent's connection in Matgate.
    public bool Tunnel { get; set; }

    // Where the tunnel may lead: addresses or networks ("10.0.0.0/8", "192.168.1.20", "nas.local"). Empty: anywhere
    // this machine can reach.
    public List<string> TunnelAllow { get; set; } = [];

    // Matgate's address ("https://matgate.example.com"). Set, the agent connects to Matgate itself and keeps
    // that connection - for a machine Matgate cannot reach (no open port, behind a router). Matgate then uses
    // this connection for everything, the tunnel included.
    public string MatgateUrl { get; set; } = "";

    public static AgentConfig LoadOrCreate(string path, out bool created)
    {
        created = !File.Exists(path);
        var config = created
            ? Default()
            : JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json) ?? Default();

        if (string.IsNullOrWhiteSpace(config.Token))
        {
            config.Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            created = true;
        }

        if (created)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
        }

        return config;
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static AgentConfig Default()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AgentConfig
        {
            Roots = [new AgentRoot { Name = "Home", Path = home }],
            Terminals = OperatingSystem.IsWindows()
                ?
                [
                    new AgentTerminal { Id = "claude", Title = "Claude Code", Command = "cmd.exe /c claude" },
                    new AgentTerminal { Id = "powershell", Title = "PowerShell", Command = "powershell.exe -NoLogo", Enabled = false },
                ]
                :
                [
                    new AgentTerminal { Id = "claude", Title = "Claude Code", Command = "claude" },
                    new AgentTerminal { Id = "shell", Title = "Shell", Command = "$SHELL -l", Enabled = false },
                ],
        };
    }
}

public sealed class AgentRoot
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";
}

public sealed class AgentTerminal
{
    // Stable id, e.g. "claude". Matgate refers to the plugin by it.
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    // The whole command line, e.g. "cmd.exe /c claude".
    public string Command { get; set; } = "";

    public bool Enabled { get; set; } = true;

    // Other ways to start the same program, offered beside "start". Null: the known ones (Claude Code's
    // "continue the last conversation" and "pick a conversation"), so an existing configuration gets them too.
    public List<AgentTerminalVariant>? Variants { get; set; }

    public IReadOnlyList<AgentTerminalVariant> EffectiveVariants => Variants ?? (Id == "claude"
        ?
        [
            new AgentTerminalVariant { Id = "continue", Title = "Letzte Unterhaltung fortsetzen", Arguments = "--continue" },
            new AgentTerminalVariant { Id = "resume", Title = "Unterhaltung auswählen", Arguments = "--resume" },
        ]
        : []);
}

public sealed class AgentTerminalVariant
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    // Appended to the program's command line.
    public string Arguments { get; set; } = "";
}
