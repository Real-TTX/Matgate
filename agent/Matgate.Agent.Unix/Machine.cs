namespace Matgate.Agent;

// What the agent says of this machine. (The Windows program has the same class with its sample machine for
// the pictures of its windows; here there are no windows yet.)
internal static class Machine
{
    public static bool Sample => false;

    public static string Name => Environment.MachineName;
}
