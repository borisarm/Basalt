using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Basalt.Domain.Sync;

/// <summary>An author or committer of a Git commit (spec 06 §8). The instant comes from <c>IClock</c>.</summary>
public readonly record struct GitSignature
{
    public GitSignature(string name, string email, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);
        if (name.Length == 0 || email.Length == 0
            || name.Contains('\n') || name.Contains('\r') || name.Contains('<') || name.Contains('>')
            || email.Contains('\n') || email.Contains('\r') || email.Contains('<') || email.Contains('>'))
        {
            throw new ArgumentException("A Git signature needs a name and an email without line breaks or angle brackets.");
        }

        Name = name;
        Email = email;
        When = when;
    }

    public string Name { get; }

    public string Email { get; }

    public DateTimeOffset When { get; }

    public override string ToString()
    {
        var offset = When.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var absolute = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"{Name} <{Email}> {When.ToUnixTimeSeconds()} {sign}{absolute.Hours:00}{absolute.Minutes:00}");
    }
}

/// <summary>
/// Computes the SHA-1 of Git commit objects (spec 06 §8): <c>commit {length}\0</c>, tree, parents, author,
/// committer, a blank line and the message (a final newline is added when missing, as Git does).
/// </summary>
public static class GitCommitHasher
{
    public static CommitSha Compute(TreeSha tree, GitSignature author, GitSignature committer, string message) =>
        Compute(tree, [], author, committer, message);

    public static CommitSha Compute(
        TreeSha tree,
        IReadOnlyList<CommitSha> parents,
        GitSignature author,
        GitSignature committer,
        string message)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(message);
        if (parents.Any(parent => parent.Value is null))
        {
            throw new ArgumentException("A parent cannot be null.", nameof(parents));
        }

        if (message.Contains('\r'))
        {
            throw new ArgumentException("The commit message cannot contain carriage returns.", nameof(message));
        }

        var content = new StringBuilder();
        content.Append("tree ").Append(tree).Append('\n');
        foreach (var parent in parents)
        {
            content.Append("parent ").Append(parent).Append('\n');
        }

        content.Append("author ").Append(author).Append('\n');
        content.Append("committer ").Append(committer).Append('\n');
        content.Append('\n');
        content.Append(message);
        if (!message.EndsWith('\n'))
        {
            content.Append('\n');
        }

        var body = Encoding.UTF8.GetBytes(content.ToString());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("commit " + body.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(body);
        return CommitSha.FromBytes(hash.GetHashAndReset());
    }
}
