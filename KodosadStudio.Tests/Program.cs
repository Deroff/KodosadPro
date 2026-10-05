using KodosadStudio;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--capture") return Capture(args[1]);
        if (args.Length == 2 && args[0] == "--capture-auth") return CaptureWindow(() => new AuthWindow(new KodosadDatabase()), 520, 700, args[1]);
        if (args.Length == 2 && args[0] == "--capture-register") return CaptureWindow(CreateRegistrationWindow, 520, 700, args[1]);
        if (args.Length == 2 && args[0] == "--capture-profile") return CaptureWindow(() => new ProfileWindow(
            new UserSession(1, "demo@example.com", "analyst", "Демонстрационный профиль"), new KodosadDatabase()), 480, 610, args[1]);
        if (args.Length == 2 && args[0] == "--capture-reset") return CaptureWindow(() => new PasswordResetWindow(new KodosadDatabase(), "demo@example.com"), 500, 680, args[1]);
        if (args.Length == 2 && args[0] == "--capture-email-verification") return CaptureWindow(() => new EmailVerificationWindow(
            new UserSession(1, "demo@example.com", "analyst", "Демонстрационный профиль"), new KodosadDatabase()), 470, 450, args[1]);
        if (args.Length == 2 && args[0] == "--capture-history") return CaptureWindow(() => new AnalysisHistoryWindow([
            new SavedAnalysisRun(2048, "пример проекта", "Хаффман", 64, 93, 142, 3.218, 4.1, 38.2,
                "Исходная строка для визуального просмотра архива", "001011100101", DateTime.UtcNow)
        ]), 1180, 760, args[1]);
        var passed = 0;
        var failed = 0;
        void Check(string name, Action body)
        {
            try { body(); passed++; Console.WriteLine($"PASS  {name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {name}: {ex.Message}"); }
        }

        Check("email verification code is one-time and six digits", () =>
        {
            var challenge = new EmailCodeChallenge(); var code = challenge.Issue();
            if (code.Length != 6 || !code.All(char.IsAsciiDigit) || !challenge.Verify(code) || challenge.Verify(code))
                throw new Exception("Registration code was not checked as a one-time value.");
            var limited = new EmailCodeChallenge(); var correct = limited.Issue(); var wrong = correct == "000000" ? "000001" : "000000";
            for (var attempt = 0; attempt < 5; attempt++) if (limited.Verify(wrong)) throw new Exception("Incorrect code was accepted.");
            if (limited.Verify(correct)) throw new Exception("Code remained valid after five failed attempts.");
        });

        const string sample = "Теория информации: aaabbbccc — 123!\n";
        foreach (var algorithm in new[] { "Хаффман", "Шеннон–Фано", "RLE" })
        {
            var captured = algorithm;
            Check($"round-trip / {captured}", () =>
            {
                var result = CompressionEngine.Run(captured, sample);
                var decoded = CompressionEngine.Decode(result.Algorithm, result.Output, result.Tree, result.Codes);
                if (decoded != sample) throw new Exception("Decoded text differs from input.");
                if (result.SourceCharacters != sample.Length || result.SourceBytes <= 0 || result.OutputBits <= 0)
                    throw new Exception("Metrics are inconsistent.");
            });
        }
        Check("empty input rejected", () => Expect<InvalidOperationException>(() => CompressionEngine.Run("Хаффман", "")));
        Check("single-symbol input round-trip", () =>
        {
            const string input = "zzzzzz";
            var result = CompressionEngine.Huffman(input);
            if (CompressionEngine.Decode(result.Algorithm, result.Output, result.Tree, result.Codes) != input)
                throw new Exception("Single-symbol decode mismatch.");
        });
        Check("RLE round-trip preserves Unicode and punctuation", () =>
        {
            const string input = "аааа — 🧭🧭🧭\n";
            var result = CompressionEngine.Rle(input);
            if (CompressionEngine.Decode("RLE", result.Output, null, null) != input)
                throw new Exception("RLE decoded text differs from input.");
        });
        Check("RLE rejects a zero-length run", () => Expect<InvalidOperationException>(() => CompressionEngine.Decode("RLE", "0:65;", null, null)));
        Check("RLE rejects values outside UTF-16", () => Expect<InvalidOperationException>(() => CompressionEngine.Decode("RLE", "1:65536;", null, null)));
        Check("RLE decoder has an output-size limit", () => Expect<InvalidOperationException>(() => CompressionEngine.Decode("RLE", $"{CompressionEngine.MaxDecodedCharacters + 1}:65;", null, null)));
        Check("Huffman decoder has an output-size limit", () =>
        {
            var single = CompressionEngine.Huffman("z");
            var oversized = new string(single.Codes['z'][0], CompressionEngine.MaxDecodedCharacters + 1);
            Expect<InvalidOperationException>(() => CompressionEngine.Decode("Хаффман", oversized, single.Tree, single.Codes));
        });
        Check("Shannon-Fano decoder has an output-size limit", () =>
        {
            var single = CompressionEngine.ShannonFano("z");
            var oversized = new string(single.Codes['z'][0], CompressionEngine.MaxDecodedCharacters + 1);
            Expect<InvalidOperationException>(() => CompressionEngine.Decode("Шеннон–Фано", oversized, null, single.Codes));
        });
        Check("Shannon-Fano decoder rejects non-binary input", () =>
        {
            var codes = CompressionEngine.ShannonFano("ab").Codes;
            Expect<InvalidOperationException>(() => CompressionEngine.Decode("Шеннон–Фано", "01x", null, codes));
        });
        Check("KHF binary archive round-trips Cyrillic and supplementary Unicode", () =>
        {
            const string input = "Кодосад · 🧭\nХаффман: без потерь!";
            var archive = HuffmanArchiveCodec.Serialize(CompressionEngine.Huffman(input));
            if (HuffmanArchiveCodec.Deserialize(archive) != input)
                throw new Exception("Imported KHF text differs from its source.");
        });
        Check("KHF archive packs bits and compresses a repetitive source", () =>
        {
            var input = new string('A', 8_000);
            var archive = HuffmanArchiveCodec.Serialize(CompressionEngine.Huffman(input));
            if (archive.Length >= System.Text.Encoding.UTF8.GetByteCount(input))
                throw new Exception("Packed archive should be smaller than this repetitive source.");
            if (HuffmanArchiveCodec.Deserialize(archive) != input)
                throw new Exception("Single-symbol archive did not round-trip.");
        });
        Check("KHF checksum rejects changed archive bytes", () =>
        {
            var archive = HuffmanArchiveCodec.Serialize(CompressionEngine.Huffman("checksum test"));
            archive[archive.Length / 2] ^= 0x40;
            Expect<InvalidDataException>(() => HuffmanArchiveCodec.Deserialize(archive));
        });
        Check("KHF rejects truncated and oversized payloads", () =>
        {
            Expect<InvalidDataException>(() => HuffmanArchiveCodec.Deserialize([1, 2, 3]));
            Expect<InvalidDataException>(() => HuffmanArchiveCodec.Deserialize(new byte[HuffmanArchiveCodec.MaxArchiveBytes + 1]));
        });
        Check("autosave serializes writes and refuses a second in-flight snapshot", () =>
        {
            var state = new DraftAutosaveCoordinator();
            if (state.TryBegin(out _) != DraftSaveStart.Clean) throw new Exception("A clean draft should not be saved.");
            state.MarkChanged();
            if (state.TryBegin(out var first) != DraftSaveStart.Started || state.TryBegin(out _) != DraftSaveStart.Busy)
                throw new Exception("Only one autosave may be active.");
            if (state.Complete(first, succeeded: true) || state.TryBegin(out _) != DraftSaveStart.Clean)
                throw new Exception("The saved revision should become clean.");
        });
        Check("autosave queues edits made during an earlier write", () =>
        {
            var state = new DraftAutosaveCoordinator();
            state.MarkChanged();
            if (state.TryBegin(out var older) != DraftSaveStart.Started) throw new Exception("First snapshot did not start.");
            state.MarkChanged();
            if (!state.Complete(older, succeeded: true) || state.TryBegin(out var newer) != DraftSaveStart.Started || newer == older)
                throw new Exception("The newer edit was not queued after the stale save.");
            if (state.Complete(newer, succeeded: true)) throw new Exception("Latest snapshot should be clean after saving.");
        });
        Check("autosave keeps a failed revision dirty for retry", () =>
        {
            var state = new DraftAutosaveCoordinator();
            state.MarkChanged();
            if (state.TryBegin(out var failed) != DraftSaveStart.Started || !state.Complete(failed, succeeded: false))
                throw new Exception("Failed save should remain pending.");
            if (state.TryBegin(out var retry) != DraftSaveStart.Started || retry != failed)
                throw new Exception("Retry did not use the still-dirty revision.");
            if (state.Complete(retry, succeeded: true)) throw new Exception("Successful retry should clear dirty state.");
        });
        Check("unknown algorithm is rejected", () => Expect<ArgumentException>(() => CompressionEngine.Run("Unknown", "text")));
        Check("Huffman output is deterministic", () =>
        {
            const string input = "mississippi — маршрут";
            if (CompressionEngine.Huffman(input).Output != CompressionEngine.Huffman(input).Output)
                throw new Exception("Same input produced different Huffman streams.");
        });
        Check("KODO project round-trip is DPAPI protected", () =>
        {
            const string source = "личный исходник · 🧭";
            var serialized = KodoProjectCodec.Serialize(new KodoProjectDocument("demo", "Хаффман", source, "101001", DateTime.UtcNow));
            var restored = KodoProjectCodec.Deserialize(serialized);
            if (serialized.Contains(source, StringComparison.Ordinal) || restored.Source != source || restored.Result != "101001")
                throw new Exception("Encrypted KODO project round-trip failed or leaked source text.");
        });
        Check("tampered KODO project is rejected", () =>
        {
            var serialized = KodoProjectCodec.Serialize(new KodoProjectDocument("demo", "RLE", "secret", null, DateTime.UtcNow));
            var envelope = JsonNode.Parse(serialized)!;
            var payload = Convert.FromBase64String(envelope["ProtectedPayload"]!.GetValue<string>());
            payload[0] ^= 0x20;
            envelope["ProtectedPayload"] = Convert.ToBase64String(payload);
            Expect<InvalidDataException>(() => KodoProjectCodec.Deserialize(envelope.ToJsonString()));
        });
        Check("valid email accepted", () => AccountSecurity.ValidateEmail("student@example.com"));
        Check("invalid email rejected", () => Expect<ArgumentException>(() => AccountSecurity.ValidateEmail("student.example.com")));
        Check("strong password accepted", () => AccountSecurity.ValidatePassword("Student!2026"));
        Check("weak password rejected", () => Expect<ArgumentException>(() => AccountSecurity.ValidatePassword("password")));
        Check("common password rejected", () => Expect<ArgumentException>(() => AccountSecurity.ValidatePassword("Password123!")));
        Check("password reset mail uses scoped purpose and bearer token", () =>
        {
            var handler = new RecordingMailHandler(HttpStatusCode.OK);
            using var http = new HttpClient(handler);
            var client = new MailApiClient(new MailApiSettings("https://mail.example.test", new string('x', 32)), http);
            client.SendPasswordResetCodeAsync("student@example.com", new string('A', 32)).GetAwaiter().GetResult();
            if (handler.Paths.Count != 1 || handler.Paths[0] != "/api/mail/account-code" ||
                handler.BearerTokens[0] != new string('x', 32) ||
                handler.Bodies[0].GetProperty("purpose").GetString() != "password-reset" ||
                handler.Bodies[0].GetProperty("appName").GetString() != "Kodosad Studio")
                throw new Exception("Reset request did not use the password-reset Mail API contract.");
        });
        Check("password reset fallback never uses registration endpoint", () =>
        {
            var handler = new RecordingMailHandler(HttpStatusCode.NotFound, HttpStatusCode.OK);
            using var http = new HttpClient(handler);
            new MailApiClient(new MailApiSettings("http://127.0.0.1:5080", new string('y', 32)), http)
                .SendPasswordResetCodeAsync("student@example.com", new string('B', 32)).GetAwaiter().GetResult();
            if (!handler.Paths.SequenceEqual(["/api/mail/account-code", "/api/mail/password-reset"]))
                throw new Exception("Reset fallback used an unexpected endpoint.");
        });
        Check("remote HTTP mail endpoint is rejected", () =>
        {
            var client = new MailApiClient(new MailApiSettings("http://mail.example.test", new string('z', 32)));
            if (client.IsConfigured) throw new Exception("Remote plaintext HTTP was accepted.");
        });
        Check("database identifier rejects SQL syntax", () =>
        {
            var original = Environment.GetEnvironmentVariable("KODOSAD_DATABASE_NAME");
            try
            {
                Environment.SetEnvironmentVariable("KODOSAD_DATABASE_NAME", "Demo]; DROP DATABASE master;--");
                Expect<InvalidOperationException>(() => _ = KodosadDatabase.DatabaseName);
            }
            finally { Environment.SetEnvironmentVariable("KODOSAD_DATABASE_NAME", original); }
        });

        Console.WriteLine($"\nИтог: {passed} успешно, {failed} ошибок.");
        return failed == 0 ? 0 : 1;
    }

    private static int Capture(string output)
    {
        try
        {
            var app = new Application();
            var window = new MainWindow(new UserSession(1, "demo@example.com", "analyst", "Демонстрационный профиль"), new KodosadDatabase());
            var source = (TextBox)typeof(MainWindow).GetField("SourceBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            source.Text = "Кодирование без потерь · Kodosad Studio\nЧастоты → коды → проверка результата.";
            typeof(MainWindow).GetMethod("RunAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1500, 920));
            content.Arrange(new Rect(0, 0, 1500, 920));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1500, 920, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            using var file = File.Create(output);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(file);
            window.Close();
            app.Shutdown();
            Console.WriteLine($"Visual capture saved: {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int CaptureWindow(Func<Window> createWindow, int width, int height, string output)
    {
        try
        {
            var app = new Application();
            var window = createWindow();
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            using var file = File.Create(output);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(file);
            window.Close();
            app.Shutdown();
            Console.WriteLine($"Visual capture saved: {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static Window CreateRegistrationWindow()
    {
        var window = new AuthWindow(new KodosadDatabase());
        var tabs = (TabControl)typeof(AuthWindow).GetField("ModeTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        tabs.SelectedIndex = 1;
        return window;
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class RecordingMailHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> BearerTokens { get; } = [];
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? "");
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            return new HttpResponseMessage(statuses[Math.Min(Paths.Count - 1, statuses.Length - 1)]);
        }
    }
}
