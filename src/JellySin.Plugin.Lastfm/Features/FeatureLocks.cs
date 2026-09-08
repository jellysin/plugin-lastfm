using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Features;

public sealed class FeatureLocks : IDisposable
{
    private readonly AccountGatePool gates = new();

    public Task<IDisposable> EnterAsync(Guid userId, CancellationToken ct) => gates.AcquireAsync(userId, ct);

    public void Dispose() => gates.Dispose();
}
