using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LLMPulse.Core.Storage;

public interface IApiKeyStore
{
    void Set(string source, string key);
    string? Get(string source);
    bool Has(string source);
}

/// <summary>Windows DPAPI(CurrentUser) 로 LocalAppData 에 암호화 저장. 평문 설정 파일 금지.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiApiKeyStore : IApiKeyStore
{
    private readonly string _dir;

    public DpapiApiKeyStore(string? dataDirectory = null)
    {
        _dir = Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "secrets");
    }

    public void Set(string source, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("API key is empty.", nameof(key));

        Directory.CreateDirectory(_dir);
        var plain = Encoding.UTF8.GetBytes(key.Trim());
        var protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(source), protectedBytes);
        CryptographicOperations.ZeroMemory(plain);
    }

    public string? Get(string source)
    {
        var path = PathFor(source);
        if (!File.Exists(path)) return null;

        var protectedBytes = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool Has(string source) => File.Exists(PathFor(source));

    private string PathFor(string source)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            source = source.Replace(c, '_');
        return Path.Combine(_dir, $"{source}.dpapi");
    }
}
