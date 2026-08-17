namespace Matgate.Models;

// How a "Website" server is opened.
public enum WebsiteRenderMode
{
    // Native reverse-proxy / iframe (default). Fast, but some sites block framing / need a real browser.
    Native = 0,

    // Open the URL in a headful Chromium on the browser-farm sidecar, viewed over Guacamole VNC.
    ChromiumVnc = 1,

    // Same, but Firefox.
    FirefoxVnc = 2,
}
