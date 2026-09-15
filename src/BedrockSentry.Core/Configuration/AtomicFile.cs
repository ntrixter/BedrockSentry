namespace BedrockSentry.Core.Configuration;

/// <summary>
/// Writes a file so that a reader never sees a half-written one.
/// </summary>
/// <remarks>
/// Write to a temp beside the target, then rename over it. Same directory so
/// the move is a rename within one volume rather than a copy that can fail
/// halfway, and a crash leaves a droppable temp rather than a plausible-looking
/// file that ends mid-content.
///
/// The temp name carries a GUID, which is the part that was being got wrong.
/// Four writers in this codebase used a fixed <c>path + ".tmp"</c>, and a fixed
/// name is not a private workspace: two web requests editing one instance, or a
/// request racing the backup scheduler, both write the same temp. The second
/// open fails outright on Windows, and where it does not, one write silently
/// lands inside the other's file. The config writer already did it this way;
/// this is that pattern with one home instead of five.
///
/// This is not atomicity against power loss — <c>File.Move</c> is not fsync —
/// but it is atomicity against another reader, another writer, and a process
/// that dies mid-write, which are the failures that actually happen here.
/// </remarks>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/>.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = TempBeside(path);

        try
        {
            File.WriteAllText(temp, contents);
            Replace(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <inheritdoc cref="WriteAllText(string, string)"/>
    public static async Task WriteAllTextAsync(
        string path, string contents, CancellationToken cancellationToken = default)
    {
        var temp = TempBeside(path);

        try
        {
            await File.WriteAllTextAsync(temp, contents, cancellationToken).ConfigureAwait(false);
            Replace(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Writes through <paramref name="write"/>, which gets the temp path.</summary>
    /// <remarks>
    /// For content that is streamed rather than held as a string — an archive,
    /// or anything large enough that materialising it twice is the wrong shape.
    /// </remarks>
    public static async Task WriteAsync(
        string path, Func<string, Task> write, CancellationToken cancellationToken = default)
    {
        var temp = TempBeside(path);

        try
        {
            await write(temp).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Replace(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Renames the temp over the target, retrying the moment Windows says no.
    /// </summary>
    /// <remarks>
    /// <see cref="File.Move(string, string, bool)"/> with overwrite is
    /// <c>MoveFileEx</c> and <c>MOVEFILE_REPLACE_EXISTING</c>, which needs the
    /// destination free for an instant. Anything holding it — a second writer
    /// mid-replace, or simply a reader — makes the call throw. That is not
    /// hypothetical: the instance Players tab re-reads <c>allowlist.json</c>
    /// every second, so a save can lose to somebody merely looking at the page.
    /// Unix <c>rename()</c> has no such window, which is why this only ever
    /// fails on the platform the wrapper ships to, and why it took a
    /// concurrency test running on Windows CI to see it at all.
    ///
    /// Last write wins either way. The retry is what makes that true rather
    /// than throwing into whichever request happened to lose the race.
    /// </remarks>
    private static void Replace(string temp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex)
                when (attempt < 12 && ex is UnauthorizedAccessException or IOException)
            {
                // Short and rising: the window is a few milliseconds wide, so
                // the first retry usually takes it.
                Thread.Sleep(5 + (attempt * 5));
            }
        }
    }

    private static string TempBeside(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException($"Cannot determine a directory for '{path}'.");

        Directory.CreateDirectory(directory);

        // Leading dot so it sorts and hides out of the way, GUID so two writers
        // never share one.
        return Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp is untidy, not dangerous — the name says what it is.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
