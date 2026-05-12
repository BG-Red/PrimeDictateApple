using System.Runtime.InteropServices;
using SherpaOnnx;

namespace PrimeDictate;

internal sealed class WhisperOnnxTranscriber : IDisposable
{
    private OfflineRecognizer? recognizer;
    private string? loadedModelDirectory;

    public string ConfigurationSummary { get; private set; } = "Whisper ONNX CPU";

    public async Task<string> TranscribeAsync(
        PcmAudioBuffer audio,
        string? selectedModelId,
        string? configuredModelPath,
        CancellationToken cancellationToken)
    {
        if (audio.IsEmpty)
        {
            return string.Empty;
        }

        if (audio.SampleRate != 16_000 || audio.BitsPerSample != 16 || audio.Channels != 1)
        {
            throw new InvalidOperationException("Whisper ONNX input must be 16 kHz, 16-bit mono PCM.");
        }

        var modelDirectory = ResolveModelDirectoryOrThrow(selectedModelId, configuredModelPath);
        ConfigurationSummary = $"Whisper ONNX CPU, model={modelDirectory}";

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recognizerInstance = this.EnsureRecognizer(modelDirectory);
            var samples = new float[audio.Pcm16KhzMono.Length / 2];
            CopyPcm16ToFloatSamples(audio.Pcm16KhzMono, samples);

            using var stream = recognizerInstance.CreateStream();
            stream.AcceptWaveform(audio.SampleRate, samples);
            recognizerInstance.Decode([stream]);

            try
            {
                return stream.Result.Text.Trim();
            }
            catch (NullReferenceException)
            {
                return string.Empty;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (this.recognizer is IDisposable disposable)
        {
            disposable.Dispose();
        }

        this.recognizer = null;
        this.loadedModelDirectory = null;
    }

    private OfflineRecognizer EnsureRecognizer(string modelDirectory)
    {
        if (this.recognizer is not null &&
            string.Equals(this.loadedModelDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return this.recognizer;
        }

        this.Dispose();
        if (!WhisperModelCatalog.TryResolveModelFiles(modelDirectory, out var modelFiles))
        {
            throw new FileNotFoundException(
                "Whisper ONNX model folder is incomplete. Expected encoder, decoder, and tokens files.",
                modelDirectory);
        }

        AppLog.Info($"Loading Whisper ONNX model from {modelDirectory}");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Tokens = modelFiles.Value.Tokens;
        config.ModelConfig.Whisper.Encoder = modelFiles.Value.Encoder;
        config.ModelConfig.Whisper.Decoder = modelFiles.Value.Decoder;
        config.ModelConfig.Whisper.Language = "en";
        config.ModelConfig.Whisper.Task = "transcribe";

        this.recognizer = new OfflineRecognizer(config);
        this.loadedModelDirectory = modelDirectory;
        return this.recognizer;
    }

    private static string ResolveModelDirectoryOrThrow(string? selectedModelId, string? configuredModelPath)
    {
        if (WhisperModelCatalog.TryResolveDirectory(configuredModelPath, out var explicitDirectory))
        {
            return explicitDirectory;
        }

        if (!string.IsNullOrWhiteSpace(configuredModelPath))
        {
            throw new FileNotFoundException(
                "Whisper ONNX model folder not found or incomplete. Pick a folder containing encoder, decoder, and tokens files.",
                configuredModelPath);
        }

        if (WhisperModelCatalog.TryGetById(selectedModelId, out var selectedOption) &&
            WhisperModelCatalog.TryResolveInstalledPath(selectedOption, out var selectedPath))
        {
            return selectedPath;
        }

        foreach (var option in WhisperModelCatalog.Options)
        {
            if (WhisperModelCatalog.TryResolveInstalledPath(option, out var installedPath))
            {
                return installedPath;
            }
        }

        throw new FileNotFoundException(
            "Whisper ONNX model not found. Run with --download-model tiny.en or pass --model-path <folder>.");
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
}
