using System.ComponentModel.DataAnnotations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace JellySin.NativeProbe;

/// <summary>Exercises real host services, with no Last.fm client or account calls.</summary>
[Route("JellySin/NativeProbe")]
[ApiController]
[Authorize]
[TypeFilter(typeof(FixtureAccessFilter))]
[RequestSizeLimit(16_384)]
public sealed class ProbeController(IUserDataManager data, IUserManager users, ILibraryManager library, IServiceProvider services) : ControllerBase
{
    private static readonly DateTime ImportedDate = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private const string WrapperType = "JellySin.Plugin.Lastfm.Features.CoordinatedUserData";

    [HttpGet]
    public object Status()
    {
        return new { Wrapped = data.GetType().FullName == WrapperType, ManagerType = data.GetType().FullName };
    }

    [HttpPost("History")]
    public object History(FixtureItem input, CancellationToken ct)
    {
        var (user, item) = Target(input);
        var initial = Read(user, item);
        initial.PlayCount = 5;
        initial.LastPlayedDate = ImportedDate.AddYears(-4);
        data.SaveUserData(user, item, initial, UserDataSaveReason.UpdateUserData, ct);
        item = library.GetItemById<Audio>(item.Id, user.Id)!;
        var stale = Read(user, item);
        InvokeWriter("ApplyHistoryFloor", user.Id, item.Id, 10, ImportedDate, ct);
        var afterImport = Read(user, item);
        var sharedSnapshot = ReferenceEquals(stale, afterImport);
        var importedCount = afterImport.PlayCount;
        var freshItem = library.GetItemById<Audio>(item.Id, user.Id)!;
        var sameItem = ReferenceEquals(item, freshItem);
        var freshItemImportedCount = Read(user, freshItem).PlayCount;
        stale.PlayCount = 6;
        stale.LastPlayedDate = ImportedDate.AddYears(-3);
        data.SaveUserData(user, item, stale, UserDataSaveReason.UpdateUserData, ct);
        var afterStale = Read(user, library.GetItemById<Audio>(item.Id, user.Id)!);
        var staleCount = afterStale.PlayCount;
        var staleDatePreserved = afterStale.LastPlayedDate == ImportedDate;
        var resetItem = library.GetItemById<Audio>(item.Id, user.Id)!;
        var fresh = Read(user, resetItem);
        fresh.PlayCount = 0;
        fresh.Played = false;
        fresh.LastPlayedDate = null;
        data.SaveUserData(user, resetItem, fresh, UserDataSaveReason.UpdateUserData, ct);
        var afterReset = Read(user, library.GetItemById<Audio>(item.Id, user.Id)!);
        var freshResetCount = afterReset.PlayCount;
        var freshResetDateCleared = afterReset.LastPlayedDate is null;
        InvokeWriter("ApplyHistoryFloor", user.Id, item.Id, 10, ImportedDate, ct);
        return new
        {
            SharedSnapshot = sharedSnapshot,
            ImportedCount = importedCount,
            SameItem = sameItem,
            FreshItemImportedCount = freshItemImportedCount,
            StaleCount = staleCount,
            StaleDatePreserved = staleDatePreserved,
            FreshResetCount = freshResetCount,
            FreshResetDateCleared = freshResetDateCleared,
            RestoredFloor = Read(user, library.GetItemById<Audio>(item.Id, user.Id)!).PlayCount
        };
    }

    [HttpPost("Favourite/Prepare")]
    public object PrepareFavourite(FixtureItem input, CancellationToken ct)
    {
        var (user, item) = Target(input);
        InvokeWriter("SetFavourite", user.Id, item.Id, true, ct);
        return new { Revision = InvokeWrapper("FavouriteRevision", user, item), Favourite = ReadLatest(user, item.Id).IsFavorite };
    }

    [HttpPost("Favourite/Review")]
    public object ReviewFavourite(FavouriteReview input, CancellationToken ct)
    {
        var (user, item) = Target(new(input.UserId, input.ItemId));
        var accepted = InvokeWrapper("TrySetFavourite", user, item, false, input.Revision, ct);
        return new { Accepted = accepted, Favourite = ReadLatest(user, item.Id).IsFavorite, Revision = InvokeWrapper("FavouriteRevision", user, item) };
    }

    private (User User, Audio Item) Target(FixtureItem input)
    {
        var user = users.GetUserById(input.UserId) ?? throw new KeyNotFoundException("Fixture user missing.");
        var item = library.GetItemById<Audio>(input.ItemId, user.Id) ?? throw new KeyNotFoundException("Fixture track missing.");
        if (!user.Username.StartsWith("JellySin Probe ", StringComparison.Ordinal)
            || !item.Path.StartsWith("/media/Public/", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        if (data.GetType().FullName != WrapperType) throw new InvalidOperationException("Native userdata decorator was not registered.");
        return (user, item);
    }

    private object? InvokeWrapper(string method, params object[] arguments)
        => data.GetType().GetMethod(method)!.Invoke(data, arguments);

    private void InvokeWriter(string method, params object[] arguments)
    {
        // Jellyfin isolates plugin assemblies: obtain the actual registered service type from the injected decorator.
        var contract = data.GetType().Assembly.GetType("JellySin.Plugin.Lastfm.Features.IMusicWriter", throwOnError: true)!;
        var writer = services.GetRequiredService(contract);
        contract.GetMethod(method)!.Invoke(writer, arguments);
    }

    private UserItemData Read(User user, BaseItem item) => data.GetUserData(user, item)
        ?? throw new InvalidOperationException("Native userdata missing.");

    private UserItemData ReadLatest(User user, Guid itemId) => Read(user, library.GetItemById<Audio>(itemId, user.Id)!);
}

public sealed record FixtureItem(Guid UserId, Guid ItemId);
public sealed record FavouriteReview(Guid UserId, Guid ItemId, [Required, MaxLength(128)] string Revision);
