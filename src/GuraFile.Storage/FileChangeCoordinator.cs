using System.Threading.Channels;

namespace GuraFile.Storage;

public sealed class FileChangeCoordinator : IAsyncDisposable
{
    private readonly Func<long, IReadOnlyCollection<string>, CancellationToken, Task> _reconcile;
    private readonly Func<string, bool> _ignorePath;
    private readonly Action<Exception>? _onError;
    private readonly TimeSpan _debounce;
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly HashSet<long> _disabledRoots = [];
    private readonly Task _processor;
    private int _disposed;

    public FileChangeCoordinator(
        ManagedRootScanner scanner,
        Action<ScanResult>? onChanged = null,
        Action<Exception>? onError = null,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _reconcile = async (rootId, paths, cancellationToken) =>
        {
            var result = await scanner.ReconcilePathsAsync(rootId, paths, cancellationToken: cancellationToken);
            if (!result.Canceled)
            {
                onChanged?.Invoke(result);
            }
        };
        _ignorePath = scanner.IsDatabasePath;
        _onError = onError;
        _debounce = ValidateDebounce(debounce ?? TimeSpan.FromMilliseconds(200));
        _processor = ProcessAsync();
    }

    internal FileChangeCoordinator(
        Func<long, IReadOnlyCollection<string>, CancellationToken, Task> reconcile,
        TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(reconcile);
        _reconcile = reconcile;
        _ignorePath = _ => false;
        _debounce = ValidateDebounce(debounce);
        _processor = ProcessAsync();
    }

    public bool Watch(ManagedRoot root)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_watchers)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_watchers.ContainsKey(root.Id))
            {
                _disabledRoots.Remove(root.Id);
                return true;
            }

            if (!Directory.Exists(root.Path))
            {
                return false;
            }

            var watcher = new FileSystemWatcher(root.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime
            };
            watcher.Created += (_, eventArgs) => Notify(root.Id, eventArgs.FullPath);
            watcher.Changed += (_, eventArgs) => Notify(root.Id, eventArgs.FullPath);
            watcher.Deleted += (_, eventArgs) => Notify(root.Id, eventArgs.FullPath);
            watcher.Renamed += (_, eventArgs) =>
            {
                Notify(root.Id, eventArgs.OldFullPath);
                Notify(root.Id, eventArgs.FullPath);
            };
            watcher.Error += (_, eventArgs) => ReportError(eventArgs.GetException());
            try
            {
                watcher.EnableRaisingEvents = true;
                _watchers.Add(root.Id, watcher);
                _disabledRoots.Remove(root.Id);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                watcher.Dispose();
                ReportError(exception);
                return false;
            }
        }
    }

    public bool Unwatch(long rootId)
    {
        lock (_watchers)
        {
            _disabledRoots.Add(rootId);
            if (!_watchers.Remove(rootId, out var watcher))
            {
                return false;
            }

            watcher.Dispose();
            return true;
        }
    }

    internal bool Notify(long rootId, string path)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return !_ignorePath(fullPath) && _changes.Writer.TryWrite(new(rootId, fullPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_watchers)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _changes.Writer.TryComplete();
        await _cancellation.CancelAsync();
        try
        {
            await _processor;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        var reader = _changes.Reader;
        var cancellationToken = _cancellation.Token;
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            var batch = new Dictionary<long, Dictionary<string, string>>();
            Drain(batch);
            await Task.Delay(_debounce, cancellationToken);
            Drain(batch);
            foreach (var (rootId, paths) in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_watchers)
                {
                    if (_disabledRoots.Contains(rootId))
                    {
                        continue;
                    }
                }

                try
                {
                    await _reconcile(rootId, paths.Values.ToArray(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportError(exception);
                }
            }
        }

        void Drain(Dictionary<long, Dictionary<string, string>> batch)
        {
            while (reader.TryRead(out var change))
            {
                if (!batch.TryGetValue(change.RootId, out var paths))
                {
                    paths = new(StringComparer.OrdinalIgnoreCase);
                    batch.Add(change.RootId, paths);
                }

                paths[change.Path] = change.Path;
            }
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _onError?.Invoke(exception);
        }
        catch
        {
        }
    }

    private static TimeSpan ValidateDebounce(TimeSpan debounce)
    {
        if (debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        return debounce;
    }

    private sealed record Change(long RootId, string Path);
}
