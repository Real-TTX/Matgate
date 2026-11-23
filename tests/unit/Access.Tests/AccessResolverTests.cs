using Matgate.Models;
using Matgate.Services;
using Xunit;

namespace Matgate.Access.Tests;

public sealed class AccessResolverTests
{
    private static readonly UserGroup It = new() { Name = "IT" };

    private static (MatgateUser User, Dictionary<string, FolderDefinition> Folders) Person(params (AccessGrant Grant, UserGroup? Group)[] grants) =>
        Person([], grants);

    private static (MatgateUser User, Dictionary<string, FolderDefinition> Folders) Person(FolderDefinition[] folders, params (AccessGrant Grant, UserGroup? Group)[] grants)
    {
        var index = folders.ToDictionary(folder => AccessResolver.FolderKey(folder.Name));
        var user = new MatgateUser { UserName = "anna" };
        user.Access = new UserAccess([.. grants.Select(entry => new ReachingGrant(entry.Grant, entry.Group))], index);
        return (user, index);
    }

    private static AccessGrant On(AccessTargetKind kind, Guid id, AccessLevel level) =>
        new() { TargetKind = kind, TargetId = id, Level = level };

    private static ServerEndpoint Server(string folder = "") => new() { Name = "host", FolderName = folder };

    [Fact]
    public void Nothing_given_means_no_access()
    {
        var (user, _) = Person();
        Assert.Equal(AccessLevel.None, AccessResolver.Level(user, Server("Lab")));
    }

    [Fact]
    public void Administrators_may_manage_everything_also_someone_elses_own()
    {
        var (user, _) = Person();
        user.IsAdmin = true;
        Assert.Equal(AccessLevel.Manage, AccessResolver.Level(user, new ServerEndpoint { OwnerUserId = Guid.NewGuid() }));
    }

    [Fact]
    public void Someone_elses_own_connection_is_not_reached_by_an_entry_on_all()
    {
        var (user, _) = Person((On(AccessTargetKind.All, Guid.Empty, AccessLevel.Manage), null));
        Assert.Equal(AccessLevel.None, AccessResolver.Level(user, new ServerEndpoint { OwnerUserId = Guid.NewGuid() }));
    }

    [Fact]
    public void The_owner_manages_their_own()
    {
        var (user, _) = Person();
        Assert.Equal(AccessLevel.Manage, AccessResolver.Level(user, new ServerEndpoint { OwnerUserId = user.Id }));
    }

    [Fact]
    public void A_folder_entry_reaches_connections_in_subfolders()
    {
        var lab = new FolderDefinition { Name = "Lab" };
        var (user, _) = Person([lab], (On(AccessTargetKind.Folder, lab.Id, AccessLevel.Connect), It));
        Assert.Equal(AccessLevel.Connect, AccessResolver.Level(user, Server("Lab")));
        Assert.Equal(AccessLevel.Connect, AccessResolver.Level(user, Server("lab/Linux/Web")));
        Assert.Equal(AccessLevel.None, AccessResolver.Level(user, Server("Laboratory")));
        Assert.Equal(AccessLevel.None, AccessResolver.Level(user, Server("")));
    }

    [Fact]
    public void The_highest_level_wins()
    {
        var lab = new FolderDefinition { Name = "Lab" };
        var server = Server("Lab");
        var (user, _) = Person([lab],
            (On(AccessTargetKind.All, Guid.Empty, AccessLevel.ReadOnly), null),
            (On(AccessTargetKind.Folder, lab.Id, AccessLevel.Edit), It),
            (On(AccessTargetKind.Server, server.Id, AccessLevel.Connect), null));
        Assert.Equal(AccessLevel.Edit, AccessResolver.Level(user, server));
        Assert.Equal(3, AccessResolver.Reasons(user, server).Count);
    }

    [Fact]
    public void A_folder_that_stops_inheriting_keeps_out_what_is_given_above_it()
    {
        var lab = new FolderDefinition { Name = "Lab" };
        var secret = new FolderDefinition { Name = "Lab/Secret", InheritanceBroken = true };
        var (user, _) = Person([lab, secret],
            (On(AccessTargetKind.All, Guid.Empty, AccessLevel.Connect), null),
            (On(AccessTargetKind.Folder, lab.Id, AccessLevel.Manage), It));
        Assert.Equal(AccessLevel.Manage, AccessResolver.Level(user, Server("Lab/Open")));
        Assert.Equal(AccessLevel.None, AccessResolver.Level(user, Server("Lab/Secret")));
        Assert.Equal(AccessLevel.None, AccessResolver.Level(user, Server("Lab/Secret/Deeper")));
    }

    [Fact]
    public void What_is_given_on_the_folder_that_stops_inheriting_still_holds_in_it()
    {
        var secret = new FolderDefinition { Name = "Lab/Secret", InheritanceBroken = true };
        var (user, _) = Person([secret], (On(AccessTargetKind.Folder, secret.Id, AccessLevel.ReadOnly), null));
        Assert.Equal(AccessLevel.ReadOnly, AccessResolver.Level(user, Server("Lab/Secret/Deeper")));
        Assert.True(AccessResolver.IsReadOnly(user, Server("Lab/Secret")));
    }

    [Fact]
    public void Server_managers_edit_every_shared_connection_also_behind_a_broken_folder()
    {
        var secret = new FolderDefinition { Name = "Secret", InheritanceBroken = true };
        var (user, _) = Person([secret]);
        user.CanManageServers = true;
        Assert.Equal(AccessLevel.Edit, AccessResolver.Level(user, Server("Secret")));
    }

    [Fact]
    public void Managing_a_folder_needs_manage_on_it_or_above()
    {
        var lab = new FolderDefinition { Name = "Lab" };
        var (user, _) = Person([lab], (On(AccessTargetKind.Folder, lab.Id, AccessLevel.Manage), It));
        Assert.True(AccessResolver.CanManageFolder(user, "Lab/Linux"));
        Assert.False(AccessResolver.CanManageFolder(user, "Other"));
        Assert.False(AccessResolver.CanManageAll(user));
    }

    [Fact]
    public void Reasons_name_the_group_the_folder_and_that_it_is_inherited()
    {
        var lab = new FolderDefinition { Name = "Lab" };
        var (user, _) = Person([lab], (On(AccessTargetKind.Folder, lab.Id, AccessLevel.Connect), It));
        var reason = Assert.Single(AccessResolver.Reasons(user, Server("Lab/Linux")));
        Assert.Equal(It, reason.Group);
        Assert.Equal("Lab", reason.FolderPath);
        Assert.True(reason.Inherited);
    }
}
