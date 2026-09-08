using System.Security.Cryptography;
using System.Text;

namespace JellySin.Plugin.Lastfm.Transport;

public static class LastfmSignature
{
    public static string Compute(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var canonical = new StringBuilder();
        foreach (var pair in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (pair.Key is not ("format" or "callback" or "api_sig")) canonical.Append(pair.Key).Append(pair.Value);
        canonical.Append(secret);
        // MD5 is mandated by Last.fm's authentication protocol, not used for password storage.
#pragma warning disable CA5351
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
#pragma warning restore CA5351
    }
}
