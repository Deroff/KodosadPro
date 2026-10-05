using System.Text;
using System.Globalization;

namespace KodosadStudio;

public sealed class CompressionResult
{
    public required string Algorithm { get; init; }
    public required string Output { get; init; }
    public Dictionary<char, string> Codes { get; init; } = [];
    public Dictionary<char, int> Frequencies { get; init; } = [];
    public HuffmanNode? Tree { get; init; }
    public List<string> Steps { get; init; } = [];
    public int SourceCharacters { get; init; }
    public int SourceBytes { get; init; }
    public int OutputBits { get; init; }
    public double Entropy { get; init; }
    public double AverageCodeLength { get; init; }
    public double Redundancy => Math.Max(0, AverageCodeLength - Entropy);
    public double RatioPercent => SourceBytes == 0 ? 0 : OutputBits / (SourceBytes * 8d) * 100;
}

public sealed class HuffmanNode
{
    public char? Symbol { get; init; }
    public int Frequency { get; init; }
    public HuffmanNode? Left { get; init; }
    public HuffmanNode? Right { get; init; }
    public System.Windows.Point Position { get; set; }
}

public static class CompressionEngine
{
    public const int MaxDecodedCharacters = 10_000_000;

    public static CompressionResult Run(string algorithm, string text) => algorithm switch
    {
        "Шеннон–Фано" => ShannonFano(text),
        "RLE" => Rle(text),
        "Хаффман" => Huffman(text),
        _ => throw new ArgumentException("Выберите один из поддерживаемых алгоритмов: Хаффман, Шеннон–Фано или RLE.", nameof(algorithm))
    };

    public static CompressionResult Huffman(string text)
    {
        Validate(text); var frequencies = Frequencies(text); var queue = frequencies.Select(x => new HuffmanNode { Symbol = x.Key, Frequency = x.Value }).ToList(); var steps = new List<string> { $"Создано листьев: {queue.Count}" };
        if (queue.Count == 1) queue.Add(new HuffmanNode { Frequency = 0 });
        while (queue.Count > 1)
        {
            queue = queue.OrderBy(x => x.Frequency).ThenBy(x => x.Symbol ?? '\uffff').ToList(); var left = queue[0]; var right = queue[1]; queue.RemoveRange(0, 2); var merged = new HuffmanNode { Frequency = left.Frequency + right.Frequency, Left = left, Right = right }; queue.Add(merged); steps.Add($"Объединены {NodeName(left)} и {NodeName(right)} → вес {merged.Frequency}");
        }
        var tree = queue[0]; var codes = new Dictionary<char, string>(); MakeCodes(tree, "", codes); var output = string.Concat(text.Select(c => codes[c])); return Result("Хаффман", text, output, frequencies, codes, tree, steps);
    }

    public static CompressionResult ShannonFano(string text)
    {
        Validate(text); var frequencies = Frequencies(text); var codes = frequencies.Keys.ToDictionary(x => x, _ => ""); var steps = new List<string>(); var ordered = frequencies.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList(); Split(ordered, codes, steps, 0); foreach (var key in codes.Keys.ToList()) if (codes[key].Length == 0) codes[key] = "0"; var output = string.Concat(text.Select(c => codes[c])); return Result("Шеннон–Фано", text, output, frequencies, codes, null, steps);
    }

    public static CompressionResult Rle(string text)
    {
        Validate(text); var encoded = new StringBuilder(); var steps = new List<string>(); var i = 0;
        while (i < text.Length) { var count = 1; while (i + count < text.Length && text[i + count] == text[i]) count++; encoded.Append(count).Append(':').Append((int)text[i]).Append(';'); steps.Add($"Серия {Display(text[i])}: {count} символ(а)"); i += count; }
        var output = encoded.ToString(); var bits = Encoding.UTF8.GetByteCount(output) * 8; var f = Frequencies(text); return new CompressionResult { Algorithm = "RLE", Output = output, Frequencies = f, SourceCharacters = text.Length, SourceBytes = Encoding.UTF8.GetByteCount(text), OutputBits = bits, Entropy = Entropy(f, text.Length), AverageCodeLength = text.Length == 0 ? 0 : bits / (double)text.Length, Steps = steps };
    }

