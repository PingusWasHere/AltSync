using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AltSync.Core;

public sealed record DiscordPresenceButton(string Label, string Url);

public sealed record DiscordReleaseProfile
{
    public string ApplicationId { get; init; } = "";
    public string Name { get; init; } = "AltSync";
    public string Details { get; init; } = "Managing Roblox accounts";
    public string State { get; init; } = "In AltSync";
    public string LargeImage { get; init; } = "altsync";
    public IReadOnlyList<DiscordPresenceButton> Buttons { get; init; } = [];
    public bool IsConfigured => ApplicationId.Length > 0;

    public DiscordReleaseProfile Validate()
    {
        if (ApplicationId.Length > 0 && (!Regex.IsMatch(ApplicationId, "^[1-9][0-9]{16,19}$") || !ulong.TryParse(ApplicationId, out _))) throw new InvalidDataException("Invalid publisher Discord Application ID.");
        foreach (string text in new[] { Name, Details, State })
            if (text.Length is < 2 or > 128 || text.Any(char.IsControl)) throw new InvalidDataException("Invalid publisher presence text.");
        bool imageKey = Regex.IsMatch(LargeImage, "^[a-z0-9_-]{1,64}$");
        bool imageUrl = LargeImage.Length <= 256 && Uri.TryCreate(LargeImage, UriKind.Absolute, out var imageUri) && imageUri.Scheme == "https" && imageUri.UserInfo.Length == 0;
        if (!imageKey && !imageUrl) throw new InvalidDataException("Use a publisher Discord image asset key or HTTPS image URL.");
        if (Buttons.Count > 2) throw new InvalidDataException("Discord supports at most two presence buttons.");
        foreach (var button in Buttons)
            if (button.Label.Length is < 1 or > 32 || button.Label.Any(char.IsControl) ||
                (button.Url.Length > 0 && (button.Url.Length > 512 || !Uri.TryCreate(button.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)))
                throw new InvalidDataException("Invalid publisher presence button.");
        return this;
    }
}

public static class DiscordBranding
{
    // This resource is compiled into the binary, never loaded from user preferences or a loose file.
    public static DiscordReleaseProfile Profile { get; } = Load();
    private static DiscordReleaseProfile Load()
    {
        using var resource = typeof(DiscordBranding).Assembly.GetManifestResourceStream("AltSync.Branding.Discord.json") ?? throw new InvalidDataException("The publisher Discord profile is missing.");
        return (JsonSerializer.Deserialize<DiscordReleaseProfile>(resource, Vault.Json) ?? throw new InvalidDataException("The publisher Discord profile is invalid.")).Validate();
    }
}
