using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AltSync.Core;

public sealed class PerformanceProfiles(string root)
{
    public static JsonObject Flags(string profile) => profile switch
    {
        "balanced" => new() { ["DFFlagTextureQualityOverrideEnabled"] = true, ["DFIntTextureQualityOverride"] = 1, ["FIntDebugForceMSAASamples"] = 1, ["FIntFRMMinGrassDistance"] = 0, ["FIntFRMMaxGrassDistance"] = 50 },
        "minimal" => new() { ["DFFlagTextureQualityOverrideEnabled"] = true, ["DFIntTextureQualityOverride"] = 0, ["FIntDebugForceMSAASamples"] = 1, ["FIntFRMMinGrassDistance"] = 0, ["FIntFRMMaxGrassDistance"] = 0 },
        "default" => new(),
        _ => throw new ArgumentException("Unknown performance profile.")
    };
    public string Apply(string executable, string profile)
    {
        var flags = Flags(profile);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "RobloxPlayerBeta.exe") throw new InvalidOperationException("Roblox Player is not installed.");
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "ClientSettings");
        var file = Path.Combine(directory, "ClientAppSettings.json");
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable))));
        var backupFile = Path.Combine(root, "Backups", id + ".json");
        var current = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("ClientAppSettings.json must contain a JSON object.") : new JsonObject();
        var backup = File.Exists(backupFile) ? JsonNode.Parse(File.ReadAllText(backupFile))!.AsObject() : new JsonObject();
        foreach (var pair in backup)
        {
            var entry = pair.Value!.AsObject();
            bool matchesApplied = JsonNode.DeepEquals(current[pair.Key], entry["applied"]);
            bool matchesOriginal = current.ContainsKey(pair.Key) == entry["existed"]!.GetValue<bool>() && JsonNode.DeepEquals(current[pair.Key], entry["original"]);
            if (!matchesApplied && !matchesOriginal) throw new InvalidOperationException($"{pair.Key} was modified by another application. Restore its previous AltSync value before changing profiles.");
        }
        if (profile == "default")
        {
            foreach (var pair in backup)
            {
                var entry = pair.Value!.AsObject();
                if (entry["existed"]!.GetValue<bool>()) current[pair.Key] = entry["original"]?.DeepClone();
                else current.Remove(pair.Key);
            }
            if (backup.Count == 0) return "No AltSync overrides to restore for this Roblox version.";
            Write(file, current);
            File.Delete(backupFile);
            return "Original graphics settings restored. Restart Roblox to use them.";
        }
        foreach (var flag in flags)
        {
            if (!backup.ContainsKey(flag.Key)) backup[flag.Key] = new JsonObject { ["existed"] = current.ContainsKey(flag.Key), ["original"] = current[flag.Key]?.DeepClone() };
            backup[flag.Key]!["applied"] = flag.Value?.DeepClone();
            current[flag.Key] = flag.Value?.DeepClone();
        }
        // Save originals before touching the client configuration.
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        Write(backupFile, backup);
        Directory.CreateDirectory(directory);
        Write(file, current);
        return "Graphics profile applied. Restart Roblox to use the new settings.";
    }
    private static void Write(string path, JsonObject value)
    {
        File.WriteAllText(path + ".tmp", value.ToJsonString(Vault.Json));
        File.Move(path + ".tmp", path, true);
    }
}
