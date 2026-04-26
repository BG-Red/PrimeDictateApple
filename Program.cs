using System.Diagnostics.CodeAnalysis;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

internal sealed class GlobalHotkeyListener : IDisposable
{
    private readonly IGlobalHook hook;
    private readonly Func<Task> onHotkeyPressedAsync;
    private readonly object configSync = new();
    private HotkeyGesture hotkey;

    public GlobalHotkeyListener(Func<Task> onHotkeyPressedAsync, HotkeyGesture hotkey)
    {
        this.onHotkeyPressedAsync = onHotkeyPressedAsync;
        this.hotkey = hotkey;
        this.hook = new SimpleGlobalHook(GlobalHookType.Keyboard);
        this.hook.KeyPressed += this.OnKeyPressed;
    }

    public Task RunAsync() => this.hook.RunAsync();

    public void Dispose()
    {
        this.hook.KeyPressed -= this.OnKeyPressed;
        this.hook.Dispose();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs args)
    {
        if (!this.IsDictationHotkey(args))
        {
            return;
        }

        args.SuppressEvent = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await this.onHotkeyPressedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Hotkey action failed: {ex.Message}");
            }
        });
    }

    public void UpdateHotkey(HotkeyGesture hotkey)
    {
        lock (this.configSync)
        {
            this.hotkey = hotkey;
        }
    }

    private bool IsDictationHotkey(KeyboardHookEventArgs args)
    {
        var mask = args.RawEvent.Mask;
        HotkeyGesture currentHotkey;
        lock (this.configSync)
        {
            currentHotkey = this.hotkey;
        }

        return args.Data.KeyCode == currentHotkey.KeyCode &&
            (!currentHotkey.Ctrl || mask.HasCtrl()) &&
            (!currentHotkey.Shift || mask.HasShift()) &&
            (!currentHotkey.Alt || mask.HasAlt());
    }
}

internal sealed class DictationController : IAsyncDisposable
{
    private static readonly TimeSpan LiveTranscribeInterval = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan LiveMinAudio = TimeSpan.FromSeconds(0.55);

    private readonly SemaphoreSlim toggleGate = new(initialCount: 1, maxCount: 1);
    private readonly DefaultMicrophoneRecorder recorder = new();
    private readonly WhisperTextInjectionPipeline textInjectionPipeline = new();
    private readonly object configSync = new();
    private bool exclusiveMicAccessWhileDictating;

    private CancellationTokenSource? liveTranscriptionCts;
    private Task? liveTranscriptionTask;
    private Guid? activeThreadId;

    public event Action<bool>? RecordingStateChanged;
    public event Action<Guid>? ThreadStarted;
    public event Action<Guid>? ThreadCompleted;
    public event Action<Guid, string>? ThreadTranscriptUpdated;

    public DictationController(bool exclusiveMicAccessWhileDictating = false)
    {
        this.exclusiveMicAccessWhileDictating = exclusiveMicAccessWhileDictating;
    }

    public bool IsRecording => this.recorder.IsRecording;

    public string ActiveMicAccessModeLabel => this.recorder.ActiveShareMode switch
    {
        AudioClientShareMode.Exclusive => "Exclusive",
        AudioClientShareMode.Shared => "Shared",
        _ => "N/A"
    };

    public void UpdateCaptureOptions(bool exclusiveMicAccessWhileDictating)
    {
        lock (this.configSync)
        {
            this.exclusiveMicAccessWhileDictating = exclusiveMicAccessWhileDictating;
        }
    }

