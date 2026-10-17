using System.Security.Cryptography;

namespace CryptoLab;

public enum PaddingMode
{
    Zeros,
    AnsiX923,
    Pkcs7,
    Iso10126,
}

internal static class BlockPadding
{
    public static byte[] Add(ReadOnlySpan<byte> input, int blockSize, PaddingMode mode)
    {
        var count = blockSize - input.Length % blockSize;
        if (mode == PaddingMode.Zeros && count == blockSize) count = 0;
        var result = new byte[checked(input.Length + count)];
        input.CopyTo(result);
        if (count == 0) return result;

        switch (mode)
        {
            case PaddingMode.Zeros:
                break;
            case PaddingMode.AnsiX923:
                result[^1] = (byte)count;
                break;
            case PaddingMode.Pkcs7:
                result.AsSpan(input.Length).Fill((byte)count);
                break;
            case PaddingMode.Iso10126:
                RandomNumberGenerator.Fill(result.AsSpan(input.Length, count - 1));
                result[^1] = (byte)count;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return result;
    }

    // Zeros cannot distinguish padding from original trailing zero bytes.
    // The caller supplies the original length rather than silently losing data.
    public static int UnpaddedLength(
        ReadOnlySpan<byte> input, int blockSize, PaddingMode mode, long? originalLength)
    {
        if (mode == PaddingMode.Zeros)
        {
            if (originalLength is null || originalLength < 0 || originalLength > input.Length
                || input.Length - originalLength >= blockSize)
            {
                throw new InvalidDataException("Zeros requires the exact original length.");
            }

            var length = (int)originalLength.Value;
            for (var i = length; i < input.Length; i++)
                if (input[i] != 0) throw new InvalidDataException("Invalid zero padding.");
            return length;
        }

        if (input.Length == 0) throw new InvalidDataException("Missing padding block.");
        var count = input[^1];
        if (count == 0 || count > blockSize || count > input.Length)
            throw new InvalidDataException("Invalid padding length.");

        if (mode is PaddingMode.Pkcs7 or PaddingMode.AnsiX923)
        {
            var expected = mode == PaddingMode.Pkcs7 ? count : 0;
            for (var i = input.Length - count; i < input.Length - 1; i++)
                if (input[i] != expected) throw new InvalidDataException("Invalid padding bytes.");
        }

        var result = input.Length - count;
        if (originalLength is not null && originalLength != result)
            throw new InvalidDataException("The original length does not match the plaintext.");
        return result;
    }
}
