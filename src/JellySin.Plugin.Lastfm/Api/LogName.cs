using System.Security.Cryptography;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>How the log names a user: the same short value for the same account, from which the account cannot be read.</summary>
internal static class LogName
{
    public static string Of(Guid userId) => Convert.ToHexStringLower(SHA256.HashData(userId.ToByteArray()))[..12];
}
