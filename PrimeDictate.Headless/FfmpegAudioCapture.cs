using System.ComponentModel;
using System.Diagnostics;

namespace PrimeDictate;

internal sealed class FfmpegAudioCapture
{
    private readonly string ffmpegPath;
    private Process? process;
    private string? outputPath;

    public FfmpegAudioCapture(string ffmpegPath)
    {
        this.ffmpegPath = ffmpegPath;
    }

    public bool IsRecording => this.process is { HasExited: false };

    public static string DefaultDeviceSpec
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return ":0";
            }

            if (OperatingSystem.IsLinux())
            {
                return "default";
            }

            return "default";
        }
    }

    public async Task PrintDevicesAsync(CancellationToken cancellationToken)
    {
        var args = OperatingSystem.IsMacOS()
            ? "-hide_banner -f avfoundation -list_devices true -i \"\""
            : OperatingSystem.IsLinux()
                ? "-hide_banner -sources pulse -f lavfi -i anullsrc -t 0.1 -f null -"
                : "-hide_banner -list_devices true -f dshow -i dummy";

        using var listProcess = CreateProcess(args, redirectStandardInput: false);
        listProcess.Start();
        var stderr = await listProcess.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await listProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Console.Error.WriteLine(stderr);
    }

    public string Start(string deviceSpec)
    {
        if (this.IsRecording)
        {
            throw new InvalidOperationException("Recording is already active.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"primedictate-{Guid.NewGuid():N}.wav");
        var args = BuildCaptureArguments(deviceSpec, tempPath);
        var nextProcess = CreateProcess(args, redirectStandardInput: true);

        try
        {
            nextProcess.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Unable to start ffmpeg at '{this.ffmpegPath}'. Install ffmpeg, or pass --ffmpeg-path <path>.",
                ex);
        }

        this.process = nextProcess;
        this.outputPath = tempPath;
        return tempPath;
    }

    public async Task<PcmAudioBuffer> StopAsync(CancellationToken cancellationToken)
    {
        var activeProcess = this.process ?? throw new InvalidOperationException("Recording is not active.");
        var activeOutputPath = this.outputPath ?? throw new InvalidOperationException("Recording output path is unavailable.");

        if (!activeProcess.HasExited)
        {
            await activeProcess.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            activeProcess.StandardInput.Close();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await activeProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!activeProcess.HasExited)
            {
                activeProcess.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("ffmpeg did not stop within 5 seconds.");
        }
        finally
        {
            this.process = null;
            this.outputPath = null;
            activeProcess.Dispose();
        }

        if (!File.Exists(activeOutputPath))
        {
            throw new FileNotFoundException("ffmpeg did not create a WAV capture file.", activeOutputPath);
        }

        try
        {
            var audio = WavFile.ReadPcm16KhzMono(activeOutputPath);
            if (audio.IsEmpty)
            {
                throw new InvalidOperationException("No audio was captured.");
            }

            return audio;
        }
        finally
        {
            TryDelete(activeOutputPath);
        }
    }

    public async Task<PcmAudioBuffer> RecordForAsync(string deviceSpec, TimeSpan duration, CancellationToken cancellationToken)
    {
        this.Start(deviceSpec);
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        return await this.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private Process CreateProcess(string arguments, bool redirectStandardInput)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = this.ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = redirectStandardInput,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
    }

    private static string BuildCaptureArguments(string deviceSpec, string outputPath)
    {
        var escapedDevice = EscapeArgument(deviceSpec);
        var escapedOutput = EscapeArgument(outputPath);

        if (OperatingSystem.IsMacOS())
        {
            return $"-hide_banner -loglevel error -f avfoundation -i {escapedDevice} -ac 1 -ar 16000 -sample_fmt s16 -y {escapedOutput}";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"-hide_banner -loglevel error -f pulse -i {escapedDevice} -ac 1 -ar 16000 -sample_fmt s16 -y {escapedOutput}";
        }

        return $"-hide_banner -loglevel error -f dshow -i audio={escapedDevice} -ac 1 -ar 16000 -sample_fmt s16 -y {escapedOutput}";
    }

    private static string EscapeArgument(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup for temp captures.
        }
    }
}
