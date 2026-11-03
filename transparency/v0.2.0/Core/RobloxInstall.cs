using System.IO;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace AltSync.Core;

public static class RobloxInstall
{
    public static string? Find()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command");
            var command = key?.GetValue(null) as string;
            var match = Regex.Match(command ?? "", "^\"([^\"]+RobloxPlayerBeta\\.exe)\"", RegexOptions.IgnoreCase);
            if (match.Success && File.Exists(match.Groups[1].Value)) return match.Groups[1].Value;
        }
        var roots = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions") };
        return roots.Where(Directory.Exists).SelectMany(root => Directory.EnumerateDirectories(root, "version-*")).Select(dir => Path.Combine(dir, "RobloxPlayerBeta.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
