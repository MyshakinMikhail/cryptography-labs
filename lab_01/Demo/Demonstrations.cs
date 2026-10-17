using System.Text;
using System.Text.Json;
using CryptoLab;
using PaddingMode = CryptoLab.PaddingMode;

internal static class Demonstrations
{
    public static async Task RunAsync(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine("lab_01", "demo-artifacts"));
        var extraFiles = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length: root = Path.GetFullPath(args[++i]); break;
                case "--file" when i + 1 < args.Length: extraFiles.Add(Path.GetFullPath(args[++i])); break;
                default: throw new ArgumentException("Usage: [--test] or [--output DIRECTORY] [--file PATH ...]");
            }
        }
        foreach (var path in extraFiles)
            if (!File.Exists(path)) throw new FileNotFoundException("Input file not found.", path);

        var fixtures = Path.Combine(root, "inputs");
        Directory.CreateDirectory(fixtures);
        var files = await SampleFiles.CreateAsync(fixtures);
        files.AddRange(extraFiles);
        Console.WriteLine("Файлы для проверки:");
        foreach (var path in files)
            Console.WriteLine($"  {Path.GetFileName(path)}: {new FileInfo(path).Length} байт");
        var random = new Random(2026);
        var data = new byte[257];
        random.NextBytes(data);
        data[^1] = 0;
        var manifest = new List<object>();

        foreach (var algorithm in new[] { "DES", "DEAL" })
        {
            var key = Convert.FromHexString(algorithm == "DES"
                ? "133457799BBCDFF1" : "000102030405060708090A0B0C0D0E0F");
            foreach (var mode in Enum.GetValues<EncryptionMode>())
            foreach (var padding in Enum.GetValues<PaddingMode>())
            {
                ISymmetricCipher cipher = algorithm == "DES" ? new DesCipher() : new DealCipher();
                var iv = new byte[cipher.BlockSizeBytes];
                var delta = new byte[cipher.BlockSizeBytes];
                random.NextBytes(iv);
                random.NextBytes(delta);
                delta[^1] |= 1;
                var extra = mode == EncryptionMode.RandomDelta ? new object[] { delta } : [];
                var context = new SymmetricCipherContext(cipher, key, mode, padding, iv, extra);
                var encrypted = await context.EncryptAsync(data);
                var restored = await context.DecryptAsync(encrypted, originalLength: data.Length);
                EnsureEqual(data, restored);
                var directory = Path.Combine(root, algorithm, $"{mode}_{padding}");
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, "random.original.bin"), data);
                await File.WriteAllBytesAsync(Path.Combine(directory, "random.encrypted.bin"), encrypted);
                await File.WriteAllBytesAsync(Path.Combine(directory, "random.restored.bin"), restored);

                var records = new List<object>();
                for (var index = 0; index < files.Count; index++)
                {
                    var source = files[index];
                    var name = $"{index + 1}_{Path.GetFileName(source)}";
                    var ciphertextPath = Path.Combine(directory, name + ".enc");
                    var restoredPath = Path.Combine(directory, "restored_" + name);
                    var length = new FileInfo(source).Length;
                    await context.EncryptAsync(source, ciphertextPath);
                    await context.DecryptAsync(ciphertextPath, restoredPath, length);
                    await EnsureFilesEqualAsync(source, restoredPath);
                    records.Add(new
                    {
                        Source = source, OriginalLength = length,
                        Ciphertext = ciphertextPath, Restored = restoredPath, Equal = true,
                    });
                }
                manifest.Add(new
                {
                    Algorithm = algorithm, Mode = mode.ToString(), Padding = padding.ToString(),
                    KeyHex = Convert.ToHexString(key), // Public, fixed demonstration keys only.
                    IvHex = mode == EncryptionMode.Ecb ? null : Convert.ToHexString(iv),
                    DeltaHex = mode == EncryptionMode.RandomDelta ? Convert.ToHexString(delta) : null,
                    ArrayLength = data.Length, CiphertextLength = encrypted.Length, Files = records,
                });
                Console.WriteLine($"1.{(algorithm == "DES" ? 5 : 7)}: {algorithm,-4} {mode,-11} {padding,-9}: "
                    + $"массив {data.Length} -> {encrypted.Length} -> {restored.Length} байт, файлов {files.Count}: OK");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Проверено {manifest.Count} сочетаний, "
            + $"{manifest.Count * files.Count} файловых восстановлений и {manifest.Count} восстановлений массива.");
        Console.WriteLine($"Исходные файлы, шифротексты, восстановленные файлы и параметры: {root}");
    }

    private static void EnsureEqual(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException("Array round trip failed.");
    }

    private static async Task EnsureFilesEqualAsync(string source, string restored)
    {
        await using var left = File.OpenRead(source);
        await using var right = File.OpenRead(restored);
        if (left.Length != right.Length) throw new InvalidOperationException("File lengths differ.");
        var a = new byte[65536];
        var b = new byte[65536];
        while (true)
        {
            var count = await left.ReadAtLeastAsync(a, a.Length, throwOnEndOfStream: false);
            if (count == 0) break;
            await right.ReadExactlyAsync(b.AsMemory(0, count));
            if (!a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, count)))
                throw new InvalidOperationException("Restored file differs from the source.");
        }
    }
}

