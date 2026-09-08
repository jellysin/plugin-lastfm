using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Lastfm.Api;
using Jellyfin.Plugin.Lastfm.Models;
using Jellyfin.Plugin.Lastfm.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.ScheduledTasks;

public class ImportLastfmData : IScheduledTask, IDisposable
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ImportLastfmData> _logger;
    private readonly LastfmApiClient _apiClient;

    public ImportLastfmData(IHttpClientFactory httpClientFactory, IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager, ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = loggerFactory.CreateLogger<ImportLastfmData>();
        _apiClient = new LastfmApiClient(httpClientFactory, _logger);
    }

    public string Name => "Import Last.fm Loved Tracks";
    public string Category => "Last.fm";
    public string Key => "ImportLastfmData";
    public string Description => "Import favourite tracks for each user with a Last.fm account configured";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var users = _userManager.GetUsers()
            .Where(user => UserHelpers.GetUser(user) is { Options.SyncFavourites: true } configured && !string.IsNullOrWhiteSpace(configured.SessionKey))
            .ToArray();
        Plugin.Syncing = true;
        try
        {
            for (var index = 0; index < users.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncUser(users[index], cancellationToken).ConfigureAwait(false);
                progress.Report((index + 1.0) / users.Length * 100);
            }
            progress.Report(100);
        }
        finally
        {
            Plugin.Syncing = false;
        }
    }

    private async Task SyncUser(User user, CancellationToken cancellationToken)
    {
        var configured = UserHelpers.GetUser(user);
        if (configured is not { Options.SyncFavourites: true } || string.IsNullOrWhiteSpace(configured.SessionKey))
        {
            return;
        }
        var lovedTracks = await GetLovedTracks(configured, cancellationToken).ConfigureAwait(false);
        var byArtist = lovedTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Artist?.MusicBrainzId) && !string.IsNullOrWhiteSpace(track.Name))
            .GroupBy(track => track.Artist!.MusicBrainzId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (byArtist.Count == 0)
        {
            return;
        }
        var artists = _libraryManager.GetArtists(new InternalItemsQuery(user) { EnableTotalRecordCount = false })
            .Items.Select(item => item.Item1).OfType<MusicArtist>();
        var matched = 0;
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artistId = Helpers.GetMusicBrainzArtistId(artist);
            if (artistId is null || !byArtist.TryGetValue(artistId, out var tracks))
            {
                continue;
            }
            // GetTaggedItems does not set ArtistIds when IncludeItemTypes is supplied.
            var songs = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ArtistIds = [artist.Id],
                IncludeItemTypes = [BaseItemKind.Audio],
                EnableTotalRecordCount = false
            }).OfType<Audio>();
            foreach (var song in songs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(song.Name) || !tracks.Any(track => StringHelper.IsLike(song.Name, track.Name)))
                {
                    continue;
                }
                var userData = _userDataManager.GetUserData(user, song);
                if (userData is null || userData.IsFavorite)
                {
                    continue;
                }
                userData.IsFavorite = true;
                _userDataManager.SaveUserData(user, song, userData, UserDataSaveReason.UpdateUserRating, cancellationToken);
                matched++;
            }
        }
        _logger.LogInformation("Imported {MatchCount} Last.fm favourites for Jellyfin user {UserId}", matched, user.Id);
    }

    private async Task<List<LastfmLovedTrack>> GetLovedTracks(LastfmUser user, CancellationToken cancellationToken)
    {
        var tracks = new List<LastfmLovedTrack>();
        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _apiClient.GetLovedTracks(user, cancellationToken, page).ConfigureAwait(false);
            if (response is null || response.IsError())
            {
                throw new InvalidOperationException("Unable to retrieve Last.fm favourites. Please try again.");
            }
            var loved = response.LovedTracks;
            if (loved?.Tracks is not { Count: > 0 })
            {
                break;
            }
            tracks.AddRange(loved.Tracks);
            if (loved.Metadata is null || loved.Metadata.TotalPages <= page)
            {
                break;
            }
            if (loved.Metadata.Page != page || page >= 10000)
            {
                throw new InvalidOperationException("Last.fm returned invalid pagination information.");
            }
        }
        return tracks;
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
