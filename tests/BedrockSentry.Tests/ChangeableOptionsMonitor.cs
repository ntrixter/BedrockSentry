using Microsoft.Extensions.Options;

namespace BedrockSentry.Tests;

/// <summary>
/// An options monitor whose value can change, and which tells its listeners.
/// </summary>
/// <remarks>
/// A second double rather than an edit to <c>StaticOptionsMonitor</c>, which
/// returns null from <c>OnChange</c> and exposes no setter — several tests depend
/// on it being inert, and making it reactive would change what they assert
/// without changing what they say.
///
/// Lives in its own file for the reason <c>TestPaths</c> does: the alternative is
/// a private nested copy in each test class that needs one, and the fourth copy
/// is the one that quietly behaves differently from the other three.
/// </remarks>
internal sealed class ChangeableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; private set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(() => _listeners.Remove(listener));
    }

    /// <summary>Replaces the value and tells everyone, the way a reload does.</summary>
    public void Set(T next)
    {
        CurrentValue = next;

        // A copy, because a listener is free to unsubscribe while being told —
        // which is what a component disposing inside its own callback does.
        foreach (var listener in _listeners.ToArray())
        {
            listener(next, null);
        }
    }

    private sealed class Unsubscriber(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose()
        {
            Interlocked.Exchange(ref _undo, null)?.Invoke();
        }
    }
}
