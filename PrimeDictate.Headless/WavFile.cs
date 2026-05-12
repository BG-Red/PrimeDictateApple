using System.Buffers.Binary;

namespace PrimeDictate;

internal static class WavFile
{
    public static PcmAudioBuffer ReadPcm16KhzMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Captured audio is not a RIFF/WAVE file.");
        }

        int? sampleRate = null;
        short? bitsPerSample = null;
        short? channels = null;
        ReadOnlySpan<byte> pcm = default;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = bytes.AsSpan(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var chunkDataOffset = offset + 8;
            if (chunkSize < 0 || chunkDataOffset + chunkSize > bytes.Length)
            {
                throw new InvalidDataException("Captured WAV file has an invalid chunk size.");
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Captured WAV file has an invalid fmt chunk.");
                }

                var format = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chunkDataOffset, 2));
                if (format != 1)
                {
                    throw new InvalidDataException($"Captured WAV file must be PCM, but format tag was {format}.");
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunkDataOffset + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(chunkDataOffset + 14, 2));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                pcm = bytes.AsSpan(chunkDataOffset, chunkSize);
            }

            offset = chunkDataOffset + chunkSize + (chunkSize % 2);
        }

        if (sampleRate != 16_000 || bitsPerSample != 16 || channels != 1)
        {
            throw new InvalidDataException(
                $"Captured WAV must be 16 kHz, 16-bit, mono PCM. Actual: {sampleRate ?? 0} Hz, {bitsPerSample ?? 0}-bit, {channels ?? 0} channel(s).");
        }

        return new PcmAudioBuffer(pcm.ToArray(), sampleRate.Value, bitsPerSample.Value, channels.Value);
    }
}
