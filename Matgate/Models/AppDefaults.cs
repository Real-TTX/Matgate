namespace Matgate.Models;

// Was ein neuer Benutzer mitbekommt, bevor er selbst etwas einstellt. Liegt als defaults.json
// im Datenverzeichnis und ist damit auch von Hand zu lesen und zu ändern. Bestehende Benutzer
// rührt eine Änderung nicht an - sonst würde eine Vorgabe stillschweigend überschreiben, was
// jemand für sich eingerichtet hat.
public sealed class AppDefaults
{
    public List<string> HomeSections { get; set; } = [];

    public List<string> HiddenHomeSections { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
