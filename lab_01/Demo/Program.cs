using CryptoLab;

Run("1.1: перестановка битов", CheckPermutation);
Run("1.3: сеть Фейстеля", CheckFeistel);
Run("1.4: DES", CheckDes);
Console.WriteLine("Все проверки пройдены.");

static void Run(string name, Action check)
{
    check();
    Console.WriteLine($"{name}: OK");
}

static void CheckPermutation()
{
    var input = new byte[] { 0b10110010, 0b01100001 };
    var rule = new[] { 1, 10, 16, 8, 9, 2, 15, 4 };
    AssertEqual(new byte[] { 0b11100001 },
        BitPermutation.Permute(input, rule, BitOrder.MostSignificantFirst, 1));

    AssertEqual(new byte[] { 0b00000001 },
        BitPermutation.Permute(new byte[] { 0b10110010 }, new[] { 1 },
            BitOrder.LeastSignificantFirst, 0));
}

static void CheckFeistel()
{
    var network = new FeistelNetwork(8, new SampleKeyExpander(),
        new SampleRoundTransformation(), swapAfterLastRound: true);
    network.SetKey(new byte[] { 1, 2, 3, 4 });
    var input = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
    var encrypted = new byte[8];
    var decrypted = new byte[8];
    network.EncryptBlock(input, encrypted);
    network.DecryptBlock(encrypted, decrypted);
    AssertEqual(input, decrypted);
}

static void CheckDes()
{
    var key = Convert.FromHexString("133457799BBCDFF1");
    var input = Convert.FromHexString("0123456789ABCDEF");
    var expected = Convert.FromHexString("85E813540F0AB405");
    var cipher = new DesCipher(key);
    var encrypted = new byte[8];
    var decrypted = new byte[8];
    cipher.EncryptBlock(input, encrypted);
    AssertEqual(expected, encrypted);
    cipher.DecryptBlock(encrypted, decrypted);
    AssertEqual(input, decrypted);
    Console.WriteLine($"  {Convert.ToHexString(input)} -> {Convert.ToHexString(encrypted)} -> {Convert.ToHexString(decrypted)}");
}

static void AssertEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"Ожидалось {Convert.ToHexString(expected)}, получено {Convert.ToHexString(actual)}.");
    }
}

internal sealed class SampleKeyExpander : IKeyExpander
{
    public IReadOnlyList<byte[]> ExpandKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 4) throw new ArgumentException("Ожидается ключ из 4 байт.", nameof(key));
        return new[] { key.ToArray(), new byte[] { 4, 3, 2, 1 } };
    }
}

internal sealed class SampleRoundTransformation : IRoundTransformation
{
    public byte[] Transform(ReadOnlySpan<byte> halfBlock, ReadOnlySpan<byte> roundKey)
    {
        var output = new byte[4];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (byte)(halfBlock[i] ^ roundKey[i]);
        }
        return output;
    }
}
