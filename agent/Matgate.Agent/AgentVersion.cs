namespace Matgate.Agent;

// The version as every window of the agent shows it (SETUP-07): "Version 0.8.0.17 · release" in the footer of the setup and of Info,
// "0.8.0.17 · release" in a row that is already labelled Version, "Matgate Agent 0.8.0.17" as the first line of the tray menu. Always
// the four parts Matgate compares (a CI build is <VersionPrefix>.<run number>). The channel is "release" for a build of the CI, which
// fills the release agent-latest, and "local" for a build on a developer's computer, whose fourth part is 0.
public static class AgentVersion
{
    public const string Release = "release";
    public const string Local = "local";

    // The sample build number of the pictures (setup-preview): they show a release, whoever draws them.
    private const int SampleBuild = 42;

    public static string Number
    {
        get
        {
            var version = typeof(AgentVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            return Machine.Sample && version.Revision <= 0
                ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{SampleBuild}"
                : Normalize(version.ToString());
        }
    }

    public static string Channel => ChannelOf(Number);

    // "Version 0.8.0.17 · release".
    public static string Text => Format(Number);

    public static string Format(string number) => $"Version {Value(number)}";

    // "0.8.0.17 · release": the same order and sign without the word, for a row labelled Version.
    public static string Value(string number) => $"{Normalize(number)} · {ChannelOf(number)}";

    public static string ChannelOf(string number) =>
        Version.TryParse(number, out var version) && version.Revision > 0 ? Release : Local;

    // 0.8.0 and 0.8.0.0 are the same version: always four parts.
    public static string Normalize(string number) =>
        Version.TryParse(number, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)).ToString() : number;
}
