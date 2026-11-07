using System.Collections.Concurrent;
using System.Text;

namespace SetSett.Diagnostics;

/// <summary>
/// A thread-safe rolling file logger that never blocks or throws into its callers. A call formats one
/// line and drops it into a bounded queue; a single background thread does the disk work. When the
/// queue is full (a burst faster than the disk) lines are dropped and counted rather than stalling a
/// sensor callback or the UI thread. The active file rolls to one previous generation at
/// <see cref="MaxBytes"/>, so the on-disk total stays under about twice that.
/// </summary>
/// <remarks>
/// An instance, not a static, so tests can use a temp folder. The app uses it through <see cref="Log"/>.
/// </remarks>
public sealed class FileLog : IDisposable
{
    public const string CurrentName = "setsett.log";
    public const string PreviousName = "setsett.prev.log";

    private const int MaxQueuedLines = 8192;
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromMilliseconds(750);

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), MaxQueuedLines);
    private readonly object _gate = new();
    private readonly Func<string> _header;
    private readonly Thread _worker;
    private readonly long _maxBytes;

    private StreamWriter? _writer;
    private long _currentSize;
    private int _dropped;
    private long _accepted;
    private long _consumed;
    private bool _disposed;

    public FileLog(string folder, Func<string> header, long maxBytes = 1024 * 1024)
    {
        Folder = folder;
        _header = header;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(folder);
        CurrentPath = Path.Combine(folder, CurrentName);
        PreviousPath = Path.Combine(folder, PreviousName);

        _worker = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "SetSett.Log",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
        TryQueue(SafeHeader());
    }

    public string Folder { get; }

    public string CurrentPath { get; }

    public string PreviousPath { get; }

    /// <summary>Queues a finished line. Returns immediately.</summary>
    public void Write(string line)
    {
        if (_disposed)
        {
            return;
        }

        if (!TryQueue(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Waits until every line queued before this call is on disk, or the timeout passes.</summary>
    public void Flush(TimeSpan? timeout = null)
    {
        if (_disposed)
        {
            return;
        }

        long target = Interlocked.Read(ref _accepted);
        long deadline = Environment.TickCount64 + (long)(timeout ?? DefaultFlushTimeout).TotalMilliseconds;

        // Not holding _gate: the writer thread needs it to make progress.
        while (Interlocked.Read(ref _consumed) < target)
        {
            if (Environment.TickCount64 >= deadline || !_worker.IsAlive)
            {
                break;
            }

            Thread.Sleep(2);
        }

        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    /// <summary>The tail of the log, using the previous file too when the current one is short.</summary>
    public string ReadRecent(int maxChars = 60_000)
    {
        Flush();
        lock (_gate)
        {
            string current = ReadShared(CurrentPath);
            string previous = current.Length < maxChars / 2 ? ReadShared(PreviousPath) : string.Empty;
            string combined = previous + current;
            return combined.Length > maxChars ? combined[^maxChars..] : combined;
        }
    }

    /// <summary>Deletes both files and starts a fresh one.</summary>
    public void Clear()
    {
        Flush();
        lock (_gate)
        {
            CloseWriter();
            TryDelete(CurrentPath);
            TryDelete(PreviousPath);
            _currentSize = 0;
        }

        TryQueue(SafeHeader());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Flush(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _disposed = true;
            _queue.CompleteAdding();
            _worker.Join(TimeSpan.FromSeconds(2));
            lock (_gate)
            {
                CloseWriter();
            }
        }
    }

    private bool TryQueue(string line)
    {
        if (!_queue.TryAdd(line))
        {
            return false;
        }

        Interlocked.Increment(ref _accepted);
        return true;
    }

    private string SafeHeader()
    {
        try
        {
            return _header();
        }
        catch
        {
            return "===== SetSett session =====";
        }
    }

    private void WriterLoop()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                lock (_gate)
                {
                    try
                    {
                        EnsureWriter();
                        int dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0)
                        {
                            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN ] [Log] Dropped {dropped} line(s): queue full");
                        }

                        WriteLine(line);
                        if (_queue.Count == 0)
                        {
                            _writer?.Flush();
                        }
                    }
                    catch
                    {
                        // Drop this line; keep the thread alive.
                    }
                    finally
                    {
                        // Counts consumption, not success, so a failing disk can't hang a Flush.
                        Interlocked.Increment(ref _consumed);
                    }
                }
            }
        }
        catch
        {
            // The collection was disposed.
        }
    }

    private void WriteLine(string line)
    {
        if (_writer is null)
        {
            return;
        }

        long bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        if (_currentSize + bytes > _maxBytes)
        {
            Roll();
        }

        if (_writer is not null)
        {
            _writer.WriteLine(line);
            _currentSize += bytes;
        }
    }

    // Append, so a previous session's tail stays; FileShare.Read so the user can open the file.
    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _currentSize = stream.Length;
    }

    private void Roll()
    {
        try
        {
            CloseWriter();
            TryDelete(PreviousPath);
            File.Move(CurrentPath, PreviousPath);
        }
        catch
        {
            // Keep appending to the existing file.
        }

        try
        {
            EnsureWriter();
        }
        catch
        {
            _writer = null;
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            _writer = null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A file the OS won't release yet is cleared by a later attempt.
        }
    }

    // ReadWrite sharing is required: the writer holds the file open, and sharing is bidirectional.
    private static string ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }
}
