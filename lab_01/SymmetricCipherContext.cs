namespace CryptoLab;

public sealed class SymmetricCipherContext
{
    private readonly ISymmetricCipher _cipher;
    private readonly byte[] _iv;
    private readonly byte[] _delta;

    public SymmetricCipherContext(ISymmetricCipher cipher, byte[] key,
        EncryptionMode mode, PaddingMode padding, byte[]? iv = null,
        params object[] modeParameters)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(modeParameters);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(padding)) throw new ArgumentOutOfRangeException(nameof(padding));
        if (cipher.BlockSizeBytes is < 1 or > 255)
            throw new ArgumentException("Block size must be between 1 and 255 bytes.", nameof(cipher));
        if (mode != EncryptionMode.Ecb && iv is null)
            throw new ArgumentException("This mode requires an IV.", nameof(iv));
        if (iv is not null && iv.Length != cipher.BlockSizeBytes)
            throw new ArgumentException("IV must have the same size as a block.", nameof(iv));

        _delta = [];
        if (mode == EncryptionMode.RandomDelta)
        {
            if (modeParameters.Length != 1 || modeParameters[0] is not byte[] delta
                || delta.Length != cipher.BlockSizeBytes || (delta[^1] & 1) == 0)
                throw new ArgumentException(
                    "Random Delta requires one block-sized, odd, big-endian delta.", nameof(modeParameters));
            _delta = delta.ToArray();
        }
        else if (modeParameters.Length != 0)
        {
            throw new ArgumentException("This mode takes no additional parameters.", nameof(modeParameters));
        }

        _cipher = cipher;
        _iv = iv?.ToArray() ?? new byte[cipher.BlockSizeBytes];
        Mode = mode;
        Padding = padding;
        // The configured cipher belongs to this context; do not rekey it externally.
        _cipher.SetKey(key);
    }

    public EncryptionMode Mode { get; }
    public PaddingMode Padding { get; }
    public int BlockSizeBytes => _cipher.BlockSizeBytes;
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    public long GetCiphertextLength(long plaintextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        var padding = BlockSizeBytes - plaintextLength % BlockSizeBytes;
        if (Padding == PaddingMode.Zeros && padding == BlockSizeBytes) padding = 0;
        return checked(plaintextLength + padding);
    }

    public Task<byte[]> EncryptAsync(byte[] input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var snapshot = input.ToArray();
        return Task.Run(() =>
        {
            var padded = BlockPadding.Add(snapshot, BlockSizeBytes, Padding);
            return NewProcessor().Transform(padded, padded.Length, true, cancellationToken);
        }, cancellationToken);
    }

    public Task<byte[]> DecryptAsync(byte[] input, long? originalLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCiphertextLength(input.LongLength, originalLength);
        var snapshot = input.ToArray();
        return Task.Run(() =>
        {
            var decrypted = NewProcessor().Transform(snapshot, snapshot.Length, false, cancellationToken);
            var length = BlockPadding.UnpaddedLength(decrypted, BlockSizeBytes, Padding, originalLength);
            return decrypted.AsSpan(0, length).ToArray();
        }, cancellationToken);
    }

    // C# does not allow ref/out in async methods. A caller-owned byte[] is the
    // output-buffer overload; the return value is the number of bytes written.
    public async Task<int> EncryptAsync(byte[] input, byte[] output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (output.LongLength < GetCiphertextLength(input.LongLength))
            throw new ArgumentException("Output buffer is too small.", nameof(output));
        var result = await EncryptAsync(input, cancellationToken).ConfigureAwait(false);
        result.CopyTo(output, 0);
        return result.Length;
    }

    public async Task<int> DecryptAsync(byte[] input, byte[] output, long? originalLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = await DecryptAsync(input, originalLength, cancellationToken).ConfigureAwait(false);
        if (output.Length < result.Length)
            throw new ArgumentException("Output buffer is too small.", nameof(output));
        result.CopyTo(output, 0);
        return result.Length;
    }

    public Task EncryptAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default) =>
        TransformFileAsync(inputPath, outputPath, true, null, cancellationToken);

    public Task DecryptAsync(string inputPath, string outputPath, long? originalLength = null,
        CancellationToken cancellationToken = default) =>
        TransformFileAsync(inputPath, outputPath, false, originalLength, cancellationToken);

    private Task TransformFileAsync(string inputPath, string outputPath, bool encrypt,
        long? originalLength, CancellationToken token)
    {
        var source = Path.GetFullPath(inputPath);
        var destination = Path.GetFullPath(outputPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output paths must be different.", nameof(outputPath));
        return Task.Run(() => TransformFileCoreAsync(source, destination, encrypt, originalLength, token), token);
    }

    private async Task TransformFileCoreAsync(string source, string destination, bool encrypt,
        long? originalLength, CancellationToken token)
    {
        var processor = NewProcessor();
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!encrypt) ValidateCiphertextLength(input.Length, originalLength);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BlockSizeBytes * 4096];
                long written = 0;
                while (true)
                {
                    var count = await input.ReadAtLeastAsync(buffer, buffer.Length,
                        throwOnEndOfStream: false, cancellationToken: token).ConfigureAwait(false);
                    byte[] transformed;
                    var last = count < buffer.Length || input.Position == input.Length;
                    if (encrypt)
                    {
                        if (count == buffer.Length)
                        {
                            transformed = processor.Transform(buffer, count, true, token);
                            await output.WriteAsync(transformed, token).ConfigureAwait(false);
                            // Even an aligned message gets a full nonzero padding block.
                            continue;
                        }
                        var padded = BlockPadding.Add(buffer.AsSpan(0, count), BlockSizeBytes, Padding);
                        transformed = processor.Transform(padded, padded.Length, true, token);
                    }
                    else
                    {
                        transformed = processor.Transform(buffer, count, false, token);
                        if (last)
                        {
                            var length = BlockPadding.UnpaddedLength(transformed, BlockSizeBytes,
                                Padding, originalLength is null ? null : originalLength - written);
                            transformed = transformed.AsSpan(0, length).ToArray();
                        }
                    }
                    await output.WriteAsync(transformed, token).ConfigureAwait(false);
                    written += transformed.Length;
                    if (last) break;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private CipherModeProcessor NewProcessor()
    {
        if (MaxDegreeOfParallelism is 0 or < -1)
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism));
        return new CipherModeProcessor(_cipher, Mode, _iv, _delta, MaxDegreeOfParallelism);
    }

    private void ValidateCiphertextLength(long length, long? originalLength)
    {
        if (length % BlockSizeBytes != 0 || (length == 0 && Padding != PaddingMode.Zeros))
            throw new InvalidDataException("Ciphertext must contain complete blocks and padding.");
        if (Padding == PaddingMode.Zeros && originalLength is null)
            throw new ArgumentException("Zeros requires originalLength.", nameof(originalLength));
        if (originalLength is not null
            && (originalLength < 0 || GetCiphertextLength(originalLength.Value) != length))
            throw new InvalidDataException("The original length does not match the ciphertext length.");
    }
}
