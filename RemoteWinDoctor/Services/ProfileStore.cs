using System.Text.Json;

namespace RemoteWinDoctor.Services;

public class MachineProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Specs { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool IsOnline { get; set; } = false;
    public DateTime? LastChecked { get; set; }
}

public class ProfileStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public ProfileStore(string? basePath = null)
    {
        var root = string.IsNullOrWhiteSpace(basePath) ? AppContext.BaseDirectory : basePath;
        _filePath = Path.Combine(root, "profiles.json");
        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                // Only the local machine is seeded: this binary ships to other people, so it must
                // never carry real client hosts or passwords.
                var defaults = new List<MachineProfile>
                {
                    new MachineProfile
                    {
                        Id = "local-workstation",
                        Name = "This PC (Local)",
                        IpAddress = "127.0.0.1",
                        Username = Environment.UserName,
                        Password = "",
                        Notes = "Machine running RemoteWinDoctor"
                    }
                };

                SaveProfilesInternal(defaults);
            }
        }
    }

    public List<MachineProfile> GetAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return new List<MachineProfile>();
            var json = File.ReadAllText(_filePath);
            var profiles = JsonSerializer.Deserialize<List<MachineProfile>>(json) ?? new List<MachineProfile>();
            bool hadPlaintext = profiles.Any(p => !string.IsNullOrEmpty(p.Password) && !p.Password.StartsWith(EncPrefix));
            foreach (var p in profiles) p.Password = Unprotect(p.Password);
            if (hadPlaintext) SaveProfilesInternal(profiles);
            return profiles;
        }
    }

    // DPAPI (CurrentUser): profiles.json is unreadable to other Windows accounts and other PCs.
    // Trade-off: copying profiles.json to another machine loses the saved passwords.
    private const string EncPrefix = "dpapi:";

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(EncPrefix)) return plain;
        var bytes = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return EncPrefix + Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(EncPrefix)) return stored;
        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(stored[EncPrefix.Length..]), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return ""; // encrypted by another user/machine
        }
    }

    public MachineProfile? GetById(string id)
    {
        return GetAll().FirstOrDefault(p => p.Id == id);
    }

    public void Save(MachineProfile profile)
    {
        lock (_lock)
        {
            var profiles = GetAll();
            var existing = profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (existing != null)
            {
                profiles.Remove(existing);
            }
            profiles.Add(profile);
            SaveProfilesInternal(profiles);
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var profiles = GetAll();
            var item = profiles.FirstOrDefault(p => p.Id == id);
            if (item != null)
            {
                profiles.Remove(item);
                SaveProfilesInternal(profiles);
                return true;
            }
            return false;
        }
    }

    private void SaveProfilesInternal(List<MachineProfile> profiles)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var toWrite = profiles.Select(p => new MachineProfile
        {
            Id = p.Id, Name = p.Name, IpAddress = p.IpAddress, Username = p.Username,
            Password = Protect(p.Password), Model = p.Model, Specs = p.Specs, Notes = p.Notes,
            IsOnline = p.IsOnline, LastChecked = p.LastChecked
        }).ToList();
        var json = JsonSerializer.Serialize(toWrite, options);
        File.WriteAllText(_filePath, json);
    }
}
