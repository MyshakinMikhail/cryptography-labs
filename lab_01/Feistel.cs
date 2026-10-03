namespace CryptoLab;

public interface IKeyExpander
{
    IReadOnlyList<byte[]> ExpandKey(ReadOnlySpan<byte> key);
}

public interface IRoundTransformation
{
    byte[] Transform(ReadOnlySpan<byte> halfBlock, ReadOnlySpan<byte> roundKey);
}

public interface ISymmetricCipher
{
    int BlockSizeBytes { get; }

    void SetKey(ReadOnlySpan<byte> key);

    void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output);

    void DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output);
}

public sealed class FeistelNetwork : ISymmetricCipher
{
    private readonly IKeyExpander _keyExpander;
    private readonly IRoundTransformation _roundTransformation;
    private readonly bool _swapAfterLastRound;
    private IReadOnlyList<byte[]>? _roundKeys;

    public FeistelNetwork(
        int blockSizeBytes,
        IKeyExpander keyExpander,
        IRoundTransformation roundTransformation,
        bool swapAfterLastRound)
    {
        if (blockSizeBytes <= 0 || blockSizeBytes % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockSizeBytes),
                "A balanced Feistel network requires a positive even block size.");
        }

        BlockSizeBytes = blockSizeBytes;
        _keyExpander = keyExpander ?? throw new ArgumentNullException(nameof(keyExpander));
        _roundTransformation = roundTransformation
            ?? throw new ArgumentNullException(nameof(roundTransformation));
        _swapAfterLastRound = swapAfterLastRound;
    }

    public int BlockSizeBytes { get; }

    public void SetKey(ReadOnlySpan<byte> key)
    {
        var roundKeys = _keyExpander.ExpandKey(key);
        if (roundKeys.Count == 0)
        {
            throw new ArgumentException("The key expander returned no round keys.", nameof(key));
        }

        _roundKeys = roundKeys.Select(roundKey => roundKey.ToArray()).ToArray();
    }

    public void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ValidateBuffers(input, output);
        var roundKeys = GetRoundKeys();
        var halfSize = BlockSizeBytes / 2;
        var left = input[..halfSize].ToArray();
        var right = input[halfSize..].ToArray();

        foreach (var roundKey in roundKeys)
        {
            var transformed = _roundTransformation.Transform(right, roundKey);
            if (transformed.Length != halfSize)
            {
                throw new InvalidOperationException("The round function returned an invalid block size.");
            }

            var nextRight = Xor(left, transformed);
            left = right;
            right = nextRight;
        }

        if (_swapAfterLastRound)
        {
            right.CopyTo(output);
            left.CopyTo(output[halfSize..]);
        }
        else
        {
            left.CopyTo(output);
            right.CopyTo(output[halfSize..]);
        }
    }

    public void DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ValidateBuffers(input, output);
        var roundKeys = GetRoundKeys();
        var halfSize = BlockSizeBytes / 2;

        var left = (_swapAfterLastRound ? input[halfSize..] : input[..halfSize]).ToArray();
        var right = (_swapAfterLastRound ? input[..halfSize] : input[halfSize..]).ToArray();

        for (var round = roundKeys.Count - 1; round >= 0; round--)
        {
            var previousRight = left;
            var transformed = _roundTransformation.Transform(previousRight, roundKeys[round]);
            if (transformed.Length != halfSize)
            {
                throw new InvalidOperationException("The round function returned an invalid block size.");
            }

            var previousLeft = Xor(right, transformed);
            left = previousLeft;
            right = previousRight;
        }

        left.CopyTo(output);
        right.CopyTo(output[halfSize..]);
    }

    private IReadOnlyList<byte[]> GetRoundKeys() =>
        _roundKeys ?? throw new InvalidOperationException("The cipher key has not been configured.");

    private static byte[] Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)(left[index] ^ right[index]);
        }

        return result;
    }

    private void ValidateBuffers(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length != BlockSizeBytes)
        {
            throw new ArgumentException($"Input must contain exactly {BlockSizeBytes} bytes.", nameof(input));
        }

        if (output.Length < BlockSizeBytes)
        {
            throw new ArgumentException($"Output must contain at least {BlockSizeBytes} bytes.", nameof(output));
        }
    }
}
