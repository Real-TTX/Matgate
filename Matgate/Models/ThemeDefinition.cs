namespace Matgate.Models;

// Eine benannte Palette. Sie beschreibt nur Farben (und, wenn gewollt, die Rundung) - die Aufteilung
// der Oberfläche gehört nicht dazu, sonst wäre ein Thema ein zweites Stylesheet.
//
// Jede Angabe ist freiwillig: was ein Thema nicht nennt, kommt aus der eingebauten Grundpalette.
// Damit kann eine Datei "nur die Akzentfarbe anders" sagen, ohne achtzehn Werte abzuschreiben.
public sealed class ThemeDefinition
{
    // Was in der Einstellung gespeichert wird. Kleingeschrieben, ohne Leerzeichen.
    public string Key { get; set; } = "";

    // Was der Benutzer liest.
    public string Name { get; set; } = "";

    // Die Werte für den hellen und den dunklen Modus, jeweils Token ohne "--" auf Farbe.
    public Dictionary<string, string> Light { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Dark { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Die Farbtupfer der Protokolle (rdp, ssh, vnc, sftp, ftp, smb, website, webdav, local).
    // Leer heißt: die eingebauten.
    public Dictionary<string, string> Protocols { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Die Strichstärke der Symbole. Leer heißt 1.75 wie bisher; 1.5 wirkt feiner, 2 kräftiger.
    public string IconStroke { get; set; } = "";
}
