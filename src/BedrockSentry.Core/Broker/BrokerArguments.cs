using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace BedrockSentry.Core.Broker;

/// <summary>
/// Which fields each <see cref="BrokerOperation"/> takes, and the shape each must
/// have. The first check a request meets, before the broker resolves anything.
/// </summary>
/// <remarks>
/// <para>Shape only. An instance id that matches <see cref="InstanceIdShape"/> still
/// has to name a configured instance, and the folder it resolves to still has to pass
/// <see cref="BrokerPathRules"/>; this class refuses what no lookup should ever see,
/// such as a path where an id belongs.</para>
/// <para>Every field the operation does not take is refused when present, so the only
/// way a value reaches the broker is through a rule written here.</para>
/// </remarks>
public static partial class BrokerArguments
{
    /// <summary>
    /// The two Minecraft package families a loopback exemption may name. Fixed rather
    /// than read from the installed packages: the exemption is for a client on this
    /// machine to reach a server on it, and any other family is a request to open
    /// loopback for an app this product has no business with.
    /// </summary>
    public static readonly ImmutableArray<string> LoopbackFamilies =
    [
        "Microsoft.MinecraftUWP_8wekyb3d8bbwe",
        "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe",
    ];

    [Flags]
    private enum Field
    {
        None = 0,
        InstanceId = 1,
        GrantTarget = 2,
        PackageFamily = 4,
        ShadowId = 8,
        ProcessId = 16,
        BuildIdentity = 32,
        Ports = 64,
        Protocol = 128,
    }

    /// <summary>The fields each operation takes; all of them are required.</summary>
    private static Field Takes(BrokerOperation operation) => operation switch
    {
        BrokerOperation.AllowServerPorts => Field.InstanceId | Field.Ports | Field.Protocol,
        BrokerOperation.ExcludeServerFolder => Field.InstanceId,
        BrokerOperation.RemoveServerRules => Field.InstanceId,
        BrokerOperation.RenameServerRules => Field.InstanceId,
        BrokerOperation.UpdateServerRules => Field.InstanceId,
        BrokerOperation.ReadDefenderExclusions => Field.None,
        BrokerOperation.ExemptLoopback => Field.PackageFamily,
        BrokerOperation.EnableVss => Field.None,
        BrokerOperation.Status => Field.None,
        BrokerOperation.ReadFirewallRules => Field.None,
        BrokerOperation.ReadLoopbackExemptions => Field.None,
        BrokerOperation.ReadDeduplication => Field.None,
        BrokerOperation.CreateShadowCopy => Field.InstanceId,
        BrokerOperation.ReleaseShadowCopy => Field.ShadowId,
        BrokerOperation.FindOrphans => Field.InstanceId,
        BrokerOperation.TerminateOrphan => Field.InstanceId | Field.ProcessId,
        BrokerOperation.StageUpdate => Field.BuildIdentity,
        BrokerOperation.ApplyUpdate => Field.BuildIdentity,

        // GrantFolder takes an instance id only for the working directory; the
        // storage roots are named by the target alone.
        BrokerOperation.GrantFolder => Field.GrantTarget,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a broker operation."),
    };

    /// <summary>
    /// Why <paramref name="request"/> is refused before anything is resolved, or null
    /// when its fields have the shape its operation takes.
    /// </summary>
    public static string? Refuse(BrokerRequest? request)
    {
        if (request is null)
        {
            return "The request is empty.";
        }

        if (!Enum.IsDefined(request.Operation))
        {
            return $"{(int)request.Operation} is not a broker operation.";
        }

        var takes = Takes(request.Operation);

        if (request.Operation == BrokerOperation.GrantFolder
            && request.GrantTarget == BrokerGrantTarget.InstanceWorkingDirectory)
        {
            takes |= Field.InstanceId;
        }

        var present = Present(request);

        if ((present & ~takes) is var extra and not Field.None)
        {
            return $"{request.Operation} does not take {Describe(extra)}.";
        }

        if ((takes & ~present) is var missing and not Field.None)
        {
            return $"{request.Operation} needs {Describe(missing)}.";
        }

        return FieldShape(request);
    }

    private static Field Present(BrokerRequest request)
    {
        var present = Field.None;

        if (request.InstanceId is not null) present |= Field.InstanceId;
        if (request.GrantTarget is not null) present |= Field.GrantTarget;
        if (request.PackageFamily is not null) present |= Field.PackageFamily;
        if (request.ShadowId is not null) present |= Field.ShadowId;
        if (request.ProcessId is not null) present |= Field.ProcessId;
        if (request.BuildIdentity is not null) present |= Field.BuildIdentity;
        if (request.FirstPort is not null || request.LastPort is not null) present |= Field.Ports;
        if (request.Protocol is not null) present |= Field.Protocol;

        return present;
    }

    private static string? FieldShape(BrokerRequest request)
    {
        if (request.InstanceId is { } id && !InstanceIdShape().IsMatch(id))
        {
            return "The instance id is not one the wrapper makes: lower-case letters, digits and single hyphens.";
        }

        if (request.GrantTarget is { } target && !Enum.IsDefined(target))
        {
            return $"{(int)target} is not a folder the broker grants.";
        }

        if (request.PackageFamily is { } family && !LoopbackFamilies.Contains(family, StringComparer.Ordinal))
        {
            return $"'{family}' is not a Minecraft package; only Minecraft and Minecraft Preview are exempted.";
        }

        if (request.ShadowId is { } shadow && !Guid.TryParseExact(shadow.Trim('{', '}'), "D", out _))
        {
            return "The shadow copy id is not a GUID.";
        }

        if (request.ProcessId is { } pid && pid <= 0)
        {
            return $"{pid} is not a process id.";
        }

        if ((request.FirstPort is null) != (request.LastPort is null))
        {
            return "A port span needs both its first and its last port.";
        }

        if (request.FirstPort is { } first && request.LastPort is { } last)
        {
            if (first > last)
            {
                return $"The span {first}-{last} runs backwards.";
            }

            if (BrokerPortRules.RefuseSpan(first, last) is { } port)
            {
                return port;
            }
        }

        if (request.Protocol is { } protocol && !Enum.IsDefined(protocol))
        {
            return $"{(int)protocol} is not a protocol.";
        }

        if (request.BuildIdentity is { } identity && !BuildIdentityShape().IsMatch(identity))
        {
            return $"'{identity}' is not a build identity.";
        }

        return null;
    }

    private static string Describe(Field fields) =>
        string.Join(", ", Enum.GetValues<Field>()
            .Where(f => f != Field.None && fields.HasFlag(f))
            .Select(f => f switch
            {
                Field.InstanceId => "an instance id",
                Field.GrantTarget => "a grant target",
                Field.PackageFamily => "a package family",
                Field.ShadowId => "a shadow copy id",
                Field.ProcessId => "a process id",
                Field.BuildIdentity => "a build identity",
                Field.Ports => "a port span",
                Field.Protocol => "a protocol",
                _ => f.ToString(),
            }));

    /// <summary>What <see cref="Instances.InstanceId.FromName"/> and <c>MakeUnique</c> produce.</summary>
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InstanceIdShape();

    /// <summary>
    /// <see cref="BuildInfo.Identity"/>'s shape: a version, an optional pre-release and
    /// an optional commit. It becomes a folder name under <c>versions\</c>, so no
    /// separator or <c>..</c> segment can get through it.
    /// </summary>
    [GeneratedRegex(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?(\+[0-9a-f]{7,40})?\z",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BuildIdentityShape();
}
