using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public static class Logged
{
    /// <summary>The name a user has in the log: short enough to follow one sign-in, and not the user id.</summary>
    public static string User(Guid userId) => Convert.ToHexStringLower(SHA256.HashData(userId.ToByteArray()))[..12];

    extension<T>(FakeLogger<T> logger)
    {
        /// <summary>The lines the class under test wrote, as the text an administrator reads.</summary>
        public IReadOnlyList<(LogLevel Level, string Text)> Lines
            => [.. logger.Collector.GetSnapshot().Select(record => (record.Level, record.Message))];
    }
}
