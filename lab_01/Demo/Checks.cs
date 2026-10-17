using CryptoLab;
using PaddingMode = CryptoLab.PaddingMode;

internal static class LabChecks
{
    private static readonly byte[] DesKey = Convert.FromHexString("133457799BBCDFF1");

    public static async Task RunAsync()
    {
        CheckDeal();
        Console.WriteLine("1.6: DEAL-128/192/256, расписание ключей, адаптер DES: OK");
        await CheckModesAsync();
        await CheckValidationAsync();
        await CheckFilesAsync();
        Console.WriteLine("1.2: 7 режимов x 4 набивки, границы блоков, параллельность, файлы: OK");
    }

    private static void CheckDeal()
    {
        foreach (var keyLength in new[] { 16, 24, 32 })
        {
            var key = Enumerable.Range(0, keyLength).Select(i => (byte)i).ToArray();
            var keys = new DealKeyExpander().ExpandKey(key);
            Ensure(keys.Count == (keyLength == 32 ? 8 : 6), "Wrong DEAL round count.");
            var input = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
            var cipher = new DealCipher(key);
            var encrypted = new byte[16];
            var restored = new byte[16];
            cipher.EncryptBlock(input, encrypted);
            cipher.DecryptBlock(encrypted, restored);
            Equal(input, restored);

            // Direct left-oriented equations from Knudsen's report, independent
            // of FeistelNetwork and of DealKeyExpander's iteration scheme.
            var partCount = keyLength / 8;
            var fixedDes = new DesCipher(Convert.FromHexString("0123456789ABCDEF"));
            byte[] constants = partCount switch
            {
                2 => [0, 0, 1, 2, 4, 8],
                3 => [0, 0, 0, 1, 2, 4],
                _ => [0, 0, 0, 0, 1, 2, 4, 8],
            };
            var left = input[..8];
            var right = input[8..];
            var previous = new byte[8];
            for (var round = 0; round < constants.Length; round++)
            {
                var material = key.AsSpan(round % partCount * 8, 8).ToArray();
                for (var i = 0; i < 8; i++) material[i] ^= previous[i];
                material[^1] ^= constants[round];
                var roundKey = new byte[8];
                fixedDes.EncryptBlock(material, roundKey);
                Equal(roundKey, keys[round]);
                previous = roundKey;

                var transformed = new byte[8];
                new DesCipher(roundKey).EncryptBlock(left, transformed);
                Equal(transformed, new DesRoundAdapter().Transform(left, roundKey));
                for (var i = 0; i < 8; i++) transformed[i] ^= right[i];
                right = left;
                left = transformed;
            }
            Equal(left.Concat(right).ToArray(), encrypted);
            cipher.EncryptBlock(input, input);
            Equal(encrypted, input);
            cipher.DecryptBlock(input, input);
            Equal(restored, input);
            Console.WriteLine($"  DEAL-{keyLength * 8}: {Convert.ToHexString(restored)} -> {Convert.ToHexString(encrypted)}");
        }
        Throws<ArgumentException>(() => new DealCipher(new byte[15]));
        Throws<ArgumentException>(() => new DealCipher(new byte[17]));
        Throws<InvalidOperationException>(() => new DealCipher().EncryptBlock(new byte[16], new byte[16]));
    }

