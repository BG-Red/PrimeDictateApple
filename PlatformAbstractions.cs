using System.Diagnostics.CodeAnalysis;

namespace PrimeDictate;

internal enum MicrophoneAccessMode
{
    Shared,
    Exclusive
}

internal interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    MicrophoneAccessMode? ActiveAccessMode { get; }

    event Action<double>? AudioLevelUpdated;

    void Start(bool exclusiveMode);

    void UpdateInputDevice(string? deviceId);

    void UpdateInputGain(double gainMultiplier);

    bool TryGetPcm16KhzMonoSnapshot(
        [NotNullWhen(true)] out PcmAudioBuffer? snapshot,
        out long capturedBytes,
        TimeSpan? maxDuration = null);

    Task<PcmAudioBuffer> StopAsync();
}

internal interface ITextInjector
{
    void InjectText(string text);

    void SendEnter();
}

internal interface IForegroundInputTarget
{
    string DisplayName { get; }

    uint ProcessId { get; }

    string? ProcessName { get; }

    string? Title { get; }

    bool IsStillForeground();

    bool TryInjectTextDirectly(string text);

    bool TryRestoreForInput();
}

internal interface IForegroundInputTargetProvider
{
    IForegroundInputTarget? Capture();
}