    public static string Decode(string algorithm, string input, HuffmanNode? tree, Dictionary<char, string>? codes)
    {
        if (algorithm == "RLE")
        {
            var sb = new StringBuilder();
            long total = 0;
            foreach (var part in input.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(':');
                if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    !int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                    count <= 0 || value < char.MinValue || value > char.MaxValue)
                    throw new InvalidOperationException("Некорректная RLE-последовательность.");
                total += count;
                if (total > MaxDecodedCharacters)
                    throw new InvalidOperationException($"Декодирование ограничено {MaxDecodedCharacters:N0} символами.");
                sb.Append((char)value, count);
            }
            return sb.ToString();
        }
        if (algorithm is "Хаффман" or "Шеннон–Фано" && input.Any(c => c is not ('0' or '1')))
            throw new InvalidOperationException("Код должен содержать только 0 и 1.");
        if (algorithm == "Хаффман") { if (tree is null) throw new InvalidOperationException("Для декодирования требуется дерево текущего проекта."); var sb = new StringBuilder(); var node = tree; foreach (var bit in input) { node = bit == '0' ? node.Left : node.Right; if (node is null) throw new InvalidOperationException("Код не соответствует дереву."); if (node.Symbol is not null) { AppendDecodedCharacter(sb, node.Symbol.Value); node = tree; } } if (node != tree) throw new InvalidOperationException("Последний код оборван."); return sb.ToString(); }
        if (codes is null || codes.Count == 0) throw new InvalidOperationException("Нет таблицы кодов Шеннона–Фано."); var reverse = codes.ToDictionary(x => x.Value, x => x.Key); var buffer = ""; var result = new StringBuilder(); foreach (var bit in input) { buffer += bit; if (reverse.TryGetValue(buffer, out var ch)) { AppendDecodedCharacter(result, ch); buffer = ""; } } if (buffer.Length > 0) throw new InvalidOperationException("Последний код оборван."); return result.ToString();
    }

    private static CompressionResult Result(string algorithm, string text, string output, Dictionary<char, int> frequencies, Dictionary<char, string> codes, HuffmanNode? tree, List<string> steps)
    {
        var avg = codes.Sum(x => frequencies[x.Key] * x.Value.Length) / (double)text.Length; return new CompressionResult { Algorithm = algorithm, Output = output, Frequencies = frequencies, Codes = codes, Tree = tree, Steps = steps, SourceCharacters = text.Length, SourceBytes = Encoding.UTF8.GetByteCount(text), OutputBits = output.Length, Entropy = Entropy(frequencies, text.Length), AverageCodeLength = avg };
    }
    private static Dictionary<char, int> Frequencies(string text) { var f = new Dictionary<char, int>(); foreach (var c in text) f[c] = f.GetValueOrDefault(c) + 1; return f; }
    private static void AppendDecodedCharacter(StringBuilder output, char value)
    {
        if (output.Length >= MaxDecodedCharacters)
            throw new InvalidOperationException($"Декодирование ограничено {MaxDecodedCharacters:N0} символами.");
        output.Append(value);
    }
    private static double Entropy(Dictionary<char, int> f, int length) => f.Values.Sum(v => { var p = v / (double)length; return -p * Math.Log2(p); });
    private static void MakeCodes(HuffmanNode? n, string prefix, Dictionary<char, string> codes) { if (n is null) return; if (n.Symbol is not null) { codes[n.Symbol.Value] = prefix.Length == 0 ? "0" : prefix; return; } MakeCodes(n.Left, prefix + "0", codes); MakeCodes(n.Right, prefix + "1", codes); }
    private static void Split(List<KeyValuePair<char, int>> items, Dictionary<char, string> codes, List<string> steps, int depth)
    {
        if (items.Count <= 1) return; var total = items.Sum(x => x.Value); var running = 0; var split = 1; var best = int.MaxValue; for (var i = 1; i < items.Count; i++) { running += items[i - 1].Value; var diff = Math.Abs(total - 2 * running); if (diff < best) { best = diff; split = i; } } var left = items.Take(split).ToList(); var right = items.Skip(split).ToList(); foreach (var x in left) codes[x.Key] += '0'; foreach (var x in right) codes[x.Key] += '1'; steps.Add($"Уровень {depth + 1}: {left.Sum(x => x.Value)} / {right.Sum(x => x.Value)}"); Split(left, codes, steps, depth + 1); Split(right, codes, steps, depth + 1);
    }
    private static string NodeName(HuffmanNode n) => n.Symbol is null ? $"узел({n.Frequency})" : $"«{Display(n.Symbol.Value)}»({n.Frequency})";
    private static string Display(char c) => c == ' ' ? "пробел" : c == '\n' ? "перенос" : c.ToString();
    private static void Validate(string text) { if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("Добавьте исходный текст или загрузите файл."); }
}
