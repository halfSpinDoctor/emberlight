namespace Emberlight.Core;

/// <summary>
/// The app version and the git commit it was built from, split out of an informational version string.
/// The .NET SDK writes that string as "0.1.0+&lt;full commit hash&gt;" when building inside a git repository
/// (SourceLink is on by default), and as plain "0.1.0" otherwise.
/// </summary>
public sealed record BuildVersion(string Version, string? Commit)
{
    /// <summary>Length of the abbreviated commit hash shown to the user, as git prints it.</summary>
    public const int ShortCommitLength = 7;

    /// <summary>The commit hash abbreviated to <see cref="ShortCommitLength"/> characters, or null.</summary>
    public string? ShortCommit => Commit is null ? null
        : Commit.Length > ShortCommitLength ? Commit[..ShortCommitLength] : Commit;

    public static BuildVersion Parse(string? informationalVersion)
    {
        var s = informationalVersion?.Trim() ?? "";
        int plus = s.IndexOf('+');
        if (plus < 0) return new BuildVersion(s.Length == 0 ? "unknown" : s, null);

        var version = s[..plus];
        // Build metadata may hold several dot-separated parts; the SDK appends the commit as the last one.
        var meta = s[(plus + 1)..];
        var commit = meta[(meta.LastIndexOf('.') + 1)..];
        return new BuildVersion(version.Length == 0 ? "unknown" : version, IsHex(commit) ? commit.ToLowerInvariant() : null);
    }

    /// <summary>"0.1.0 (commit 1a2b3c4)", or just "0.1.0" when the commit is unknown.</summary>
    public override string ToString() => ShortCommit is null ? Version : $"{Version} (commit {ShortCommit})";

    private static bool IsHex(string s) => s.Length >= ShortCommitLength && s.All(Uri.IsHexDigit);
}
