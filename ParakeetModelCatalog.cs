using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;

namespace PrimeDictate;

internal sealed record ParakeetModelOption(
    string Id,
    string DisplayName,
    string InstallDirectoryName,
    string ArchiveFileName,
    string Description,
    long ApproximateBytes,
    bool Recommended = false)
{
    public string DownloadUri =>
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{this.ArchiveFileName}";

    public string ApproximateSizeLabel => WhisperModelCatalog.FormatCatalogSize(this.ApproximateBytes);
}

internal readonly record struct ParakeetModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes)
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

internal static class ParakeetModelCatalog
{
    private static readonly string ManagedModelsDirectory = Path.Combine(
        ModelStorage.GetManagedModelsDirectory(),
        "parakeet");

    public static IReadOnlyList<ParakeetModelOption> Options { get; } =
    [
        new(
            Id: "parakeet-tdt-0.6b-v2",
            DisplayName: "Parakeet TDT 0.6B v2",
            InstallDirectoryName: "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
            ArchiveFileName: "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2",
            Description: "The earlier 0.6B Parakeet TDT release. Useful when you want to compare v2 against the newer v3 build in the same local workflow.",
            ApproximateBytes: 690L * 1024 * 1024),
        new(
            Id: "parakeet-tdt-0.6b-v3",
            DisplayName: "Parakeet TDT 0.6B v3",
            InstallDirectoryName: "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
            ArchiveFileName: "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
            Description: "A newer sherpa-onnx Parakeet model for fast, fully local English transcription on Windows. Good for testing a non-Whisper backend in the same PrimeDictate workflow.",
            ApproximateBytes: 710L * 1024 * 1024,
            Recommended: true)
    ];

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    internal static bool TryGetById(string? id, [NotNullWhen(true)] out ParakeetModelOption? option)
    {
        option = Options.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return option is not null;
    }

    internal static ParakeetModelOption? TryGetByPath(string? modelPath)
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
        ParakeetModelOption option,
        [NotNullWhen(true)] out string? installedPath)
    {
        var candidate = Path.Combine(ManagedModelsDirectory, option.InstallDirectoryName);
        if (IsValidModelDirectory(candidate))
        {
            installedPath = Path.GetFullPath(candidate);
            return true;
        }

        installedPath = null;
        return false;
    }

    internal static bool IsValidModelDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        foreach (var requiredFile in GetRequiredFiles())
        {
            if (!File.Exists(Path.Combine(directoryPath, requiredFile)))
            {
                return false;
            }
        }

        return true;
    }

    internal static IReadOnlyList<string> GetRequiredFiles() =>
    [
        "encoder.int8.onnx",
        "decoder.int8.onnx",
        "joiner.int8.onnx",
        "tokens.txt"
    ];
}

internal static class ParakeetModelDownloader
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<string> DownloadAsync(
        ParakeetModelOption option,
        IProgress<ParakeetModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modelsDirectory = ParakeetModelCatalog.GetManagedModelsDirectory();
        var destinationPath = Path.Combine(modelsDirectory, option.InstallDirectoryName);
        Directory.CreateDirectory(modelsDirectory);

        if (ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
        {
            progress?.Report(new ParakeetModelDownloadProgress("ready", 1, 1));
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
                        progress?.Report(new ParakeetModelDownloadProgress("download", bytesDownloaded, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ParakeetModelDownloadProgress("extract", bytesDownloaded, totalBytes));
            await ExtractArchiveAsync(archivePath, extractPath, cancellationToken).ConfigureAwait(false);

            var extractedDirectory = Path.Combine(extractPath, option.InstallDirectoryName);
            if (!ParakeetModelCatalog.IsValidModelDirectory(extractedDirectory))
            {
                throw new InvalidOperationException(
                    $"The extracted Parakeet model is incomplete. Expected {string.Join(", ", ParakeetModelCatalog.GetRequiredFiles())}.");
            }

            Directory.Move(extractedDirectory, destinationPath);
            progress?.Report(new ParakeetModelDownloadProgress("ready", bytesDownloaded, bytesDownloaded));
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

            if (!completed && Directory.Exists(destinationPath) && !ParakeetModelCatalog.IsValidModelDirectory(destinationPath))
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
            ?? throw new InvalidOperationException("Unable to start tar.exe to extract the Parakeet model archive.");
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
            throw new InvalidOperationException($"Parakeet archive extraction failed: {detail.Trim()}");
        }
    }
}
