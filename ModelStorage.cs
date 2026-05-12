using System.IO;

namespace PrimeDictate;

internal static class ModelStorage
{
    private static readonly string ManagedModelsDirectory = Path.Combine(GetApplicationDataDirectory(), "models");

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    private static string GetApplicationDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PrimeDictate");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "PrimeDictate");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(home, ".local", "share", "PrimeDictate")
            : Path.Combine(xdgDataHome, "PrimeDictate");
    }
}
