using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using Microsoft.Extensions.Time.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class ScrobbleThresholdTests
{
    private readonly FakeTimeProvider _clock = TestTime.Clock();
    private readonly Guid _user = Guid.NewGuid();

    [Fact]
    public void DefaultsAreTheLastfmRule()
    {
        var config = new PluginConfiguration();
        Assert.Equal(ScrobbleThreshold.Lastfm, config.Threshold());
        Assert.Equal(new ScrobbleThreshold(50, 240, 30), config.Threshold());
    }

    [Theory]
    [InlineData(100, 50)]
    [InlineData(480, 240)]
    [InlineData(1000, 240)]
    [InlineData(31, 15.5)]
    public void DefaultRequiresHalfOrFourMinutesWhicheverIsEarlier(double duration, double required)
        => Assert.Equal(required, ScrobbleThreshold.Lastfm.RequiredSeconds(duration));

    [Theory]
    [InlineData(-5, 50)]
    [InlineData(0, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void PercentCannotBeLooserThanLastfmOrAboveTheWholeTrack(int value, int stored)
        => Assert.Equal(stored, new PluginConfiguration { ScrobblePercent = value }.ScrobblePercent);

    [Theory]
    [InlineData(int.MinValue, 240)]
    [InlineData(239, 240)]
    [InlineData(240, 240)]
    [InlineData(7200, 7200)]
    [InlineData(7201, 7200)]
    public void CeilingCannotBeLooserThanFourMinutes(int value, int stored)
        => Assert.Equal(stored, new PluginConfiguration { ScrobbleCeilingSeconds = value }.ScrobbleCeilingSeconds);

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(600, 600)]
    [InlineData(601, 600)]
    public void MinimumLengthCannotBeLooserThanThirtySeconds(int value, int stored)
        => Assert.Equal(stored, new PluginConfiguration { ScrobbleMinimumLengthSeconds = value }.ScrobbleMinimumLengthSeconds);

    [Fact]
    public void OutOfRangeValuesInSavedSettingsLoadClamped()
    {
        const string Saved = "<?xml version=\"1.0\"?><PluginConfiguration><ScrobblePercent>10</ScrobblePercent>"
            + "<ScrobbleCeilingSeconds>60</ScrobbleCeilingSeconds><ScrobbleMinimumLengthSeconds>5</ScrobbleMinimumLengthSeconds></PluginConfiguration>";
        using var reader = new StringReader(Saved);
        var loaded = Assert.IsType<PluginConfiguration>(new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader));
        Assert.Equal(ScrobbleThreshold.Lastfm, loaded.Threshold());
    }

    [Fact]
    public void StricterSettingsRoundtripXml()
    {
        var config = new PluginConfiguration { ScrobblePercent = 75, ScrobbleCeilingSeconds = 600, ScrobbleMinimumLengthSeconds = 90 };
        using var writer = new StringWriter();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var loaded = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal(new ScrobbleThreshold(75, 600, 90), loaded.Threshold());
    }

    [Fact]
    public void SettingsSavedBeforeTheThresholdExistedKeepTheLastfmRule()
    {
        const string Saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled></PluginConfiguration>";
        using var reader = new StringReader(Saved);
        var loaded = Assert.IsType<PluginConfiguration>(new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader));
        Assert.Equal(ScrobbleThreshold.Lastfm, loaded.Threshold());
    }

    [Fact]
    public void WithoutAThresholdTheTrackerKeepsTheLastfmRule()
        => Assert.Equal(50, HeardWhenScrobbled(100, null));

    [Theory]
    [InlineData(100, 50, 240, 50)]
    [InlineData(100, 80, 240, 80)]
    [InlineData(100, 100, 240, 100)]
    [InlineData(600, 50, 240, 240)]
    [InlineData(600, 50, 400, 300)]
    [InlineData(600, 90, 400, 400)]
    public void TrackerScrobblesOnceTheConfiguredShareIsHeard(int duration, int percent, int ceiling, int heard)
        => Assert.Equal(heard, HeardWhenScrobbled(duration, new ScrobbleThreshold(percent, ceiling, 30)));

    [Fact]
    public void StricterPercentDoesNotScrobbleAtHalf()
    {
        var threshold = new ScrobbleThreshold(80, 240, 30);
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0, 100, threshold));
        for (var second = 10; second <= 70; second += 10)
        {
            _clock.Advance(10);
            Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Progress, second, 100, threshold)).Scrobble);
        }
        _clock.Advance(9);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 79, 100, threshold)).Scrobble);
        Assert.Empty(tracker.Pending());
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(61, true)]
    public void TracksAtMostTheMinimumLengthAreIgnored(int duration, bool counted)
    {
        var tracker = new PlaybackTracker(_clock);
        var started = tracker.Observe(Snapshot(PlaybackSignal.Start, 0, duration, new ScrobbleThreshold(50, 240, 60))).NowPlaying;
        Assert.Equal(counted, started is not null);
    }

    [Fact]
    public void ThresholdChangedDuringAPlayAppliesFromTheNextTrack()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0, 100, ScrobbleThreshold.Lastfm));
        var stricter = new ScrobbleThreshold(100, 240, 30);
        EligibleListen? scrobble = null;
        for (var second = 10; second <= 50 && scrobble is null; second += 10)
        {
            _clock.Advance(10);
            scrobble = tracker.Observe(Snapshot(PlaybackSignal.Progress, second, 100, stricter)).Scrobble;
        }
        Assert.NotNull(scrobble);
    }

    private int? HeardWhenScrobbled(int duration, ScrobbleThreshold? threshold)
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0, duration, threshold));
        for (var second = 10; second <= duration; second += 10)
        {
            _clock.Advance(10);
            if (tracker.Observe(Snapshot(PlaybackSignal.Progress, second, duration, threshold)).Scrobble is not null) return second;
        }
        return null;
    }

    private PlaybackSnapshot Snapshot(PlaybackSignal signal, int position, int duration, ScrobbleThreshold? threshold)
        => new("session", _user, new MusicTrack(Guid.Empty, "Artist", "Track", "Album", null, duration), signal,
            TimeSpan.FromSeconds(position).Ticks, false, false, _clock.GetTimestamp(), _clock.GetUtcNow(), Threshold: threshold);
}
