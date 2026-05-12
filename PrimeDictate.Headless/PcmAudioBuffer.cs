namespace PrimeDictate;

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
