namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// Raising an event at subscribers that are somebody else's code.
/// </summary>
/// <remarks>
/// Written once and shared because the rule has two parts and both are easy to
/// get wrong, and a second hand-written copy is how this codebase's recurring
/// defect starts. The services that raise these events are singletons living for
/// the months a Windows Service runs, and their subscribers are Blazor circuits
/// that come and go with a browser tab.
/// </remarks>
public static class EventRaising
{
    /// <summary>
    /// Invokes every handler, letting none of them stop the others or the caller.
    /// </summary>
    /// <param name="handlers">The event's delegate, or null when nobody is listening.</param>
    /// <param name="onFailure">
    /// Told about each handler that threw. Takes an exception rather than a logger
    /// so <c>Core</c> keeps its promise of no framework dependencies.
    /// </param>
    /// <remarks>
    /// <b>One at a time, not a single multicast invoke.</b> A multicast delegate
    /// stops at the first exception, so one circuit that had gone away would
    /// silently stop every other open page from being told — which presents as
    /// exactly the stale-panel defect these events exist to fix, and only for some
    /// of the pages.
    ///
    /// <b>And nothing escapes to the caller.</b> The methods that raise these
    /// events document that they never throw, and that promise has to keep
    /// covering them once somebody else's code runs inside them: a page navigating
    /// away at the wrong moment must not be able to fault a background service.
    /// </remarks>
    public static void RaiseIndependently(Action? handlers, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onFailure);

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
        }
    }

    /// <summary>
    /// The same, for an event that carries an argument.
    /// </summary>
    /// <remarks>
    /// An overload rather than a second hand-written loop beside the next event
    /// that needs one. The rule has two halves and both are easy to drop; a copy
    /// of it living next to each event is how this codebase's recurring defect
    /// starts, and the first candidate for that copy turned up within two days of
    /// the first overload being written.
    /// </remarks>
    public static void RaiseIndependently<T>(Action<T>? handlers, T argument, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onFailure);

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(argument);
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
        }
    }
}
