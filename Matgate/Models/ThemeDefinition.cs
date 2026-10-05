namespace Matgate.Models;

// A named palette. It describes colours only (and, if wanted, the corner rounding) - the layout of
// the UI is not part of it, otherwise a theme would be a second stylesheet.
//
// Every entry is optional: what a theme does not name comes from the built-in base palette. That
// way a file can say "only the accent colour is different" without copying eighteen values.
public sealed class ThemeDefinition
{
    // What gets stored in the setting. Lower case, no spaces.
    public string Key { get; set; } = "";

    // What the user reads.
    public string Name { get; set; } = "";

    // The values for light and dark mode, each token without "--" mapped to a colour.
    public Dictionary<string, string> Light { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Dark { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // The protocol colour dots (rdp, ssh, vnc, sftp, ftp, smb, website, webdav, local).
    // Empty means: the built-in ones.
    public Dictionary<string, string> Protocols { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // The same for dark mode; empty means: the same as above.
    public Dictionary<string, string> ProtocolsDark { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // The stroke width of the icons. Empty means 1.75 as before; 1.5 looks finer, 2 bolder.
    public string IconStroke { get; set; } = "";
}
