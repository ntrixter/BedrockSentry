namespace BedrockSentry.Services.Security;

/// <summary>
/// Refuses sign-in attempts from an address after too many wrong ones (<c>D125</c>).
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> A six-digit code is a million guesses; without
/// a limit it is a minute's work for a script, and the password step had no limit
/// either. The same rule now covers both steps.</para>
///
/// <para><strong>Per address, not per account.</strong> There is one account, so a
/// lock on the account would let anybody lock its administrator out by typing wrong
/// passwords. The second step adds an address-free key as well, because reaching it
/// means the password is already known, and then the cost of locking the
/// administrator out for a few minutes is the right trade against an unlimited
/// spread of addresses guessing codes.</para>
///
/// <para><strong>Bounded.</strong> A key whose failures have all aged out is dropped,
/// so one wrong password from each of many addresses does not stay in memory for the
/// life of the service. At most <see cref="Capacity"/> keys are counted apart; once
/// that many hold live failures, a key not already counted shares
/// <see cref="OverflowKey"/> until room frees. No counted key loses its failures early,
/// so churning addresses cannot unlock one; the price, paid only while that many
/// addresses are failing at once, is that a new address may be refused for failures
/// it did not make. <see cref="SecondStepKey"/> is always counted apart.</para>
///
/// <para>One lock covers the table and every queue in it, so pruning, removal and a
/// new failure cannot interleave. Sign-ins are rare enough that the contention does
/// not matter.</para>
///
/// <para>In memory: a restart clears it, which an attacker cannot cause from the sign-in
/// page.</para>
/// </remarks>
public sealed class SignInThrottle
{
    /// <summary>Wrong attempts inside <see cref="Window"/> that a key may make.</summary>
    public const int AllowedFailures = 5;

    /// <summary>How long a wrong attempt counts against its key.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The address-free key the second step counts against too.</summary>
    public const string SecondStepKey = "second-step";

    /// <summary>The key every uncounted key shares while the table is full.</summary>
    public const string OverflowKey = "overflow";

    /// <summary>How many keys are counted apart before new ones share <see cref="OverflowKey"/>.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep;

    public SignInThrottle(TimeProvider time)
        : this(time, DefaultCapacity)
    {
    }

    internal SignInThrottle(TimeProvider time, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        _capacity = capacity;
        _lastSweep = time.GetUtcNow();
    }

    /// <summary>The most keys counted apart, not counting the two fixed keys.</summary>
    public int Capacity => _capacity;

    /// <summary>Keys currently held, the fixed ones included.</summary>
    internal int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count;
            }
        }
    }

    /// <summary>The password step's key for a remote address; one key for an unknown one.</summary>
    public static string PasswordKey(string? remoteAddress) => "password:" + (remoteAddress ?? "unknown");

    /// <summary>
    /// The second step's key for a remote address. Apart from <see cref="PasswordKey"/>,
    /// so a right password does not wipe the wrong codes counted against the address.
    /// </summary>
    public static string CodeKey(string? remoteAddress) => "code:" + (remoteAddress ?? "unknown");

    /// <summary>
    /// When every one of <paramref name="keys"/> may try again, or null when all may
    /// try now.
    /// </summary>
    public DateTimeOffset? RefusedUntil(params string[] keys)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? latest = null;

        lock (_gate)
        {
            SweepIfDue(now);

            foreach (var key in keys)
            {
                var queue = QueueFor(key, now);

                if (queue is null)
                {
                    continue;
                }

                if (queue.Count >= AllowedFailures)
                {
                    // Free again when the oldest counted failure ages out.
                    var until = queue.Peek() + Window;
                    latest = latest is null || until > latest ? until : latest;
                }
            }
        }

        return latest;
    }

    /// <summary>Counts a wrong attempt against every one of <paramref name="keys"/>.</summary>
    public void RecordFailure(params string[] keys)
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            SweepIfDue(now);

            foreach (var key in keys)
            {
                if (!_failures.TryGetValue(key, out var queue))
                {
                    var target = HasRoomFor(key, now) ? key : OverflowKey;

                    if (!_failures.TryGetValue(target, out queue))
                    {
                        queue = new Queue<DateTimeOffset>();
                        _failures[target] = queue;
                    }
                }

                Prune(queue, now);
                queue.Enqueue(now);
            }
        }
    }

    /// <summary>Forgets the failures of every one of <paramref name="keys"/>.</summary>
    /// <remarks>Never <see cref="OverflowKey"/>: one right password does not clear what other addresses did.</remarks>
    public void RecordSuccess(params string[] keys)
    {
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (key != OverflowKey)
                {
                    _failures.Remove(key);
                }
            }
        }
    }

    /// <summary>The sentence a refused attempt shows.</summary>
    public static string RefusalMessage(DateTimeOffset until, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));

        return $"Too many wrong attempts. Try again in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }

    // The queue a key is counted in: its own, or the overflow one while the table is
    // full and the key is not in it. Null when neither holds anything.
    private Queue<DateTimeOffset>? QueueFor(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var queue))
        {
            if (IsFixed(key) || CountedKeys() < _capacity || !_failures.TryGetValue(OverflowKey, out queue))
            {
                return null;
            }
        }

        Prune(queue, now);
        return queue;
    }

    private bool HasRoomFor(string key, DateTimeOffset now)
    {
        if (IsFixed(key) || CountedKeys() < _capacity)
        {
            return true;
        }

        Sweep(now);
        return CountedKeys() < _capacity;
    }

    private int CountedKeys() =>
        _failures.Count
        - (_failures.ContainsKey(SecondStepKey) ? 1 : 0)
        - (_failures.ContainsKey(OverflowKey) ? 1 : 0);

    private static bool IsFixed(string key) => key is SecondStepKey or OverflowKey;

    private void SweepIfDue(DateTimeOffset now)
    {
        if (now - _lastSweep >= Window)
        {
            Sweep(now);
        }
    }

    // Drops every key whose failures have all aged out.
    private void Sweep(DateTimeOffset now)
    {
        _lastSweep = now;

        List<string>? empty = null;

        foreach (var (key, queue) in _failures)
        {
            Prune(queue, now);

            if (queue.Count == 0)
            {
                (empty ??= []).Add(key);
            }
        }

        if (empty is not null)
        {
            foreach (var key in empty)
            {
                _failures.Remove(key);
            }
        }
    }

    private static void Prune(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && queue.Peek() + Window <= now)
        {
            queue.Dequeue();
        }
    }
}
