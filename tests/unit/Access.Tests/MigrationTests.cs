using System.Text.Json;
using Matgate.Models;
using Matgate.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Matgate.Access.Tests;

// Access as it was kept before 0.41.0 (on the user, on the group) becomes entries in grants.json - and nobody
// gets more or less than before.
public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "matgate-access-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Matgate";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private JsonDataStore Store()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Matgate:DataDirectory"] = _directory, ["Matgate:SecretKey"] = "unit-test-key" })
            .Build();
        return new JsonDataStore(configuration, new Environment(_directory), NullLogger<JsonDataStore>.Instance, new SecretProtector(configuration, NullLogger<SecretProtector>.Instance));
    }

    private void Write<T>(string file, List<T> values)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, file), JsonSerializer.Serialize(values, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task Old_access_becomes_entries_with_the_same_outcome()
    {
        var lab = new ServerEndpoint { Name = "lab-1", FolderName = "Lab/Linux" };
        var single = new ServerEndpoint { Name = "single" };
        var files = new ServerEndpoint { Name = "files", Protocol = ServerProtocol.Sftp };
        var other = new ServerEndpoint { Name = "other" };
        var everything = new MatgateUser { UserName = "all", ServerAccessAll = true };
        var some = new MatgateUser
        {
            UserName = "some",
            ServerAccess = [single.Id, files.Id],
            FileAccessRules = [new FileAccessRule { ServerId = files.Id, ReadOnly = true }],
        };
        var allButReadOnly = new MatgateUser
        {
            UserName = "mixed",
            ServerAccessAll = true,
            FileAccessRules = [new FileAccessRule { ServerId = files.Id, ReadOnly = true }],
        };
        var member = new MatgateUser { UserName = "member" };
        var group = new UserGroup { Name = "IT", MemberIds = [member.Id], Folders = ["Lab"], ServerIds = [other.Id] };
        Write("servers.json", new List<ServerEndpoint> { lab, single, files, other });
        Write("users.json", new List<MatgateUser> { everything, some, allButReadOnly, member });
        Write("groups.json", new List<UserGroup> { group });

        var store = Store();
        await store.MigrateAccessAsync();
        var users = (await store.GetUsersAsync()).ToDictionary(user => user.UserName);
        var servers = new[] { lab, single, files, other };
        string Levels(string name) => string.Join(",", servers.Select(server => AccessResolver.Level(users[name], server)));

        Assert.Equal("Connect,Connect,Connect,Connect", Levels("all"));
        Assert.Equal("None,Connect,ReadOnly,None", Levels("some"));
        Assert.Equal("Connect,Connect,ReadOnly,Connect", Levels("mixed"));
        Assert.Equal("Connect,None,None,Connect", Levels("member"));
        Assert.All(users.Values, user => Assert.False(user.ServerAccessAll));
        Assert.All(users.Values, user => Assert.Empty(user.ServerAccess));
        Assert.Empty((await store.GetGroupsAsync()).Single().Folders);

        // Once only: a second start changes nothing.
        var count = (await store.GetGrantsAsync()).Count;
        await store.MigrateAccessAsync();
        Assert.Equal(count, (await store.GetGrantsAsync()).Count);
    }
}
