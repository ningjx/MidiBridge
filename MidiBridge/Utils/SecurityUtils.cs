using System.Security.Cryptography;
using System.Text;

namespace MidiBridge.Utils;

public static class SecurityUtils
{
    public static byte[] ComputeSHA256Hash(string data)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    public static bool CompareDigests(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}
