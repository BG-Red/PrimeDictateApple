using System.IO;

namespace PrimeDictate;

internal static class ModelStorage
{
    private static readonly string ManagedModelsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrimeDictate",
        "models");

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;
}
