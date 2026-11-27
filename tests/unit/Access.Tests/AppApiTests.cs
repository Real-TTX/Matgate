using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Matgate.Models;
using Matgate.Services;
using Xunit;

namespace Matgate.Access.Tests;

// The app API (ThixGate): the sign-in through the browser with PKCE, the devices, what the library shows, and the relay.
public sealed class AppApiTests : IDisposable
{
    private const string Redirect = "http://127.0.0.1:50123/callback";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matgate-app-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private AppDeviceStore Store() => new(Path.Combine(_folder, "app-devices.json"));

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = AppDeviceStore.Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, AppDeviceStore.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    [Theory]
    [InlineData("http://127.0.0.1:50123/callback", true)]
    [InlineData("http://localhost:4000/", true)]
    [InlineData("http://[::1]:4000/x", true)]
    [InlineData("http://127.0.0.1/callback", false)]
    [InlineData("https://127.0.0.1:50123/callback", false)]
    [InlineData("http://evil.example:50123/callback", false)]
    [InlineData("http://127.0.0.1.evil.example:50123/", false)]
    [InlineData("http://user@127.0.0.1:50123/", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    public void Only_an_address_on_the_device_itself_gets_the_code(string redirect, bool allowed) =>
        Assert.Equal(allowed, AppDeviceStore.IsLoopbackRedirect(redirect));

    [Fact]
    public void The_code_is_traded_once_with_the_right_verifier_for_a_token_whose_hash_alone_is_stored()
    {
        var store = Store();
        var user = Guid.NewGuid();
        var (verifier, challenge) = Pkce();
        var code = store.CreateCode(user, "thixgate", " ANNA-PC\n", Redirect, challenge, Now);

        var pending = store.RedeemCode(code, verifier, Redirect, Now.AddSeconds(5));
        Assert.NotNull(pending);
        Assert.Equal("ANNA-PC", pending.DeviceName);
        Assert.Null(store.RedeemCode(code, verifier, Redirect, Now.AddSeconds(6)));

        var issued = store.AddDevice(pending, "10.0.0.5", Now);
        Assert.StartsWith(AppDeviceStore.TokenPrefix, issued.Token);
        Assert.Equal(issued.Device.Id, store.FindByToken(issued.Token)?.Id);
        Assert.Null(store.FindByToken(issued.Token + "x"));
        Assert.Null(store.FindByToken(null));

        var file = File.ReadAllText(Path.Combine(_folder, "app-devices.json"));
        Assert.DoesNotContain(issued.Token, file);
        Assert.Contains(AppDeviceStore.Hash(issued.Token), file);

        // Read again from the file: the device is still there and still found by its token.
        Assert.Equal(issued.Device.Id, Store().FindByToken(issued.Token)?.Id);
    }

    [Fact]
    public void A_wrong_verifier_another_address_or_an_old_code_get_nothing_and_use_the_code_up()
    {
        var store = Store();
        var (verifier, challenge) = Pkce();
        var (otherVerifier, _) = Pkce();

        var code = store.CreateCode(Guid.NewGuid(), "thixgate", "PC", Redirect, challenge, Now);
        Assert.Null(store.RedeemCode(code, otherVerifier, Redirect, Now));
        Assert.Null(store.RedeemCode(code, verifier, Redirect, Now));

        code = store.CreateCode(Guid.NewGuid(), "thixgate", "PC", Redirect, challenge, Now);
        Assert.Null(store.RedeemCode(code, verifier, "http://127.0.0.1:50124/callback", Now));

        code = store.CreateCode(Guid.NewGuid(), "thixgate", "PC", Redirect, challenge, Now);
        Assert.Null(store.RedeemCode(code, verifier, Redirect, Now + AppDeviceStore.CodeLifetime));

        Assert.Null(store.RedeemCode("made-up", verifier, Redirect, Now));
    }

    [Fact]
    public void A_device_is_signed_out_by_its_user_only_and_with_the_user()
    {
        var store = Store();
        var (verifier, challenge) = Pkce();
        var anna = Guid.NewGuid();
        var issued = store.AddDevice(store.RedeemCode(store.CreateCode(anna, "thixgate", "PC", Redirect, challenge, Now), verifier, Redirect, Now)!, "", Now);

        Assert.False(store.Remove(Guid.NewGuid(), issued.Device.Id));
        Assert.NotNull(store.FindByToken(issued.Token));
        Assert.True(store.Remove(anna, issued.Device.Id));
        Assert.Null(store.FindByToken(issued.Token));

        (verifier, challenge) = Pkce();
        issued = store.AddDevice(store.RedeemCode(store.CreateCode(anna, "thixgate", "PC", Redirect, challenge, Now), verifier, Redirect, Now)!, "", Now);
        store.RemoveUser(anna);
        Assert.Null(store.FindByToken(issued.Token));
        Assert.Empty(store.ForUser(anna));
    }

    [Fact]
    public void The_library_holds_the_desktops_and_terminals_the_user_may_work_in_without_secrets()
    {
        var lab = new FolderDefinition { Name = "Lab" };
        var user = new MatgateUser { UserName = "anna", DisplayName = "Anna" };
        user.Access = new UserAccess(
            [new ReachingGrant(new AccessGrant { TargetKind = AccessTargetKind.Folder, TargetId = lab.Id, Level = AccessLevel.Connect }, null)],
            new Dictionary<string, FolderDefinition> { [AccessResolver.FolderKey("Lab")] = lab });
        var watched = new ServerEndpoint { Name = "Watched", Protocol = ServerProtocol.Rdp, Host = "w" };
        user.Access = new UserAccess(
            [.. user.Access.Grants, new ReachingGrant(new AccessGrant { TargetKind = AccessTargetKind.Server, TargetId = watched.Id, Level = AccessLevel.ReadOnly }, null)],
            user.Access.Folders);

        var servers = new[]
        {
            new ServerEndpoint { Name = "Web", Protocol = ServerProtocol.Rdp, Host = "web.lab", Port = 3389, FolderName = "Lab/Linux", UserName = "admin", Password = "secret", Domain = "LAB" },
            new ServerEndpoint { Name = "Shell", Protocol = ServerProtocol.Ssh, Host = "ssh.lab", Port = 22, FolderName = "Lab", PrivateKey = "-----BEGIN KEY-----" },
            new ServerEndpoint { Name = "Files", Protocol = ServerProtocol.Sftp, Host = "files.lab", Port = 22, FolderName = "Lab", FileRootPath = "/srv" },
            new ServerEndpoint { Name = "Wiki", Protocol = ServerProtocol.Website, Host = "wiki.lab", WebsiteUrl = "https://wiki.lab/start", FolderName = "Lab" },
            new ServerEndpoint { Name = "Shop-DB", Protocol = ServerProtocol.Postgres, Host = "db.lab", Port = 5432, FolderName = "Lab", DatabaseName = "shop", Password = "secret" },
            new ServerEndpoint { Name = "Agent", Protocol = ServerProtocol.Agent, Host = "agent.lab", FolderName = "Lab" },
            new ServerEndpoint { Name = "Off", Protocol = ServerProtocol.Vnc, Host = "off.lab", FolderName = "Lab", IsEnabled = false },
            new ServerEndpoint { Name = "Elsewhere", Protocol = ServerProtocol.Rdp, Host = "x", FolderName = "Office" },
            new ServerEndpoint { Name = "Mine", Protocol = ServerProtocol.Vnc, Host = "mine", Port = 5901, OwnerUserId = user.Id },
            watched,
        };

        var library = AppLibrary.Build(user, servers);

        // "Watched" (RDP, read only) is there too: it opens as a session on Matgate, which keeps it read-only.
        // Files, websites and databases too (the app opens them with its own engine); Matgate's own kinds (agents) not.
        Assert.Equal(["Files", "Mine", "Shell", "Shop-DB", "Watched", "Web", "Wiki"], library.Connections.Select(connection => connection.Name).Order().ToArray());
        var files = library.Connections.Single(connection => connection.Name == "Files");
        Assert.Equal(("sftp", "/srv", "", ""), (files.Protocol, files.RootPath, files.Url, files.Database));
        var wiki = library.Connections.Single(connection => connection.Name == "Wiki");
        Assert.Equal(("website", "https://wiki.lab/start"), (wiki.Protocol, wiki.Url));
        var database = library.Connections.Single(connection => connection.Name == "Shop-DB");
        Assert.Equal(("postgres", "shop", true), (database.Protocol, database.Database, database.HasStoredLogin));
        Assert.Equal("ReadOnly", library.Connections.Single(connection => connection.Name == "Watched").Level);
        Assert.Equal(["Lab", "Lab/Linux"], library.Folders.Select(folder => folder.Path).ToArray());
        Assert.Equal(("Linux", "Lab"), (library.Folders[1].Name, library.Folders[1].Parent));

        var web = library.Connections.Single(connection => connection.Name == "Web");
        Assert.Equal(("rdp", "web.lab", 3389, "admin", "LAB", "Lab/Linux", "Connect", true), (web.Protocol, web.Host, web.Port, web.UserName, web.Domain, web.Folder, web.Level, web.HasStoredLogin));
        Assert.True(library.Connections.Single(connection => connection.Name == "Mine").IsPrivate);

        var json = System.Text.Json.JsonSerializer.Serialize(library, AppLibrary.JsonOptions);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("BEGIN KEY", json);

        // The revision follows what is in it - and only that.
        Assert.Equal(library.Revision, AppLibrary.Build(user, servers).Revision);
        servers[0].Name = "Web 2";
        Assert.NotEqual(library.Revision, AppLibrary.Build(user, servers).Revision);
    }

    [Fact]
    public async Task An_open_relay_tells_how_far_the_host_is_from_Matgate()
    {
        var host = new TcpListener(IPAddress.Loopback, 0);
        host.Start();
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)host.LocalEndpoint).Port);
        using var accepted = await host.AcceptTcpClientAsync();
        var (device, server) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(AppRelay.HostRoundTrip(device, server));
        using (AppRelay.Track(target.Client, device, server))
        {
            Assert.NotNull(AppRelay.HostRoundTrip(device, server));
            Assert.Null(AppRelay.HostRoundTrip(Guid.NewGuid(), server)); // another device asks for its own relay only
        }

