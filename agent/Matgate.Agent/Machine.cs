using Matgate.Agent.Setup;

namespace Matgate.Agent;

// What the windows show of this machine: its name, the user at the screen, the home folder and the folders of the agent. For the
// pictures that "snapshot" draws, the same sample machine everywhere (the computer ANNA-PC, the user Anna), so that no picture
// carries a real computer name, user name or path and every one of them may be published (MatDesign, docs/DESKTOP.md section 6).
internal static class Machine
{
    // The sample machine is of the system the pictures are drawn on: its paths look as that system's do.
    private static readonly bool Windows = OperatingSystem.IsWindows();

    // Set by snapshot, before any window is made.
    public static bool Sample { get; set; }

    public static string Name => Sample ? "ANNA-PC" : Environment.MachineName;

    public static string User => !Sample ? AgentInstall.InteractiveUser : Windows ? @"ANNA-PC\Anna" : "anna";

    public static string Home => !Sample ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Windows ? @"C:\Users\Anna" : "/home/anna";

    public static string ProgramFolder => !Sample || !Windows ? AgentInstall.ProgramFolder : @"C:\Program Files\Matgate Agent";

    public static string DataFolder => !Sample || !Windows ? AgentInstall.DataFolder : @"C:\ProgramData\Matgate Agent";
}
