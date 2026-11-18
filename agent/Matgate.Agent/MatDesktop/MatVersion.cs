// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray can take it too.
namespace MatDesktop;

/// <summary>
/// The version as every program shows it (SETUP-07): <c>Version 1.4.2 · release</c>, <c>Version nightly-212-20261115 · nightly</c>, <c>Version local-20261115 · local</c>; at the foot of the
/// menu, in About, in the footer of the setup and in Info. The first line of the tray menu is the name and the number (<see cref="Title"/>). A local build says that it gets no updates.
/// </summary>
public static class MatVersion
{
    /// <summary>The channel of a published release.</summary>
    public const string Release = "release";

    /// <summary>The channel of the builds of the development branch.</summary>
    public const string Nightly = "nightly";

    /// <summary>The channel of a build on a developer's computer: it gets no updates.</summary>
    public const string Local = "local";

    /// <summary><c>Version 1.4.2 · release</c>.</summary>
    public static string Format(string version, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return MatText.Format("version", version.Trim(), channel.Trim());
    }

    /// <summary>
    /// <c>1.4.2 · release</c>: the same order and sign without the word, for the value of a row that is already labelled Version (the facts of Info, About), so that it never reads
    /// another way, like <c>1.4.2 (release)</c>.
    /// </summary>
    public static string Value(string version, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return $"{version.Trim()} · {channel.Trim()}";
    }

    /// <summary>The first line of the tray menu: the name and the number (<c>Example Agent 1.4.2</c>).</summary>
    public static string Title(string appName, string version) => $"{appName} {version}";

    /// <summary>True when a build of the channel gets updates (every channel but local).</summary>
    public static bool GetsUpdates(string channel) => !string.Equals(channel?.Trim(), Local, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sentence that a local build gets no updates, or null for any other channel.</summary>
    public static string? LocalNote(string channel) => GetsUpdates(channel) ? null : MatText.Get("version.local");
}
