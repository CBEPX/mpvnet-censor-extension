namespace Censor.Core;

public readonly record struct OperationTicket(
    long MediaSessionId,
    long OperationRevision);

public sealed class MediaSessionCoordinator : IDisposable
{
    private readonly Lock _lock = new();
    private CancellationTokenSource _sessionCancellation = new();
    private bool _disposed;
    private long _mediaSessionId;
    private long _operationRevision;

    public CancellationToken SessionToken
    {
        get
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _sessionCancellation.Token;
            }
        }
    }

    public OperationTicket BeginMediaSession()
    {
        CancellationTokenSource previous;
        OperationTicket ticket;
        lock (_lock)
        {
            ThrowIfDisposed();
            _mediaSessionId++;
            _operationRevision++;
            previous = _sessionCancellation;
            _sessionCancellation = new();
            ticket = SnapshotUnsafe();
        }
        previous.Cancel();
        previous.Dispose();
        return ticket;
    }

    public OperationTicket BeginOperation()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _operationRevision++;
            return SnapshotUnsafe();
        }
    }

    public OperationTicket Snapshot()
    {
        lock (_lock)
            return SnapshotUnsafe();
    }

    public bool IsCurrent(OperationTicket ticket)
    {
        lock (_lock)
        {
            return !_disposed &&
                ticket.MediaSessionId == _mediaSessionId &&
                ticket.OperationRevision == _operationRevision;
        }
    }

    public bool IsCurrentMediaSession(OperationTicket ticket)
    {
        lock (_lock)
        {
            return !_disposed && ticket.MediaSessionId == _mediaSessionId;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            cancellation = _sessionCancellation;
        }
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private OperationTicket SnapshotUnsafe() =>
        new(_mediaSessionId, _operationRevision);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
