using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SamedisCare.SplSync.Core.Config;

/// <summary>
/// Loads and saves config.yml. Secrets prefixed with `enc:` are decrypted with DPAPI on Windows
/// (LocalMachine scope) and re-encrypted on save. Plain values are accepted for first-time setup
/// and left in place — the GUI is expected to upgrade them to enc: form on the next save.
/// </summary>
public static class ConfigStore
{
    private const string EncPrefix = "enc:";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"config.yml not found at {path}");

        var yaml = File.ReadAllText(path);
        var cfg = Deserializer.Deserialize<AppConfig>(yaml) ?? new AppConfig();
        DecryptSecretsInPlace(cfg);
        return cfg;
    }

    public static void Save(string path, AppConfig cfg, bool encryptSecrets = true)
    {
        if (encryptSecrets)
            EncryptSecretsInPlace(cfg);
        var yaml = Serializer.Serialize(cfg);
        File.WriteAllText(path, yaml);
    }

    private static void DecryptSecretsInPlace(AppConfig cfg)
    {
        cfg.Auth.ClientSecret = TryDecrypt(cfg.Auth.ClientSecret);
        cfg.Http.ProxyPassword = TryDecrypt(cfg.Http.ProxyPassword);
    }

    private static void EncryptSecretsInPlace(AppConfig cfg)
    {
        cfg.Auth.ClientSecret = TryEncrypt(cfg.Auth.ClientSecret);
        cfg.Http.ProxyPassword = TryEncrypt(cfg.Http.ProxyPassword);
    }

    private static string TryDecrypt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!value.StartsWith(EncPrefix)) return value; // plain
        if (!OperatingSystem.IsWindows()) return value; // DPAPI is Windows-only; on other OS leave as-is

        try
        {
            var b64 = value.Substring(EncPrefix.Length);
            var encrypted = Convert.FromBase64String(b64);
            var clear = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return value;
        }
    }

    private static string TryEncrypt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.StartsWith(EncPrefix)) return value; // already encrypted
        if (!OperatingSystem.IsWindows()) return value; // see above

        try
        {
            var clear = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(clear, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            return EncPrefix + Convert.ToBase64String(encrypted);
        }
        catch
        {
            return value;
        }
    }
}
