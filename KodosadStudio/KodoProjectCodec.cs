using System.IO;
using System.Text.Json;

namespace KodosadStudio;

public sealed record KodoProjectDocument(string Name, string Algorithm, string Source, string? Result, DateTime SavedAt);

public static class KodoProjectCodec
{
    private const int CurrentVersion = 1;
    private const int MaxSourceCharacters = 10_000_000;
    private const int MaxFileCharacters = 64_000_000;
    private static readonly string[] Algorithms = ["Хаффман", "Шеннон–Фано", "RLE"];
    private sealed record Envelope(int Version, byte[] ProtectedPayload);

    public static string Serialize(KodoProjectDocument project)
    {
        Validate(project);
        var payload = JsonSerializer.Serialize(project);
        var envelope = new Envelope(CurrentVersion, DataProtection.Protect(payload));
        return JsonSerializer.Serialize(envelope);
    }

    public static KodoProjectDocument Deserialize(string serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized) || serialized.Length > MaxFileCharacters)
            throw new InvalidDataException("Файл проекта пуст или превышает допустимый размер.");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(serialized)
                ?? throw new InvalidDataException("Не удалось прочитать контейнер KODO.");
            if (envelope.Version != CurrentVersion || envelope.ProtectedPayload.Length == 0)
                throw new InvalidDataException("Версия или содержимое проекта KODO не поддерживается.");
            var payload = DataProtection.Unprotect(envelope.ProtectedPayload);
            var project = JsonSerializer.Deserialize<KodoProjectDocument>(payload)
                ?? throw new InvalidDataException("Не удалось прочитать данные проекта KODO.");
            Validate(project);
            return project;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidDataException("Проект повреждён или защищён профилем другого пользователя Windows.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный формат проекта KODO.", ex);
        }
    }

    private static void Validate(KodoProjectDocument project)
    {
        if (string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 120)
            throw new InvalidDataException("Название проекта должно содержать от 1 до 120 символов.");
        if (!Algorithms.Contains(project.Algorithm, StringComparer.Ordinal))
            throw new InvalidDataException("В проекте указан неизвестный алгоритм.");
        if (project.Source is null || project.Source.Length > MaxSourceCharacters)
            throw new InvalidDataException("Исходный текст отсутствует или превышает ограничение в 10 миллионов символов.");
        if (project.Result?.Length > MaxFileCharacters)
            throw new InvalidDataException("Результат в проекте превышает допустимый размер.");
    }
}
