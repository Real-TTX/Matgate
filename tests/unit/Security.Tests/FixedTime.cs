namespace Matgate.Display.Rdp.Security.Tests;

// A clock that stands still, for the one place where the client reads the time.
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
