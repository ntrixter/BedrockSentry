namespace BedrockSentry.Core.Diagnostics;

/// <summary>
/// A byte count as a person reads it — <c>12.3 GB</c>.
/// </summary>
/// <remarks>
/// Moved here from <c>HostPanel.razor</c> on 2026-09-04, unchanged, because
/// <see cref="VolumeDeduplication.Describe"/> needed it and a second copy in
/// <c>Core</c> would have been the <c>T126</c> shape all over again: two
/// formatters that agree today and drift the first time one is touched.
/// Binary units, one decimal, and the largest unit that keeps the number at or
/// above one.
/// </remarks>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long value)
    {
        double size = value;
        var unit = 0;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {Units[unit]}";
    }
}
