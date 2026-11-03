using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AltSync.Core;

internal sealed record UpdatePlan(string InstallRoot, string WorkRoot, string StageRoot, string Version, Dictionary<string, string> Files, int ParentId = 0, bool Restart = true);

internal static class UpdatePackage
{
    public static string Inside(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Invalid update path.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path is outside its folder.");
        return path;
    }
    public static Dictionary<string, string> Extract(string zipPath, string stageRoot, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is < 5 or > 5000) throw new InvalidDataException("Unexpected update package.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<(ZipArchiveEntry Entry, string Relative)>();
        long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("AltSync-App/", StringComparison.Ordinal) || name.Contains(':') || name.Split('/').Any(part => part is "." or "..") || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The update package contains an unsafe path.");
            string relative = name[12..];
            if (relative.Length == 0 || name.EndsWith('/')) continue;
            Inside(stageRoot, relative);
            if (!names.Add(relative) || Path.GetFileName(relative).Equals("accounts.json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(relative).Equals(".env", StringComparison.OrdinalIgnoreCase) || new[] { ".cs", ".csproj", ".sln", ".ps1", ".bat" }.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The update package contains unexpected files.");
            bytes = checked(bytes + entry.Length);
            if (bytes > 512L * 1024 * 1024) throw new InvalidDataException("The update package is too large.");
            items.Add((entry, relative));
        }
        foreach (string required in new[] { "AltSync.exe", "AltSync.dll", "AltSync.deps.json", "AltSync.runtimeconfig.json", "Web/index.html" })
            if (!names.Contains(required)) throw new InvalidDataException("The update package is incomplete.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, relative) in items)
        {
            token.ThrowIfCancellationRequested();
            string target = Inside(stageRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, false);
            using var input = File.OpenRead(target);
            files[relative] = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
        return files;
    }
}
