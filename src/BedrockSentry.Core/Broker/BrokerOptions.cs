namespace BedrockSentry.Core.Broker;

/// <summary>Where the privileged work runs.</summary>
public enum BrokerMode
{
    /// <summary>
    /// In this process, which has only its own account's rights: an install with
    /// <c>-NoBroker</c>, or a run outside a service.
    /// </summary>
    InProcess = 0,

    /// <summary>In the broker service, asked over its pipe (milestone 18.5).</summary>
    Pipe = 1,
}

/// <summary>
/// Every name that identifies an install derives from the installed main service's
/// name (<c>docs/design.md</c> § 12.4): the broker service, both pipes, the virtual
/// account and its service SID, and the event log source.
/// </summary>
/// <remarks>
/// The application's own names do not: the default data root folder, the data
/// protection application name and the companion pack's author stay
/// <c>BedrockSentry</c>, since changing them would orphan keys and data rather than
/// tell two services apart.
/// </remarks>
public static class BrokerNames
{
    /// <summary>The main service's name when the installer was given none.</summary>
    public const string DefaultMainService = "BedrockSentry";

    /// <summary>The switch <c>install.ps1</c> writes into both services' ImagePath.</summary>
    public const string ServiceSwitch = "--service";

    /// <summary>
    /// The switch <c>install.ps1</c> writes into the main service's ImagePath when it
    /// registers the broker: <c>--broker pipe</c>.
    /// </summary>
    /// <remarks>
    /// On the command line rather than in the configuration, so the install writes it
    /// in the one place it writes the data root and the name, and only an administrator
    /// can change it. Nothing in it is a permission: a mode set wrongly only costs the
    /// service the work it cannot do itself.
    /// </remarks>
    public const string ModeSwitch = "--broker";

    /// <summary>The broker service's name.</summary>
    public static string Service(string mainServiceName) => mainServiceName + "Broker";

    /// <summary>The broker's pipe.</summary>
    public static string Pipe(string mainServiceName) => mainServiceName + ".broker";

    /// <summary>
    /// The main service's administrators-only pipe: the installer's drain and its
    /// call-off (<c>D62</c>, <c>D100</c>). Part of the frozen installer contract.
    /// </summary>
    public static string AdminPipe(string mainServiceName) => mainServiceName + ".admin";

    /// <summary>
    /// Whether <paramref name="name"/> can name the main service: ASCII letters, digits,
    /// <c>-</c> and <c>_</c>, up to 64 characters. Nothing that would change a pipe
    /// path, an account name or a registry key when the suffixes above are added.
    /// </summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The value of <see cref="ServiceSwitch"/>, or null when it is absent.</summary>
    /// <exception cref="ArgumentException">
    /// The switch is there with nothing after it, or with a name <see cref="IsValid"/>
    /// refuses. A service started under a name it cannot derive from must not fall back
    /// to the default and answer for another install.
    /// </exception>
    public static string? MainServiceArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals(ServiceSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = i + 1 < args.Count ? args[i + 1] : null;

            return IsValid(name)
                ? name
                : throw new ArgumentException($"{ServiceSwitch} names no usable service: '{name}'.", nameof(args));
        }

        return null;
    }

    /// <summary>
    /// <see cref="BrokerMode.Pipe"/> for <c>--broker pipe</c>, <see cref="BrokerMode.InProcess"/>
    /// when the switch is absent.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The switch is there with anything else after it. A service started with a mode
    /// it cannot read must not guess one.
    /// </exception>
    public static BrokerMode ModeArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals(ModeSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mode = i + 1 < args.Count ? args[i + 1] : null;

            return mode is not null && mode.Equals("pipe", StringComparison.OrdinalIgnoreCase)
                ? BrokerMode.Pipe
                : throw new ArgumentException($"{ModeSwitch} names no mode this build knows: '{mode}'.", nameof(args));
        }

        return BrokerMode.InProcess;
    }
}

/// <summary>The main service's installed name, for the services that derive a name from it.</summary>
public sealed record InstalledService(string Name)
{
    public static readonly InstalledService Default = new(BrokerNames.DefaultMainService);
}
