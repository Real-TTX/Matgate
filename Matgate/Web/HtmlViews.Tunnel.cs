using Matgate.Models;

namespace Matgate.Web;

// "Connect via": directly, or through one of the agents into the network it stands in (see TunnelService).
public sealed partial class HtmlViews
{
    // The agents a connection form may offer as the way in, put there by the endpoint that shows the form.
    public const string TunnelChoicesKey = "matgate.tunnels";

    private static string TunnelChoice(HttpContext context, ServerEndpoint? server)
    {
        var de = Language(context) == "de";
        var agents = context.Items[TunnelChoicesKey] as IReadOnlyList<ServerEndpoint> ?? [];
        if (agents.Count == 0 && server?.TunnelId is null)
        {
            return "";
        }

        var options = string.Join("", agents
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .Select(agent => $"""<option value="{agent.Id}"{(agent.Id == server?.TunnelId ? " selected" : "")}>{E(de ? $"Tunnel über {agent.Name}" : $"Tunnel through {agent.Name}")}</option>"""));
        return $$"""
            <label class="conn-wide" data-protocols="rdp,vnc,ssh,sftp,smb,mssql,mysql,postgres">{{(de ? "Verbindung über" : "Connect via")}}
                <select name="tunnelId">
                    <option value="">{{(de ? "Direkt" : "Directly")}}</option>
                    {{options}}
                </select>
                <small class="muted">{{(de
                    ? "Über einen Agent mit eingeschaltetem Tunnel erreicht Matgate Rechner in dessen Netz. Getunnelte Verbindungen öffnet immer Matgates eigene Engine."
                    : "Through an agent with its tunnel switched on, Matgate reaches machines in its network. Tunnelled connections always open with Matgate's own engine.")}}</small>
            </label>
            """;
    }
}
