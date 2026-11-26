namespace Matgate.Models;

// A device an app (ThixGate) was allowed on: "ThixGate on ANNA-PC" (Account -> Security -> Devices). The app signs
// in with a token of its own; Matgate keeps only the token's hash, so a copy of the data directory signs nobody in.
// Removing the device ends its access at once.
public sealed class AppDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    // The app that asked ("thixgate").
    public string App { get; set; } = "";

    // The name the device gave itself ("ANNA-PC").
    public string Name { get; set; } = "";

    // SHA-256 of the token, hex.
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    // The address it last came from, for the list (no tracking beyond the last one).
    public string LastAddress { get; set; } = "";
}
