using System.Security.Cryptography;
using System.Text;

namespace RelayBench.Services;

internal static class CodexOAuthPkceService
{
    public static CodexOAuthPkcePair Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(96);
        var verifier = Base64UrlEncode(bytes);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new CodexOAuthPkcePair(verifier, challenge);
    }

    public static string GenerateState()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal sealed record CodexOAuthPkcePair(string CodeVerifier, string CodeChallenge);
