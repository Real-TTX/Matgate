using Matgate.Agent.Setup;

namespace Matgate.Agent.Ui;

// The four states of the tray (TRAY-02), each a colour (Theme.State) and a word: the second line of the menu, the tooltip and the
// first line of Info. The colour is never the only sign (COLOR-04).
internal enum AgentState
{
    // The agent answers on its status pipe: green.
    Running,

    // It is starting or stopping, or was just asked to: amber.
    Waiting,

    // Installed, but it does not answer: red.
    Stopped,

    // Not installed on this machine: grey.
    NotSetUp,
}

internal static class AgentStates
{
    public static string Word(this AgentState state) => state switch
    {
        AgentState.Running => "Läuft",
        AgentState.Waiting => "Wartet",
        AgentState.Stopped => "Gestoppt",
        _ => "Nicht eingerichtet",
    };

    // The tooltip of the icon: "Matgate Agent: Läuft".
    public static string Tooltip(this AgentState state) => $"{Installer.ProductName}: {state.Word()}";
}
