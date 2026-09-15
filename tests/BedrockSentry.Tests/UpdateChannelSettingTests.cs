using System;
using BedrockSentry.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace BedrockSentry.Tests;

/// <summary>
/// The channel setting survives the rename of its own values.
/// </summary>
/// <remarks>
/// <para><c>UpdateChannel.Prod</c> became <c>Stable</c> on 2026-09-15. The enum had
/// <strong>no test coverage at all</strong> before that, which is the same hole the
/// removal notice's stylesheet was in — the setting most able to stop the service
/// starting, unpinned.</para>
///
/// <para>The case that matters is not a typo. Every install written before the rename
/// carries the shipped default <c>"Channel": "Prod"</c>, so the rename turns every one of
/// them into a config the binder cannot read. That is not a cosmetic failure: the service
/// does not start, the health gate rolls the build back, and the operator is left reading
/// an exception about an enum member.</para>
/// </remarks>
public class UpdateChannelSettingTests
{
    [Theory]
    [InlineData("Stable", UpdateChannel.Stable)]
    [InlineData("stable", UpdateChannel.Stable)]
    [InlineData("  Stable  ", UpdateChannel.Stable)]
    [InlineData("Build", UpdateChannel.Build)]
    [InlineData("build", UpdateChannel.Build)]
    public void A_known_channel_is_read_whatever_its_casing(string raw, UpdateChannel expected)
    {
        Assert.True(UpdateChannelSetting.Parse(raw, out var channel));
        Assert.Equal(expected, channel);
    }

    /// <summary>
    /// Absent is the default, not a mistake, and must not warn.
    /// </summary>
    /// <remarks>
    /// A warning on every start of a perfectly ordinary install is a warning people
    /// learn to scroll past, which costs the one that matters its audience.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_channel_is_stable_and_is_not_a_complaint(string? raw)
    {
        Assert.True(UpdateChannelSetting.Parse(raw, out var channel));
        Assert.Equal(UpdateChannel.Stable, channel);
    }

    /// <summary>
    /// The old spelling is recognised as wrong, not as Build.
    /// </summary>
    /// <remarks>
    /// Falling back to <c>Stable</c> rather than <c>Build</c> is the whole point: if the
    /// wrapper cannot tell which channel was asked for, moving the install onto
    /// in-progress releases on the strength of a bad string would be the worse guess by
    /// a wide margin.
    /// </remarks>
    [Theory]
    [InlineData("Prod")]
    [InlineData("prod")]
    [InlineData("Production")]
    [InlineData("stabel")]
    [InlineData("7")]
    public void An_unknown_channel_falls_back_to_stable_and_says_so(string raw)
    {
        Assert.False(UpdateChannelSetting.Parse(raw, out var channel));
        Assert.Equal(UpdateChannel.Stable, channel);
    }

    /// <summary>
    /// The message names the old spelling, because that is who will read it.
    /// </summary>
    [Fact]
    public void The_complaint_names_the_old_spelling_and_both_valid_values()
    {
        var message = UpdateChannelSetting.Unrecognised("Prod");

        Assert.Contains("Prod", message);
        Assert.Contains(nameof(UpdateChannel.Stable), message);
        Assert.Contains(nameof(UpdateChannel.Build), message);
        Assert.Contains(UpdateChannelSetting.Key, message);
    }

