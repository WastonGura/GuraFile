using System.Threading.Channels;

namespace GuraFile.Storage;

public sealed class FileChangeCoordinator : IAsyncDisposable
{
    private readonly ManagedRootScanner? _scanner;
    private readonly Func<long, IReadOnlyCollection<string>, CancellationToken, Task> _reconcile;
    private readonly Func<string, bool> _ignorePath;
    private readonly Action<ScanResult>? _onChanged;
    private readonly Action<Exception>? _onError;
    private readonly Action? _onRootChanged;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _retryInterval;
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Recovery> _recoveries = Channel.CreateUnbounded<Recovery>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<long, ManagedRoot> _knownRoots = [];
    private readonly HashSet<long> _disabledRoots = [];
    private readonly HashSet<long> _pendingRecoveries = [];
    private readonly HashSet<long> _activeRecoveries = [];
    private readonly HashSet<long> _rerunRecoveries = [];
    private readonly Task _processor;
    private readonly Task _recoveryProcessor;
    private readonly Task _retryProcessor;
    private int _disposed;

    public FileChangeCoordinator(
        ManagedRootScanner scanner,
        Action<ScanResult>? onChanged = null,
        Action<Exception>? onError = null,
        TimeSpan? debounce = null,
        TimeSpan? retryInterval = null,
        Action? onRootChanged = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
        _onChanged = onChanged;
        _reconcile = async (rootId, paths, cancellationToken) =>
        {
            var result = await scanner.ReconcilePathsAsync(rootId, paths, cancellationToken: cancellationToken);
            if (!result.Canceled)
            {
                NotifyChanged(result);
            }
        };
        _ignorePath = scanner.IsDatabasePath;
        _onError = onError;
        _onRootChanged = onRootChanged;
        _debounce = ValidateDebounce(debounce ?? TimeSpan.FromMilliseconds(200));
        _retryInterval = ValidateInterval(retryInterval ?? TimeSpan.FromSeconds(2), nameof(retryInterval));
        _processor = ProcessAsync();
        _recoveryProcessor = ProcessRecoveriesAsync();
        _retryProcessor = RetryOfflineRootsAsync();
    }

    internal FileChangeCoordinator(
        Func<long, IReadOnlyCollection<string>, CancellationToken, Task> reconcile,
        TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(reconcile);
        _reconcile = reconcile;
        _ignorePath = _ => false;
        _debounce = ValidateDebounce(debounce);
        _retryInterval = TimeSpan.FromHours(1);
        _processor = ProcessAsync();
        _recoveryProcessor = ProcessRecoveriesAsync();
        _retryProcessor = RetryOfflineRootsAsync();
    }