    private static async Task CheckModesAsync()
    {
        var random = new Random(2026);
        foreach (var deal in new[] { false, true })
        foreach (var mode in Enum.GetValues<EncryptionMode>())
        foreach (var padding in Enum.GetValues<PaddingMode>())
        {
            var context = Context(deal, mode, padding, 4);
            var size = context.BlockSizeBytes;
            foreach (var length in new[] { 0, 1, size - 1, size, size + 1, 2 * size, 2 * size + 1 })
            {
                var input = new byte[length];
                random.NextBytes(input);
                if (length > 0) input[^1] = 0;
                var encrypted = await context.EncryptAsync(input);
                Ensure(encrypted.LongLength == context.GetCiphertextLength(length), "Wrong padded size.");
                Equal(input, await context.DecryptAsync(encrypted, originalLength: length));
                // ISO 10126 uses fresh random padding; compare its decrypted data instead.
                if (padding != PaddingMode.Iso10126)
                    Equal(encrypted, await Context(deal, mode, padding, 1).EncryptAsync(input));
                Equal(input, await Context(deal, mode, padding, 1).DecryptAsync(encrypted, length));
            }
        }

        // Check mode equations, not just encrypt/decrypt symmetry.
        var plain = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray();
        var iv = new byte[8];
        iv[^1] = 254; // Cross a carry boundary in the big-endian counter.
        foreach (var mode in Enum.GetValues<EncryptionMode>())
        {
            var extra = mode == EncryptionMode.RandomDelta ? new object[] { Delta(8) } : [];
            var context = new SymmetricCipherContext(new DesCipher(), DesKey, mode, PaddingMode.Zeros, iv, extra);
            var actual = await context.EncryptAsync(plain);
            var expected = ReferenceModes(plain, mode, iv);
            Equal(expected, actual);
            Equal(plain, await context.DecryptAsync(expected, plain.Length));
        }

        var shared = Context(false, EncryptionMode.Cbc, PaddingMode.Pkcs7, 4);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shared.EncryptAsync(plain)));
        foreach (var result in results) Equal(results[0], result);
        var buffer = new byte[shared.GetCiphertextLength(plain.Length)];
        Ensure(await shared.EncryptAsync(plain, buffer) == buffer.Length, "Wrong output length.");
        var restoredBuffer = new byte[plain.Length];
        Ensure(await shared.DecryptAsync(buffer, restoredBuffer) == plain.Length, "Wrong plaintext length.");
        Equal(plain, restoredBuffer);
    }

    private static byte[] ReferenceModes(byte[] plain, EncryptionMode mode, byte[] iv)
    {
        var des = new DesCipher(DesKey);
        var result = new byte[plain.Length];
        var feedback = iv.ToArray();
        for (var offset = 0; offset < plain.Length; offset += 8)
        {
            var block = plain.AsSpan(offset, 8).ToArray();
            var encrypted = new byte[8];
            if (mode is EncryptionMode.Cbc or EncryptionMode.Pcbc)
                for (var i = 0; i < 8; i++) block[i] ^= feedback[i];
            if (mode is EncryptionMode.Ecb or EncryptionMode.Cbc or EncryptionMode.Pcbc)
                des.EncryptBlock(block, encrypted);
            else if (mode is EncryptionMode.Cfb or EncryptionMode.Ofb)
            {
                des.EncryptBlock(feedback, encrypted);
                feedback = encrypted.ToArray();
                for (var i = 0; i < 8; i++) encrypted[i] ^= block[i];
            }
            else
            {
                var counter = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counter,
                    254UL + (ulong)(offset / 8) * (mode == EncryptionMode.RandomDelta ? 3UL : 1UL));
                if (mode == EncryptionMode.Ctr)
                {
                    des.EncryptBlock(counter, encrypted);
                    for (var i = 0; i < 8; i++) encrypted[i] ^= block[i];
                }
                else
                {
                    for (var i = 0; i < 8; i++) block[i] ^= counter[i];
                    des.EncryptBlock(block, encrypted);
                }
            }
            encrypted.CopyTo(result, offset);
            if (mode is EncryptionMode.Cbc or EncryptionMode.Cfb or EncryptionMode.Pcbc)
                feedback = encrypted.ToArray();
            if (mode == EncryptionMode.Pcbc)
                for (var i = 0; i < 8; i++) feedback[i] ^= plain[offset + i];
        }
        return result;
    }

    private static async Task CheckValidationAsync()
    {
        Throws<ArgumentException>(() => new SymmetricCipherContext(new DesCipher(), DesKey,
            EncryptionMode.Cbc, PaddingMode.Pkcs7));
        Throws<ArgumentException>(() => new SymmetricCipherContext(new DesCipher(), DesKey,
            EncryptionMode.Ctr, PaddingMode.Pkcs7, new byte[7]));
        Throws<ArgumentException>(() => new SymmetricCipherContext(new DesCipher(), DesKey,
            EncryptionMode.RandomDelta, PaddingMode.Pkcs7, new byte[8], new byte[8]));
        foreach (var padding in new[] { PaddingMode.Pkcs7, PaddingMode.AnsiX923, PaddingMode.Iso10126 })
        {
            var context = Context(false, EncryptionMode.Ecb, padding, 1);
            await ThrowsAsync<InvalidDataException>(() => context.DecryptAsync([]));
            await ThrowsAsync<InvalidDataException>(() => context.DecryptAsync(new byte[7]));
            var badPadding = new byte[8];
            badPadding[^1] = 9;
            var encrypted = new byte[8];
            new DesCipher(DesKey).EncryptBlock(badPadding, encrypted);
            await ThrowsAsync<InvalidDataException>(() => context.DecryptAsync(encrypted));
            if (padding != PaddingMode.Iso10126)
            {
                badPadding[^1] = 2;
                badPadding[^2] = 7;
                new DesCipher(DesKey).EncryptBlock(badPadding, encrypted);
                await ThrowsAsync<InvalidDataException>(() => context.DecryptAsync(encrypted));
            }
        }
        var zeros = Context(false, EncryptionMode.Ecb, PaddingMode.Zeros, 1);
        await ThrowsAsync<ArgumentException>(() => zeros.DecryptAsync(new byte[8]));
        var valid = await zeros.EncryptAsync([1, 2, 0, 0]);
        Equal([1, 2, 0, 0], await zeros.DecryptAsync(valid, 4));
        await ThrowsAsync<InvalidDataException>(() => zeros.DecryptAsync(valid, 0));
        var context2 = Context(false, EncryptionMode.Cbc, PaddingMode.Pkcs7, 1);
        await ThrowsAsync<ArgumentException>(() => context2.EncryptAsync([1], new byte[1]));
        var twoBytes = await context2.EncryptAsync([1, 2]);
        await ThrowsAsync<ArgumentException>(() => context2.DecryptAsync(twoBytes, new byte[1]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => context2.EncryptAsync([1], cancellation.Token));
    }

    private static async Task CheckFilesAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cryptolab-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.bin");
            var encryptedPath = Path.Combine(directory, "encrypted.bin");
            var restoredPath = Path.Combine(directory, "restored.bin");
            var random = new Random(14);
            foreach (var mode in Enum.GetValues<EncryptionMode>())
            foreach (var padding in Enum.GetValues<PaddingMode>())
            foreach (var length in new[] { 0, 32767, 32768, 32769 })
            {
                var input = new byte[length];
                random.NextBytes(input);
                if (length > 0) input[^1] = 0;
                await File.WriteAllBytesAsync(inputPath, input);
                var context = Context(false, mode, padding, 4);
                await context.EncryptAsync(inputPath, encryptedPath);
                var bytes = await File.ReadAllBytesAsync(encryptedPath);
                // File chunks and whole-array processing must preserve the same feedback.
                if (padding != PaddingMode.Iso10126) Equal(await context.EncryptAsync(input), bytes);
                Equal(input, await context.DecryptAsync(bytes, length));
                await context.DecryptAsync(encryptedPath, restoredPath, length);
                Equal(input, await File.ReadAllBytesAsync(restoredPath));
            }
            // DEAL's 16-byte block size gives a different file-buffer boundary.
            var dealInput = new byte[65537];
            random.NextBytes(dealInput);
            await File.WriteAllBytesAsync(inputPath, dealInput);
            var deal = Context(true, EncryptionMode.Ctr, PaddingMode.Pkcs7, 4);
            await deal.EncryptAsync(inputPath, encryptedPath);
            Equal(await deal.EncryptAsync(dealInput), await File.ReadAllBytesAsync(encryptedPath));
            await deal.DecryptAsync(encryptedPath, restoredPath);
            Equal(dealInput, await File.ReadAllBytesAsync(restoredPath));

            var context2 = Context(false, EncryptionMode.Ecb, PaddingMode.Pkcs7, 1);
            await ThrowsAsync<ArgumentException>(() => context2.EncryptAsync(inputPath, inputPath));
            await File.WriteAllBytesAsync(encryptedPath, [1, 2, 3]);
            await File.WriteAllBytesAsync(restoredPath, [42]);
            await ThrowsAsync<InvalidDataException>(() => context2.DecryptAsync(encryptedPath, restoredPath));
            Equal([42], await File.ReadAllBytesAsync(restoredPath));
            Ensure(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary file was not cleaned up.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SymmetricCipherContext Context(bool deal, EncryptionMode mode, PaddingMode padding, int parallelism)
    {
        ISymmetricCipher cipher = deal ? new DealCipher() : new DesCipher();
        var key = deal ? Enumerable.Range(0, 16).Select(i => (byte)i).ToArray() : DesKey;
        var extra = mode == EncryptionMode.RandomDelta ? new object[] { Delta(cipher.BlockSizeBytes) } : [];
        return new SymmetricCipherContext(cipher, key, mode, padding,
            new byte[cipher.BlockSizeBytes], extra) { MaxDegreeOfParallelism = parallelism };
    }

    private static byte[] Delta(int size)
    {
        var delta = new byte[size];
        delta[^1] = 3;
        return delta;
    }

    private static void Equal(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Byte sequences differ.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
