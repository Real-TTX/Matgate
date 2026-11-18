namespace Matgate.Agent;

// The picture this machine shows in Matgate, on the agent's start page: a company's logo, a photo of the
// machine, whatever tells its user at a glance where they are.
//
// It is a file next to the settings: the one named in the configuration ("Logo", a path - relative ones
// start at the settings folder), or without that a file called logo.png (.jpg, .webp, .gif, .svg) there.
// The setup puts it there; a file dropped into the folder by hand is found just the same, without a restart.
public static class AgentLogo
{
    public const int MaxBytes = 1024 * 1024;

    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg"];

    public const string DialogFilter = "Bilder (*.png;*.jpg;*.jpeg;*.webp;*.gif;*.svg)|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.svg";

    public sealed record Picture(string Path, string Type, long Stamp, long Size);

    public static string? TypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        _ => null,
    };

    // The logo as it is right now, or null: none set, the file gone, too large, or not a picture.
    public static Picture? Find(AgentConfig config, string folder)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.Logo))
        {
            candidates.Add(System.IO.Path.IsPathRooted(config.Logo) ? config.Logo : System.IO.Path.Combine(folder, config.Logo));
        }

        candidates.AddRange(Extensions.Select(extension => System.IO.Path.Combine(folder, "logo" + extension)));
        foreach (var candidate in candidates)
        {
            try
            {
                var file = new FileInfo(candidate);
                if (file.Exists && file.Length is > 0 and <= MaxBytes && TypeOf(candidate) is { } type)
                {
                    return new Picture(file.FullName, type, file.LastWriteTimeUtc.Ticks ^ file.Length, file.Length);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A path that cannot be looked at is no logo.
            }
        }

        return null;
    }

    // Why a file cannot be the logo, as a sentence for the person who picked it; null when it can.
    public static string? Problem(string path)
    {
        if (TypeOf(path) is null)
        {
            return "Als Logo gehen PNG, JPG, WebP, GIF und SVG.";
        }

        var file = new FileInfo(path);
        return !file.Exists ? "Die Datei gibt es nicht."
            : file.Length == 0 ? "Die Datei ist leer."
            : file.Length > MaxBytes ? "Das Bild ist größer als 1 MB - bitte ein kleineres nehmen."
            : null;
    }

    // Takes a copy of the picture into the settings folder (so it stays when the original moves) and
    // returns what goes into the configuration.
    public static string Store(string source, string folder)
    {
        if (Problem(source) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var name = "logo" + System.IO.Path.GetExtension(source).ToLowerInvariant();
        var target = System.IO.Path.Combine(folder, name);
        var bytes = File.ReadAllBytes(source);
        Remove(folder);
        File.WriteAllBytes(target, bytes);
        return name;
    }

    public static void Remove(string folder)
    {
        foreach (var extension in Extensions)
        {
            try
            {
                File.Delete(System.IO.Path.Combine(folder, "logo" + extension));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // What cannot be deleted stays; the configuration no longer names it.
            }
        }
    }
}
