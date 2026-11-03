namespace AltSync.Core;

public static class CookieSession
{
    public static string Parse(string input)
    {
        string value = input.Trim();
        if (value.StartsWith(".ROBLOSECURITY=", StringComparison.Ordinal))
        {
            value = value[15..].Trim().TrimEnd(';').Trim();
        }
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (value.Length is < 32 or > 16384 || value.Any(c => c < '!' || c > '~' || c is ';' or ',' or '"' or '\\'))
            throw new ArgumentException("Paste the .ROBLOSECURITY cookie value, without other cookies or headers.");
        return value;
    }
}
