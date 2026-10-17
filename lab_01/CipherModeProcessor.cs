using System.Numerics;

namespace CryptoLab;

public enum EncryptionMode
{
    Ecb,
    Cbc,
    Pcbc,
    Cfb,
    Ofb,
    Ctr,
    RandomDelta,
}

// One processor per operation: feedback and block indices never leak between calls.
internal sealed class CipherModeProcessor
{
    private readonly ISymmetricCipher _cipher;
    private readonly EncryptionMode _mode;
    private readonly int _parallelism;
    private readonly BigInteger _initial;
    private readonly BigInteger _step;
    private readonly BigInteger _modulus;
    private byte[] _feedback;
    private long _blockIndex;

    public CipherModeProcessor(ISymmetricCipher cipher, EncryptionMode mode,
        byte[] iv, byte[] delta, int parallelism)
    {
        _cipher = cipher;
        _mode = mode;
        _feedback = iv.ToArray();
        _parallelism = parallelism;
        _initial = new BigInteger(iv, isUnsigned: true, isBigEndian: true);
        _step = mode == EncryptionMode.RandomDelta
            ? new BigInteger(delta, isUnsigned: true, isBigEndian: true) : BigInteger.One;
        _modulus = BigInteger.One << (8 * cipher.BlockSizeBytes);
    }

    public byte[] Transform(byte[] input, int count, bool encrypt, CancellationToken token)
    {
        var size = _cipher.BlockSizeBytes;
        var blocks = count / size;
        var output = new byte[count];
        if (_mode is EncryptionMode.Ctr or EncryptionMode.RandomDelta
            && (BigInteger)_blockIndex + blocks > _modulus)
            throw new InvalidOperationException("The counter sequence would repeat.");

        var parallel = _mode is EncryptionMode.Ecb or EncryptionMode.Ctr or EncryptionMode.RandomDelta
            || (!encrypt && _mode is EncryptionMode.Cbc or EncryptionMode.Cfb);
        if (parallel)
        {
            var options = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = _parallelism,
            };
            Parallel.For(0, blocks, options, index =>
            {
                var offset = index * size;
                var block = input.AsSpan(offset, size);
                var target = output.AsSpan(offset, size);
                switch (_mode)
                {
                    case EncryptionMode.Ecb:
                        if (encrypt) _cipher.EncryptBlock(block, target);
                        else _cipher.DecryptBlock(block, target);
                        break;
                    case EncryptionMode.Cbc:
                        _cipher.DecryptBlock(block, target);
                        XorInto(target, index == 0 ? _feedback : input.AsSpan(offset - size, size));
                        break;
                    case EncryptionMode.Cfb:
                        _cipher.EncryptBlock(
                            index == 0 ? _feedback : input.AsSpan(offset - size, size), target);
                        XorInto(target, block);
                        break;
                    case EncryptionMode.Ctr:
                        _cipher.EncryptBlock(Counter(_blockIndex + index), target);
                        XorInto(target, block);
                        break;
                    case EncryptionMode.RandomDelta:
                        var mask = Counter(_blockIndex + index);
                        if (encrypt)
                        {
                            XorInto(mask, block);
                            _cipher.EncryptBlock(mask, target);
                        }
                        else
                        {
                            _cipher.DecryptBlock(block, target);
                            XorInto(target, mask);
                        }
                        break;
                }
            });

            if (blocks > 0 && _mode is EncryptionMode.Cbc or EncryptionMode.Cfb)
                _feedback = input.AsSpan(count - size, size).ToArray();
        }
        else
        {
            for (var offset = 0; offset < count; offset += size)
            {
                token.ThrowIfCancellationRequested();
                var block = input.AsSpan(offset, size);
                var target = output.AsSpan(offset, size);
                switch (_mode)
                {
                    case EncryptionMode.Cbc:
                    case EncryptionMode.Pcbc:
                        if (encrypt)
                        {
                            var mixed = block.ToArray();
                            XorInto(mixed, _feedback);
                            _cipher.EncryptBlock(mixed, target);
                        }
                        else
                        {
                            _cipher.DecryptBlock(block, target);
                            XorInto(target, _feedback);
                        }
                        _feedback = (encrypt ? target : block).ToArray();
                        if (_mode == EncryptionMode.Pcbc)
                            XorInto(_feedback, encrypt ? block : target);
                        break;
                    case EncryptionMode.Cfb:
                        _cipher.EncryptBlock(_feedback, target);
                        XorInto(target, block);
                        _feedback = target.ToArray();
                        break;
                    case EncryptionMode.Ofb:
                        var nextFeedback = new byte[size];
                        _cipher.EncryptBlock(_feedback, nextFeedback);
                        _feedback = nextFeedback;
                        _feedback.CopyTo(target);
                        XorInto(target, block);
                        break;
                }
            }
        }

        _blockIndex = checked(_blockIndex + blocks);
        return output;
    }

    private byte[] Counter(long index)
    {
        var bytes = ((_initial + index * _step) % _modulus)
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[_cipher.BlockSizeBytes];
        bytes.CopyTo(result, result.Length - bytes.Length);
        return result;
    }

    private static void XorInto(Span<byte> target, ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < target.Length; i++) target[i] ^= value[i];
    }
}
