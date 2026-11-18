// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray can take it too.
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace MatDesktop;

/// <summary>
/// One instance per user session (DESK-05): the first start takes a named mutex and listens on a named pipe; a second start writes its arguments to the pipe and ends, and the first one
/// shows its window (<see cref="SecondStart"/>). The names carry the product and the user (<c>Example.ShowWindow.anna</c>). .NET makes the same calls work on macOS and Linux: there a named
/// mutex is a lock file and a named pipe is a Unix domain socket in the temporary folder. Ported from the desktop program that had it first.
/// </summary>
public sealed class MatSingleInstance : IDisposable
{
    private readonly string _mutexName;
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;
    private Thread? _listener;

    /// <summary>The names for <paramref name="product"/> and the user who runs it; <paramref name="scope"/> (a data folder of a development run) keeps two copies apart.</summary>
    public MatSingleInstance(string product, string? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        var suffix = string.IsNullOrEmpty(scope) ? string.Empty : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToUpperInvariant())))[..16];
        var user = Safe(Environment.UserName);
        _mutexName = $"{Safe(product)}.SingleInstance{suffix}.{user}";
        PipeName = $"{Safe(product)}.ShowWindow{suffix}.{user}";
    }

    /// <summary>A second start: its arguments (raised on a thread of the listener; post to the UI thread to show the window).</summary>
    public event EventHandler<string[]>? SecondStart;

    /// <summary>The name of the pipe.</summary>
    public string PipeName { get; }

    /// <summary>True when this process is the first instance.</summary>
    public bool IsFirst { get; private set; }

    /// <summary>
    /// Takes the instance: true when this is the first start (it listens for second starts from now on); false when another instance runs, which then got <paramref name="args"/> and
    /// shows itself; this process should end.
    /// </summary>
    public bool TryClaim(string[] args, TimeSpan? timeout = null)
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var isFirst);
        IsFirst = isFirst;
        if (!isFirst)
        {
            _mutex.Dispose();
            _mutex = null;
            Knock(args, timeout ?? TimeSpan.FromSeconds(2));
            return false;
        }

        _listener = new Thread(Listen) { IsBackground = true, Name = "MatSingleInstance listener" };
        _listener.Start();
        return true;
    }

    /// <summary>Hands <paramref name="args"/> to the instance that runs; false when it could not be reached.</summary>
    public bool Knock(string[] args, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            var data = Encoding.UTF8.GetBytes(string.Join('\0', args ?? []));
            client.Write(BitConverter.GetBytes(data.Length));
            client.Write(data);
            client.Flush();
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // released by another thread than the one that took it: the handle goes with Dispose
            }

            _mutex.Dispose();
            _mutex = null;
        }

        // the source stays: the listener may still look at its token while it ends
    }

    private void Listen()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                var length = new byte[4];
                server.ReadExactly(length);
                var data = new byte[Math.Clamp(BitConverter.ToInt32(length), 0, 1 << 20)];
                server.ReadExactly(data);
                var text = Encoding.UTF8.GetString(data);
                SecondStart?.Invoke(this, text.Length == 0 ? [] : text.Split('\0'));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or EndOfStreamException)
            {
                if (token.WaitHandle.WaitOne(500))
                {
                    return;
                }
            }
        }
    }

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
