using System.Globalization;

namespace PrimeDictate;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = HeadlessOptions.Parse(args);
        if (options.ShowHelp)
        {
            HeadlessOptions.PrintHelp();
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var capture = new FfmpegAudioCapture(options.FfmpegPath);

            if (options.ListAudioDevices)
            {
                await capture.PrintDevicesAsync(shutdown.Token).ConfigureAwait(false);
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(options.DownloadModelId))
            {
                await DownloadModelAsync(options.DownloadModelId, shutdown.Token).ConfigureAwait(false);
                return 0;
            }

            await EnsureModelAvailableAsync(options, shutdown.Token).ConfigureAwait(false);

            using var transcriber = new WhisperOnnxTranscriber();
            var injector = new SharpHookTextInjector();

            if (options.OnceDuration is { } duration)
            {
                AppLog.Info($"Recording for {duration.TotalSeconds:N1}s from device {options.AudioDeviceSpec}.");
                var audio = await capture.RecordForAsync(options.AudioDeviceSpec, duration, shutdown.Token).ConfigureAwait(false);
                await TranscribeAndMaybeInjectAsync(audio, transcriber, injector, options, shutdown.Token).ConfigureAwait(false);
                return 0;
            }

            return await RunInteractiveAsync(capture, transcriber, injector, options, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex.Message);
            if (options.Verbose)
            {
                AppLog.Error(ex.ToString());
            }

            return 1;
        }
    }

    private static async Task<int> RunInteractiveAsync(
        FfmpegAudioCapture capture,
        WhisperOnnxTranscriber transcriber,
        SharpHookTextInjector injector,
        HeadlessOptions options,
        CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(1, 1);
        string? activePath = null;

        async Task ToggleAsync()
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!capture.IsRecording)
                {
                    activePath = capture.Start(options.AudioDeviceSpec);
                    AppLog.Info("Recording started. Press Ctrl+Shift+Space again to transcribe, or Ctrl+Shift+Enter to discard.");
                    return;
                }

                AppLog.Info("Recording stopped. Transcribing...");
                var audio = await capture.StopAsync(cancellationToken).ConfigureAwait(false);
                activePath = null;
                await TranscribeAndMaybeInjectAsync(audio, transcriber, injector, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        async Task StopAsync()
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!capture.IsRecording)
                {
                    AppLog.Info("No active recording to discard.");
                    return;
                }

                _ = await capture.StopAsync(cancellationToken).ConfigureAwait(false);
                activePath = null;
                AppLog.Info("Recording discarded.");
            }
            finally
            {
                gate.Release();
            }
        }

        using var hotkeys = new HeadlessHotkeyListener(ToggleAsync, StopAsync);
        var hookTask = hotkeys.RunAsync();

        PrintStartupInstructions(options);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            hotkeys.Dispose();
            if (capture.IsRecording)
            {
                try
                {
                    _ = await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    if (activePath is not null)
                    {
                        AppLog.Info("Stopped active recording during shutdown.");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Failed to stop active recording during shutdown: {ex.Message}");
                }
            }

            try
            {
                await hookTask.ConfigureAwait(false);
            }
            catch
            {
                // Disposing the hook can fault the background task on some platforms.
            }
        }

        return 0;
    }

    private static async Task TranscribeAndMaybeInjectAsync(
        PcmAudioBuffer audio,
        WhisperOnnxTranscriber transcriber,
        SharpHookTextInjector injector,
        HeadlessOptions options,
        CancellationToken cancellationToken)
    {
        AppLog.Info($"Captured {audio.Duration.TotalSeconds:N2}s of 16 kHz mono PCM.");
        var text = await transcriber.TranscribeAsync(
            audio,
            options.ModelId,
            options.ModelPath,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Info("No transcript text produced.");
            return;
        }

        Console.Out.WriteLine(text);
        if (!options.Inject)
        {
            return;
        }

        injector.InjectText(text);
        AppLog.Info($"Injected transcript ({text.Length:N0} chars).");
    }

    private static async Task EnsureModelAvailableAsync(HeadlessOptions options, CancellationToken cancellationToken)
    {
        if (WhisperModelCatalog.TryResolveDirectory(options.ModelPath, out _) ||
            WhisperModelCatalog.TryGetById(options.ModelId, out var selected) &&
            WhisperModelCatalog.TryResolveInstalledPath(selected, out _) ||
            WhisperModelCatalog.Options.Any(option => WhisperModelCatalog.TryResolveInstalledPath(option, out _)))
        {
            return;
        }

        var modelId = string.IsNullOrWhiteSpace(options.ModelId) ? "tiny.en" : options.ModelId;
        AppLog.Info($"No local Whisper ONNX model found. Downloading {modelId}.");
        await DownloadModelAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        if (!WhisperModelCatalog.TryGetById(modelId, out var option))
        {
            var valid = string.Join(", ", WhisperModelCatalog.Options.Select(candidate => candidate.Id));
            throw new ArgumentException($"Unknown model id '{modelId}'. Valid model ids: {valid}");
        }

        var progress = new Progress<WhisperModelDownloadProgress>(p =>
        {
            var percent = p.Percentage is { } value
                ? $" ({value.ToString("N1", CultureInfo.InvariantCulture)}%)"
                : string.Empty;
            AppLog.Info($"{p.ProgressLabel}{percent}");
        });

        var path = await WhisperModelDownloader.DownloadAsync(option, progress, cancellationToken).ConfigureAwait(false);
        AppLog.Info($"Model ready: {path}");
    }

    private static void PrintStartupInstructions(HeadlessOptions options)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("PrimeDictate Headless is running.");
        Console.Error.WriteLine("Hotkeys:");
        Console.Error.WriteLine("  Ctrl+Shift+Space  Start/stop recording and inject final transcript");
        Console.Error.WriteLine("  Ctrl+Shift+Enter  Discard active recording");
        Console.Error.WriteLine("  Ctrl+C            Exit");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Audio device: {options.AudioDeviceSpec}");
        Console.Error.WriteLine($"Models: {WhisperModelCatalog.GetManagedModelsDirectory()}");
        if (OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("macOS requirements:");
            Console.Error.WriteLine("  brew install ffmpeg");
            Console.Error.WriteLine("  Grant Microphone, Accessibility, and Input Monitoring permissions to Terminal or this binary.");
        }

        Console.Error.WriteLine();
    }
}

