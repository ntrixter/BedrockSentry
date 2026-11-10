using System.Security.AccessControl;

namespace BedrockSentry.Services.Security;

/// <summary>Where an access entry applies.</summary>
public enum AccessScope
{
    /// <summary>The object itself, inherited by nothing.</summary>
    ThisOnly,

    /// <summary>The folder and everything created beneath it.</summary>
    ThisAndBelow,

    /// <summary>Everything beneath the folder, not the folder itself.</summary>
    BelowOnly,
}

/// <summary>One allow entry of an access list <see cref="AccessPlan"/> describes.</summary>
/// <param name="Sid">The identity, as a SID string.</param>
/// <param name="Rights">What it may do.</param>
/// <param name="Scope">Where it applies.</param>
public sealed record AccessEntry(string Sid, FileSystemRights Rights, AccessScope Scope);

/// <summary>An access entry as it was read back from the file system.</summary>
public sealed record AccessRuleRead(string Sid, FileSystemRights Rights, bool Allow, bool Inherited, AccessScope Scope);

/// <summary>
/// The access lists the data root and the secret files carry, described without
/// touching the file system so the rules can be tested anywhere.
/// </summary>
/// <remarks>
/// <para>Every list is protected: <c>%ProgramData%</c> grants <c>BUILTIN\Users</c> read
/// by default and a child inherits it unless inheritance is broken.</para>
/// <para>The account the service runs as gets <see cref="FileSystemRights.Modify"/>,
/// never <see cref="FileSystemRights.FullControl"/>: FullControl carries
/// <c>WRITE_DAC</c> and <c>WRITE_OWNER</c>, and a service that can rewrite the data
/// root's list can hand it to anyone.</para>
/// <para>On the data root itself it gets less: it may read it and create in it, but
/// holds no <c>DELETE</c> there, so it cannot rename the root away and put a junction
/// to somewhere else in its place for the next elevated grant to follow.</para>
/// </remarks>
public static class AccessPlan
{
    // Literals rather than SecurityIdentifier, which throws off Windows: these are fixed
    // by Windows on every install and in every language.
    public const string LocalSystem = "S-1-5-18";

    public const string Administrators = "S-1-5-32-544";

    /// <summary>
    /// Read the root, list it, and create files and folders in it; no delete, no list
    /// change, and no attribute writes, which a reparse point on the root would need.
    /// </summary>
    public const FileSystemRights RootItself =
        FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;

    /// <summary>The data root: inherited by everything beneath it that does not break inheritance.</summary>
    /// <param name="serviceSid">The account the main service runs as.</param>
    public static IReadOnlyList<AccessEntry> ForDataRoot(string serviceSid)
    {
        List<AccessEntry> entries =
        [
            new(LocalSystem, FileSystemRights.FullControl, AccessScope.ThisAndBelow),
            new(Administrators, FileSystemRights.FullControl, AccessScope.ThisAndBelow),
        ];

        // When the account is LocalSystem itself: already named, and a second entry
        // for it would only be read as a mistake.
        if (serviceSid != LocalSystem)
        {
            entries.Add(new(serviceSid, RootItself, AccessScope.ThisOnly));
            entries.Add(new(serviceSid, FileSystemRights.Modify, AccessScope.BelowOnly));
        }

        return entries;
    }

    /// <summary>A secret file: the same identities, on the file alone.</summary>
    /// <param name="accountSid">The account the main service runs as.</param>
    public static IReadOnlyList<AccessEntry> ForSecretFile(string accountSid)
    {
        List<AccessEntry> entries =
        [
            new(LocalSystem, FileSystemRights.FullControl, AccessScope.ThisOnly),
            new(Administrators, FileSystemRights.FullControl, AccessScope.ThisOnly),
        ];

        if (accountSid != LocalSystem)
        {
            entries.Add(new(accountSid, FileSystemRights.Modify, AccessScope.ThisOnly));
        }

        return entries;
    }

    /// <summary>
    /// Whether a protected list read back from disk is exactly <paramref name="planned"/>,
    /// so writing it again would change nothing.
    /// </summary>
    /// <remarks>
    /// The service cannot rewrite the list of a file it does not own, since
    /// <see cref="FileSystemRights.Modify"/> carries no <c>WRITE_DAC</c>; a file the
    /// installer re-granted is one of those. Asking first means writing to such a
    /// file in place does not fail on a list that is already right.
    /// </remarks>
    public static bool Matches(bool isProtected, IReadOnlyCollection<AccessRuleRead> current, IReadOnlyList<AccessEntry> planned)
    {
        if (!isProtected || current.Count != planned.Count)
        {
            return false;
        }

        return planned.All(entry => current.Any(rule =>
            rule.Allow
            && !rule.Inherited
            && rule.Sid == entry.Sid
            && WithoutSynchronize(rule.Rights) == WithoutSynchronize(entry.Rights)
            && rule.Scope == entry.Scope));
    }

    /// <summary>The rights that change a folder's contents or its list.</summary>
    public const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>CREATOR OWNER: an entry for whoever creates a child, which is one of the writers named beside it.</summary>
    public const string CreatorOwner = "S-1-3-0";

    /// <summary>
    /// Why an inheritable list may not be written over what is already beneath a
    /// folder with <paramref name="current"/> as its list, or null when it may.
    /// </summary>
    /// <remarks>
    /// Writing a folder's inheritable list rewrites, by name and as an administrator,
    /// every entry beneath it that inherits. Anything a writer other than LocalSystem
    /// and Administrators could have put there, a hard link above all, would carry
    /// that write to a file elsewhere on the machine. So the list is written over a
    /// tree only those two could have written, and over an empty folder.
    /// </remarks>
    public static string? WhyNotSpreadOver(string? ownerSid, IReadOnlyCollection<AccessRuleRead> current)
    {
        if (ownerSid is not (LocalSystem or Administrators))
        {
            return $"it is owned by {ownerSid ?? "an owner that could not be read"}";
        }

        var writer = current.FirstOrDefault(rule =>
            rule.Allow
            && (rule.Rights & WriteRights) != 0
            && rule.Sid is not (LocalSystem or Administrators or CreatorOwner));

        return writer is null ? null : $"{writer.Sid} can write in it";
    }

    /// <summary>The inheritance flags that put an entry in <paramref name="scope"/>.</summary>
    public static (InheritanceFlags Inheritance, PropagationFlags Propagation) FlagsOf(AccessScope scope) => scope switch
    {
        AccessScope.ThisOnly => (InheritanceFlags.None, PropagationFlags.None),
        AccessScope.ThisAndBelow => (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None),
        AccessScope.BelowOnly => (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>The scope a read-back entry's flags put it in.</summary>
    public static AccessScope ScopeOf(InheritanceFlags inheritance, PropagationFlags propagation) =>
        inheritance == InheritanceFlags.None ? AccessScope.ThisOnly
        : (propagation & PropagationFlags.InheritOnly) != 0 ? AccessScope.BelowOnly
        : AccessScope.ThisAndBelow;

    // An allow rule written through .NET gains Synchronize, so FullControl reads back
    // as written and Modify does not.
    private static FileSystemRights WithoutSynchronize(FileSystemRights rights) =>
        rights & ~FileSystemRights.Synchronize;
}
