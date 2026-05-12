using Microsoft.AspNetCore.DataProtection;

namespace Animarr.Web.Services;

/// <summary>
/// H-10: wraps <see cref="IDataProtector"/> to encrypt/decrypt sensitive
/// values (API keys, tokens) stored in <c>AppConfig</c>. Stored values are
/// prefixed with <see cref="EncryptedPrefix"/> so unencrypted legacy values
/// keep working on first read after upgrade — they are re-encrypted lazily
/// on the next write.
/// </summary>
public class SecretProtector
{
    public const string EncryptedPrefix = "enc:v1:";
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("Animarr.AppConfig.Secrets.v1");

    public string Encrypt(string plaintext)
        => EncryptedPrefix + _protector.Protect(plaintext);

    public string? TryDecrypt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return value; // legacy plaintext — return as-is
        try
        {
            return _protector.Unprotect(value[EncryptedPrefix.Length..]);
        }
        catch
        {
            // Key ring changed or value corrupted — surface as null so the user
            // knows to re-enter the secret, instead of leaking ciphertext to TMDB.
            return null;
        }
    }

    /// <summary>Keys whose value must be encrypted at rest.</summary>
    public static readonly HashSet<string> SecretKeys = new(StringComparer.Ordinal)
    {
        AppConfigKeys.TmdbApiKey,
        AppConfigKeys.MalClientId,
        AppConfigKeys.LlmApiKey,
    };

    public static bool IsSecret(string key) => SecretKeys.Contains(key);
}