internal sealed record HeadlessOptions(
    string FfmpegPath,
    string AudioDeviceSpec,
    string? ModelId,
    string? ModelPath,
    string? DownloadModelId,
    TimeSpan? OnceDuration,
    bool Inject,
    bool ListAudioDevices,
    bool Verbose,
    bool ShowHelp)
{
    public static HeadlessOptions Parse(IReadOnlyList<string> args)
    {
        var options = new HeadlessOptions(
            FfmpegPath: "ffmpeg",
            AudioDeviceSpec: FfmpegAudioCapture.DefaultDeviceSpec,
            ModelId: "tiny.en",
            ModelPath: null,
            DownloadModelId: null,
            OnceDuration: null,
            Inject: false,
            ListAudioDevices: false,
            Verbose: false,
            ShowHelp: false);

        var sawInjectionMode = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options = options with { ShowHelp = true };
                    break;
                case "--verbose":
                    options = options with { Verbose = true };
                    break;
                case "--inject":
                    sawInjectionMode = true;
                    options = options with { Inject = true };
                    break;
                case "--print-only":
                    sawInjectionMode = true;
                    options = options with { Inject = false };
                    break;
                case "--list-audio-devices":
                    options = options with { ListAudioDevices = true };
                    break;
                case "--ffmpeg-path":
                    options = options with { FfmpegPath = ReadValue(args, ref i, arg) };
                    break;
                case "--audio-device":
                    options = options with { AudioDeviceSpec = ReadValue(args, ref i, arg) };
                    break;
                case "--model":
                    options = options with { ModelId = ReadValue(args, ref i, arg) };
                    break;
                case "--model-path":
                    options = options with { ModelPath = ReadValue(args, ref i, arg) };
                    break;
                case "--download-model":
                    options = options with { DownloadModelId = ReadValue(args, ref i, arg) };
                    break;
                case "--once":
                    var seconds = double.Parse(ReadValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    options = options with { OnceDuration = TimeSpan.FromSeconds(seconds) };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (!sawInjectionMode && options.OnceDuration is null)
        {
            options = options with { Inject = true };
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            PrimeDictate Headless

            Usage:
              PrimeDictate.Headless
              PrimeDictate.Headless --once 5
              PrimeDictate.Headless --download-model tiny.en
              PrimeDictate.Headless --list-audio-devices

            Options:
              --audio-device <spec>     ffmpeg input device. macOS default is :0.
              --ffmpeg-path <path>      ffmpeg executable path. Default: ffmpeg.
              --model <id>              Whisper ONNX model id. Default: tiny.en.
              --model-path <folder>     Existing Whisper ONNX model folder.
              --download-model <id>     Download a model and exit.
              --once <seconds>          Record once, transcribe, print, then exit.
              --inject                  Inject transcript into the active app.
              --print-only              Print transcript without injection.
              --list-audio-devices      Print ffmpeg audio devices and exit.
              --verbose                 Print exception details.
              -h, --help                Show this help.

            macOS quick start:
              brew install ffmpeg
              ./PrimeDictate.Headless --download-model tiny.en
              ./PrimeDictate.Headless
            """);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }
}
