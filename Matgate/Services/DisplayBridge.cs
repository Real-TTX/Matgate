using System.Collections.Concurrent;
using Matgate.Display;
using Matgate.Display.Video;

namespace Matgate.Services;

// Matgate's own engine for RDP and VNC. The protocol clients (Matgate/Display) run inside Matgate itself:
// each viewer gets a session that connects to the remote desktop and speaks the screen protocol of the
// Matgate agent - H.264 pictures, the pointer, sound, a clipboard with files - so the browser shows it with
// the same page.
public sealed class DisplayBridge
{
    private readonly ILoggerFactory _loggers;

    public DisplayBridge(ILoggerFactory loggers)
    {
        _loggers = loggers;
    }

    // Whether RDP and VNC can use Matgate's own engine here (otherwise Guacamole shows them). The one thing it
    // needs from outside is the H.264 encoder library, which the container image brings along.
    public static bool Available => H264Encoder.IsAvailable;

    // Why not, for the log.
    public static string? UnavailableReason => H264Encoder.UnavailableReason;

    // Open screens by session id - a download of a file copied on the remote desktop finds its session here.
    public ConcurrentDictionary<string, ScreenSession> Live { get; } = new();

    // The screens a viewer can come back to, by session id: while a browser shows them, and - for a user who
    // set it up that way - for a while after it went.
    public ConcurrentDictionary<string, HeldScreen> Held { get; } = new();

    public const int MaxHeldPerUser = 16;

    // A screen and whoever is looking at it right now. One viewer at a time: the next one takes it.
    public sealed class HeldScreen(ScreenSession screen, Guid userId, Guid serverId)
    {
        private readonly object _gate = new();
        private TaskCompletionSource _replaced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _expiry;

        public ScreenSession Screen => screen;

        public Guid UserId => userId;

        public Guid ServerId => serverId;

        // The viewer the session was opened by. What comes back completes when another viewer takes the screen.
        public Task First()
        {
            lock (_gate)
            {
                return _replaced.Task;
            }
        }

        // A viewer takes the screen: whoever had it is told, and a screen that was waiting for someone no
        // longer runs out.
        public Task Take(Action<Matgate.Agent.AgentFrame> viewer)
        {
            lock (_gate)
            {
                _expiry?.Cancel();
                _expiry = null;
                var before = _replaced;
                _replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                before.TrySetResult();
                screen.Attach(viewer);
                return _replaced.Task;
            }
        }

        // The viewer left. True when the screen stays: because another viewer has it already, or because it
        // is kept for the time given - then `expired` is called when nobody came, or when the remote desktop
        // ended meanwhile.
        public bool Leave(Task mine, TimeSpan keep, Action expired)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(mine, _replaced.Task))
                {
                    return true;
                }

                if (keep <= TimeSpan.Zero || screen.Completion.IsCompleted)
                {
                    return false;
                }

                screen.Detach();
                var expiry = new CancellationTokenSource();
                _expiry = expiry;
                _ = Task.WhenAny(Task.Delay(keep, expiry.Token), screen.Completion).ContinueWith(_ =>
                {
                    if (!expiry.IsCancellationRequested)
                    {
                        expired();
                    }
                }, TaskScheduler.Default);
                return true;
            }
        }
    }

    public ScreenSession Open(ScreenTarget target)
    {
        return new ScreenSession(
            target,
            static (target, host) => target.Protocol == "vnc" ? new VncBackend(target, host) : new RdpBackend(target, host),
            _loggers.CreateLogger("Matgate.Display"));
    }

    // Windows' keyboard layout ids (KLID) for the layouts Matgate's connection form offers.
    public static uint KeyboardLayoutId(string? layout) => (layout ?? "").Trim().ToLowerInvariant() switch
    {
        "de-de-qwertz" => 0x00000407,
        "de-ch-qwertz" => 0x00000807,
        "en-us-qwerty" => 0x00000409,
        "en-gb-qwerty" => 0x00000809,
        "fr-fr-azerty" => 0x0000040C,
        "fr-be-azerty" => 0x0000080C,
        "fr-ch-qwertz" => 0x0000100C,
        "it-it-qwerty" => 0x00000410,
        "es-es-qwerty" => 0x0000040A,
        "es-latam-qwerty" => 0x0000080A,
        "pt-br-qwerty" => 0x00000416,
        "sv-se-qwerty" => 0x0000041D,
        "da-dk-qwerty" => 0x00000406,
        "nb-no-qwerty" => 0x00000414,
        "hu-hu-qwertz" => 0x0000040E,
        "tr-tr-qwerty" => 0x0000041F,
        "ja-jp-qwerty" => 0x00000411,
        _ => 0x00000407,
    };
}
