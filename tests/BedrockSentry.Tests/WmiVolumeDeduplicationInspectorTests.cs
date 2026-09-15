using BedrockSentry.Core.Diagnostics;
using BedrockSentry.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BedrockSentry.Tests;

/// <summary>
/// The WMI read behind the deduplication column (`D55`). Everything about what
/// the cell says is <see cref="VolumeDeduplicationTests"/>; these prove only
/// that the reader answers rather than throws, on both kinds of machine.
/// </summary>
public class WmiVolumeDeduplicationInspectorTests
{
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "WMI only exists on Windows.";
            }
        }
    }

    private static WmiVolumeDeduplicationInspector Inspector() =>
        new(NullLogger<WmiVolumeDeduplicationInspector>.Instance);

    // CI runs on windows-latest, a Server image without the role, so the
    // expected answer there is NotInstalled. NotEnabled or Enabled would mean
    // the role is present, which is fine. Unknown is the one that must not
    // happen on an administrator runner: it would mean the missing-namespace
    // case is surfacing as some other error and being mistaken for a denial.
    [WindowsFact]
    public void Answers_for_every_root_without_throwing()
    {
        var result = Inspector().Inspect([@"C:\"]);

        var state = Assert.Single(result).Value;
        Assert.True(
            state.State != DeduplicationState.Unknown,
            $"Deduplication read as Unknown on a build agent: {state.Detail}");
        Assert.False(string.IsNullOrWhiteSpace(state.Describe(DateTimeOffset.UtcNow)));
    }

    // Off Windows there is nothing to ask, and that is reported as a state
    // rather than thrown at the page.
    [Fact]
    public void Off_windows_it_says_so_rather_than_throwing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = Inspector().Inspect([@"C:\", @"D:\"]);

        Assert.All(result.Values, v => Assert.Equal(DeduplicationState.Unknown, v.State));
        Assert.Contains("Windows", result[@"C:\"].Detail);
    }
}
