using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltSync.Core;

public sealed class Vault
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public string Root { get; }
    public SavedData Data { get; private set; } = new();
    public Vault(string? root = null) { Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltSync"); Directory.CreateDirectory(Root); }
    public void Load()
    {
        PortableBackup.Recover(Root);
        var file = Path.Combine(Root, "accounts.json");
        if (!File.Exists(file)) return;
        Data = JsonSerializer.Deserialize<SavedData>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("The account vault is empty or invalid.");
        if (Data.Accounts is null || Data.Settings is null) throw new InvalidDataException("The account vault has an invalid structure.");
    }
    public void Save()
    {
        var file = Path.Combine(Root, "accounts.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(Data, Json));
        File.Move(file + ".tmp", file, true);
    }
    public void AddOrRefresh(Account account)
    {
        int index = Data.Accounts.FindIndex(existing => existing.Id == account.Id);
        Account? old = index >= 0 ? Data.Accounts[index] : null;
        if (old is not null) { account.Group = old.Group; account.AddedAt = old.AddedAt; Data.Accounts[index] = account; }
        else Data.Accounts.Add(account);
        try { Save(); }
        catch
        {
            if (old is not null) Data.Accounts[index] = old;
            else Data.Accounts.Remove(account);
            throw;
        }
    }
    public static string Protect(string cookie) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string secret) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser));
}
