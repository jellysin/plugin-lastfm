using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tests.Core;

/// <summary>Keeps the lines a class under test writes to the log, as the text an administrator reads.</summary>
public sealed class LogLines<T> : ILogger<T>
{
    public List<(LogLevel Level, string Text)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Add((logLevel, formatter(state, exception)));
}

public static class Logged
{
    /// <summary>The name a user has in the log: short enough to follow one sign-in, and not the user id.</summary>
    public static string User(Guid userId) => Convert.ToHexStringLower(SHA256.HashData(userId.ToByteArray()))[..12];
}
