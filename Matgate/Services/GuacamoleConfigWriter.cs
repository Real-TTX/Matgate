using System.Xml.Linq;
using Matgate.Models;

namespace Matgate.Services;

public sealed class GuacamoleConfigWriter
{
    private readonly JsonDataStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GuacamoleConfigWriter> _logger;

    public GuacamoleConfigWriter(JsonDataStore store, IConfiguration configuration, ILogger<GuacamoleConfigWriter> logger)
    {
        _store = store;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var users = await _store.GetUsersAsync(cancellationToken);
        var servers = await _store.GetServersAsync(cancellationToken);
        var enabledServers = servers
            .Where(server => server.IsEnabled && ServerEndpoint.IsGuacamoleProtocol(server.Protocol))
            .ToList();

        var configHome = _store.DataDirectory;
        Directory.CreateDirectory(configHome);

        await WritePropertiesAsync(configHome, cancellationToken);
        await WriteUserMappingAsync(configHome, users, enabledServers, cancellationToken);

        _logger.LogInformation(
            "Synchronized Guacamole config with {UserCount} user(s) and {ServerCount} server(s).",
            users.Count,
            enabledServers.Count);
    }

    public static string ConnectionName(ServerEndpoint server)
    {
        return $"{server.Name} [{server.Id.ToString("N")[..8]}]";
    }

    public static string ProtocolName(ServerProtocol protocol)
    {
        return protocol.ToString().ToLowerInvariant();
    }

    private async Task WritePropertiesAsync(string configHome, CancellationToken cancellationToken)
    {
        var guacdHost = _configuration["Guacamole:GuacdHost"]
            ?? Environment.GetEnvironmentVariable("GUACD_HOSTNAME")
            ?? "guacd";
        var guacdPort = _configuration["Guacamole:GuacdPort"]
            ?? Environment.GetEnvironmentVariable("GUACD_PORT")
            ?? "4822";

        var content = string.Join('\n',
            $"guacd-hostname: {guacdHost}",
            $"guacd-port: {guacdPort}",
            "user-mapping: /etc/guacamole/user-mapping.xml",
            "enable-websocket: true",
            "");

        await File.WriteAllTextAsync(Path.Combine(configHome, "guacamole.properties"), content, cancellationToken);
    }

    private static async Task WriteUserMappingAsync(
        string configHome,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<ServerEndpoint> servers,
        CancellationToken cancellationToken)
    {
        var root = new XElement("user-mapping");

        foreach (var user in users.Where(user => user.IsEnabled).OrderBy(user => user.UserName))
        {
            var authorize = new XElement(
                "authorize",
                new XAttribute("username", user.UserName),
                new XAttribute("password", user.GuacamolePassword));

            var allowedServers = servers
                .Where(server =>
                    user.IsAdmin
                    || server.OwnerUserId == user.Id
                    || (server.OwnerUserId is null && (user.CanManageServers || user.ServerAccessAll || user.ServerAccess.Contains(server.Id))))
                .OrderBy(server => server.Name);

            foreach (var server in allowedServers)
            {
                authorize.Add(BuildConnection(server));
            }

            root.Add(authorize);
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        await File.WriteAllTextAsync(Path.Combine(configHome, "user-mapping.xml"), document.ToString(), cancellationToken);
    }

    private static XElement BuildConnection(ServerEndpoint server)
    {
        var connection = new XElement(
            "connection",
            new XAttribute("name", ConnectionName(server)),
            new XElement("protocol", ProtocolName(server.Protocol)),
            Param("hostname", server.Host),
            Param("port", server.Port.ToString()));

        if (server.Protocol is ServerProtocol.Rdp or ServerProtocol.Ssh
            && !string.IsNullOrWhiteSpace(server.UserName))
        {
            connection.Add(Param("username", server.UserName));
        }

        if (!string.IsNullOrWhiteSpace(server.Password))
        {
            connection.Add(Param("password", server.Password));
        }

        if (server.Protocol == ServerProtocol.Rdp)
        {
            if (!string.IsNullOrWhiteSpace(server.Domain))
            {
                connection.Add(Param("domain", server.Domain));
            }

            connection.Add(Param("security", "any"));
            connection.Add(Param("ignore-cert", server.IgnoreCertificate ? "true" : "false"));
            connection.Add(Param("server-layout", string.IsNullOrWhiteSpace(server.KeyboardLayout)
                ? ServerEndpoint.DefaultKeyboardLayout
                : server.KeyboardLayout.Trim()));
            connection.Add(Param("resize-method", "reconnect"));
            connection.Add(Param("enable-wallpaper", "false"));
        }
        else if (server.Protocol == ServerProtocol.Vnc)
        {
            // Standard outbound VNC connections only need the shared target
            // password and the common hostname / port parameters above.
        }
        else if (server.Protocol == ServerProtocol.Ssh)
        {
            connection.Add(Param("font-name", "monospace"));
            connection.Add(Param("font-size", ServerEndpoint.NormalizeTerminalFontSize(server.TerminalFontSize).ToString()));
        }

        return connection;
    }

    private static XElement Param(string name, string value)
    {
        return new XElement("param", new XAttribute("name", name), value);
    }
}
