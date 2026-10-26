using System.IO.Pipes;
using Avalonia.Threading;
using KanTuNext.Core;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// One running copy per user (per data folder): a second launch asks the first to come forward and exits.
/// Windows holds a named mutex. Elsewhere an exclusive FileStream on instance.lock takes a kernel flock, which is
/// released if the process dies; a named Mutex is not reliable there (on Linux every launch from a desktop menu
/// acquired it). The activation request travels over a named pipe (a Unix socket outside Windows).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "activate";

    private readonly Mutex? _mutex;
    private readonly FileStream? _lockFile;
    private readonly CancellationTokenSource _cts = new();
    private Action? _onActivate;

    private SingleInstance(Mutex? mutex, FileStream? lockFile)
    {
        _mutex = mutex;
        _lockFile = lockFile;
    }

    // Per data folder, so an isolated KANTUNEXT_DATA_DIR instance is its own; Unix pipes are sockets in a shared
    // temp directory, so the user name keeps different users apart
    private static string Id
    {
        get
        {
            var hash = 2166136261u;
            foreach (var c in AppPaths.DataDirectory) hash = (hash ^ c) * 16777619;
            return $"KanTuNext-{Environment.UserName}-{hash:x8}";
        }
    }

    /// <summary>The first instance gets the guard; null means another copy is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(true, $"Local\\{Id}", out var createdNew);
            if (createdNew) return new SingleInstance(mutex, null);
            mutex.Dispose();
            return null;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var lockFile = new FileStream(Path.Combine(AppPaths.DataDirectory, "instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(null, lockFile);
        }
        catch (IOException)
        {
            return null; // locked by the running copy
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstance(null, null); // can't lock at all: better to run than to refuse
        }
    }

    /// <summary>Asks the running copy to show its window; false if it didn't answer within two seconds.</summary>
    public static bool NotifyRunning()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
                client.Connect(150);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ActivateMessage);
                return true;
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    /// <summary>Starts listening for later launches; <paramref name="onActivate"/> runs on the UI thread.</summary>
    public void Listen(Action onActivate)
    {
        _onActivate = onActivate;
        _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) == ActivateMessage && _onActivate is { } handler)
                    Dispatcher.UIThread.Post(handler);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that hung up mid-message; wait for the next one
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        _lockFile?.Dispose();
    }
}