    /// <summary>
    /// The guard is not theatre: without it, the binder really does throw.
    /// </summary>
    /// <remarks>
    /// <para>This is the assertion that justifies the whole file. It would be easy to
    /// write a tolerant parser and assume it was needed; this pins that the untreated
    /// value <strong>fails</strong>, so the next person to decide the sanitising step in
    /// <c>Program.cs</c> is redundant finds out here rather than on a box.</para>
    ///
    /// <para>It also pins <em>where</em> the failure is, which is why the fix cannot be a
    /// <c>PostConfigure</c>: the throw happens during <c>Bind</c>, so anything running
    /// after binding never runs at all.</para>
    /// </remarks>
    [Fact]
    public void The_binder_throws_on_the_old_spelling_which_is_why_the_guard_exists()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [UpdateChannelSetting.Key] = "Prod",
            })
            .Build();

        var options = new UpdateOptions();

        Assert.ThrowsAny<Exception>(
            () => configuration.GetSection(UpdateOptions.SectionName).Bind(options));
    }

    /// <summary>
    /// And with the guard applied first, the same configuration binds and reads Stable.
    /// </summary>
    /// <remarks>
    /// The end-to-end guarantee, in the same shape <c>Program.cs</c> performs it: parse
    /// the raw value, write the corrected one back into the configuration in memory, then
    /// bind. The operator's file is untouched, so the wrong value stays visible to be
    /// corrected rather than being laundered on first boot.
    /// </remarks>
    [Fact]
    public void Sanitising_first_lets_a_pre_rename_config_bind_to_stable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [UpdateChannelSetting.Key] = "Prod",
                [$"{UpdateOptions.SectionName}:Mode"] = "Notify",
            })
            .Build();

        Assert.False(
            UpdateChannelSetting.Parse(configuration[UpdateChannelSetting.Key], out var channel));

        configuration[UpdateChannelSetting.Key] = channel.ToString();

        var options = new UpdateOptions();
        configuration.GetSection(UpdateOptions.SectionName).Bind(options);

        Assert.Equal(UpdateChannel.Stable, options.Channel);

        // The rest of the section still binds — the override touches one key, not the
        // section, which a broader "just reset it" fix would not have preserved.
        Assert.Equal(UpdateMode.Notify, options.Mode);
    }

    /// <summary>
    /// The feed URL each member actually produces, pinned so a rename cannot leave the
    /// doc comment describing values that no member can make.
    /// </summary>
    /// <remarks>
    /// <para><c>T193</c>. <c>FeedUrlTemplate</c>'s summary said <c>{channel}</c> becomes
    /// <c>build</c> or <c>prod</c> — ten lines below the enum whose rename made
    /// <c>prod</c> impossible, in the same file the rename was written in. Nothing could
    /// fail, because the sentence was prose and the substitution was
    /// <c>Channel.ToString().ToLowerInvariant()</c> somewhere else entirely.</para>
    ///
    /// <para>So this pins the pair rather than the sentence: the substitution is
    /// reproduced from <c>UpdateClient.FetchManifestAsync</c>, and renaming a member
    /// without revisiting the comment fails here. A doc comment naming the values of a
    /// substitution is load-bearing code (<c>T145</c>), and the only way to keep one
    /// honest is to make something able to fail.</para>
    /// </remarks>
    [Theory]
    [InlineData(UpdateChannel.Stable, "channel-stable")]
    [InlineData(UpdateChannel.Build, "channel-build")]
    public void The_feed_url_carries_the_lowercased_member_name(UpdateChannel channel, string expectedTag)
    {
        var options = new UpdateOptions { Channel = channel };

        // Exactly what UpdateClient does. Copied on purpose rather than referenced:
        // Services is where the real one lives, and the point is to pin the STRING a
        // member produces, so if that substitution ever changes shape this stops
        // matching and somebody reads both.
        var url = options.FeedUrlTemplate.Replace(
            "{channel}",
            channel.ToString().ToLowerInvariant(),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(expectedTag, url);
        Assert.DoesNotContain("{channel}", url);

        // The spelling the rename retired must not be producible by any member.
        Assert.DoesNotContain("prod", url, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every member renders to a distinct, lowercase tag — so no future member can
    /// silently collide with an existing release tag.
    /// </summary>
    [Fact]
    public void Every_channel_renders_a_distinct_lowercase_tag()
    {
        var tags = Enum.GetValues<UpdateChannel>()
            .Select(c => c.ToString().ToLowerInvariant())
            .ToArray();

        Assert.Equal(tags.Length, tags.Distinct().Count());
        Assert.All(tags, tag => Assert.Equal(tag, tag.ToLowerInvariant()));
    }

    /// <summary>
    /// The numeric values are unchanged, so nothing persisted as a number moves channel.
    /// </summary>
    /// <remarks>
    /// Renaming a member is safe for names; renumbering one silently re-points every
    /// stored integer. Pinned because the two are one edit apart.
    /// </remarks>
    [Fact]
    public void The_numeric_values_did_not_move_in_the_rename()
    {
        Assert.Equal(0, (int)UpdateChannel.Stable);
        Assert.Equal(1, (int)UpdateChannel.Build);
    }
}
