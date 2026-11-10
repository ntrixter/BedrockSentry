namespace BedrockSentry.Services.Players;

/// <summary>
/// What became of setting a player's face by hand (<c>D116</c>). Each is a
/// different sentence on the panel, which <see cref="PlayerFaces.Describe"/> says.
/// </summary>
public enum SetFaceOutcome
{
    /// <summary>The face is kept and every list shows it.</summary>
    Set,

    /// <summary>The player has no XUID to keep a face under.</summary>
    NotAXuid,

    /// <summary>The skin, uploaded or fetched, is not one a face can be cut from.</summary>
    NotASkin,

    /// <summary>The typed text cannot be a Java username, so nothing was asked.</summary>
    NotAJavaName,

    /// <summary>Mojang holds no Java account by that name.</summary>
    NoSuchJavaPlayer,

    /// <summary>The Java account wears a default skin, which its profile does not name.</summary>
    NoJavaSkin,

    /// <summary>Mojang or the texture host did not answer; nothing changed.</summary>
    Unreachable,
}
