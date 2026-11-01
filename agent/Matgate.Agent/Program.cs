using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Matgate.Agent;

// The Matgate agent: a small server on the machine that Matgate connects to - like an RDP server, with
// host, port and a password (the token). What it offers is up to the agent: Matgate asks after
// connecting and shows exactly that.
//
//   matgate-agent [--config path]
var configPath = Path.Combine(AppContext.BaseDirectory, "matgate-agent.json");
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--config")
    {
        configPath = Path.GetFullPath(args[i + 1]);
    }
}

var config = AgentConfig.LoadOrCreate(configPath, out var created);
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
var certificate = LoadOrCreateCertificate(config, Path.GetDirectoryName(configPath)!);

Console.WriteLine($"Matgate agent {version}");
Console.WriteLine($"  configuration: {configPath}{(created ? " (created)" : "")}");
Console.WriteLine($"  listening on:  {config.ListenAddress}:{config.Port}");
Console.WriteLine($"  token:         {config.Token}");
Console.WriteLine($"  certificate:   SHA-256 {certificate.GetCertHashString(HashAlgorithmName.SHA256)}");

var files = new FilesPlugin(config.Roots);
var terminals = new TerminalPlugin(config, files);
var tokenBytes = Encoding.UTF8.GetBytes(config.Token);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options =>
{
    var address = IPAddress.TryParse(config.ListenAddress, out var parsed) ? parsed : IPAddress.Any;
    options.Listen(address, config.Port, listen => listen.UseHttps(certificate));
});
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.Map("/matgate", async context =>
{
    // The token is compared in constant time, and a wrong one gets nothing back but the status.
    var header = context.Request.Headers.Authorization.ToString();
    var offered = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(header[7..]) : [];
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!CryptographicOperations.FixedTimeEquals(offered, tokenBytes))
    {
        await Task.Delay(500);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine($"{DateTime.Now:T} Matgate connected from {context.Connection.RemoteIpAddress}");
    await new Connection(socket, config, version, files, terminals).RunAsync(context.RequestAborted);
    Console.WriteLine($"{DateTime.Now:T} Matgate disconnected");
});

await app.RunAsync();

static X509Certificate2 LoadOrCreateCertificate(AgentConfig config, string directory)
{
    var path = Path.IsPathRooted(config.CertificatePath)
        ? config.CertificatePath
        : Path.Combine(directory, config.CertificatePath);
    if (File.Exists(path))
    {
        return X509CertificateLoader.LoadPkcs12FromFile(path, config.CertificatePassword);
    }

    using var key = RSA.Create(3072);
    var request = new CertificateRequest($"CN={Environment.MachineName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName(Environment.MachineName);
    names.AddDnsName("localhost");
    request.CertificateExtensions.Add(names.Build());
    using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    File.WriteAllBytes(path, created.Export(X509ContentType.Pfx, config.CertificatePassword));
    return X509CertificateLoader.LoadPkcs12FromFile(path, config.CertificatePassword);
}

// One connection from Matgate. Requests are answered concurrently (a file copy must not hold up a
// terminal); every send goes through one lock, because a WebSocket takes one message at a time.
internal sealed class Connection(WebSocket socket, AgentConfig config, string version, FilesPlugin files, TerminalPlugin terminals)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(Hello());
            while (socket.State == WebSocketState.Open)
            {
                var frame = await AgentWire.ReceiveAsync(socket, cancellationToken);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Kind)
                {
                    case "req":
                        _ = Task.Run(() => AnswerAsync(frame, cancellationToken), cancellationToken);
                        break;
                    case "evt" when frame.Op == "pty.input":
                        terminals.Input(frame);
                        break;
                    case "evt" when frame.Op == "pty.resize":
                        terminals.Resize(frame);
                        break;
                    case "evt" when frame.Op == "pty.detach":
                        terminals.Detach(frame);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidDataException)
        {
            // Gone - the sessions keep running for the next connection.
        }
        finally
        {
            terminals.DetachAll(SendAsync);
            files.DropAllUploads();
        }
    }

    private async Task AnswerAsync(AgentFrame request, CancellationToken cancellationToken)
    {
        AgentFrame response;
        try
        {
            response = request.Op switch
            {
                "hello" => Hello(request.Id),
                "pty.open" => terminals.Open(request, SendAsync),
                "pty.kill" => terminals.Kill(request),
                "pty.list" => terminals.List(request),
                _ when request.Op.StartsWith("files.", StringComparison.Ordinal) && files.IsAvailable
                    => await files.HandleAsync(request, cancellationToken),
                _ => throw new InvalidOperationException("The agent does not offer this."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Error = Explain(exception) };
        }

        try
        {
            await SendAsync(response);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
        }
    }

    private AgentFrame Hello(long id = 0)
    {
        var plugins = new JsonArray();
        if (files.IsAvailable)
        {
            plugins.Add(new JsonObject { ["id"] = "files", ["kind"] = "files", ["title"] = "Dateien" });
        }

        foreach (var terminal in terminals.Available)
        {
            plugins.Add(new JsonObject { ["id"] = terminal.Id, ["kind"] = "terminal", ["title"] = string.IsNullOrWhiteSpace(terminal.Title) ? terminal.Id : terminal.Title });
        }

        return new AgentFrame
        {
            Id = id,
            Kind = id == 0 ? "evt" : "res",
            Op = "hello",
            Data = new JsonObject
            {
                ["protocol"] = AgentWire.ProtocolVersion,
                ["version"] = version,
                ["name"] = string.IsNullOrWhiteSpace(config.Name) ? Environment.MachineName : config.Name,
                ["machine"] = Environment.MachineName,
                ["user"] = Environment.UserName,
                ["os"] = RuntimeInformation.OSDescription,
                ["plugins"] = plugins,
            },
        };
    }

    private async Task SendAsync(AgentFrame frame)
    {
        await _sendLock.WaitAsync();
        try
        {
            await AgentWire.SendAsync(socket, frame, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // The sentence a person sees in Matgate. The machine's own messages may name local paths, so only
    // the kinds of failure that are the user's to fix are passed on as they are.
    private static string Explain(Exception exception) => exception switch
    {
        InvalidOperationException => exception.Message,
        FileNotFoundException or DirectoryNotFoundException => "This path does not exist.",
        UnauthorizedAccessException => "No permission for this path on the machine.",
        IOException => "The file is in use or could not be written.",
        _ => "The agent could not do this.",
    };
}
