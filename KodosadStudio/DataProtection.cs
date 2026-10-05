using System.Security.Cryptography;
using System.Text;

namespace KodosadStudio;

public static class DataProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KodosadStudio|current-user|v1");

    public static byte[] Protect(string text) => ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser);

    public static string Unprotect(byte[] protectedBytes) => Encoding.UTF8.GetString(
        ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser));
}
