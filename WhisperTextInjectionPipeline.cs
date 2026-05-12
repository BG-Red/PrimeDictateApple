using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

/// <summary>
/// Transcribes through the selected engine, then updates the focused control via final-only Unicode input.
/// Target injection is intentionally final-only: partial hypotheses are not typed into editors because repeated
/// correction loops fight autocomplete, caret movement, and slow input targets.
/// </summary>
internal sealed class WhisperTextInjectionPipeline
{
    private readonly TranscriptionEngineHost transcriptionEngines = new();
    private readonly ITextInjector textInjector;

    public WhisperTextInjectionPipeline(ITextInjector? textInjector = null)
    {
        this.textInjector = textInjector ?? new SharpHookTextInjector();
    }

    public string ConfigurationSummary => this.transcriptionEngines.ConfigurationSummary;

    public void UpdateConfiguration(
        TranscriptionBackendKind transcriptionBackend,
        TranscriptionComputeInterface transcriptionComputeInterface,
        string? selectedModelId,
        string? configuredModelPath)
    {
        this.transcriptionEngines.UpdateConfiguration(
            transcriptionBackend,
            transcriptionComputeInterface,
            selectedModelId,
            configuredModelPath);
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

        var backend = this.transcriptionEngines.ConfiguredBackendName;
        if (logTranscript)
        {
            AppLog.Info(
                $"Transcription request: {this.ConfigurationSummary}; audio={audio.Duration.TotalSeconds:0.00}s, bytes={audio.Pcm16KhzMono.Length:N0}.");
        }

        var text = await this.transcriptionEngines.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (logTranscript)
            {
                AppLog.Info($"{backend} returned no text for this audio buffer.");
            }

            return string.Empty;
        }

        if (logTranscript)
        {
            AppLog.Info($"Transcribed ({backend}, {text.Length:N0} chars): {text}");
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

        this.textInjector.InjectText(target);
    }

    public void SendEnterToTarget() => this.textInjector.SendEnter();

    public async ValueTask DisposeAsync()
    {
        await this.transcriptionEngines.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class SharpHookTextInjector : ITextInjector
{
    private readonly EventSimulator eventSimulator = new();

    public void InjectText(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsUnicodeInput.SendText(text);
            return;
        }

        var textResult = this.eventSimulator.SimulateTextEntry(text);
        if (textResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {textResult}.");
        }
    }

    public void SendEnter()
    {
        var keyResult = this.eventSimulator.SimulateKeyStroke(new[] { KeyCode.VcEnter });
        if (keyResult != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Enter key simulation failed with status {keyResult}.");
        }
    }
}
