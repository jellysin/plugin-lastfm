using System.Globalization;
using Jellyfin.Plugin.Lastfm.Utils;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

public sealed class HelpersTests
{
    [Theory]
    [InlineData("test", "098f6bcd4621d373cade4e832627b4f6")]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    public void Md5MatchesLastfmLowercaseVectors(string input, string expected)
    {
        Helpers.CreateMd5Hash(input).ShouldBe(expected);
    }

    [Fact]
    public void SignatureIgnoresTransportFieldsAndUsesOrdinalOrderingAcrossCultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var first = new Dictionary<string, string> { ["z"] = "last", ["ä"] = "umlaut", ["a"] = "first" };
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Helpers.AppendSignature(ref first);

            var second = new Dictionary<string, string>
            {
                ["a"] = "first",
                ["ä"] = "umlaut",
                ["z"] = "last",
                ["format"] = "json",
                ["callback"] = "ignored",
                ["api_sig"] = "old-signature"
            };
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Helpers.AppendSignature(ref second);
            second["api_sig"].ShouldBe(first["api_sig"]);
            second["api_sig"].ShouldMatch("^[a-f0-9]{32}$");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void QueryEncodingPreservesReservedCharactersAndUnicode()
    {
        var data = new Dictionary<string, string> { ["artist"] = "Björk & A+B=100%", ["track"] = "?/#", ["empty"] = "" };
        var parsed = TestHttpClientFactory.ParseForm(Helpers.DictionaryToQueryString(data));
        parsed["artist"].ShouldBe(data["artist"]);
        parsed["track"].ShouldBe(data["track"]);
        parsed.ShouldNotContainKey("empty");
    }

    [Fact]
    public void TimestampsMatchKnownUtcInstants()
    {
        Helpers.ToTimestamp(DateTime.UnixEpoch).ShouldBe(0);
        Helpers.ToTimestamp(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ShouldBe(1704067200);
        Helpers.FromTimestamp(1704067200).ToUniversalTime().ShouldBe(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
