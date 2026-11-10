namespace BedrockSentry.Services.Broker;

/// <summary>
/// One broker read in flight per key: a caller that asks while the same read is running
/// waits on that read instead of starting another.
/// </summary>
/// <remarks>
/// <para>The broker serves <see cref="BrokerPipeServer.Instances"/> callers at once, and a
/// firewall or Defender read holds one for a whole PowerShell run. The Status page, each
/// server's Status tab and every tab's orphan check asked at once, so a few open pages
/// held every instance and the next caller's connect timed out (<c>T412</c>).</para>
/// <para>Nothing is kept once a read ends, so no answer is older than the read it came
/// from. A change through the broker calls <see cref="Changed"/>: a read started before
/// it is not joined after it, or a recheck after Apply fix could take the answer from
/// before the fix.</para>
/// <para>The shared read runs on <see cref="CancellationToken.None"/> and each caller
/// stops waiting on its own token, so one page navigated away does not cancel the read
/// another page is waiting on.</para>
/// </remarks>
public sealed class SharedRead<TKey, TValue>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, (Task<TValue> Read, long Change, object Token)> _running = [];
    private long _change;

    public Task<TValue> ReadAsync(TKey key, Func<Task<TValue>> read, CancellationToken cancellationToken = default)
    {
        Task<TValue> task;

        lock (_gate)
        {
            if (_running.TryGetValue(key, out var running)
                && !running.Read.IsCompleted
                && running.Change == _change)
            {
                task = running.Read;
            }
            else
            {
                var token = new object();
                task = Run(key, read, token);
                _running[key] = (task, _change, token);
            }
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Something the reads describe has changed; reads already running are not joined.</summary>
    public void Changed()
    {
        lock (_gate)
        {
            _change++;
        }
    }

    private async Task<TValue> Run(TKey key, Func<Task<TValue>> read, object token)
    {
        try
        {
            // On the pool rather than the caller's context: the entry is recorded before
            // the read can end, and no page's circuit carries a read others wait on.
            return await Task.Run(read).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_running.TryGetValue(key, out var running) && ReferenceEquals(running.Token, token))
                {
                    _running.Remove(key);
                }
            }
        }
    }
}
