// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray can take it too.
using System.Globalization;

namespace MatDesktop;

/// <summary>
/// Every text that MatDesktop shows, in English and German (DESK-08): German when the language of the user interface is German, English for every other. <see cref="Language"/> overrides the
/// choice (a setting of the program). Texts with a <c>{0}</c> take the name of the program or a value. A test holds both languages to the same keys and every key the code asks for.
/// </summary>
public static class MatText
{
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // the states (TRAY-02)
        ["state.running"] = "Running",
        ["state.waiting"] = "Waiting",
        ["state.stopped"] = "Stopped",
        ["state.notSetUp"] = "Not set up",
        ["state.tooltip"] = "{0}: {1}",

        // the tray menu (TRAY-01)
        ["tray.open"] = "Open {0}",
        ["tray.info"] = "Info …",
        ["tray.setUp"] = "Set up …",
        ["tray.settings"] = "Settings …",
        ["tray.logFolder"] = "Open log folder",
        ["tray.installUpdate"] = "Install update …",
        ["tray.serviceStart"] = "Start service …",
        ["tray.serviceStop"] = "Stop service …",
        ["tray.serviceRestart"] = "Restart service …",
        ["tray.hideIcon"] = "Hide icon",
        ["tray.quit"] = "Quit",

        // the version (SETUP-07)
        ["version"] = "Version {0} · {1}",
        ["version.local"] = "A local build gets no updates.",

        // the title bar (DESK-03)
        ["caption.minimise"] = "Minimise",
        ["caption.maximise"] = "Maximise",
        ["caption.restore"] = "Restore",
        ["caption.close"] = "Close",

        // the menu (DESK-04)
        ["nav.fold"] = "Fold the menu",
        ["nav.unfold"] = "Open the menu",
        ["nav.about"] = "About {0}",
        ["nav.settings"] = "Settings",
        ["nav.update"] = "Update to {0}",
        ["nav.updateAvailable"] = "Update to {0} available",

        // Info (TRAY-05)
        ["info.title"] = "Info",
        ["info.open"] = "Open",
        ["info.logFolder"] = "Log folder",
        ["info.close"] = "Close",

        // the setup (SETUP-01, SETUP-02, SETUP-04)
        ["setup.mode.setup"] = "Setup",
        ["setup.mode.update"] = "Update",
        ["setup.mode.uninstall"] = "Uninstall",
        ["setup.next"] = "Next",
        ["setup.back"] = "Back",
        ["setup.cancel"] = "Cancel",
        ["setup.install"] = "Install",
        ["setup.update"] = "Update",
        ["setup.uninstall"] = "Uninstall",
        ["setup.done"] = "Done",
        ["setup.welcome"] = "Welcome",
        ["setup.role"] = "Role",
        ["setup.connect"] = "Connect",
        ["setup.options"] = "Options",
        ["setup.progress.setup"] = "Installing …",
        ["setup.progress.update"] = "Updating …",
        ["setup.progress.uninstall"] = "Removing …",
        ["setup.finish.setup"] = "{0} is ready",
        ["setup.finish.update"] = "{0} is up to date",
        ["setup.finish.uninstall"] = "{0} was removed",
        ["setup.failed.setup"] = "The installation failed",
        ["setup.failed.update"] = "The update failed",
        ["setup.failed.uninstall"] = "The uninstall failed",
        ["setup.startNow"] = "Start {0} now",
        ["setup.uninstallTitle"] = "Remove {0}",
        ["setup.deleteData"] = "Also delete my data",
        ["setup.deleteData.text"] = "Settings, logs and what the program stored on this computer. Without the tick they stay for a later installation.",
        ["setup.updateText"] = "Installed is {0}, this setup brings {1}. The settings are kept.",
        ["setup.option.autostart"] = "Start at sign-in",
        ["setup.option.shortcut"] = "Shortcut on the desktop",
        ["setup.option.updates"] = "Install updates automatically",
        ["setup.step.done"] = "Done",
        ["setup.step.running"] = "Running",
        ["setup.step.waiting"] = "Waiting",
        ["setup.step.failed"] = "Failed",
        ["setup.error"] = "Something is missing",

