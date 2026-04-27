using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SherpaOnnx;
using SharpHook;
using SharpHook.Data;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace PrimeDictate;

internal static class ModelFileLocator
{
    private const string DefaultModelFileName = "ggml-large-v3-turbo.bin";
    private static readonly string DefaultRelativePath = Path.Combine("models", DefaultModelFileName);
    private static readonly string ManagedModelsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrimeDictate",
        "models");

    internal static string ResolveOrThrow()
    {
        var fromEnv = Environment.GetEnvironmentVariable("PRIME_DICTATE_MODEL");
        if (TryResolveExactPath(fromEnv, out var resolvedOverride))
        {
            return resolvedOverride;
        }

        if (TryResolveKnownModel(DefaultModelFileName, out var resolvedDefault))
        {
            return resolvedDefault;
        }

        throw new FileNotFoundException(
            "Whisper model not found. Download one in onboarding, place ggml-large-v3-turbo.bin in .\\models, set PRIME_DICTATE_MODEL, or run from the repository root.");
    }

    internal static string GetManagedModelsDirectory() => ManagedModelsDirectory;

    internal static string GetManagedModelPath(string fileName) => Path.Combine(ManagedModelsDirectory, fileName);

    internal static bool TryResolveExactPath(string? path, [NotNullWhen(true)] out string? resolvedPath)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            resolvedPath = Path.GetFullPath(path);
            return true;
        }

        resolvedPath = null;
        return false;
    }

    internal static bool TryResolveKnownModel(string fileName, [NotNullWhen(true)] out string? resolvedPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateCandidatePaths(fileName))
        {
            var normalizedCandidate = Path.GetFullPath(candidate);
            if (!seen.Add(normalizedCandidate))
            {
                continue;
            }

            if (File.Exists(normalizedCandidate))
            {
                resolvedPath = normalizedCandidate;
                return true;
            }
        }

        resolvedPath = null;
        return false;
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string fileName)
    {
        yield return GetManagedModelPath(fileName);
        yield return Path.Combine("models", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "models", fileName);

        foreach (var upwardCandidate in EnumeratePathsAboveWorkingDirectory(fileName))
        {
            yield return upwardCandidate;
        }
    }

    private static IEnumerable<string> EnumeratePathsAboveWorkingDirectory(string fileName)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            yield return Path.Combine(dir.FullName, "models", fileName);
            dir = dir.Parent;
        }
    }
}

/// <summary>
/// Lifecycle holder for the Whisper native model and the configured processor. Both must be disposed
/// in native wrapper order: processor, then factory.
/// </summary>
internal sealed class WhisperModelSession : IAsyncDisposable
{
    public WhisperModelSession(WhisperFactory factory, WhisperProcessor processor)
    {
        this.Factory = factory;
        this.Processor = processor;
    }

    public WhisperFactory Factory { get; }

    public WhisperProcessor Processor { get; }

    public async ValueTask DisposeAsync()
    {
        await this.Processor.DisposeAsync().ConfigureAwait(false);
        this.Factory.Dispose();
    }
}

/// <summary>
/// Transcribes with Whisper, then updates the focused control via final-only Unicode input (no clipboard).
/// Target injection is intentionally final-only: partial hypotheses are not typed into editors because repeated
/// correction loops fight autocomplete, caret movement, and slow input targets.
/// </summary>
internal sealed class WhisperTextInjectionPipeline
{
    private readonly SemaphoreSlim initGate = new(initialCount: 1, maxCount: 1);
    private readonly SemaphoreSlim parakeetInitGate = new(initialCount: 1, maxCount: 1);
    private readonly SemaphoreSlim moonshineInitGate = new(initialCount: 1, maxCount: 1);
    private readonly EventSimulator eventSimulator = new();
    private readonly object configurationSync = new();
    private WhisperModelSession? session;
    private string? loadedModelPath;
    private OfflineRecognizer? parakeetRecognizer;
    private string? loadedParakeetModelPath;
    private OfflineRecognizer? moonshineRecognizer;
    private string? loadedMoonshineModelPath;
    private TranscriptionBackendKind transcriptionBackend = TranscriptionBackendKind.Whisper;
    private string? selectedModelId;
    private string? configuredModelPath;
    private bool useGpuForWhisper = true;
    private bool? loadedWhisperGpuMode;

    public void UpdateConfiguration(
        TranscriptionBackendKind transcriptionBackend,
        string? selectedModelId,
        string? configuredModelPath,
        bool useGpuForWhisper)
    {
        lock (this.configurationSync)
        {
            this.transcriptionBackend = transcriptionBackend;
            this.selectedModelId = string.IsNullOrWhiteSpace(selectedModelId) ? null : selectedModelId.Trim();
            this.configuredModelPath = string.IsNullOrWhiteSpace(configuredModelPath) ? null : configuredModelPath.Trim();
            this.useGpuForWhisper = useGpuForWhisper;
        }
    }

