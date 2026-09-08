using System.Reflection;
using JellySin.Plugin.Lastfm.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Configuration;

public sealed class ApplicationCredentialService(IStateStore store, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("JellySin.Lastfm.Application.v1");

    public async Task<ApplicationCredentials> GetAsync(CancellationToken cancellationToken)
    {
        var value = await store.ReadAsync<StoredCredentials>(Guid.Empty, "credentials", cancellationToken).ConfigureAwait(false);
        if (value is null) return ProjectCredentials();
        return new ApplicationCredentials(_protector.Unprotect(value.Key), _protector.Unprotect(value.Secret));
    }

    public Task SetAsync(ApplicationCredentials credentials, CancellationToken cancellationToken)
    {
        if (!credentials.IsConfigured || !credentials.ApiKey.All(Uri.IsHexDigit) || !credentials.Secret.All(Uri.IsHexDigit))
            throw new ArgumentException("Last.fm application credentials must be 32 hexadecimal characters.", nameof(credentials));
        return store.WriteAsync(Guid.Empty, "credentials", new StoredCredentials(_protector.Protect(credentials.ApiKey), _protector.Protect(credentials.Secret)), cancellationToken);
    }

    public sealed record StoredCredentials(string Key, string Secret);

    private static ApplicationCredentials ProjectCredentials()
    {
        var metadata = typeof(ApplicationCredentialService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return new ApplicationCredentials(metadata.FirstOrDefault(item => item.Key == "JellySinLastfmApiKey")?.Value ?? string.Empty,
            metadata.FirstOrDefault(item => item.Key == "JellySinLastfmSharedSecret")?.Value ?? string.Empty);
    }
}
