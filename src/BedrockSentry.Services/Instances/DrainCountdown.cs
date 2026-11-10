namespace BedrockSentry.Services.Instances;

/// <summary>
/// A drain's countdown that its caller can call off until the stops begin (<c>D100</c>).
/// </summary>
/// <remarks>
/// The drain's own cancellation token cannot do this: it means "stop waiting", and a
/// cancelled countdown goes straight to the stop. Calling off and beginning the stops are
/// decided under one lock, so a call that arrives as the countdown ends is either in time,
/// and nothing stops, or too late, and says so.
/// </remarks>
public sealed class DrainCountdown
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _calledOff = new();
    private bool _callOffRequested;
    private bool _stopping;

    /// <summary>Cancelled once the countdown is called off.</summary>
    internal CancellationToken Token => _calledOff.Token;

    /// <summary>Calls the countdown off; false once the stops have begun.</summary>
    public bool TryCallOff()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return false;
            }

            _callOffRequested = true;
        }

        // Outside the lock: cancelling runs the countdown's continuation, which takes it.
        _calledOff.Cancel();
        return true;
    }

    /// <summary>Whether the stops may begin; false once the countdown has been called off.</summary>
    internal bool TryBeginStopping()
    {
        lock (_gate)
        {
            if (_callOffRequested)
            {
                return false;
            }

            _stopping = true;
            return true;
        }
    }
}

/// <summary>
/// The countdowns <c>install.ps1</c> has asked for and not yet seen end, so its Ctrl+C can
/// call them off (<c>D100</c>).
/// </summary>
public sealed class DrainCountdowns
{
    private readonly object _gate = new();
    private readonly List<DrainCountdown> _running = [];

    /// <summary>Starts tracking a countdown for one drain.</summary>
    public DrainCountdown Begin()
    {
        var countdown = new DrainCountdown();

        lock (_gate)
        {
            _running.Add(countdown);
        }

        return countdown;
    }

    /// <summary>Stops tracking a countdown whose drain has returned.</summary>
    public void End(DrainCountdown countdown)
    {
        lock (_gate)
        {
            _running.Remove(countdown);
        }
    }

    /// <summary>
    /// Calls off every countdown being tracked, and counts those in time and those whose
    /// stops had already begun.
    /// </summary>
    public (int CalledOff, int TooLate) CallOffAll()
    {
        List<DrainCountdown> running;

        lock (_gate)
        {
            running = [.. _running];
        }

        var calledOff = running.Count(c => c.TryCallOff());
        return (calledOff, running.Count - calledOff);
    }
}
