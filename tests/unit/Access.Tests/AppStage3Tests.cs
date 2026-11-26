using System.IO.Compression;
using System.Text.Json.Nodes;
using Matgate.Agent;
using Matgate.Display;
using Matgate.Models;
using Matgate.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Matgate.Access.Tests;

// The app API, stage 3: a session on Matgate whose picture goes to the app as compressed pieces of the screen, and
// connections written back from the app - with the rules of the connection form, and never a secret.
public sealed class AppStage3Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static MatgateUser UserWith(AccessLevel level, ServerEndpoint server, bool createPrivate = false, bool manage = false)
    {
        var user = new MatgateUser { UserName = "anna", CanCreateServers = createPrivate, CanManageServers = manage };
        user.Access = new UserAccess(
            [new ReachingGrant(new AccessGrant { TargetKind = AccessTargetKind.Server, TargetId = server.Id, Level = level }, null)],
            new Dictionary<string, FolderDefinition>());
        return user;
    }

    [Fact]
    public void A_tile_is_the_rectangle_as_bgrx_rows_in_zlib()
    {
        const int width = 7;
        const int height = 5;
        const int stride = width * 4 + 8;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index * 31);
        }

        var packed = ScreenSession.PackTile(pixels, stride, 2, 1, 3, 2);

        using var unpacked = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
        {
            zlib.CopyTo(unpacked);
        }

        var expected = new List<byte>();
        for (var row = 1; row < 3; row++)
        {
            expected.AddRange(pixels.AsSpan(row * stride + 2 * 4, 3 * 4).ToArray());
        }

        Assert.Equal(expected, unpacked.ToArray());
    }

    [Fact]
    public async Task A_session_for_an_app_sends_the_whole_screen_first_then_what_changed_paced_by_its_answers()
    {
        var backend = new FakeBackend(64, 48);
        var frames = new List<AgentFrame>();
        var sent = new SemaphoreSlim(0);
        var screen = new ScreenSession(
            new ScreenTarget { Protocol = "rdp", Host = "h", Session = "test-session-1", Quality = "auto" },
            (_, host) => backend.With(host),
            NullLogger.Instance,
            tiles: true);
        screen.OnEvent = frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }

            if (frame.Op == "screen.tiles")
            {
                sent.Release();
            }
        };

        try
        {
            var ready = await screen.OpenAsync(CancellationToken.None);
            Assert.Equal(64, ready["width"]!.GetValue<int>());

            Assert.True(await sent.WaitAsync(TimeSpan.FromSeconds(5)));
            AgentFrame first;
            lock (frames)
            {
                first = frames.Last(frame => frame.Op == "screen.tiles");
                Assert.Contains(frames, frame => frame.Op == "screen.monitor");
            }

            Assert.True(first.Data!["key"]!.GetValue<bool>());
            Assert.Equal((0, 0, 64, 48), (first.Int("x"), first.Int("y"), first.Int("width"), first.Int("height")));
            Assert.Equal(ScreenSession.TileFormat, first.Text("format"));
            screen.Post("screen.ack", new JsonObject { ["seq"] = first.Long("seq") }, default);

            backend.Host!.Dirty(10, 20, 5, 4);
            Assert.True(await sent.WaitAsync(TimeSpan.FromSeconds(5)));
            AgentFrame second;
            lock (frames)
            {
                second = frames.Last(frame => frame.Op == "screen.tiles");
            }

            Assert.False(second.Data!["key"]!.GetValue<bool>());
            Assert.Equal((10, 20, 5, 4), (second.Int("x"), second.Int("y"), second.Int("width"), second.Int("height")));
            Assert.Equal(first.Long("seq") + 1, second.Long("seq"));
        }
        finally
        {
            await screen.DisposeAsync();
        }
    }

    [Fact]
    public void Watching_is_enough_for_a_session_on_Matgate_but_not_for_the_relay()
    {
        var rdp = new ServerEndpoint { Name = "Desk", Protocol = ServerProtocol.Rdp, Host = "desk" };
        var ssh = new ServerEndpoint { Name = "Shell", Protocol = ServerProtocol.Ssh, Host = "shell" };
        var watcher = UserWith(AccessLevel.ReadOnly, rdp);
        Assert.True(AppLibrary.CanWatch(watcher, rdp));
        Assert.False(AppLibrary.CanUse(watcher, rdp));
        Assert.False(AppLibrary.CanWatch(UserWith(AccessLevel.Connect, ssh), ssh));
        Assert.False(AppLibrary.CanWatch(UserWith(AccessLevel.None, rdp), rdp));
        rdp.IsEnabled = false;
        Assert.False(AppLibrary.CanWatch(UserWith(AccessLevel.Manage, rdp), rdp));
    }

    [Fact]
    public void Changing_needs_edit_and_deleting_needs_manage()
    {
        var server = new ServerEndpoint { Name = "Desk", Protocol = ServerProtocol.Rdp, Host = "desk" };
        Assert.False(AppLibrary.CanEdit(UserWith(AccessLevel.Connect, server), server));
        Assert.True(AppLibrary.CanEdit(UserWith(AccessLevel.Edit, server), server));
        Assert.False(AppLibrary.CanDelete(UserWith(AccessLevel.Edit, server), server));
        Assert.True(AppLibrary.CanDelete(UserWith(AccessLevel.Manage, server), server));
        // A server manager deletes shared connections, as in the form.
        Assert.True(AppLibrary.CanDelete(UserWith(AccessLevel.Edit, server, manage: true), server));

        // Websites, files and databases are the app's too now; Matgate's own kinds (agents) are not.
        var website = new ServerEndpoint { Name = "Site", Protocol = ServerProtocol.Website, Host = "site" };
        Assert.True(AppLibrary.CanEdit(UserWith(AccessLevel.Manage, website), website));
        var agent = new ServerEndpoint { Name = "Agent", Protocol = ServerProtocol.Agent, Host = "agent" };
        Assert.False(AppLibrary.CanEdit(UserWith(AccessLevel.Manage, agent), agent));
    }

    [Fact]
    public void Websites_files_and_databases_are_written_back_with_their_own_fields()
    {
        var creator = new MatgateUser { UserName = "c", CanCreateServers = true };
        var now = DateTimeOffset.UtcNow;

        var site = new AppLibrary.ConnectionChange("Wiki", "website", null, null, null, null, "Lab", null, null, true, false, Url: "wiki.lab/start");
        Assert.Null(AppLibrary.Check(site, null));
        var created = AppLibrary.Create(site, creator, now);
        Assert.Equal((ServerProtocol.Website, "https://wiki.lab/start", "wiki.lab", 0), (created.Protocol, created.WebsiteUrl, created.Host, created.Port));
        Assert.Equal(("wiki.lab", 443), AppLibrary.RelayTarget(created));
        Assert.Equal("url", AppLibrary.Check(site with { Url = "" }, null)?.Field);
        Assert.Equal("url", AppLibrary.Check(site with { Url = "ftp://x" }, null)?.Field);

        var share = AppLibrary.Create(new AppLibrary.ConnectionChange("Ablage", "smb", "nas", null, "anna", "LAB", null, null, null, null, false, RootPath: "daten/projekte"), creator, now);
        Assert.Equal((ServerProtocol.Smb, 445, "LAB", "daten/projekte"), (share.Protocol, share.Port, share.Domain, share.FileRootPath));

        var database = AppLibrary.Create(new AppLibrary.ConnectionChange("DB", "mssql", "sql", 0, "sa", null, null, null, null, false, false, Database: "erp"), creator, now);
        Assert.Equal((ServerProtocol.MsSql, 1433, "erp", false), (database.Protocol, database.Port, database.DatabaseName, database.IgnoreCertificate));

        // FTP opens further connections for its data: the relay does not carry it, everything else it does.
        Assert.False(AppLibrary.IsRelayProtocol(ServerProtocol.Ftp));
        Assert.True(AppLibrary.IsRelayProtocol(ServerProtocol.Smb));
        Assert.True(AppLibrary.IsRelayProtocol(ServerProtocol.Postgres));
    }

    [Fact]
    public void Creating_is_private_with_create_connections_and_shared_with_manage_connections()
    {
        var nobody = new MatgateUser { UserName = "x" };
        Assert.False(AppLibrary.CanCreate(nobody, shared: false));
        Assert.False(AppLibrary.CanCreate(nobody, shared: true));
        var creator = new MatgateUser { UserName = "c", CanCreateServers = true };
        Assert.True(AppLibrary.CanCreate(creator, shared: false));
        Assert.False(AppLibrary.CanCreate(creator, shared: true));
        Assert.True(AppLibrary.CanCreate(new MatgateUser { UserName = "m", CanManageServers = true }, shared: true));

        var library = AppLibrary.Build(creator, []);
        Assert.Equal((true, false), (library.User.CanCreatePrivate, library.User.CanCreateShared));

        var server = AppLibrary.Create(
            new AppLibrary.ConnectionChange(" Neu ", "vnc", " vnc.lab ", null, "anna", "IGNORED", "Lab//Linux/", "Notiz", null, false, false),
            creator,
            Now);
        Assert.Equal(("Neu", ServerProtocol.Vnc, "vnc.lab", 5900, "anna", "", "Lab/Linux"), (server.Name, server.Protocol, server.Host, server.Port, server.UserName, server.Domain, server.FolderName));
        Assert.Equal(creator.Id, server.OwnerUserId);
        Assert.False(server.IgnoreCertificate);
        Assert.Equal("", server.Password);
    }

    [Theory]
    [InlineData(null, "h", "rdp", "name", "required")]
    [InlineData("n", " ", "rdp", "host", "required")]
    [InlineData("n", "a b", "rdp", "host", "invalid")]
    [InlineData("n", "h", "agent", "protocol", "protocol")]
    public void A_new_connection_needs_a_name_a_host_and_an_app_protocol(string? name, string host, string protocol, string field, string error)
    {
        var problem = AppLibrary.Check(new AppLibrary.ConnectionChange(name, protocol, host, null, null, null, null, null, null, null, null), null);
        Assert.Equal(new AppLibrary.ChangeProblem(field, error), problem);
    }

    [Fact]
    public void A_change_takes_over_what_a_connection_is_and_never_a_secret_a_tunnel_or_the_owner()
    {
        var owner = Guid.NewGuid();
        var tunnel = Guid.NewGuid();
        var server = new ServerEndpoint
        {
            Name = "Old", Protocol = ServerProtocol.Rdp, Host = "old", Port = 3390, UserName = "u", Password = "secret",
            Domain = "D", TunnelId = tunnel, OwnerUserId = owner, KeyboardLayout = "de-de-qwertz",
        };

        Assert.Equal(new AppLibrary.ChangeProblem("protocol", "protocol_fixed"),
            AppLibrary.Check(new AppLibrary.ConnectionChange(null, "ssh", null, null, null, null, null, null, null, null, null), server));
        Assert.Equal(new AppLibrary.ChangeProblem("port", "invalid"),
            AppLibrary.Check(new AppLibrary.ConnectionChange(null, null, null, 70000, null, null, null, null, null, null, null), server));

        var change = new AppLibrary.ConnectionChange("New", "rdp", "new", 0, "v", "E", "Office", null, "EN-US-QWERTY", null, true);
        Assert.Null(AppLibrary.Check(change, server));
        AppLibrary.Apply(server, change, Now);

        Assert.Equal(("New", "new", 3389, "v", "E", "Office", "en-us-qwerty"), (server.Name, server.Host, server.Port, server.UserName, server.Domain, server.FolderName, server.KeyboardLayout));
        Assert.Equal(("secret", tunnel, owner), (server.Password, server.TunnelId, server.OwnerUserId));

        // A layout that is no name stays as it was; a connection with stored credentials keeps its user from them.
        server.CredentialId = Guid.NewGuid();
        AppLibrary.Apply(server, new AppLibrary.ConnectionChange(null, null, null, null, "w", null, null, null, "<script>", null, null), Now);
        Assert.Equal(("v", "en-us-qwerty"), (server.UserName, server.KeyboardLayout));
    }

    // A remote desktop of 'width' x 'height' that only changes when the test says so.
    private sealed class FakeBackend(int width, int height) : IScreenBackend
    {
        private readonly byte[] _pixels = new byte[width * height * 4];
        private readonly TaskCompletionSource<string?> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IScreenHost? Host { get; private set; }

        public FakeBackend With(IScreenHost host)
        {
            Host = host;
            return this;
        }

        public string Name => "fake";

        public bool ClipImages => false;

        public bool ClipFiles => false;

        public bool Sound => false;

        public int ColorDepth => 32;

        public bool QualityColours => false;

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<string?> RunAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => _end.TrySetResult(null));
            return await _end.Task;
        }

        public bool WithFrame(FrameReader reader)
        {
            reader(_pixels, width * 4, width, height);
            return true;
        }

        public void Input(JsonObject data)
        {
        }

        public void Resize(int newWidth, int newHeight)
        {
        }

        public void Clip(string op, JsonObject data, ReadOnlyMemory<byte> payload)
        {
        }

        public Task<ClipPiece> ClipReadAsync(string token, int index, long offset, CancellationToken cancellationToken) =>
            Task.FromException<ClipPiece>(new ScreenException("none"));

        public void SecureAttention()
        {
        }

        public void SoundEnabled(bool on)
        {
        }

        public ValueTask DisposeAsync()
        {
            _end.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }
}
