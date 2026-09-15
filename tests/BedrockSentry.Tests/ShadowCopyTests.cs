using BedrockSentry.Services.Backups;

namespace BedrockSentry.Tests;

/// <summary>
/// docs/design.md § 8.4. Mapping a live path into a shadow copy is the part that is easy
/// to get subtly wrong and impossible to notice from the outside: an answer that
/// is off by a separator produces "file not found" for every world file, which
/// reads exactly like a snapshot that captured nothing.
/// </summary>
public sealed class ShadowCopyPathTests
{
    /// <summary>A fact that reports itself skipped rather than passed off Windows.</summary>
    /// <remarks>
    /// Drive letters are not a concept on Linux — <c>Path.GetPathRoot(@"C:\x")</c>
    /// answers an empty string there rather than <c>"C:\"</c>. Asserting the
    /// Windows shapes anywhere else would be asserting the runtime's opinion of
    /// a foreign path format, so those cases say out loud that they did not run.
    /// </remarks>
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Drive-letter paths only mean anything on Windows.";
            }
        }
    }

    private const string Device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3";

    // Win32_ShadowCopy wants "C:\", not "C:", and answers a bare drive letter
    // with a generic failure rather than saying which of the two it wanted.
    [WindowsFact]
    public void A_volume_root_keeps_its_trailing_separator()
    {
        Assert.Equal(@"C:\", ShadowCopyPaths.VolumeRootOf(@"C:\servers\survival\worlds"));
        Assert.Equal(@"C:\", ShadowCopyPaths.VolumeRootOf(@"C:\"));
    }

    [WindowsFact]
    public void A_path_maps_under_the_device_object()
    {
        Assert.Equal(
            Device + @"\servers\survival\worlds",
            ShadowCopyPaths.Inside(Device, @"C:\servers\survival\worlds"));
    }

    // The device object stands in for the volume root, so the root is replaced
    // rather than appended to. Grafting "C:\..." on would produce a path with a
    // drive letter in the middle of it.
    [WindowsFact]
    public void The_drive_letter_does_not_survive_the_mapping()
    {
        var mapped = ShadowCopyPaths.Inside(Device, @"D:\worlds\Bedrock level");

        Assert.DoesNotContain("D:", mapped[Device.Length..], StringComparison.Ordinal);
        Assert.Equal(Device + @"\worlds\Bedrock level", mapped);
    }

    [WindowsFact]
    public void A_network_share_is_refused_rather_than_mapped_to_nothing()
    {
        Assert.Throws<InvalidOperationException>(
            () => ShadowCopyPaths.Inside(Device, @"\\nas\minecraft\worlds"));
    }

    /// <summary>
    /// The property that has to hold on any platform: whatever the volume root
    /// is, the mapping replaces it with the device and keeps everything after.
    /// </summary>
    /// <remarks>
    /// Written against a real local path rather than a literal, so it exercises
    /// the same arithmetic on the build machine as it does on the target — the
    /// Windows-shaped cases above cannot run here and this one can.
    /// </remarks>
    [Fact]
    public void The_mapping_replaces_the_root_and_keeps_the_rest()
    {
        var live = Path.Combine(Path.GetTempPath(), "servers", "survival", "worlds");
        var root = ShadowCopyPaths.VolumeRootOf(live);

        var mapped = ShadowCopyPaths.Inside(Device, live);

        Assert.StartsWith(Device + Path.DirectorySeparatorChar, mapped, StringComparison.Ordinal);

        Assert.Equal(
            Path.GetFullPath(live)[root.Length..].TrimStart(Path.DirectorySeparatorChar),
            mapped[(Device.Length + 1)..]);
    }

    // The volume root itself maps to the device with nothing appended — one
    // trailing separator here would put a doubled one into every path built on
    // top of it.
    [Fact]
    public void The_root_itself_maps_to_the_bare_device()
    {
        var root = ShadowCopyPaths.VolumeRootOf(Path.GetTempPath());

        Assert.Equal(Device, ShadowCopyPaths.Inside(Device, root));
    }

    [Fact]
    public void A_device_object_with_a_stray_separator_does_not_double_it()
    {
        var live = Path.Combine(Path.GetTempPath(), "worlds");

        Assert.Equal(
            ShadowCopyPaths.Inside(Device, live),
            ShadowCopyPaths.Inside(Device + Path.DirectorySeparatorChar, live));
    }
}

/// <summary>
/// Reading what Windows said about a shadow copy it just took.
/// </summary>
public sealed class ShadowCopyParseTests
{
    // The shape Win32_ShadowCopy actually answers with, through ConvertTo-Json.
    [Fact]
    public void The_id_and_device_path_are_read_out()
    {
        var shadow = ShadowCopyService.Parse(
            """{"ShadowId":"{8C1B2E2A-0000-0000-0000-000000000001}","DeviceObject":"\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy3"}""",
            @"C:\");

        Assert.Equal("{8C1B2E2A-0000-0000-0000-000000000001}", shadow.ShadowId);
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3", shadow.DeviceObject);
    }

    /// <summary>
    /// A succeeded-but-unreadable answer is a failure, not a
    /// <see cref="ShadowCopy"/> with empty fields.
    /// </summary>
    /// <remarks>
    /// The empty-field version is the dangerous one: it would go on to read every
    /// world file out of the path <c>"\file"</c>, fail on all of them, and report
    /// a missing world rather than a snapshot that never happened.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"ShadowId":"{x}"}""")]
    [InlineData("""{"DeviceObject":"\\\\?\\GLOBALROOT\\Device\\X"}""")]
    [InlineData("""{"ShadowId":"","DeviceObject":""}""")]
    public void An_unreadable_answer_is_a_failure(string json)
    {
        Assert.Throws<InvalidOperationException>(() => ShadowCopyService.Parse(json, @"C:\"));
    }
}

/// <summary>
/// The two PowerShell scripts, checked for the things a compiler cannot see.
/// </summary>
/// <remarks>
/// They are also parsed with a real PowerShell during development — a script
/// that does not parse fails at the one moment it is needed and cannot be caught
/// here. What these assert is the part a parser would happily accept and a
/// server would then get wrong.
/// </remarks>
public sealed class ShadowCopyScriptTests
{
    [Fact]
    public void The_create_script_asks_for_a_client_accessible_snapshot()
    {
        var script = ShadowCopyService.CreateScript(@"C:\");

        // The context matters. A Backup-context snapshot expects a requester
        // holding the VSS session open for the life of the copy, which this
        // process is not — pretending otherwise leaves shadow copies that vanish
        // mid-read.
        Assert.Contains("Context = 'ClientAccessible'", script);
        Assert.Contains(@"Volume = 'C:\'", script);

        // CIM cmdlets rather than the [wmiclass] accelerator: both work under
        // powershell.exe, only these work under PowerShell 7.
        Assert.Contains("Invoke-CimMethod", script);
        Assert.DoesNotContain("wmiclass", script);
    }

    [Fact]
    public void The_release_script_targets_exactly_one_shadow_copy()
    {
        var script = ShadowCopyService.ReleaseScript("{8C1B2E2A-0000-0000-0000-000000000001}");

        Assert.Contains("Remove-CimInstance", script);
        Assert.Contains("$_.ID -eq '{8C1B2E2A-0000-0000-0000-000000000001}'", script);
    }

    /// <summary>
    /// A quote in a value cannot end the literal it sits in.
    /// </summary>
    /// <remarks>
    /// Both values reaching these scripts are machine-generated today — a drive
    /// root and a GUID — so this is a belt rather than a live hole. It is
    /// asserted because the day one of them becomes operator-supplied, the
    /// escaping should already be there rather than remembered.
    /// </remarks>
    [Fact]
    public void A_quote_in_a_value_is_escaped_rather_than_closing_the_literal()
    {
        var script = ShadowCopyService.ReleaseScript(@"{x}'; Remove-Item C:\ -Recurse; '");

        // The whole literal, quotes and all: every apostrophe from the value
        // doubled, and the only unpaired ones the two this script wrote. A
        // looser check passes on the unescaped output too, because "''" contains
        // "'" — which is exactly the shape of assertion that proves nothing.
        Assert.Contains(@"'{x}''; Remove-Item C:\ -Recurse; '''", script);
    }
}