    public async Task ToggleRecordingAsync()
    {
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (!this.recorder.IsRecording)
            {
                bool useExclusiveMicAccess;
                lock (this.configSync)
                {
                    useExclusiveMicAccess = this.exclusiveMicAccessWhileDictating;
                }

                var threadId = Guid.NewGuid();
                this.activeThreadId = threadId;
                this.ThreadStarted?.Invoke(threadId);
                this.textInjectionPipeline.BeginLiveDictationSession();
                this.liveTranscriptionCts = new CancellationTokenSource();
                var liveToken = this.liveTranscriptionCts.Token;
                this.liveTranscriptionTask = Task.Run(() => this.LiveTranscriptionLoopAsync(liveToken), CancellationToken.None);
                this.recorder.Start(useExclusiveMicAccess);
                AppLog.Info($"Recording started (live transcription, mic mode: {this.ActiveMicAccessModeLabel}).", threadId);
                this.RecordingStateChanged?.Invoke(true);
                return;
            }

            this.liveTranscriptionCts?.Cancel();
            if (this.liveTranscriptionTask is { } liveTask)
            {
                try
                {
                    await liveTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Error($"Live transcription loop failed: {ex.Message}", this.activeThreadId);
                }

                this.liveTranscriptionTask = null;
            }

            this.liveTranscriptionCts?.Dispose();
            this.liveTranscriptionCts = null;

            var audio = await this.recorder.StopAsync().ConfigureAwait(false);
            AppLog.Info(
                $"Recording stopped: {audio.Duration.TotalSeconds:N2}s, {audio.Pcm16KhzMono.Length:N0} bytes PCM.",
                this.activeThreadId);
            this.RecordingStateChanged?.Invoke(false);

            await this.HandleRecordedAudioAsync(audio).ConfigureAwait(false);
            if (this.activeThreadId is Guid completedId)
            {
                this.ThreadCompleted?.Invoke(completedId);
                this.activeThreadId = null;
            }
        }
        finally
        {
            this.toggleGate.Release();
        }
    }

    private async Task LiveTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        var lastCommittedSnapBytes = 0;

        try
        {
            while (true)
            {
                try
                {
                    await Task.Delay(LiveTranscribeInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!this.recorder.TryGetPcm16KhzMonoSnapshot(out var snap) || snap.IsEmpty)
                {
                    continue;
                }

                if (snap.Pcm16KhzMono.Length == lastCommittedSnapBytes)
                {
                    continue;
                }

                if (snap.Duration < LiveMinAudio)
                {
                    continue;
                }

                try
                {
                    var transcript = await this.textInjectionPipeline
                        .TranscribeAndSyncToTargetAsync(snap, cancellationToken)
                        .ConfigureAwait(false);
                    lastCommittedSnapBytes = snap.Pcm16KhzMono.Length;
                    if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(transcript))
                    {
                        this.ThreadTranscriptUpdated?.Invoke(threadId, transcript);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Live transcription failed: {ex.Message}", this.activeThreadId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.toggleGate.WaitAsync().ConfigureAwait(false);

        try
        {
            try
            {
                if (this.recorder.IsRecording)
                {
                    this.liveTranscriptionCts?.Cancel();
                    if (this.liveTranscriptionTask is { } t)
                    {
                        try
                        {
                            await t.ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    this.liveTranscriptionCts?.Dispose();
                    this.liveTranscriptionCts = null;
                    this.liveTranscriptionTask = null;
                    _ = await this.recorder.StopAsync().ConfigureAwait(false);
                    this.RecordingStateChanged?.Invoke(false);
                }

                this.recorder.Dispose();
            }
            finally
            {
                await this.textInjectionPipeline.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            this.toggleGate.Release();
            this.toggleGate.Dispose();
        }
    }

    private async Task HandleRecordedAudioAsync(PcmAudioBuffer audio)
    {
        if (audio.IsEmpty)
        {
            AppLog.Info("No audio captured.", this.activeThreadId);
            return;
        }

        try
        {
            var finalTranscript = await this.textInjectionPipeline.TranscribeAndSyncToTargetAsync(audio, CancellationToken.None)
                .ConfigureAwait(false);
            if (this.activeThreadId is Guid threadId && !string.IsNullOrWhiteSpace(finalTranscript))
            {
                this.ThreadTranscriptUpdated?.Invoke(threadId, finalTranscript);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Transcription or text injection failed: {ex.Message}", this.activeThreadId);
        }
    }
}

internal sealed class DefaultMicrophoneRecorder : IDisposable
{
    private const int TargetSampleRate = 16_000;
    private const int TargetBitsPerSample = 16;
    private const int TargetChannels = 1;

    private readonly object syncRoot = new();

    private WasapiCapture? capture;
    private MemoryStream? captureBuffer;
    private WaveFormat? captureFormat;
    private TaskCompletionSource<Exception?>? stoppedSignal;
    private AudioClientShareMode? activeShareMode;

    public bool IsRecording
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.capture is not null;
            }
        }
    }

    public AudioClientShareMode? ActiveShareMode
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.activeShareMode;
            }
        }
    }

    public void Start(bool exclusiveMode)
    {
        lock (this.syncRoot)
        {
            if (this.capture is not null)
            {
                throw new InvalidOperationException("Recording is already in progress.");
            }

            try
            {
                this.InitializeAndStartCapture(exclusiveMode);
            }
            catch (Exception ex) when (exclusiveMode)
            {
                AppLog.Info($"Exclusive microphone mode failed ({ex.Message}). Falling back to shared mode.");
                this.InitializeAndStartCapture(exclusiveMode: false);
            }
        }
    }

    /// <summary>
    /// Copies captured audio so far, resampled to 16 kHz mono. Returns false if not recording.
    /// </summary>
    public bool TryGetPcm16KhzMonoSnapshot([NotNullWhen(true)] out PcmAudioBuffer? snapshot)
    {
        byte[] rawAudio;
        WaveFormat rawFormat;

        lock (this.syncRoot)
        {
            if (this.capture is null || this.captureBuffer is null || this.captureFormat is null)
            {
                snapshot = null;
                return false;
            }

            rawAudio = this.captureBuffer.ToArray();
            rawFormat = this.captureFormat;
        }

        var pcm16KhzMono = ConvertToPcm16KhzMono(rawAudio, rawFormat);
        snapshot = new PcmAudioBuffer(pcm16KhzMono, TargetSampleRate, TargetBitsPerSample, TargetChannels);
        return true;
    }

    public async Task<PcmAudioBuffer> StopAsync()
    {
        WasapiCapture activeCapture;
        TaskCompletionSource<Exception?> activeStoppedSignal;

        lock (this.syncRoot)
        {
            activeCapture = this.capture ?? throw new InvalidOperationException("Recording is not in progress.");
            activeStoppedSignal = this.stoppedSignal ?? throw new InvalidOperationException("Recorder state is invalid.");
        }

        activeCapture.StopRecording();

        var stopException = await activeStoppedSignal.Task.ConfigureAwait(false);
        if (stopException is not null)
        {
            throw new InvalidOperationException("Microphone capture failed.", stopException);
        }

        byte[] rawAudio;
        WaveFormat rawFormat;

        lock (this.syncRoot)
        {
            rawAudio = this.captureBuffer?.ToArray() ?? [];
            rawFormat = this.captureFormat ?? throw new InvalidOperationException("Capture format is unavailable.");

            this.ResetCaptureState(activeCapture);
        }

        var pcm16KhzMono = ConvertToPcm16KhzMono(rawAudio, rawFormat);
        return new PcmAudioBuffer(pcm16KhzMono, TargetSampleRate, TargetBitsPerSample, TargetChannels);
    }

    public void Dispose()
    {
        lock (this.syncRoot)
        {
            if (this.capture is not null)
            {
                this.ResetCaptureState(this.capture);
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (this.syncRoot)
        {
            this.captureBuffer?.Write(args.Buffer, 0, args.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        TaskCompletionSource<Exception?>? signal;

        lock (this.syncRoot)
        {
            signal = this.stoppedSignal;
        }

        signal?.TrySetResult(args.Exception);
    }

    private void ResetCaptureState(WasapiCapture captureToDispose)
    {
        captureToDispose.DataAvailable -= this.OnDataAvailable;
        captureToDispose.RecordingStopped -= this.OnRecordingStopped;
        captureToDispose.Dispose();

        this.captureBuffer?.Dispose();
        this.captureBuffer = null;
        this.captureFormat = null;
        this.capture = null;
        this.stoppedSignal = null;
        this.activeShareMode = null;
    }

    private void InitializeAndStartCapture(bool exclusiveMode)
    {
        var newCapture = new WasapiCapture
        {
            ShareMode = exclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared
        };

        this.capture = newCapture;
        this.captureFormat = newCapture.WaveFormat;
        this.captureBuffer = new MemoryStream();
        this.stoppedSignal = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.activeShareMode = newCapture.ShareMode;

        newCapture.DataAvailable += this.OnDataAvailable;
        newCapture.RecordingStopped += this.OnRecordingStopped;

        try
        {
            newCapture.StartRecording();
        }
        catch
        {
            this.ResetCaptureState(newCapture);
            throw;
        }
    }

    private static byte[] ConvertToPcm16KhzMono(byte[] rawAudio, WaveFormat rawFormat)
    {
        if (rawAudio.Length == 0)
        {
            return [];
        }

        if (rawFormat.Encoding == WaveFormatEncoding.Pcm &&
            rawFormat.SampleRate == TargetSampleRate &&
            rawFormat.BitsPerSample == TargetBitsPerSample &&
            rawFormat.Channels == TargetChannels)
        {
            return rawAudio;
        }

        using var rawStream = new MemoryStream(rawAudio, writable: false);
        using var waveStream = new RawSourceWaveStream(rawStream, rawFormat);
        using var resampler = new MediaFoundationResampler(
            waveStream,
            new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels))
        {
            ResamplerQuality = 60
        };
        using var convertedStream = new MemoryStream();

        var buffer = new byte[TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8)];
        int bytesRead;

        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            convertedStream.Write(buffer, 0, bytesRead);
        }

        return convertedStream.ToArray();
    }
}

internal sealed record PcmAudioBuffer(
    byte[] Pcm16KhzMono,
    int SampleRate,
    int BitsPerSample,
    int Channels)
{
    public bool IsEmpty => this.Pcm16KhzMono.Length == 0;

    public TimeSpan Duration
    {
        get
        {
            var bytesPerSecond = this.SampleRate * this.Channels * (this.BitsPerSample / 8);
            return bytesPerSecond == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)this.Pcm16KhzMono.Length / bytesPerSecond);
        }
    }
}
