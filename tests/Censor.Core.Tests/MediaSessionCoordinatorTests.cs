using System.Collections.Concurrent;
using Censor.Core;

namespace Censor.Core.Tests;

public sealed class MediaSessionCoordinatorTests
{
    [Fact]
    public void InvalidatesPriorMediaAndOperationTickets()
    {
        using var gate = new MediaSessionCoordinator();
        var media = gate.BeginMediaSession();
        var firstSessionToken = gate.SessionToken;
        var load = gate.BeginOperation();

        Assert.Equal(media.MediaSessionId, load.MediaSessionId);
        Assert.NotEqual(media.OperationRevision, load.OperationRevision);
        Assert.True(gate.IsCurrent(load));
        Assert.False(gate.IsCurrent(media));
        Assert.True(gate.IsCurrentMediaSession(media));

        var nextOperation = gate.BeginOperation();
        Assert.Equal(load.MediaSessionId, nextOperation.MediaSessionId);
        Assert.NotEqual(load.OperationRevision, nextOperation.OperationRevision);
        Assert.False(gate.IsCurrent(load));

        var nextMedia = gate.BeginMediaSession();
        Assert.NotEqual(nextOperation.MediaSessionId, nextMedia.MediaSessionId);
        Assert.True(firstSessionToken.IsCancellationRequested);
        var canceledCallbackRan = false;
        using var registration = firstSessionToken.Register(() => canceledCallbackRan = true);
        Assert.True(canceledCallbackRan);
        Assert.False(gate.SessionToken.IsCancellationRequested);
        Assert.False(gate.IsCurrentMediaSession(media));
        Assert.True(gate.IsCurrent(nextMedia));
    }

    [Fact]
    public void ConcurrentMediaSessionsKeepIdsUniqueAndLatestTicketCurrent()
    {
        using var gate = new MediaSessionCoordinator();
        var tickets = new ConcurrentBag<OperationTicket>();

        Parallel.For(0, 64, _ => tickets.Add(gate.BeginMediaSession()));

        var ordered = tickets.OrderBy(ticket => ticket.MediaSessionId).ToArray();
        Assert.Equal(64, ordered.Select(ticket => ticket.MediaSessionId).Distinct().Count());
        Assert.Equal(64, ordered.Select(ticket => ticket.OperationRevision).Distinct().Count());
        Assert.True(gate.IsCurrent(ordered[^1]));
        Assert.All(ordered[..^1], ticket => Assert.False(gate.IsCurrent(ticket)));
    }

    [Fact]
    public void CapturedSessionTokenRemainsUsableAfterDispose()
    {
        var gate = new MediaSessionCoordinator();
        gate.BeginMediaSession();
        var token = gate.SessionToken;

        gate.Dispose();

        Assert.True(token.IsCancellationRequested);
        var callbackRan = false;
        using var registration = token.Register(() => callbackRan = true);
        Assert.True(callbackRan);
        Assert.Throws<ObjectDisposedException>(() => gate.BeginOperation());
    }

    [Fact]
    public void CancelCurrentSessionStopsWorkWithoutChangingItsIdentity()
    {
        using var gate = new MediaSessionCoordinator();
        var ticket = gate.BeginMediaSession();
        var token = gate.SessionToken;

        gate.CancelCurrentSession();

        Assert.True(token.IsCancellationRequested);
        Assert.True(gate.IsCurrentMediaSession(ticket));
    }
}
