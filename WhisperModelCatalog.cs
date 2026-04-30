using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;

namespace PrimeDictate;

internal sealed record WhisperModelOption(
    string Id,
    string DisplayName,
    string InstallDirectoryName,
    string ArchiveFileName,
    string ModelStem,
    string Description,
    long ApproximateBytes,
    bool Recommended = false)
{
    public string DownloadUri =>
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{this.ArchiveFileName}";

    public string ApproximateSizeLabel => WhisperModelCatalog.FormatCatalogSize(this.ApproximateBytes);
}

internal readonly record struct WhisperModelFiles(string Encoder, string Decoder, string Tokens);

internal readonly record struct WhisperModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes)
{
    public double? Percentage => this.Stage == "download" && this.TotalBytes is > 0
        ? Math.Min(100d, this.BytesDownloaded * 100d / this.TotalBytes.Value)
        : null;

    public string ProgressLabel => this.Stage switch
    {
        "extract" => "Extracting model files",
        "ready" => "Installed",
        _ => this.TotalBytes is > 0
            ? $"{WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)} / {WhisperModelCatalog.FormatByteSize(this.TotalBytes.Value)}"
            : WhisperModelCatalog.FormatByteSize(this.BytesDownloaded)
    };
}

internal static class WhisperModelCatalog
{
    private static readonly string ManagedModelsDirectory = Path.Combine(
        ModelStorage.GetManagedModelsDirectory(),
        "whisper");

