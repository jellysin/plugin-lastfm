using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using JellySin.Plugin.Lastfm.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Configuration;

public enum ApplicationSource { None, Project, Override, Unreadable }

public sealed class ApplicationCredentialService(IStateStore store, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("JellySin.Lastfm.Application.v1");

    public static string Identity(ApplicationCredentials credentials) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiKey)));

    public async Task<ApplicationCredentials> GetAsync(CancellationToken cancellationToken)
    {
        var value = await store.ReadAsync<StoredCredentials>(Guid.Empty, "credentials", cancellationToken).ConfigureAwait(false);
        if (value is null) return ProjectCredentials();
        return Unprotect(value) ?? new ApplicationCredentials(string.Empty, string.Empty);
    }

    public Task SetAsync(ApplicationCredentials credentials, CancellationToken cancellationToken)
    {
        if (!credentials.IsConfigured || !credentials.ApiKey.All(Uri.IsHexDigit) || !credentials.Secret.All(Uri.IsHexDigit))
            throw new ArgumentException("Last.fm application credentials must be 32 hexadecimal characters.", nameof(credentials));
        return store.WriteAsync(Guid.Empty, "credentials", new StoredCredentials(_protector.Protect(credentials.ApiKey), _protector.Protect(credentials.Secret)), cancellationToken);
    }

    /// <summary>Where the application in use comes from: an administrator's override, the build, or nowhere.</summary>
    public async Task<ApplicationSource> GetSourceAsync(CancellationToken cancellationToken)
    {
        if (await store.ReadAsync<StoredCredentials>(Guid.Empty, "credentials", cancellationToken).ConfigureAwait(false) is { } value)
            return Unprotect(value) is null ? ApplicationSource.Unreadable : ApplicationSource.Override;
        return ProjectCredentials().IsConfigured ? ApplicationSource.Project : ApplicationSource.None;
    }

    /// <summary>Removes the override. Accounts connected through it need a new sign-in, as after any application change.</summary>
    public Task DeleteAsync(CancellationToken cancellationToken) => store.DeleteAsync(Guid.Empty, "credentials", cancellationToken);

    public sealed record StoredCredentials(string Key, string Secret);

    /// <summary>
    /// An override this server's key ring cannot read, as after a restore without jellysin-lastfm/keys, is not used and
    /// not replaced by the build's application: nothing reaches Last.fm until an administrator enters it again.
    /// </summary>
    private ApplicationCredentials? Unprotect(StoredCredentials value)
    {
        try { return new ApplicationCredentials(_protector.Unprotect(value.Key), _protector.Unprotect(value.Secret)); }
        catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
    }

    private static ApplicationCredentials ProjectCredentials()
    {
        var metadata = typeof(ApplicationCredentialService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return new ApplicationCredentials(metadata.FirstOrDefault(item => item.Key == "JellySinLastfmApiKey")?.Value ?? string.Empty,
            metadata.FirstOrDefault(item => item.Key == "JellySinLastfmSharedSecret")?.Value ?? string.Empty);
    }
}
