using BedrockSentry.Core.Configuration;

namespace BedrockSentry.Services.Configuration;

/// <summary>
/// Writes a starter <c>bedrocksentry.json</c> on first run.
///
/// The main config file is optional, so before this existed a fresh install had
/// a config folder containing only <c>auth.json</c> — and the documentation for
/// every setting began "edit bedrocksentry.json". An administrator had to create
/// a file nobody had mentioned, in a shape they could not see, guessing at
/// capitalisation. A present-but-empty file is a far better starting point than
/// an absent one: it can be opened, it shows the nesting, and it is somewhere
/// obvious to type.
/// </summary>
public static class StarterConfig
{
    /// <summary>
    /// Plain JSON, deliberately without comments. The configuration system skips
    /// them happily, but <c>install.ps1</c> parses this file with
    /// <c>ConvertFrom-Json</c> under Windows PowerShell 5.1, which does not — so
    /// a commented starter file would break a reinstall that passes
    /// <c>-AllowRemoteSetup</c>. The pointer to the real documentation does the
    /// job the comments would have.
    /// </summary>
    private const string Template =
        """
        {
          "_readme": "Settings for BedrockSentry. See docs/windows-setup.md for every option. Add servers under Sentry.Instances.Servers, then restart the BedrockSentry service - the instance list is read once at startup. Credentials live in auth.json, not here.",
          "Sentry": {
            "HostDetails": {
              "DiscoverPublicAddress": true,
              "PublicAddressEndpoint": "https://api.ipify.org",
              "Domain": ""
            },
            "Instances": {
              "Servers": {
              }
            }
          }
        }

        """;

    /// <summary>
    /// Creates the file if it is missing. Never modifies an existing one — this
    /// runs on every startup, and a "helpful" merge into a file an administrator
    /// has edited is how config gets silently rewritten.
    /// </summary>
    /// <returns>True if a file was written.</returns>
    public static bool EnsureCreated(ISentryPaths paths)
    {
        var path = Path.Combine(paths.Config, "bedrocksentry.json");
        if (File.Exists(path))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(paths.Config);

            // CreateNew so two racing starts cannot have one truncate the other's
            // file; losing the race simply means someone else wrote it.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(Template);
            return true;
        }
        catch (IOException)
        {
            // Already created between the check and the open.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Not being able to write a convenience file is not a reason to fail
            // startup; everything it contains is optional.
            return false;
        }
    }
}
