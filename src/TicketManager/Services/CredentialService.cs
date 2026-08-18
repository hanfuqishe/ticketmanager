using System.Security.Cryptography;
using System.Text;

namespace TicketManager.Services;

/// <summary>使用 Windows DPAPI（当前用户作用域）加解密敏感配置，密文落库。</summary>
public static class CredentialService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TicketManager.2026.credential");

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var data = Encoding.UTF8.GetBytes(plainText);
        var enc = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var enc = Convert.FromBase64String(base64);
            var dec = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch
        {
            return ""; // 密钥不可用（如换了用户）时返回空
        }
    }
}
