using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KodosadStudio;

internal static class LocalDbPipeResolver
{
    private const string LocalDbPrefix = "(localdb)\\";
    private const string PipeLabel = "Instance pipe name:";

    public static string ResolveServer(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource) || !dataSource.StartsWith(LocalDbPrefix, StringComparison.OrdinalIgnoreCase))
            return dataSource;

        var instance = dataSource.Substring(LocalDbPrefix.Length).Trim();
        if (instance.Length == 0 || instance.Any(c => !IsInstanceCharacter(c)))
            throw new InvalidOperationException("Имя экземпляра LocalDB задано некорректно.");

        var executable = FindSqlLocalDb();
        var info = Run(executable, "info", instance);
        var pipe = ReadPipe(info.Output);
        if (pipe is null)
        {
            var start = Run(executable, "start", instance);
            if (start.ExitCode != 0)
                throw new InvalidOperationException($"Не удалось запустить LocalDB «{instance}»: {start.Error.Trim()} {start.Output.Trim()}".Trim());

            for (var attempt = 0; attempt < 20 && pipe is null; attempt++)
            {
                System.Threading.Thread.Sleep(250);
                info = Run(executable, "info", instance);
                pipe = ReadPipe(info.Output);
            }
        }

        if (pipe is null)
            throw new InvalidOperationException($"LocalDB «{instance}» не выдал канал подключения. {info.Error.Trim()} {info.Output.Trim()}".Trim());
        return pipe;
    }

    private static string? ReadPipe(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = line.IndexOf(PipeLabel, StringComparison.OrdinalIgnoreCase);
            if (marker < 0) continue;
            var pipe = line.Substring(marker + PipeLabel.Length).Trim();
            if (pipe.StartsWith("np:\\\\.\\pipe\\LOCALDB#", StringComparison.OrdinalIgnoreCase)) return pipe;
        }
        return null;
    }

    private static bool IsInstanceCharacter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.';

    private static string FindSqlLocalDb()
    {
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator)
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Select(p => Path.Combine(p.Trim(), "SqlLocalDB.exe")))
            if (File.Exists(path)) return path;

        foreach (var programFiles in new[]
                 { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                     .Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var root = Path.Combine(programFiles, "Microsoft SQL Server");
            if (!Directory.Exists(root)) continue;
            foreach (var version in Directory.GetDirectories(root))
                foreach (var relative in new[] { Path.Combine("Tools", "Binn", "SqlLocalDB.exe"), Path.Combine("LocalDB", "Binn", "SqlLocalDB.exe") })
                {
                    var candidate = Path.Combine(version, relative);
                    if (File.Exists(candidate)) return candidate;
                }
        }

        throw new FileNotFoundException("Не найден SqlLocalDB.exe. Установите SQL Server Express LocalDB.");
    }

    private static ProcessResult Run(string executable, string verb, string instance)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"{verb} \"{instance}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить SqlLocalDB.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed class ProcessResult
    {
        public ProcessResult(int exitCode, string output, string error) => (ExitCode, Output, Error) = (exitCode, output, error);
        public int ExitCode { get; }
        public string Output { get; }
        public string Error { get; }
    }
}