    /// <summary>
    /// Full-buffer transcription with no target mutation.
    /// </summary>
    public async ValueTask<string> TranscribeAsync(
        PcmAudioBuffer audio,
        CancellationToken cancellationToken = default,
        bool logTranscript = true)
    {
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        var backend = this.GetConfiguredBackend();
        var text = await this.TranscribeToStringAsync(audio, cancellationToken).ConfigureAwait(false);
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (logTranscript)
            {
                AppLog.Info($"{backend} returned no text.");
            }

            return string.Empty;
        }

        if (logTranscript)
        {
            AppLog.Info($"Transcribed ({backend}): {text}");
        }

        return text;
    }

    public void InjectTextToTarget(string text)
    {
        var target = text.Trim();
        if (target.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsUnicodeInput.SendText(target);
            return;
        }

        var textResult = this.eventSimulator.SimulateTextEntry(target);
        if (textResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {textResult}.");
        }
    }

    public void SendEnterToTarget()
    {
        var keyResult = this.eventSimulator.SimulateKeyStroke(new[] { KeyCode.VcEnter });
        if (keyResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Enter key simulation failed with status {keyResult}.");
        }
    }

    private async Task<string> TranscribeToStringAsync(PcmAudioBuffer audio, CancellationToken cancellationToken)
    {
        var configuration = this.GetConfigurationSnapshot();
        return configuration.Backend switch
        {
            TranscriptionBackendKind.Moonshine => await this.TranscribeWithMoonshineAsync(audio, configuration, cancellationToken)
                .ConfigureAwait(false),
            TranscriptionBackendKind.Parakeet => await this.TranscribeWithParakeetAsync(audio, configuration, cancellationToken)
                .ConfigureAwait(false),
            _ => await this.TranscribeWithWhisperAsync(audio, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<string> TranscribeWithWhisperAsync(PcmAudioBuffer audio, CancellationToken cancellationToken)
    {
        await this.UnloadParakeetRecognizerAsync().ConfigureAwait(false);
        await this.UnloadMoonshineRecognizerAsync().ConfigureAwait(false);
        var modelSession = await this.EnsureSessionAsync().ConfigureAwait(false);
        if (audio.SampleRate != 16_000 || audio.BitsPerSample != 16 || audio.Channels != 1)
        {
            throw new InvalidOperationException("Whisper input must be 16 kHz, 16-bit mono PCM.");
        }

        var sampleCount = audio.Pcm16KhzMono.Length / 2;
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        var samples = ArrayPool<float>.Shared.Rent(sampleCount);
        var builder = new StringBuilder();

        try
        {
            CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples.AsSpan(0, sampleCount));

            await foreach (var result in modelSession.Processor
                               .ProcessAsync(new ReadOnlyMemory<float>(samples, 0, sampleCount), cancellationToken)
                               .ConfigureAwait(false))
            {
                builder.Append(result.Text);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(samples);
        }

        return builder.ToString().Trim();
    }

    private async Task<string> TranscribeWithParakeetAsync(
        PcmAudioBuffer audio,
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await this.UnloadWhisperSessionAsync().ConfigureAwait(false);
        await this.UnloadMoonshineRecognizerAsync().ConfigureAwait(false);

        if (audio.SampleRate != 16_000 || audio.BitsPerSample != 16 || audio.Channels != 1)
        {
            throw new InvalidOperationException("Parakeet input must be 16 kHz, 16-bit mono PCM.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sampleCount = audio.Pcm16KhzMono.Length / 2;
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        var recognizer = await this.EnsureParakeetRecognizerAsync(configuration).ConfigureAwait(false);
        var samples = new float[sampleCount];
        CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

        var stream = recognizer.CreateStream();
        stream.AcceptWaveform(audio.SampleRate, samples);
        recognizer.Decode([stream]);
        return stream.Result.Text.Trim();
    }

    private async Task<string> TranscribeWithMoonshineAsync(
        PcmAudioBuffer audio,
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await this.UnloadWhisperSessionAsync().ConfigureAwait(false);
        await this.UnloadParakeetRecognizerAsync().ConfigureAwait(false);

        if (audio.SampleRate != 16_000 || audio.BitsPerSample != 16 || audio.Channels != 1)
        {
            throw new InvalidOperationException("Moonshine input must be 16 kHz, 16-bit mono PCM.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sampleCount = audio.Pcm16KhzMono.Length / 2;
        if (sampleCount == 0)
        {
            return string.Empty;
        }

        var recognizer = await this.EnsureMoonshineRecognizerAsync(configuration).ConfigureAwait(false);
        var samples = new float[sampleCount];
        CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

        var stream = recognizer.CreateStream();
        stream.AcceptWaveform(audio.SampleRate, samples);
        recognizer.Decode([stream]);
        return stream.Result.Text.Trim();
    }

    private static void CopyPcm16ToFloatSamples(byte[] pcm16, Span<float> destination)
    {
        var pcmBytes = pcm16.AsSpan(0, destination.Length * 2);
        var samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = samples[i] / 32768f;
        }
    }

    private async Task<WhisperModelSession> EnsureSessionAsync()
    {
        var configuration = this.GetConfigurationSnapshot();
        var modelPath = ModelFileLocator.ResolveOrThrow();
        if (this.session is not null &&
            string.Equals(this.loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            this.loadedWhisperGpuMode == configuration.UseGpuForWhisper)
        {
            return this.session;
        }

        await this.initGate.WaitAsync().ConfigureAwait(false);

        try
        {
            configuration = this.GetConfigurationSnapshot();
            modelPath = ModelFileLocator.ResolveOrThrow();
            if (this.session is not null &&
                string.Equals(this.loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
                this.loadedWhisperGpuMode == configuration.UseGpuForWhisper)
            {
                return this.session;
            }

            if (this.session is not null)
            {
                await this.session.DisposeAsync().ConfigureAwait(false);
                this.session = null;
                this.loadedModelPath = null;
                this.loadedWhisperGpuMode = null;
            }

            ConfigureWhisperRuntimeOrder(configuration.UseGpuForWhisper);
            AppLog.Info($"Loaded transcription backend: Whisper from {modelPath} ({(configuration.UseGpuForWhisper ? "GPU auto" : "CPU only")})");
            var factory = WhisperFactory.FromPath(modelPath);
            var processor = factory.CreateBuilder()
                .WithLanguageDetection()
                .Build();
            this.session = new WhisperModelSession(factory, processor);
            this.loadedModelPath = modelPath;
            this.loadedWhisperGpuMode = configuration.UseGpuForWhisper;
            return this.session;
        }
        finally
        {
            this.initGate.Release();
        }
    }

    private async Task<OfflineRecognizer> EnsureParakeetRecognizerAsync(TranscriptionConfiguration configuration)
    {
        var modelDirectory = ResolveParakeetModelPathOrThrow(configuration);
        if (this.parakeetRecognizer is not null &&
            string.Equals(this.loadedParakeetModelPath, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return this.parakeetRecognizer;
        }

        await this.parakeetInitGate.WaitAsync().ConfigureAwait(false);

        try
        {
            modelDirectory = ResolveParakeetModelPathOrThrow(configuration);
            if (this.parakeetRecognizer is not null &&
                string.Equals(this.loadedParakeetModelPath, modelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return this.parakeetRecognizer;
            }

            DisposeRecognizer(this.parakeetRecognizer);
            this.parakeetRecognizer = null;
            this.loadedParakeetModelPath = null;

            AppLog.Info($"Loaded transcription backend: Parakeet from {modelDirectory}");
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16_000;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
            config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
            config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
            config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, "joiner.int8.onnx");
            config.ModelConfig.ModelType = "nemo_transducer";
            config.DecodingMethod = "greedy_search";
            config.MaxActivePaths = 4;
            config.ModelConfig.Debug = 0;

            this.parakeetRecognizer = new OfflineRecognizer(config);
            this.loadedParakeetModelPath = modelDirectory;
            return this.parakeetRecognizer;
        }
        finally
        {
            this.parakeetInitGate.Release();
        }
    }

    private async Task<OfflineRecognizer> EnsureMoonshineRecognizerAsync(TranscriptionConfiguration configuration)
    {
        var modelDirectory = ResolveMoonshineModelPathOrThrow(configuration);
        if (this.moonshineRecognizer is not null &&
            string.Equals(this.loadedMoonshineModelPath, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return this.moonshineRecognizer;
        }

        await this.moonshineInitGate.WaitAsync().ConfigureAwait(false);

        try
        {
            modelDirectory = ResolveMoonshineModelPathOrThrow(configuration);
            if (this.moonshineRecognizer is not null &&
                string.Equals(this.loadedMoonshineModelPath, modelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return this.moonshineRecognizer;
            }

            DisposeRecognizer(this.moonshineRecognizer);
            this.moonshineRecognizer = null;
            this.loadedMoonshineModelPath = null;

            AppLog.Info($"Loaded transcription backend: Moonshine from {modelDirectory}");
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16_000;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
            config.ModelConfig.Moonshine.Preprocessor = Path.Combine(modelDirectory, "preprocess.onnx");
            config.ModelConfig.Moonshine.Encoder = Path.Combine(modelDirectory, "encode.int8.onnx");
            config.ModelConfig.Moonshine.UncachedDecoder = Path.Combine(modelDirectory, "uncached_decode.int8.onnx");
            config.ModelConfig.Moonshine.CachedDecoder = Path.Combine(modelDirectory, "cached_decode.int8.onnx");
            config.ModelConfig.Debug = 0;

            this.moonshineRecognizer = new OfflineRecognizer(config);
            this.loadedMoonshineModelPath = modelDirectory;
            return this.moonshineRecognizer;
        }
        finally
        {
            this.moonshineInitGate.Release();
        }
    }

    private async Task UnloadWhisperSessionAsync()
    {
        await this.initGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (this.session is not null)
            {
                await this.session.DisposeAsync().ConfigureAwait(false);
                this.session = null;
                this.loadedModelPath = null;
                this.loadedWhisperGpuMode = null;
            }
        }
        finally
        {
            this.initGate.Release();
        }
    }

    private async Task UnloadParakeetRecognizerAsync()
    {
        await this.parakeetInitGate.WaitAsync().ConfigureAwait(false);

        try
        {
            DisposeRecognizer(this.parakeetRecognizer);
            this.parakeetRecognizer = null;
            this.loadedParakeetModelPath = null;
        }
        finally
        {
            this.parakeetInitGate.Release();
        }
    }

    private async Task UnloadMoonshineRecognizerAsync()
    {
        await this.moonshineInitGate.WaitAsync().ConfigureAwait(false);

        try
        {
            DisposeRecognizer(this.moonshineRecognizer);
            this.moonshineRecognizer = null;
            this.loadedMoonshineModelPath = null;
        }
        finally
        {
            this.moonshineInitGate.Release();
        }
    }

    private static void DisposeRecognizer(object? recognizer)
    {
        if (recognizer is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private string ResolveParakeetModelPathOrThrow(TranscriptionConfiguration configuration)
    {
        if (ParakeetModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Parakeet model folder not found or incomplete. Pick a folder containing encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx, and tokens.txt.");
        }

        if (ParakeetModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            ParakeetModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in ParakeetModelCatalog.Options)
        {
            if (ParakeetModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Parakeet model not found. Download one in onboarding or browse to a local Parakeet model folder.");
    }

    private string ResolveMoonshineModelPathOrThrow(TranscriptionConfiguration configuration)
    {
        if (MoonshineModelCatalog.TryResolveDirectory(configuration.ConfiguredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuration.ConfiguredModelPath))
        {
            throw new FileNotFoundException(
                "Moonshine model folder not found or incomplete. Pick a folder containing preprocess.onnx, encode.int8.onnx, uncached_decode.int8.onnx, cached_decode.int8.onnx, and tokens.txt.");
        }

        if (MoonshineModelCatalog.TryGetById(configuration.SelectedModelId, out var selectedOption) &&
            MoonshineModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in MoonshineModelCatalog.Options)
        {
            if (MoonshineModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Moonshine model not found. Download one in onboarding or browse to a local Moonshine model folder.");
    }

    private TranscriptionConfiguration GetConfigurationSnapshot()
    {
        lock (this.configurationSync)
        {
            return new TranscriptionConfiguration(
                this.transcriptionBackend,
                this.selectedModelId,
                this.configuredModelPath,
                this.useGpuForWhisper);
        }
    }

    private static void ConfigureWhisperRuntimeOrder(bool useGpuForWhisper)
    {
        RuntimeOptions.LoadedLibrary = null;
        RuntimeOptions.RuntimeLibraryOrder = useGpuForWhisper
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
    }

    private string GetConfiguredBackend() => this.GetConfigurationSnapshot().Backend switch
    {
        TranscriptionBackendKind.Moonshine => "Moonshine",
        TranscriptionBackendKind.Parakeet => "Parakeet",
        _ => "Whisper"
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.UnloadWhisperSessionAsync().ConfigureAwait(false);
            await this.UnloadParakeetRecognizerAsync().ConfigureAwait(false);
            await this.UnloadMoonshineRecognizerAsync().ConfigureAwait(false);
        }
        finally
        {
            this.initGate.Dispose();
            this.parakeetInitGate.Dispose();
            this.moonshineInitGate.Dispose();
        }
    }

    private readonly record struct TranscriptionConfiguration(
        TranscriptionBackendKind Backend,
        string? SelectedModelId,
        string? ConfiguredModelPath,
        bool UseGpuForWhisper);
}
