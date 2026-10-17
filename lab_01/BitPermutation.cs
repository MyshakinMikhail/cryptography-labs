namespace CryptoLab;

public enum BitOrder
{
    MostSignificantFirst,
    LeastSignificantFirst,
}

public static class BitPermutation
{
    public static byte[] Permute(
        ReadOnlySpan<byte> value,
        IReadOnlyList<int> permutation,
        BitOrder bitOrder = BitOrder.MostSignificantFirst,
        int initialBitNumber = 1)
    {
        if (initialBitNumber is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialBitNumber),
                "The initial bit number must be 0 or 1.");
        }

        var result = new byte[(permutation.Count + 7) / 8];
        for (var destinationBit = 0; destinationBit < permutation.Count; destinationBit++)
        {
            var sourceBit = permutation[destinationBit] - initialBitNumber;
            if ((uint)sourceBit >= (uint)(value.Length * 8))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(permutation),
                    $"Bit index {permutation[destinationBit]} is outside the input value.");
            }

            var bit = ReadBit(value, sourceBit, bitOrder);
            WriteBit(result, destinationBit, bit, bitOrder);
        }

        return result;
    }

    private static int ReadBit(ReadOnlySpan<byte> value, int bitIndex, BitOrder bitOrder)
    {
        var byteIndex = bitIndex / 8;
        var offset = bitIndex % 8;
        var shift = bitOrder == BitOrder.MostSignificantFirst ? 7 - offset : offset;
        return (value[byteIndex] >> shift) & 1;
    }

    private static void WriteBit(Span<byte> value, int bitIndex, int bit, BitOrder bitOrder)
    {
        if (bit == 0)
        {
            return;
        }

        var byteIndex = bitIndex / 8;
        var offset = bitIndex % 8;
        var shift = bitOrder == BitOrder.MostSignificantFirst ? 7 - offset : offset;
        value[byteIndex] |= (byte)(1 << shift);
    }
}
