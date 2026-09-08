using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class RequestValidationTests
{
    [Theory]
    [InlineData("application", true)]
    [InlineData("application", false)]
    [InlineData("quick-connect", true)]
    [InlineData("quick-connect", false)]
    [InlineData("playlist", true)]
    [InlineData("playlist", false)]
    public void AspNetValidatesRecordConstructorMetadataWithoutThrowing(string requestType, bool valid)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        object request = requestType switch
        {
            "application" => new ApplicationInput(valid ? new string('a', 32) : "invalid", new string('b', 32)),
            "quick-connect" => new QuickConnectInput(valid ? new string('x', 32) : "short"),
            _ => new PlaylistInput(Guid.Empty, valid ? "My music" : string.Empty, PlaylistSource.Loved, Limit: valid ? 50 : 0)
        };
        // This is MVC's real validator, including record-bound constructor metadata.
        // Attributes on generated record properties instead of constructor parameters
        // throw before ordinary field validation and previously broke live requests.
        provider.GetRequiredService<IObjectModelValidator>().Validate(action, validationState: null, prefix: string.Empty, model: request);
        Assert.Equal(valid, action.ModelState.IsValid);
        if (!valid) Assert.NotEmpty(action.ModelState.Values.SelectMany(value => value.Errors));
    }

    [Theory]
    [InlineData(1, 200, 200)]
    [InlineData(2, 200, 200)]
    [InlineData(3, 1, 101)]
    [InlineData(999, 1, 101)]
    [InlineData(-1, 200, 200)]
    public void HistoryPreviewPaginatesBothCollectionsWithoutLosingTotals(int requestedPage, int expectedMatches, int expectedUnmatched)
    {
        var track = new JellySin.Plugin.Lastfm.Features.MusicTrack("Artist", "Track");
        var entries = Enumerable.Range(0, 401).Select(index => new HistoryImportEntry(Guid.NewGuid(), track with { Title = "Matched " + index }, 1, 2, null, null)).ToArray();
        var unmatched = Enumerable.Range(0, 501).Select(index => new MusicMatch(track with { Title = "Missing " + index }, null, "missing")).ToArray();
        var preview = new HistoryImportPreview(Guid.NewGuid(), new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), entries, unmatched, false, 4, 1234);
        var page = HistoryPreviewView.From(preview, requestedPage);
        Assert.Equal(expectedMatches, page.Entries.Count);
        Assert.Equal(expectedUnmatched, page.Unmatched.Count);
        Assert.Equal(401, page.MatchedCount);
        Assert.Equal(501, page.UnmatchedCount);
        Assert.Equal(3, page.Pages);
        Assert.Equal(Math.Clamp(requestedPage, 1, 3), page.Page);
        Assert.Equal(preview.Id, page.Id);
        Assert.Equal(4, page.NextPage);
        Assert.Equal(1234, page.Until);
        Assert.False(page.Complete);
    }
}
