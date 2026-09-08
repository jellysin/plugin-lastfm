using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Features;

internal static class AccountOperations
{
    public static CancellationTokenSource LinkOperation(this AccountService accounts, Guid userId, CancellationToken ct)
    {
        if (Plugin.Instance?.Configuration.Enabled == false) throw new InvalidOperationException("The administrator has disabled the Last.fm integration.");
        return CancellationTokenSource.CreateLinkedTokenSource(ct, accounts.GetOperationToken(userId));
    }
}