    public void Start(ManagedRoot root)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RequestRecovery(root);
    }

    public bool Watch(ManagedRoot root)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_watchers)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _knownRoots[root.Id] = root;
            return WatchCore(root);
        }
    }

    private bool WatchCore(ManagedRoot root)
    {
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
        watcher.Error += (_, eventArgs) =>
        {
            var exception = eventArgs.GetException();
            if (RequestRecoveryFromWatcher(root, watcher, exception))
            {
                ReportError(exception);
            }
        };
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

    public bool Unwatch(long rootId)
    {
        lock (_watchers)
        {
            _knownRoots.Remove(rootId);
            _pendingRecoveries.Remove(rootId);
            _rerunRecoveries.Remove(rootId);
            return DisableWatcher(rootId);
        }
    }

    internal bool RequestRecovery(ManagedRoot root, Exception? error = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        lock (_watchers)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            _knownRoots[root.Id] = root;
            return QueueRecovery(root.Id, error);
        }
    }

    private bool RequestRecoveryFromWatcher(
        ManagedRoot root,
        FileSystemWatcher watcher,
        Exception error)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        lock (_watchers)
        {
            if (Volatile.Read(ref _disposed) != 0
                || !_knownRoots.ContainsKey(root.Id)
                || !_watchers.TryGetValue(root.Id, out var current)
                || !ReferenceEquals(current, watcher))
            {
                return false;
            }

            QueueRecovery(root.Id, error);
            return true;
        }
    }

    private bool RequestRecoveryForKnownRoot(long rootId)
    {
        lock (_watchers)
        {
            return Volatile.Read(ref _disposed) == 0
                && _knownRoots.ContainsKey(rootId)
                && QueueRecovery(rootId, null);
        }
    }

    private bool QueueRecovery(long rootId, Exception? error)
    {
        if (!_pendingRecoveries.Add(rootId))
        {
            if (_activeRecoveries.Contains(rootId))
            {
                _rerunRecoveries.Add(rootId);
            }

            return false;
        }

        if (_recoveries.Writer.TryWrite(new(rootId, error)))
        {
            return true;
        }

        _pendingRecoveries.Remove(rootId);
        return false;
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
        _recoveries.Writer.TryComplete();
        await _cancellation.CancelAsync();
        try
        {
            await Task.WhenAll(_processor, _recoveryProcessor, _retryProcessor);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ProcessRecoveriesAsync()
    {
        var cancellationToken = _cancellation.Token;
        await foreach (var recovery in _recoveries.Reader.ReadAllAsync(cancellationToken))
        {
            await Task.Delay(_debounce, cancellationToken);
            ManagedRoot? root;
            lock (_watchers)
            {
                _knownRoots.TryGetValue(recovery.RootId, out root);
                if (root is null)
                {
                    _pendingRecoveries.Remove(recovery.RootId);
                }
                else
                {
                    _activeRecoveries.Add(recovery.RootId);
                }
            }

            if (root is null)
            {
                continue;
            }

            try
            {
                _scanner!.SetRootStatus(root.Id, ManagedRootStatus.Recovering, recovery.Error?.Message);
                NotifyRootChanged();
                lock (_watchers)
                {
                    DisableWatcher(root.Id);
                    if (_knownRoots.ContainsKey(root.Id))
                    {
                        WatchCore(root);
                    }
                }

                var result = await _scanner.ScanAsync(root.Id, cancellationToken: cancellationToken);
                if (result.Canceled)
                {
                    continue;
                }

                var refreshed = _scanner.ListRoots().Single(item => item.Id == root.Id);
                var watchFailed = false;
                lock (_watchers)
                {
                    if (_knownRoots.ContainsKey(root.Id))
                    {
                        if (refreshed.Status == ManagedRootStatus.Offline)
                        {
                            DisableWatcher(root.Id);
                        }
                        else if (!_watchers.ContainsKey(root.Id) && !WatchCore(refreshed))
                        {
                            watchFailed = true;
                        }
                    }
                }

                if (watchFailed)
                {
                    _scanner.SetRootStatus(
                        root.Id,
                        ManagedRootStatus.Offline,
                        "Managed root watcher could not be started.");
                }

                NotifyChanged(result);
                NotifyRootChanged();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                try
                {
                    _scanner!.SetRootStatus(root.Id, ManagedRootStatus.Offline, exception.Message);
                    NotifyRootChanged();
                }
                catch
                {
                }

                ReportError(exception);
            }
            finally
            {
                var rerun = false;
                lock (_watchers)
                {
                    _activeRecoveries.Remove(recovery.RootId);
                    rerun = _knownRoots.ContainsKey(recovery.RootId) && _rerunRecoveries.Remove(recovery.RootId);
                    if (!rerun)
                    {
                        _pendingRecoveries.Remove(recovery.RootId);
                    }
                }

                if (rerun && !_recoveries.Writer.TryWrite(new(recovery.RootId, null)))
                {
                    lock (_watchers)
                    {
                        _pendingRecoveries.Remove(recovery.RootId);
                    }
                }
            }
        }
    }

    private async Task RetryOfflineRootsAsync()
    {
        using var timer = new PeriodicTimer(_retryInterval);
        var cancellationToken = _cancellation.Token;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            ManagedRoot[] roots;
            lock (_watchers)
            {
                roots = _knownRoots.Values
                    .Where(root => !_watchers.ContainsKey(root.Id) && !_pendingRecoveries.Contains(root.Id))
                    .ToArray();
            }

            foreach (var root in roots.Where(root => Directory.Exists(root.Path)))
            {
                RequestRecoveryForKnownRoot(root.Id);
            }
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

    private void NotifyChanged(ScanResult result)
    {
        try
        {
            _onChanged?.Invoke(result);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private void NotifyRootChanged()
    {
        try
        {
            _onRootChanged?.Invoke();
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private bool DisableWatcher(long rootId)
    {
        _disabledRoots.Add(rootId);
        if (!_watchers.Remove(rootId, out var watcher))
        {
            return false;
        }

        watcher.Dispose();
        return true;
    }

    private static TimeSpan ValidateDebounce(TimeSpan debounce)
    {
        if (debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        return debounce;
    }

    private static TimeSpan ValidateInterval(TimeSpan interval, string parameterName)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return interval;
    }

    private sealed record Change(long RootId, string Path);
    private sealed record Recovery(long RootId, Exception? Error);
}
