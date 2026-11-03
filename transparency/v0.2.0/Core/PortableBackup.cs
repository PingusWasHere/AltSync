using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AltSync.Core;
public sealed record BackupAccount(long Id,string Username,string DisplayName,string Group,string Cookie,string Avatar,DateTimeOffset AddedAt,bool Expired);
public sealed record BackupPayload(int Version,List<BackupAccount> Accounts,LibraryData? Library,Preferences? Settings);
internal sealed record BackupEnvelope(int Version,int Iterations,string Salt,string Nonce,string Tag,string Data);
public static class PortableBackup
{
    private const int Iterations=600000,MaximumBytes=16*1024*1024;
    private static readonly byte[] aad=Encoding.UTF8.GetBytes("AltSync portable backup v1");
    public static byte[] Export(Vault vault,Library library,string password,IReadOnlySet<long>? selected,bool accounts,bool includeLibrary,bool settings)
    {
        if(password.Length is <12 or >1024)throw new ArgumentException("Use a backup password of at least 12 characters.");
        if(!accounts&&!includeLibrary&&!settings)throw new ArgumentException("Select something to include in the backup.");
        var chosen=accounts?vault.Data.Accounts.Where(a=>selected is null||selected.Count==0||selected.Contains(a.Id)).Select(a=>new BackupAccount(a.Id,a.Username,a.DisplayName,a.Group,Vault.Unprotect(a.Secret),a.Avatar,a.AddedAt,a.Expired)).ToList():[];
        var payload=new BackupPayload(1,chosen,includeLibrary?library.Data:null,settings?vault.Data.Settings:null);
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(payload,Vault.Json),salt=RandomNumberGenerator.GetBytes(16),nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16];
        if(plain.Length>MaximumBytes)throw new InvalidOperationException("This backup is too large.");
        var key=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        try {byte[] cipher=new byte[plain.Length];using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,cipher,tag,aad);return JsonSerializer.SerializeToUtf8Bytes(new BackupEnvelope(1,Iterations,Convert.ToBase64String(salt),Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher)),Vault.Json);}
        finally {CryptographicOperations.ZeroMemory(plain);CryptographicOperations.ZeroMemory(key);}
    }
    public static BackupPayload Read(byte[] file,string password)
    {
        if(file.Length>MaximumBytes*2||password.Length>1024)throw new InvalidDataException("Invalid backup file.");
        try
        {
            var envelope=JsonSerializer.Deserialize<BackupEnvelope>(file,Vault.Json)??throw new InvalidDataException("Invalid backup file.");
            if(envelope.Version!=1||envelope.Iterations!=Iterations)throw new InvalidDataException("Unsupported backup format.");
            byte[] salt=Convert.FromBase64String(envelope.Salt),nonce=Convert.FromBase64String(envelope.Nonce),tag=Convert.FromBase64String(envelope.Tag),cipher=Convert.FromBase64String(envelope.Data);
            if(salt.Length!=16||nonce.Length!=12||tag.Length!=16||cipher.Length>MaximumBytes)throw new InvalidDataException("Invalid backup file.");
            byte[] key=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32),plain=new byte[cipher.Length];
            try {using var aes=new AesGcm(key,16);aes.Decrypt(nonce,cipher,tag,plain,aad);var payload=JsonSerializer.Deserialize<BackupPayload>(plain,Vault.Json)??throw new InvalidDataException("Invalid backup contents.");Validate(payload);return payload;}
            finally {CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
        }
        catch(Exception ex)when(ex is CryptographicException or JsonException or FormatException or ArgumentNullException){throw new InvalidDataException("The password is incorrect or the backup is damaged.");}
    }
    private static void Validate(BackupPayload data)
    {
        if(data.Version!=1||data.Accounts is null||data.Accounts.Count>1000||data.Accounts.Select(a=>a.Id).Distinct().Count()!=data.Accounts.Count)throw new InvalidDataException("Invalid backup contents.");
        foreach(var a in data.Accounts)
        {if(a.Id<=0||string.IsNullOrWhiteSpace(a.Username)||a.Username.Length>100||a.DisplayName is null||a.DisplayName.Length>100||a.Group is null||a.Group.Length>40||a.Avatar is null||a.Avatar.Length>1024||a.Avatar.Length>0&&!GameBrowser.ImageUrl(a.Avatar,out _))throw new InvalidDataException("Invalid account metadata in the backup.");_ = CookieSession.Parse(a.Cookie);}
        if(data.Library is not null)Library.ValidateData(data.Library);
        if(data.Settings is not null){var s=data.Settings;if(s.LaunchDelay is <3 or >30||s.PlaceId is null||s.PlaceId.Length>2048||s.JobId is null||s.JobId.Length>100)throw new InvalidDataException("Invalid preferences in the backup.");if(s.PlaceId.Length>0)_=LaunchDestination.Parse(s.PlaceId);if(s.JobId.Length>0&&!Guid.TryParse(s.JobId,out _))throw new InvalidDataException("Invalid saved server.");PerformanceProfiles.Flags(s.Performance);}
    }
    public static void Import(Vault vault,Library library,BackupPayload payload)
    {
        Validate(payload);
        var next=JsonSerializer.Deserialize<SavedData>(JsonSerializer.Serialize(vault.Data,Vault.Json),Vault.Json)!;
        foreach(var a in payload.Accounts){next.Accounts.RemoveAll(old=>old.Id==a.Id);next.Accounts.Add(new Account{Id=a.Id,Username=a.Username,DisplayName=a.DisplayName,Group=a.Group,Secret=Vault.Protect(a.Cookie),Avatar=a.Avatar,AddedAt=a.AddedAt,Expired=a.Expired});}
        if(payload.Settings is not null)next.Settings=payload.Settings;
        var combined=JsonSerializer.Deserialize<LibraryData>(JsonSerializer.Serialize(library.Data,Vault.Json),Vault.Json)!;
        if(payload.Library is not null)
        {
            foreach(var p in payload.Library.Profiles){combined.Profiles.RemoveAll(old=>old.Id==p.Id);if(combined.Profiles.Any(old=>old.Name.Equals(p.Name,StringComparison.OrdinalIgnoreCase)))continue;combined.Profiles.Add(p);}
            foreach(var g in payload.Library.Games){combined.Games.RemoveAll(old=>old.Game.PlaceId==g.Game.PlaceId);combined.Games.Add(g);}
            foreach(var d in payload.Library.Destinations){combined.Destinations.RemoveAll(old=>old.AccountId==d.AccountId);combined.Destinations.Add(d);}
            foreach(var w in payload.Library.Windows){combined.Windows.RemoveAll(old=>old.AccountId==w.AccountId);combined.Windows.Add(w);}
        }
        Library.ValidateData(combined);
        Commit(vault.Root,JsonSerializer.SerializeToUtf8Bytes(next,Vault.Json),JsonSerializer.SerializeToUtf8Bytes(combined,Vault.Json));
        vault.Load();library.Load();
    }
    private sealed record Journal(string Id,bool HadAccounts,bool HadLibrary);
    private static void Commit(string root,byte[] accounts,byte[] library)
    {
        Recover(root);string id=Guid.NewGuid().ToString("N"),directory=Path.Combine(root,"Backups","import-"+id);Directory.CreateDirectory(directory);
        string accountFile=Path.Combine(root,"accounts.json"),libraryFile=Path.Combine(root,"library.json"),journalFile=Path.Combine(root,"backup-import.pending.json");
        bool hadAccounts=File.Exists(accountFile),hadLibrary=File.Exists(libraryFile);
        if(hadAccounts)File.Copy(accountFile,Path.Combine(directory,"accounts.json"));if(hadLibrary)File.Copy(libraryFile,Path.Combine(directory,"library.json"));
        File.WriteAllText(journalFile,JsonSerializer.Serialize(new Journal(id,hadAccounts,hadLibrary),Vault.Json));
        try {File.WriteAllBytes(accountFile+".import",accounts);File.WriteAllBytes(libraryFile+".import",library);File.Move(accountFile+".import",accountFile,true);File.Move(libraryFile+".import",libraryFile,true);File.Delete(journalFile);}
        catch {Recover(root);throw;}
    }
    public static void Recover(string root)
    {
        string journalFile=Path.Combine(root,"backup-import.pending.json");if(!File.Exists(journalFile))return;
        var journal=JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalFile),Vault.Json)??throw new InvalidDataException("Invalid backup recovery journal.");
        if(!Guid.TryParseExact(journal.Id,"N",out _))throw new InvalidDataException("Invalid backup recovery path.");
        var directory=Path.Combine(root,"Backups","import-"+journal.Id);
        foreach(var pair in new[]{("accounts.json",journal.HadAccounts),("library.json",journal.HadLibrary)})
        {var destination=Path.Combine(root,pair.Item1);if(pair.Item2){var source=Path.Combine(directory,pair.Item1);File.Copy(source,destination+".restore",true);File.Move(destination+".restore",destination,true);}else File.Delete(destination);}
        File.Delete(journalFile);
    }
}
