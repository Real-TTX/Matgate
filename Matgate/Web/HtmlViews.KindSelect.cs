namespace Matgate.Web;

// The choice of a connection's kind: one field that shows the chosen kind and opens a list of all of them,
// grouped (remote access, files, databases, web, network). It replaced a row of chips that had grown to
// thirteen. The options stay in the page as hidden buttons - they are what is chosen, the list only shows
// them - so a page script listens to them as before, and the layout script turns them into the menu.
public sealed partial class HtmlViews
{
    internal sealed record KindOption(string Value, string Name, string Desc, string IconKey, string Color, string Group);

    // The group of a kind, by its protocol in lower case ("rdp", "mssql", ...).
    internal static string KindGroup(string protocol, bool de) => protocol.ToLowerInvariant() switch
    {
        "rdp" or "vnc" or "ssh" or "agent" => de ? "Fernzugriff" : "Remote access",
        "sftp" or "ftp" or "smb" or "webdav" => de ? "Dateien" : "Files",
        "mssql" or "mysql" or "postgres" => de ? "Datenbanken" : "Databases",
        "website" => "Web",
        _ => de ? "Netz" : "Network",
    };

    // optionAttribute names the attribute that carries an option's value (the page scripts look for it).
    internal static string KindSelect(IReadOnlyList<KindOption> options, string current, string optionAttribute, bool de)
    {
        // Each group stands where its first member stands in the order given (the user's order in quick
        // connect), and keeps that order inside.
        var groupOrder = options.Select(option => option.Group).Distinct().ToList();
        var ordered = options
            .Select((option, index) => (option, index))
            .OrderBy(entry => groupOrder.IndexOf(entry.option.Group))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.option)
            .ToList();
        var chosen = ordered.FirstOrDefault(option => string.Equals(option.Value, current, StringComparison.OrdinalIgnoreCase))
            ?? ordered.FirstOrDefault();

        var buttons = string.Join("", ordered.Select(option => $$"""
            <button type="button" class="kind-option{{(option == chosen ? " active" : "")}}" {{optionAttribute}}="{{A(option.Value)}}" data-kind-value="{{A(option.Value)}}" data-kind-group="{{A(option.Group)}}" style="--proto: {{A(option.Color)}}" tabindex="-1">{{KindFace(option)}}</button>
            """));
        var title = de ? "Art der Verbindung" : "Kind of connection";
        return $$"""
            <div class="kind-select" data-kind-select data-title="{{A(title)}}" data-close="{{A(de ? "Schließen" : "Close")}}">
                <button type="button" class="kind-select-button" data-kind-button aria-haspopup="menu" aria-expanded="false" aria-label="{{A(title)}}">
                    <span class="kind-select-current" data-kind-current>{{(chosen is null ? "" : KindFace(chosen))}}</span>
                    <span class="kind-select-chevron">{{Icon("chevron-down")}}</span>
                </button>
                <div class="kind-select-options" data-kind-options hidden>{{buttons}}</div>
            </div>
            """;
    }

    private static string KindFace(KindOption option) => $$"""
        <span class="kind-icon" style="--proto: {{A(option.Color)}}">{{Icon(option.IconKey)}}</span><span class="kind-text"><span class="kind-name">{{E(option.Name)}}</span><span class="kind-desc">{{E(option.Desc)}}</span></span>
        """;
}
