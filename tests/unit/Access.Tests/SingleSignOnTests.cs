using System.Security.Claims;
using Matgate.Models;
using Matgate.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Matgate.Access.Tests;

public sealed class SingleSignOnTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "test"));

    [Fact]
    public void MatIdentity_names_subject_address_and_groups()
    {
        var identity = SingleSignOn.Read(SsoProvider.MatIdentity, Principal(
            ("iss", "https://id.example/"), ("sub", "abc"), ("email", "anna@example.com"), ("email_verified", "true"),
            ("name", "Anna Berg"), ("preferred_username", "anna"), ("groups", "Technik"), ("groups", "Familie"), ("role", "user")));
        Assert.NotNull(identity);
        Assert.Equal("abc", identity.Subject);
        Assert.True(identity.EmailVerified);
        Assert.Equal(["matidentity:Technik", "matidentity:Familie", "matidentity:user"], identity.Groups);
    }

    [Fact]
    public void Entra_is_known_by_tenant_and_object_id_and_its_sign_in_address()
    {
        var identity = SingleSignOn.Read(SsoProvider.Entra, Principal(
            ("iss", "https://login.microsoftonline.com/t/v2.0"), ("sub", "per-app"), ("oid", "object-1"), ("tid", "tenant-1"),
            ("preferred_username", "anna.berg@firma.de"), ("name", "Anna Berg"), ("groups", "3f2a"), ("roles", "Gate.Admins")));
        Assert.NotNull(identity);
        Assert.Equal("tenant-1/object-1", identity.Subject);
        Assert.Equal("anna.berg@firma.de", identity.Email);
        Assert.Equal(["entra:3f2a", "entra:Gate.Admins"], identity.Groups);
    }

    [Fact]
    public void Without_a_subject_nobody_signed_in()
    {
        Assert.Null(SingleSignOn.Read(SsoProvider.MatIdentity, Principal(("email", "x@example.com"))));
    }

    [Fact]
    public void A_new_user_name_comes_from_the_address_and_is_unique()
    {
        var identity = new ExternalIdentity("entra", "", "s", "anna.berg@firma.de", true, "Anna", "anna.berg@firma.de", []);
        Assert.Equal("anna.berg", SingleSignOn.NewUserName(identity, _ => false));
        Assert.Equal("anna.berg3", SingleSignOn.NewUserName(identity, name => name is "anna.berg" or "anna.berg2"));
        var shortName = identity with { UserName = "al", Email = "" };
        Assert.Equal("entra-al", SingleSignOn.NewUserName(shortName, _ => false));
    }

    [Fact]
    public void Groups_follow_what_the_service_names_and_only_linked_groups_are_touched()
    {
        var user = Guid.NewGuid();
        var technik = new UserGroup { Name = "Technik", ExternalGroups = ["matidentity:Technik"] };
        var stale = new UserGroup { Name = "Alt", ExternalGroups = ["matidentity:Alt"], MemberIds = [user] };
        var manual = new UserGroup { Name = "Hand", MemberIds = [user] };
        var other = new UserGroup { Name = "Entra", ExternalGroups = ["entra:abc"], MemberIds = [user] };
        var groups = new List<UserGroup> { technik, stale, manual, other };

        Assert.True(SingleSignOn.SyncGroups(user, "matidentity", ["matidentity:technik"], groups));
        Assert.Contains(user, technik.MemberIds);
        Assert.DoesNotContain(user, stale.MemberIds);
        Assert.Contains(user, manual.MemberIds);
        Assert.Contains(user, other.MemberIds);
        Assert.False(SingleSignOn.SyncGroups(user, "matidentity", ["matidentity:Technik"], groups));
    }

    [Fact]
    public void Services_are_on_only_with_their_settings()
    {
        var none = SsoSettings.From(new ConfigurationBuilder().Build());
        Assert.Empty(none.Providers);
        var both = SsoSettings.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MATIDENTITY_AUTHORITY"] = "https://id.example",
            ["MATIDENTITY_CLIENT_ID"] = "matgate",
            ["MATGATE_ENTRA_TENANT_ID"] = "t1",
            ["MATGATE_ENTRA_CLIENT_ID"] = "c1",
            ["MATGATE_SSO_ONLY"] = "true",
        }).Build());
        Assert.Equal(["matidentity", "entra"], both.Providers.Select(provider => provider.Key));
        Assert.Equal("https://id.example/", both.Find("matidentity")!.Authority);
        Assert.Equal("https://login.microsoftonline.com/t1/v2.0/", both.Find("entra")!.Authority);
        Assert.Equal("/signin-oidc", both.Find("matidentity")!.CallbackPath);
        Assert.True(both.SsoOnly);
    }

    [Fact]
    public void Entra_with_too_many_groups_is_marked_incomplete()
    {
        var overage = SingleSignOn.Read(SsoProvider.Entra, Principal(
            ("oid", "o1"), ("tid", "t1"), ("preferred_username", "anna@firma.de"),
            ("_claim_names", "{\"groups\":\"src1\"}")));
        Assert.NotNull(overage);
        Assert.True(overage!.GroupsIncomplete);
        Assert.Empty(overage.Groups);

        var implicitFlow = SingleSignOn.Read(SsoProvider.Entra, Principal(("oid", "o1"), ("tid", "t1"), ("hasgroups", "true")));
        Assert.True(implicitFlow!.GroupsIncomplete);

        var normal = SingleSignOn.Read(SsoProvider.Entra, Principal(("oid", "o1"), ("tid", "t1"), ("groups", "3f2a")));
        Assert.False(normal!.GroupsIncomplete);
        Assert.Equal(["entra:3f2a"], normal.Groups);

        var noGroupsAtAll = SingleSignOn.Read(SsoProvider.Entra, Principal(("oid", "o1"), ("tid", "t1")));
        Assert.False(noGroupsAtAll!.GroupsIncomplete);
    }
}
