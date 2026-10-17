namespace CryptoLab;

public sealed class DealKeyExpander : IKeyExpander
{
    public IReadOnlyList<byte[]> ExpandKey(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (16 or 24 or 32))
            throw new ArgumentException("DEAL requires a 16-, 24-, or 32-byte key.", nameof(key));

        var des = new DesCipher(Convert.FromHexString("0123456789ABCDEF"));
        var parts = key.Length / 8;
        var keys = new byte[key.Length == 32 ? 8 : 6][];
        var previous = new byte[8];
        for (var round = 0; round < keys.Length; round++)
        {
            var block = key.Slice(round % parts * 8, 8).ToArray();
            for (var i = 0; i < 8; i++) block[i] ^= previous[i];
            // Knudsen's original report defines <i> as the usual integer bit string.
            if (round >= parts) block[^1] ^= (byte)(1 << (round - parts));
            var roundKey = new byte[8];
            des.EncryptBlock(block, roundKey);
            keys[round] = previous = roundKey;
        }
        return keys;
    }
}

// Adapter: the round function of DEAL is an entire DES encryption, not a DES round.
public sealed class DesRoundAdapter : IRoundTransformation
{
    public byte[] Transform(ReadOnlySpan<byte> halfBlock, ReadOnlySpan<byte> roundKey)
    {
        var result = new byte[8];
        new DesCipher(roundKey).EncryptBlock(halfBlock, result);
        return result;
    }
}

public sealed class DealCipher : ISymmetricCipher
{
    private readonly FeistelNetwork _network = new(16,
        new DealKeyExpander(), new DesRoundAdapter(), swapAfterLastRound: false);

    public DealCipher() { }

    public DealCipher(ReadOnlySpan<byte> key) => SetKey(key);

    public int BlockSizeBytes => 16;

    public void SetKey(ReadOnlySpan<byte> key) => _network.SetKey(key);

    public void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ValidateBuffers(input, output);
        var swapped = SwapHalves(input);
        Span<byte> result = stackalloc byte[16];
        _network.EncryptBlock(swapped, result);
        SwapHalves(result).CopyTo(output);
    }

    public void DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ValidateBuffers(input, output);
        var swapped = SwapHalves(input);
        Span<byte> result = stackalloc byte[16];
        _network.DecryptBlock(swapped, result);
        SwapHalves(result).CopyTo(output);
    }

    // DEAL transforms the LEFT half: (L, R) -> (DES_K(L) xor R, L).
    // Conjugating the existing right-oriented network by a swap reuses it unchanged.
    private static byte[] SwapHalves(ReadOnlySpan<byte> block)
    {
        var result = new byte[16];
        block[8..].CopyTo(result);
        block[..8].CopyTo(result.AsSpan(8));
        return result;
    }

    private static void ValidateBuffers(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length != 16 || output.Length < 16)
            throw new ArgumentException("DEAL operates on exactly 16-byte blocks.");
    }
}
