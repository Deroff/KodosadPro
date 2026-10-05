using System.IO;
using System.Security.Cryptography;

namespace KodosadStudio;

/// <summary>A compact, checksummed binary container for a Huffman bitstream.</summary>
public static class HuffmanArchiveCodec
{
    public const int MaxArchiveBytes = 64_000_000;
    private static ReadOnlySpan<byte> Magic => "KHF1"u8;
    private const int ChecksumLength = 32;

    private sealed class DecodeNode
    {
        public char? Symbol { get; set; }
        public DecodeNode? Zero { get; set; }
        public DecodeNode? One { get; set; }
    }

    public static byte[] Serialize(CompressionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Algorithm != "Хаффман" || result.SourceCharacters is <= 0 or > CompressionEngine.MaxDecodedCharacters)
            throw new InvalidDataException("Для архива KHF нужен непустой результат Хаффмана размером не более 10 миллионов символов.");
        if (result.Output.Length != result.OutputBits || result.Codes.Count is 0 or > char.MaxValue + 1)
            throw new InvalidDataException("Результат Хаффмана имеет некорректные метаданные.");

        long codeTableBytes = 0;
        foreach (var (symbol, code) in result.Codes)
        {
            if (string.IsNullOrEmpty(code) || code.Length > ushort.MaxValue || code.Any(bit => bit is not ('0' or '1')))
                throw new InvalidDataException("Таблица кодов Хаффмана повреждена.");
            codeTableBytes += sizeof(ushort) * 2L + (code.Length + 7L) / 8;
        }

        var payloadBytes = (result.OutputBits + 7L) / 8;
        var bodyLength = 4L + sizeof(int) * 3L + codeTableBytes + payloadBytes;
        if (bodyLength + ChecksumLength > MaxArchiveBytes)
            throw new InvalidDataException("Архив превысит ограничение размера 64 МБ.");

        using var bodyStream = new MemoryStream((int)bodyLength);
        using (var writer = new BinaryWriter(bodyStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(result.SourceCharacters);
            writer.Write(result.OutputBits);
            writer.Write(result.Codes.Count);
            foreach (var (symbol, code) in result.Codes.OrderBy(pair => pair.Key))
            {
                writer.Write((ushort)symbol);
                writer.Write((ushort)code.Length);
                writer.Write(PackBits(code));
            }
            writer.Write(PackBits(result.Output));
        }

        var body = bodyStream.ToArray();
        var archive = new byte[body.Length + ChecksumLength];
        body.CopyTo(archive, 0);
        SHA256.HashData(body).CopyTo(archive, body.Length);
        return archive;
    }

    public static string Deserialize(ReadOnlySpan<byte> archive)
    {
        if (archive.Length < 4 + sizeof(int) * 3 + ChecksumLength || archive.Length > MaxArchiveBytes)
            throw new InvalidDataException("Архив KHF пуст, слишком велик или усечён.");

        var body = archive[..^ChecksumLength];
        var actualChecksum = SHA256.HashData(body);
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, archive[^ChecksumLength..]))
            throw new InvalidDataException("Контрольная сумма KHF не совпадает: архив повреждён.");

        try
        {
            using var stream = new MemoryStream(body.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("Сигнатура файла не соответствует формату KHF.");

            var characterCount = reader.ReadInt32();
            var bitCount = reader.ReadInt32();
            var codeCount = reader.ReadInt32();
            if (characterCount is <= 0 or > CompressionEngine.MaxDecodedCharacters || bitCount < 0 ||
                codeCount is <= 0 or > char.MaxValue + 1 || codeCount > characterCount)
                throw new InvalidDataException("Заголовок KHF содержит недопустимые размеры.");

            var codes = new Dictionary<char, string>(codeCount);
            for (var i = 0; i < codeCount; i++)
            {
                var symbol = (char)reader.ReadUInt16();
                var codeLength = reader.ReadUInt16();
                if (codeLength == 0 || codes.ContainsKey(symbol))
                    throw new InvalidDataException("Таблица кодов KHF содержит повтор или пустой код.");
                var code = UnpackBits(ReadExact(reader, (codeLength + 7) / 8), codeLength);
                codes.Add(symbol, code);
            }

            var payloadLength = (bitCount + 7L) / 8;
            if (payloadLength != stream.Length - stream.Position)
                throw new InvalidDataException("Длина битового потока KHF не совпадает с заголовком.");
            var payload = ReadExact(reader, (int)payloadLength);
            ValidatePadding(payload, bitCount);

            var root = BuildDecodeTree(codes);
            var output = new System.Text.StringBuilder(Math.Min(characterCount, 1_000_000));
            var node = root;
            for (var i = 0; i < bitCount; i++)
            {
                var bit = (payload[i >> 3] >> (7 - (i & 7))) & 1;
                node = bit == 0 ? node.Zero! : node.One!;
                if (node is null)
                    throw new InvalidDataException("Битовый поток не соответствует таблице кодов KHF.");
                if (node.Symbol is { } symbol)
                {
                    if (output.Length >= CompressionEngine.MaxDecodedCharacters)
                        throw new InvalidDataException("Восстановленный текст превышает ограничение 10 миллионов символов.");
                    output.Append(symbol);
                    node = root;
                }
            }

            if (node != root || output.Length != characterCount)
                throw new InvalidDataException("Поток KHF оборван или восстановил неверное число символов.");
            return output.ToString();
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Архив KHF усечён.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Размеры в архиве KHF выходят за допустимый диапазон.", ex);
        }
    }

    private static DecodeNode BuildDecodeTree(Dictionary<char, string> codes)
    {
        var root = new DecodeNode();
        foreach (var (symbol, code) in codes)
        {
            var node = root;
            foreach (var bit in code)
            {
                if (node.Symbol is not null)
                    throw new InvalidDataException("Таблица KHF содержит коды с неоднозначным префиксом.");
                if (bit == '0') node = node.Zero ??= new DecodeNode();
                else node = node.One ??= new DecodeNode();
            }
            if (node.Symbol is not null || node.Zero is not null || node.One is not null)
                throw new InvalidDataException("Таблица KHF содержит повторяющиеся или неоднозначные коды.");
            node.Symbol = symbol;
        }
        return root;
    }

    private static byte[] PackBits(string bits)
    {
        var packed = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1') packed[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return packed;
    }

    private static string UnpackBits(byte[] packed, int bitCount)
    {
        var bits = new char[bitCount];
        for (var i = 0; i < bitCount; i++)
            bits[i] = (packed[i >> 3] & (1 << (7 - (i & 7)))) == 0 ? '0' : '1';
        ValidatePadding(packed, bitCount);
        return new string(bits);
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var data = reader.ReadBytes(count);
        if (data.Length != count) throw new EndOfStreamException();
        return data;
    }

    private static void ValidatePadding(byte[] packed, int bitCount)
    {
        var usedInLastByte = bitCount & 7;
        if (usedInLastByte != 0 && packed.Length > 0)
        {
            var unusedMask = (1 << (8 - usedInLastByte)) - 1;
            if ((packed[^1] & unusedMask) != 0)
                throw new InvalidDataException("Заполняющие биты архива KHF должны быть нулевыми.");
        }
    }
}
