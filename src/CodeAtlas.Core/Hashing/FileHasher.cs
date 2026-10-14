using System.Security.Cryptography;

namespace CodeAtlas.Core.Hashing;

public static class FileHasher
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
