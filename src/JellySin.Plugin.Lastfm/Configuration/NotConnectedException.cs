namespace JellySin.Plugin.Lastfm.Configuration;

/// <summary>The caller has no Last.fm account connected. Its own type lets the API tell this apart from other conflicts.</summary>
public sealed class NotConnectedException() : InvalidOperationException("Connect a Last.fm account first.");
