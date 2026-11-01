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
    public int DetachedSessionMinutes { get; set; } = 60;

    // Matgate may bring new versions on its own (when no terminal is open). Off: only "Jetzt aktualisieren".
    public bool AutoUpdate { get; set; } = true;

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
            Terminals =
            [
                new AgentTerminal { Id = "claude", Title = "Claude Code", Command = OperatingSystem.IsWindows() ? "cmd.exe /c claude" : "claude" },
                new AgentTerminal { Id = "powershell", Title = "PowerShell", Command = "powershell.exe -NoLogo", Enabled = false },
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
}
