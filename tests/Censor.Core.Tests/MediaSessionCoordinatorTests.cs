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
        Assert.False(gate.SessionToken.IsCancellationRequested);
        Assert.False(gate.IsCurrentMediaSession(media));
        Assert.True(gate.IsCurrent(nextMedia));
    }
}
