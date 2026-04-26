using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;

namespace PrimeDictate;

internal sealed record WhisperModelOption(
    string Id,
    string DisplayName,
    string FileName,
    string Description,
    long ApproximateBytes,
    bool Recommended = false)
{
    public string DownloadUri => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{this.FileName}";

    public string ApproximateSizeLabel => WhisperModelCatalog.FormatCatalogSize(this.ApproximateBytes);
}

internal readonly record struct WhisperModelDownloadProgress(long BytesDownloaded, long? TotalBytes)
{
    public double? Percentage => this.TotalBytes is > 0
        ? Math.Min(100d, this.BytesDownloaded * 100d / this.TotalBytes.Value)
        : null;

    public string ProgressLabel => this.TotalBytes is > 0
        ? $"{WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)} / {WhisperModelCatalog.FormatByteSize(this.TotalBytes.Value)}"
        : WhisperModelCatalog.FormatByteSize(this.BytesDownloaded);
}

internal static class WhisperModelCatalog
{
    public static IReadOnlyList<WhisperModelOption> Options { get; } =
    [
        new(
            Id: "tiny",
            DisplayName: "Tiny",
            FileName: "ggml-tiny.bin",
            Description: "The lightest multilingual model. Best when you want the fastest download and quickest turnaround.",
            ApproximateBytes: 78L * 1024 * 1024),
        new(
            Id: "base",
            DisplayName: "Base",
            FileName: "ggml-base.bin",
            Description: "A good entry-level multilingual model for short edits and lighter Windows laptops.",
            ApproximateBytes: 148L * 1024 * 1024),
        new(
            Id: "small",
            DisplayName: "Small",
            FileName: "ggml-small.bin",
            Description: "Balanced speed and quality for everyday dictation into editors, chat, and documentation tools.",
            ApproximateBytes: 489L * 1024 * 1024,
            Recommended: true),
        new(
            Id: "medium",
            DisplayName: "Medium",
            FileName: "ggml-medium.bin",
            Description: "Higher accuracy for longer dictation sessions if your PC can spare more RAM and CPU time.",
            ApproximateBytes: 1_600L * 1024 * 1024),
        new(
            Id: "large-v3-turbo",
            DisplayName: "Large v3 Turbo",
            FileName: "ggml-large-v3-turbo.bin",
            Description: "PrimeDictate's highest-accuracy multilingual option. Recommended when you want the best text quality on a modern Windows PC.",
            ApproximateBytes: 1_620L * 1024 * 1024,
            Recommended: true)
    ];

    internal static bool TryGetById(string? id, [NotNullWhen(true)] out WhisperModelOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    internal static WhisperModelOption? TryGetByPath(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(modelPath);
        return Options.FirstOrDefault(candidate => string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryResolveInstalledPath(
        WhisperModelOption option,
        [NotNullWhen(true)] out string? installedPath)
    {
        return ModelFileLocator.TryResolveKnownModel(option.FileName, out installedPath);
    }

    internal static string FormatByteSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var suffixIndex = 0;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        var format = suffixIndex == 0 ? "0" : "0.#";
        return string.Format(CultureInfo.InvariantCulture, "{0:" + format + "} {1}", size, suffixes[suffixIndex]);
    }

    internal static string FormatCatalogSize(long bytes)
    {
        var megabytes = bytes / (1024d * 1024d);
        return string.Format(CultureInfo.InvariantCulture, "{0:N0} MB", megabytes);
    }
}

internal static class WhisperModelDownloader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<string> DownloadAsync(
        WhisperModelOption option,
        IProgress<WhisperModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destinationPath = ModelFileLocator.GetManagedModelPath(option.FileName);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Managed model directory is unavailable.");
        Directory.CreateDirectory(destinationDirectory);

        if (File.Exists(destinationPath))
        {
            var existingLength = new FileInfo(destinationPath).Length;
            progress?.Report(new WhisperModelDownloadProgress(existingLength, existingLength));
            return destinationPath;
        }

        var temporaryPath = destinationPath + ".download";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        var completed = false;
        long bytesDownloaded = 0;
        long? totalBytes = null;
        try
        {
            using var response = await HttpClient.GetAsync(
                    option.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            totalBytes = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        bytesDownloaded += read;
                        progress?.Report(new WhisperModelDownloadProgress(bytesDownloaded, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The temp file must be closed before Windows can rename it into place.
            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(new WhisperModelDownloadProgress(bytesDownloaded, bytesDownloaded));
            completed = true;
            return destinationPath;
        }
        finally
        {
            if (!completed && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
