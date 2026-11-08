#if AIRCON_DEVELOPER_DIAGNOSTICS
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// Developer log extraction boundary. Only self-owned AIrCon records, or Host records
// explicitly addressed solely to AIrCon, may be supplied to AIrCon's analysis.
// All host-side recording/EPG/other-plugin events are rejected by default.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
            return RunSelfTests();

        if (args.Length != 3 || args[1] != "--output" || string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine("Usage: AIrCon.DevLogScope <TvAIr developer JSON or ZIP> --output <AIrCon-only JSONL>");
            return 2;
        }

        try
        {
            using JsonDocument input = ReadDocument(args[0]);
            if (input.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Developer log root must be a JSON array.");

            string destination = Path.GetFullPath(args[2]);
            string source = Path.GetFullPath(args[0]);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Input and output must be different files.");

            // Atomic replacement avoids leaving a usable-looking partial report on failure.
            string temp = destination + ".pending";
            int accepted = 0;
            int examined = 0;
            try
            {
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
                {
                    foreach (JsonElement element in input.RootElement.EnumerateArray())
                    {
                        examined++;
                        if (!IsAIrConOwned(element)) continue;
                        writer.WriteLine(element.GetRawText());
                        accepted++;
                    }
                }
                File.Move(temp, destination, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            Console.Error.WriteLine($"AIrCon-only records: {accepted} / {examined}. Unrelated records excluded.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Extraction failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static JsonDocument ReadDocument(string path)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return JsonDocument.Parse(stream);
        }
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries
            .Where(e => e.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                        e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault() ?? throw new InvalidDataException("No JSON/TXT developer log in ZIP.");
        using var zipped = entry.Open();
        return JsonDocument.Parse(zipped);
    }

    private static bool IsAIrConOwned(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        string category = GetString(item, "event");
        string title = GetString(item, "title");
        string message = GetString(item, "message");
        if (category.Equals("AIrCon", StringComparison.OrdinalIgnoreCase))
            return true;

        // Host resolution of an AIrCon-owned UI entry is a direct, scoped observation.
        if (category.Equals("PLUGIN_MENU_ACTION_RESOLVE", StringComparison.Ordinal) &&
            title.Equals("AIrCon", StringComparison.OrdinalIgnoreCase) &&
            FieldIs(message, "route", "aircon"))
            return true;

        // Shared dispatch counts cannot be attributed to AIrCon alone. Include only
        // unambiguously single-recipient delivery, not "aircon,other-plugin".
        if (category.Equals("PLUGIN_TYPED_EVENT_DISPATCH", StringComparison.Ordinal) &&
            FieldIs(message, "pluginIds", "aircon"))
            return true;
        return false;
    }

    private static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

    private static bool FieldIs(string message, string name, string expected)
    {
        foreach (string field in message.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = field.IndexOf('=');
            if (equals < 0) continue;
            if (field.AsSpan(0, equals).Equals(name, StringComparison.Ordinal) &&
                field.AsSpan(equals + 1).Equals(expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int RunSelfTests()
    {
        (string Json, bool Allowed)[] cases =
        {
            ("{\"event\":\"AIrCon\",\"message\":\"own projection\"}", true),
            ("{\"event\":\"EPG_IMPORT_COMMIT\",\"message\":\"AIrCon mentioned\"}", false),
            ("{\"event\":\"PLUGIN_MENU_ACTION_RESOLVE\",\"title\":\"AIrCon\",\"message\":\"route=aircon result=OK\"}", true),
            ("{\"event\":\"PLUGIN_MENU_ACTION_RESOLVE\",\"title\":\"AIrCon\",\"message\":\"route=another\"}", false),
            ("{\"event\":\"PLUGIN_TYPED_EVENT_DISPATCH\",\"message\":\"pluginIds=aircon,other deliveredCount=2\"}", false),
            ("{\"event\":\"PLUGIN_TYPED_EVENT_DISPATCH\",\"message\":\"pluginIds=aircon deliveredCount=1\"}", true),
            ("{\"event\":\"TUNER_CONFLICT\",\"title\":\"AIrCon\",\"message\":\"route=aircon\"}", false),
            ("{\"event\":\"OtherPlugin\",\"message\":\"AIrCon error\"}", false),
        };
        for (int i = 0; i < cases.Length; i++)
        {
            using var doc = JsonDocument.Parse(cases[i].Json);
            if (IsAIrConOwned(doc.RootElement) != cases[i].Allowed)
            {
                Console.Error.WriteLine($"FAILED case {i + 1}");
                return 1;
            }
        }
        Console.Error.WriteLine($"Scope filter tests passed: {cases.Length}");
        return 0;
    }
}
#endif
