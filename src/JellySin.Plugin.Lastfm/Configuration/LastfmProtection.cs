using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Configuration;

public sealed class LastfmProtection
{
    public LastfmProtection(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Provider = DataProtectionProvider.Create(new DirectoryInfo(directory), options => options.SetApplicationName("JellySin.Lastfm.v1"));
    }

    public IDataProtectionProvider Provider { get; }
}
