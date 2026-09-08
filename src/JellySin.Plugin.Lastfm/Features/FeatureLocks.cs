namespace JellySin.Plugin.Lastfm.Features;

public sealed class FeatureLocks : IDisposable
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> EnterAsync(Guid userId, CancellationToken ct)
    {
        var gate = gates[(uint)userId.GetHashCode() % (uint)gates.Length];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    public void Dispose()
    {
        foreach (var gate in gates) gate.Dispose();
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? current = gate;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
