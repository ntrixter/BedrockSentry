using Microsoft.Extensions.Hosting;

namespace BedrockSentry.Services.Instances;

/// <summary>
/// Walks every instance on <see cref="InstanceSizeSampler.MaxAge"/>'s cadence, so a
/// world's day of samples fills in whether or not its Status tab is open (<c>D48</c>).
/// </summary>
/// <remarks>
/// A walk started only by a page would leave the growth row with whatever hours somebody
/// happened to be looking, and "quiet now but busy this afternoon" is the question it is
/// there to answer. Each pass also forgets instances that have been removed, so a later
/// one given the same id does not inherit their history.
/// </remarks>
public sealed class InstanceSizeSchedule(
    InstanceRegistry registry, InstanceSizeSampler sampler, TimeProvider time) : BackgroundService
{
    /// <summary>Starts a walk of every instance, and forgets the ones that are gone.</summary>
    public void Pass()
    {
        var live = registry.All.ToDictionary(s => s.InstanceId, StringComparer.Ordinal);

        foreach (var supervisor in live.Values)
        {
            sampler.Refresh(supervisor.InstanceId, supervisor.WorkingDirectory);
        }

        foreach (var known in sampler.Known)
        {
            if (!live.ContainsKey(known))
            {
                sampler.Forget(known);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(InstanceSizeSampler.MaxAge, time);

        do
        {
            Pass();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
