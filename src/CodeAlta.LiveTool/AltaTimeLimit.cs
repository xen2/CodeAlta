namespace CodeAlta.LiveTool;

/// <summary>
/// The time a call of the <c>alta</c> tool may take. It is the time of the work of the command, not of the user: a command
/// that waits for the user to answer a card pauses it (<see cref="AltaCommandContext.PauseTimeLimit"/>) for that wait.
/// </summary>
public sealed class AltaTimeLimit : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellation;
    private TimeSpan _left;
    private long _runningSince;
    private int _pauses;
    private bool _disposed;

    /// <summary>Starts the time limit of a call.</summary>
    /// <param name="limit">The time the call may take, waits for the user aside.</param>
    /// <param name="cancellationToken">Cancels the call at once, whatever the time left: the run that made it stopped.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative.</exception>
    public AltaTimeLimit(TimeSpan limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, TimeSpan.Zero);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _left = limit;
        _runningSince = Environment.TickCount64;
        _cancellation.CancelAfter(limit);
    }

    /// <summary>Gets the token canceled once the time is up or the call is canceled.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Stops the time until the returned object is disposed; pauses nest, and the time runs again after the last.</summary>
    /// <returns>What resumes the time when disposed.</returns>
    public IDisposable Pause()
    {
        lock (_gate)
        {
            if (!_disposed && _pauses++ == 0)
            {
                _left -= TimeSpan.FromMilliseconds(Environment.TickCount64 - _runningSince);
                _cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        return new Resume(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _cancellation.Dispose();
    }

    private void Continue()
    {
        lock (_gate)
        {
            if (_disposed || --_pauses != 0) return;
            _runningSince = Environment.TickCount64;
            _cancellation.CancelAfter(_left > TimeSpan.Zero ? _left : TimeSpan.Zero);
        }
    }

    private sealed class Resume(AltaTimeLimit limit) : IDisposable
    {
        private AltaTimeLimit? _limit = limit;

        public void Dispose() => Interlocked.Exchange(ref _limit, null)?.Continue();
    }
}