        Assert.Null(AppRelay.HostRoundTrip(device, server));
        host.Stop();
    }

    [Fact]
    public async Task The_relay_carries_bytes_both_ways_and_ends_with_either_side()
    {
        // The host: echoes what it gets, upper case, then closes after "BYE".
        var host = new TcpListener(IPAddress.Loopback, 0);
        host.Start();
        var hostTask = Task.Run(async () =>
        {
            using var client = await host.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                var text = Encoding.ASCII.GetString(buffer, 0, read);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToUpperInvariant()));
                if (read == 0 || text.Contains("bye", StringComparison.Ordinal))
                {
                    return;
                }
            }
        });

        // A WebSocket pair over a socket pair: the app's end and Matgate's end.
        var pairListener = new TcpListener(IPAddress.Loopback, 0);
        pairListener.Start();
        using var appSide = new TcpClient();
        await appSide.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pairListener.LocalEndpoint).Port);
        using var matgateSide = await pairListener.AcceptTcpClientAsync();
        using var app = WebSocket.CreateFromStream(appSide.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        using var matgate = WebSocket.CreateFromStream(matgateSide.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);

        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)host.LocalEndpoint).Port);
        var relay = AppRelay.RelayAsync(matgate, target.GetStream(), CancellationToken.None);

        async Task<string> Exchange(string text)
        {
            await app.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Binary, true, CancellationToken.None);
            var buffer = new byte[1024];
            var result = await app.ReceiveAsync(buffer, CancellationToken.None);
            return Encoding.ASCII.GetString(buffer, 0, result.Count);
        }

        Assert.Equal("HELLO", await Exchange("hello"));
        Assert.Equal("BYE", await Exchange("bye"));

        // The host closed: the relay closes the WebSocket with its handshake, and ends once the app answered.
        var closing = await app.ReceiveAsync(new byte[16], CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, closing.MessageType);
        await app.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        var (up, down) = await relay.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal((8L, 8L), (up, down));
        Assert.Equal(WebSocketState.Closed, app.State);
        await hostTask;
        host.Stop();
        pairListener.Stop();
    }
}