    public static IReadOnlyList<WhisperModelOption> Options { get; } =
    [
        new(
            Id: "tiny.en",
            DisplayName: "Tiny English ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-tiny.en",
            ArchiveFileName: "sherpa-onnx-whisper-tiny.en.tar.bz2",
            ModelStem: "tiny.en",
            Description: "The lightest English Whisper ONNX model. Best for fast local dictation and slower laptops.",
            ApproximateBytes: 118_071_777,
            Recommended: true),
        new(
            Id: "base.en",
            DisplayName: "Base English ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-base.en",
            ArchiveFileName: "sherpa-onnx-whisper-base.en.tar.bz2",
            ModelStem: "base.en",
            Description: "A good English Whisper ONNX balance for everyday dictation on CPU.",
            ApproximateBytes: 208_576_005,
            Recommended: true),
        new(
            Id: "distil-small.en",
            DisplayName: "Distil Small English ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-distil-small.en",
            ArchiveFileName: "sherpa-onnx-whisper-distil-small.en.tar.bz2",
            ModelStem: "distil-small.en",
            Description: "A faster distilled English Whisper ONNX model for longer local dictation sessions.",
            ApproximateBytes: 453_710_017),
        new(
            Id: "small.en",
            DisplayName: "Small English ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-small.en",
            ArchiveFileName: "sherpa-onnx-whisper-small.en.tar.bz2",
            ModelStem: "small.en",
            Description: "Higher English accuracy with a larger local ONNX download and more compute cost.",
            ApproximateBytes: 635_693_775),
        new(
            Id: "tiny",
            DisplayName: "Tiny Multilingual ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-tiny",
            ArchiveFileName: "sherpa-onnx-whisper-tiny.tar.bz2",
            ModelStem: "tiny",
            Description: "The lightest multilingual Whisper ONNX model when you need non-English dictation.",
            ApproximateBytes: 116_204_861),
        new(
            Id: "base",
            DisplayName: "Base Multilingual ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-base",
            ArchiveFileName: "sherpa-onnx-whisper-base.tar.bz2",
            ModelStem: "base",
            Description: "A balanced multilingual Whisper ONNX model for local dictation.",
            ApproximateBytes: 207_557_382),
        new(
            Id: "small",
            DisplayName: "Small Multilingual ONNX",
            InstallDirectoryName: "sherpa-onnx-whisper-small",
            ArchiveFileName: "sherpa-onnx-whisper-small.tar.bz2",
            ModelStem: "small",
            Description: "Higher multilingual accuracy with a larger ONNX model and more compute cost.",
            ApproximateBytes: 639_387_718)
    ];

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    internal static bool TryGetById(string? id, [NotNullWhen(true)] out WhisperModelOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    internal static WhisperModelOption? TryGetByPath(string? modelPath)
    {
        if (!TryResolveDirectory(modelPath, out var resolvedPath))
        {
            return null;
        }

        var directoryName = Path.GetFileName(resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Options.FirstOrDefault(candidate =>
            string.Equals(candidate.InstallDirectoryName, directoryName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryResolveDirectory(string? path, [NotNullWhen(true)] out string? resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            var candidate = Path.GetFullPath(path);
            if (IsValidModelDirectory(candidate))
            {
                resolvedPath = candidate;
                return true;
            }
        }

        resolvedPath = null;
        return false;
    }

    internal static bool TryResolveInstalledPath(
        WhisperModelOption option,
        [NotNullWhen(true)] out string? installedPath)
    {
        foreach (var root in EnumerateInstallRoots())
        {
            var candidate = Path.Combine(root, option.InstallDirectoryName);
            if (IsValidModelDirectory(candidate))
            {
                installedPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        installedPath = null;
        return false;
    }

    internal static bool IsValidModelDirectory(string? directoryPath)
    {
        return TryResolveModelFiles(directoryPath, out _);
    }

    internal static bool TryResolveModelFiles(
        string? directoryPath,
        [NotNullWhen(true)] out WhisperModelFiles? modelFiles)
    {
        modelFiles = null;
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        var encoder = FindSingleModelFile(directoryPath, "*-encoder.int8.onnx")
            ?? FindSingleModelFile(directoryPath, "*-encoder.onnx");
        var decoder = FindSingleModelFile(directoryPath, "*-decoder.int8.onnx")
            ?? FindSingleModelFile(directoryPath, "*-decoder.onnx");
        var tokens = FindSingleModelFile(directoryPath, "*-tokens.txt");

        if (encoder is null || decoder is null || tokens is null)
        {
            return false;
        }

        modelFiles = new WhisperModelFiles(encoder, decoder, tokens);
        return true;
    }

    internal static IReadOnlyList<string> GetRequiredFiles() =>
    [
        "*-encoder.int8.onnx or *-encoder.onnx",
        "*-decoder.int8.onnx or *-decoder.onnx",
        "*-tokens.txt"
    ];

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

    private static string? FindSingleModelFile(string directoryPath, string pattern)
    {
        var matches = Directory.GetFiles(directoryPath, pattern, SearchOption.TopDirectoryOnly);
        return matches.Length == 1 ? Path.GetFullPath(matches[0]) : null;
    }

    private static IEnumerable<string> EnumerateInstallRoots()
    {
        yield return ManagedModelsDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "models", "whisper");

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            yield return Path.Combine(dir.FullName, "models", "whisper");
            dir = dir.Parent;
        }
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
        var modelsDirectory = WhisperModelCatalog.GetManagedModelsDirectory();
        var destinationPath = Path.Combine(modelsDirectory, option.InstallDirectoryName);
        Directory.CreateDirectory(modelsDirectory);

        if (WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            progress?.Report(new WhisperModelDownloadProgress("ready", 1, 1));
            return installedPath;
        }

        var archivePath = Path.Combine(modelsDirectory, option.ArchiveFileName + ".download");
        var extractPath = Path.Combine(modelsDirectory, option.InstallDirectoryName + ".extract");
        var completed = false;

        try
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            if (Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }

            long bytesDownloaded = 0;
            long? totalBytes = null;

            using (var response = await HttpClient.GetAsync(
                       option.DownloadUri,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                totalBytes = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new FileStream(
                    archivePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);

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
                        progress?.Report(new WhisperModelDownloadProgress("download", bytesDownloaded, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new WhisperModelDownloadProgress("extract", bytesDownloaded, totalBytes));
            await ExtractArchiveAsync(archivePath, extractPath, cancellationToken).ConfigureAwait(false);

            var extractedDirectory = Path.Combine(extractPath, option.InstallDirectoryName);
            if (!WhisperModelCatalog.IsValidModelDirectory(extractedDirectory))
            {
                throw new InvalidOperationException(
                    $"The extracted Whisper ONNX model is incomplete. Expected {string.Join(", ", WhisperModelCatalog.GetRequiredFiles())}.");
            }

            Directory.Move(extractedDirectory, destinationPath);
            progress?.Report(new WhisperModelDownloadProgress("ready", bytesDownloaded, bytesDownloaded));
            completed = true;
            return destinationPath;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, recursive: true);
            }

            if (!completed && Directory.Exists(destinationPath) && !WhisperModelCatalog.IsValidModelDirectory(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string extractPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(extractPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xjf \"{archivePath}\" -C \"{extractPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start tar.exe to extract the Whisper ONNX model archive.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Whisper ONNX archive extraction failed: {detail.Trim()}");
        }
    }
}
