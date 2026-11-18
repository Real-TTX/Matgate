using Matgate.Agent.Setup;

namespace Matgate.Agent;

// What the windows show of this machine: its name, the user at the screen, the home folder and the folders of the agent. For the
// pictures that setup-preview draws, the same sample machine everywhere (the computer ANNA-PC, the user Anna), so that no picture
// carries a real computer name, user name or path and every one of them may be published (MatDesign, docs/DESKTOP.md section 6).
internal static class Machine
{
    // Set by setup-preview, before any window is made.
    public static bool Sample { get; set; }

    public static string Name => Sample ? "ANNA-PC" : Environment.MachineName;

    public static string User => Sample ? @"ANNA-PC\Anna" : WindowsNative.InteractiveUser();

    public static string Home => Sample ? @"C:\Users\Anna" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ProgramFolder => Sample ? @"C:\Program Files\Matgate Agent" : Installer.ProgramFolder;

    public static string DataFolder => Sample ? @"C:\ProgramData\Matgate Agent" : Installer.DataFolder;
}
