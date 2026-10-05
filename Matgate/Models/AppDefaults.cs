namespace Matgate.Models;

// What a new user starts out with, before setting anything themselves. Lives as defaults.json in
// the data directory, so it can be read and edited by hand. A change leaves existing users alone -
// otherwise a default would silently overwrite what someone has already set up for themselves.
public sealed class AppDefaults
{
    public List<string> HomeSections { get; set; } = [];

    public List<string> HiddenHomeSections { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
