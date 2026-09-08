using System.ComponentModel.DataAnnotations;
using JellySin.Plugin.Lastfm.Features;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[Route("JellySin/Lastfm/Me/Favourites")]
public sealed class FavouritesController(FavouritesService favourites) : PrivateController
{
    [HttpGet]
    public async Task<FavouriteReviewView> Get(CancellationToken cancellationToken, [FromQuery, Range(1, 100)] int page = 1) =>
        FavouriteReviewView.From(await favourites.GetReviewAsync(CallerId, cancellationToken).ConfigureAwait(false), page);

    [HttpPut]
    public async Task<FavouriteReviewView> Enable(ToggleRequest request, CancellationToken cancellationToken) =>
        FavouriteReviewView.From(await favourites.SetEnabledAsync(CallerId, request.Enabled, cancellationToken).ConfigureAwait(false), 1);

    [HttpPost("Sync")]
    public async Task<FavouriteReviewView> Sync(CancellationToken cancellationToken) =>
        FavouriteReviewView.From(await favourites.SyncAsync(CallerId, cancellationToken).ConfigureAwait(false), 1);

    [HttpPost("Review")]
    public async Task<FavouriteReviewView> Review(RemovalDecision request, CancellationToken cancellationToken) =>
        FavouriteReviewView.From(await favourites.ReviewRemovalAsync(CallerId, request.ReviewId, request.Apply, cancellationToken).ConfigureAwait(false), 1);
}

public sealed record RemovalDecision(Guid ReviewId, bool Apply);

public sealed record FavouriteReviewView(bool Enabled, IReadOnlyList<FavouriteRemoval> Pending, DateTimeOffset? LastSync,
    string? Status, int Total, int Page, int Pages)
{
    public static FavouriteReviewView From(FavouriteReview review, int page)
    {
        var pages = Math.Max(1, (review.Pending.Count + 199) / 200);
        page = Math.Clamp(page, 1, pages);
        return new(review.Enabled, review.Pending.Skip((page - 1) * 200).Take(200).ToArray(), review.LastSync,
            review.Status, review.Pending.Count, page, pages);
    }
}
