namespace Matgate.Display.Vnc;

// What a VncClient tells whoever shows the desktop. Everything but CredentialsAsync is called on the client's
// read loop, between two messages of the server: it must return quickly and must not wait for the client.
public interface IVncHandler
{
    // A part of the framebuffer changed (it is already in VncClient.Framebuffer). Called once for every update
    // of the server, with the rectangle around everything that update touched - so a picture taken after
    // this call never shows half an update.
    void Updated(int x, int y, int width, int height);

    // The desktop has a new size; VncClient.Framebuffer was replaced.
    void Resized(int width, int height);

    // The pointer's shape as RGBA (R first, straight alpha, top-down); width == 0 means the server hides it.
    // The span is only valid inside the call.
    void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba);

    // Text was copied on the remote desktop. Lines end with a line feed, whatever the server sent.
    void ClipboardText(string text);

    // The server wants credentials the options did not carry, or refused the ones given (failed = true).
    // needUsername: this security type takes a user name as well. Return null to give up.
    Task<VncCredentials?> CredentialsAsync(bool needUsername, bool failed, CancellationToken cancellationToken);
}