        // copying a value (AGENT-02)
        ["copy"] = "Copy",
        ["copy.named"] = "Copy {0}",
        ["copied"] = "Copied",
    };

    private static readonly Dictionary<string, string> De = new(StringComparer.Ordinal)
    {
        ["state.running"] = "Läuft",
        ["state.waiting"] = "Wartet",
        ["state.stopped"] = "Gestoppt",
        ["state.notSetUp"] = "Nicht eingerichtet",
        ["state.tooltip"] = "{0}: {1}",

        ["tray.open"] = "{0} öffnen",
        ["tray.info"] = "Info …",
        ["tray.setUp"] = "Einrichten …",
        ["tray.settings"] = "Einstellungen …",
        ["tray.logFolder"] = "Log-Ordner öffnen",
        ["tray.installUpdate"] = "Update installieren …",
        ["tray.serviceStart"] = "Dienst starten …",
        ["tray.serviceStop"] = "Dienst beenden …",
        ["tray.serviceRestart"] = "Dienst neu starten …",
        ["tray.hideIcon"] = "Symbol ausblenden",
        ["tray.quit"] = "Beenden",

        ["version"] = "Version {0} · {1}",
        ["version.local"] = "Ein lokaler Build bekommt keine Updates.",

        ["caption.minimise"] = "Minimieren",
        ["caption.maximise"] = "Maximieren",
        ["caption.restore"] = "Wiederherstellen",
        ["caption.close"] = "Schließen",

        ["nav.fold"] = "Menü einklappen",
        ["nav.unfold"] = "Menü ausklappen",
        ["nav.about"] = "Über {0}",
        ["nav.settings"] = "Einstellungen",
        ["nav.update"] = "Update auf {0}",
        ["nav.updateAvailable"] = "Update auf {0} verfügbar",

        ["info.title"] = "Info",
        ["info.open"] = "Öffnen",
        ["info.logFolder"] = "Log-Ordner",
        ["info.close"] = "Schließen",

        ["setup.mode.setup"] = "Einrichtung",
        ["setup.mode.update"] = "Update",
        ["setup.mode.uninstall"] = "Deinstallation",
        ["setup.next"] = "Weiter",
        ["setup.back"] = "Zurück",
        ["setup.cancel"] = "Abbrechen",
        ["setup.install"] = "Installieren",
        ["setup.update"] = "Aktualisieren",
        ["setup.uninstall"] = "Deinstallieren",
        ["setup.done"] = "Fertig",
        ["setup.welcome"] = "Willkommen",
        ["setup.role"] = "Rolle",
        ["setup.connect"] = "Verbinden",
        ["setup.options"] = "Optionen",
        ["setup.progress.setup"] = "Installiere …",
        ["setup.progress.update"] = "Aktualisiere …",
        ["setup.progress.uninstall"] = "Entferne …",
        ["setup.finish.setup"] = "{0} ist bereit",
        ["setup.finish.update"] = "{0} ist aktuell",
        ["setup.finish.uninstall"] = "{0} wurde entfernt",
        ["setup.failed.setup"] = "Die Installation ist fehlgeschlagen",
        ["setup.failed.update"] = "Das Update ist fehlgeschlagen",
        ["setup.failed.uninstall"] = "Die Deinstallation ist fehlgeschlagen",
        ["setup.startNow"] = "{0} jetzt starten",
        ["setup.uninstallTitle"] = "{0} entfernen",
        ["setup.deleteData"] = "Auch meine Daten löschen",
        ["setup.deleteData.text"] = "Einstellungen, Protokolle und was das Programm auf diesem Computer gespeichert hat. Ohne Haken bleiben sie für eine spätere Installation erhalten.",
        ["setup.updateText"] = "Installiert ist {0}, dieses Setup bringt {1}. Die Einstellungen bleiben erhalten.",
        ["setup.option.autostart"] = "Bei der Anmeldung starten",
        ["setup.option.shortcut"] = "Verknüpfung auf dem Desktop",
        ["setup.option.updates"] = "Updates automatisch installieren",
        ["setup.step.done"] = "Erledigt",
        ["setup.step.running"] = "Läuft",
        ["setup.step.waiting"] = "Wartet",
        ["setup.step.failed"] = "Fehlgeschlagen",
        ["setup.error"] = "Etwas fehlt",

        ["copy"] = "Kopieren",
        ["copy.named"] = "{0} kopieren",
        ["copied"] = "Kopiert",
    };

    /// <summary>The texts in English, by key.</summary>
    public static IReadOnlyDictionary<string, string> English => En;

    /// <summary>The texts in German, by key.</summary>
    public static IReadOnlyDictionary<string, string> German => De;

    /// <summary>"de" or "en" to override the choice by the system (a setting of the program); null follows <see cref="CultureInfo.CurrentUICulture"/>.</summary>
    public static string? Language { get; set; }

    /// <summary>True when the texts are German.</summary>
    public static bool IsGerman => ChoosesGerman(Language, CultureInfo.CurrentUICulture);

    /// <summary>The choice: the language of the setting when there is one, else German for a German user interface (<c>de</c>, <c>de-AT</c> ...) and English for every other.</summary>
    public static bool ChoosesGerman(string? language, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return (language ?? culture.TwoLetterISOLanguageName).Equals("de", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The text of a key in the language in force.</summary>
    public static string Get(string key) => (IsGerman ? De : En).TryGetValue(key, out var text) ? text : throw new KeyNotFoundException($"MatText has no text {key}.");

    /// <summary>The text of a key with its values filled in (numbers in the invariant culture).</summary>
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.InvariantCulture, Get(key), values);
}
