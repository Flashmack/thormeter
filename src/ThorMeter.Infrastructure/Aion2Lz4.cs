namespace ThorMeter.Infrastructure;

/// <summary>Bounded LZ4 block decoder adapted from the MIT SkeeveTV Aion-DPS-Meter reference.</summary>
internal static class Aion2Lz4
{
    private const int MaxOutput = 1 << 20;
    public static bool TryDecompress(ReadOnlySpan<byte> source, int expectedSize, out byte[] result)
    {
        result = [];
        if (expectedSize <= 0 || expectedSize > MaxOutput) return false;
        var output = new byte[expectedSize]; int i = 0, written = 0;
        while (i < source.Length)
        {
            int token = source[i++]; int literals = token >> 4;
            if (literals == 15)
            {
                int extra;
                do { if (i >= source.Length) return false; extra = source[i++]; literals += extra; }
                while (extra == 255);
            }
            if (literals > source.Length - i || literals > expectedSize - written) return false;
            source.Slice(i, literals).CopyTo(output.AsSpan(written)); i += literals; written += literals;
            if (i >= source.Length) break;
            if (i + 2 > source.Length) return false;
            int offset = source[i] | source[i + 1] << 8; i += 2;
            int length = token & 15;
            if (length == 15)
            {
                int extra;
                do { if (i >= source.Length) return false; extra = source[i++]; length += extra; }
                while (extra == 255);
            }
            length += 4;
            if (offset == 0 || offset > written || length > expectedSize - written) return false;
            for (int n = 0; n < length; n++) { output[written] = output[written - offset]; written++; }
        }
        if (written != expectedSize) return false;
        result = output; return true;
    }
}
