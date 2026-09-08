using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Configuration;

public sealed class AccountLifecycle(AccountService accounts, ILogger<AccountLifecycle> logger) :
    IEventConsumer<UserDeletedEventArgs>, IEventConsumer<UserUpdatedEventArgs>, IEventConsumer<UserLockedOutEventArgs>
{
    public async Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        accounts.SuspendHostUser(eventArgs.Argument.Id);
        try { await accounts.DisconnectAsync(eventArgs.Argument.Id, deadline.Token).ConfigureAwait(false); }
        catch (Exception error) { logger.LogWarning("Removed user's Last.fm storage cleanup needs retry ({ErrorType}).", error.GetType().Name); }
    }

    public Task OnEvent(UserUpdatedEventArgs eventArgs)
    {
        if (eventArgs.Argument.HasPermission(PermissionKind.IsDisabled)) accounts.SuspendHostUser(eventArgs.Argument.Id);
        return Task.CompletedTask;
    }

    public Task OnEvent(UserLockedOutEventArgs eventArgs)
    {
        accounts.SuspendHostUser(eventArgs.Argument.Id);
        return Task.CompletedTask;
    }
}