// Valid media and source-code fixtures make the demonstration self-contained.
internal static class SampleFiles
{
    public static async Task<List<string>> CreateAsync(string directory)
    {
        var samples = new Dictionary<string, byte[]>
        {
            ["sample.txt"] = Encoding.UTF8.GetBytes("Лабораторная №1: DES и DEAL.\nТекст с нулевым байтом в конце.\0"),
            ["sample.bmp"] = Bitmap(),
            ["sample.wav"] = Wave(),
            ["sample.avi"] = Video(),
        };
        var paths = new List<string>();
        foreach (var (name, content) in samples)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(path, content);
            paths.Add(path);
        }
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "SourceSamples");
        foreach (var source in Directory.GetFiles(sourceDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            var path = Path.Combine(directory, Path.GetFileName(source));
            await File.WriteAllBytesAsync(path, await File.ReadAllBytesAsync(source));
            paths.Add(path);
        }
        return paths;
    }

    private static byte[] Pixels(int width, int height, int frame = 0)
    {
        var stride = (width * 3 + 3) / 4 * 4;
        var result = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + 3 * x;
            var square = x >= frame && x < frame + 5 && y >= 5 && y < 10;
            result[offset] = square ? (byte)255 : (byte)(255 * x / width);
            result[offset + 1] = square ? (byte)255 : (byte)(255 * y / height);
            result[offset + 2] = square ? (byte)255 : (byte)64;
        }
        return result;
    }

    private static void BitmapInfo(BinaryWriter writer, int width, int height)
    {
        writer.Write(40); writer.Write(width); writer.Write(height);
        writer.Write((short)1); writer.Write((short)24); writer.Write(0);
        writer.Write((width * 3 + 3) / 4 * 4 * height); writer.Write(2835); writer.Write(2835);
        writer.Write(0); writer.Write(0);
    }

    private static byte[] Bitmap()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var pixels = Pixels(32, 24);
        writer.Write(Encoding.ASCII.GetBytes("BM")); writer.Write(54 + pixels.Length);
        writer.Write(0); writer.Write(54); BitmapInfo(writer, 32, 24); writer.Write(pixels);
        return stream.ToArray();
    }

    private static byte[] Wave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        const int sampleRate = 8000;
        const int noteSamples = sampleRate / 4;
        // Eight distinct notes form a two-second C-major melody, not a single tone.
        double[] notes = [261.63, 329.63, 392.00, 523.25, 440.00, 349.23, 392.00, 523.25];
        var dataLength = notes.Length * noteSamples * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataLength);
        foreach (var frequency in notes)
        for (var i = 0; i < noteSamples; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i / 80.0, (noteSamples - 1 - i) / 160.0));
            writer.Write((short)(12000 * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate)));
        }
        return stream.ToArray();
    }

    private static byte[] Video()
    {
        const int width = 16, height = 16, frameCount = 8, framesPerSecond = 8;
        const int frameSize = width * height * 3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Chunk(writer, "RIFF", () =>
        {
            FourCC(writer, "AVI ");
            Chunk(writer, "LIST", () =>
            {
                FourCC(writer, "hdrl");
                Chunk(writer, "avih", () =>
                {
                    foreach (var value in new[] { 1000000 / framesPerSecond, frameSize * framesPerSecond,
                        0, 16, frameCount, 0, 1, frameSize, width, height, 0, 0, 0, 0 }) writer.Write(value);
                });
                Chunk(writer, "LIST", () =>
                {
                    FourCC(writer, "strl");
                    Chunk(writer, "strh", () =>
                    {
                        FourCC(writer, "vids"); FourCC(writer, "DIB ");
                        writer.Write(0); writer.Write((short)0); writer.Write((short)0);
                        foreach (var value in new[] { 0, 1, framesPerSecond, 0, frameCount, frameSize, -1, 0 }) writer.Write(value);
                        writer.Write((short)0); writer.Write((short)0);
                        writer.Write((short)width); writer.Write((short)height);
                    });
                    Chunk(writer, "strf", () => BitmapInfo(writer, width, height));
                });
            });
            var offsets = new List<int>();
            Chunk(writer, "LIST", () =>
            {
                var start = stream.Position;
                FourCC(writer, "movi");
                for (var frame = 0; frame < frameCount; frame++)
                {
                    offsets.Add(checked((int)(stream.Position - start)));
                    Chunk(writer, "00db", () => writer.Write(Pixels(width, height, frame)));
                }
            });
            Chunk(writer, "idx1", () =>
            {
                foreach (var offset in offsets)
                {
                    FourCC(writer, "00db"); writer.Write(16); writer.Write(offset); writer.Write(frameSize);
                }
            });
        });
        return stream.ToArray();
    }

    private static void FourCC(BinaryWriter writer, string value) => writer.Write(Encoding.ASCII.GetBytes(value));

    private static void Chunk(BinaryWriter writer, string kind, Action writeContent)
    {
        FourCC(writer, kind);
        var sizePosition = writer.BaseStream.Position;
        writer.Write(0);
        var start = writer.BaseStream.Position;
        writeContent();
        var end = writer.BaseStream.Position;
        writer.BaseStream.Position = sizePosition;
        writer.Write(checked((int)(end - start)));
        writer.BaseStream.Position = end;
        if ((end - start) % 2 != 0) writer.Write((byte)0);
    }
}
