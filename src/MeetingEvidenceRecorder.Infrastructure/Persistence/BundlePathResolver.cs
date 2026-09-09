using System.Text.RegularExpressions;

namespace MeetingEvidenceRecorder.Infrastructure.Persistence;

public sealed class BundlePathException(string message) : IOException(message);

/// <summary>Portable paths, with all existing symlink/reparse components rejected.</summary>
public sealed class BundlePathResolver
{
    public string Root { get; }
    public BundlePathResolver(string root)
    {
        Root = Path.GetFullPath(root);
        RejectLink(Root);
    }

    public static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') ||
            path.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            throw new BundlePathException("Expected a portable bundle-relative path with forward slashes.");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new BundlePathException("Unsafe or nonportable path component.");
        }
    }

    public string Resolve(string path)
    {
        ValidateRelative(path);
        var resolved = Path.GetFullPath(Path.Combine(Root, path));
        var relative = Path.GetRelativePath(Root, resolved);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new BundlePathException("Path escapes bundle root.");
        RejectLink(Root);
        var current = Root;
        foreach (var part in path.Split('/'))
        {
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return resolved;
    }

    private static void RejectLink(string path)
    {
        // LinkTarget also detects dangling symlinks, for which File.Exists is false.
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new BundlePathException("Symlink/reparse references are not supported.");
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new BundlePathException("Symlink/reparse references are not supported.");
    }
}
