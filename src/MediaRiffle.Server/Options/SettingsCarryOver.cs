using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MediaRiffle.Server.Options;

/// <summary>
/// Keeps settings someone put in the program folder's appsettings.json, which every upgrade replaces, by carrying them
/// into the settings file that stays (/etc/mediariffle/appsettings.json, %ProgramData%\MediaRiffle\appsettings.json).
/// The installers save the old program-folder file before replacing it, and run the new program with
/// <c>--carry-settings &lt;saved file&gt;</c>. A setting is carried when it isn't the default (MediaRiffle's built-in
/// defaults, or the new program-folder file for the rest: Logging, Kestrel, …) and the settings file doesn't set it, or
/// sets it only to the default: a setting the settings file itself changed always wins.
/// </summary>
public static class SettingsCarryOver
{
    public const string Argument = "--carry-settings";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Defaults = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// What everything is when nobody set it: the new program-folder file, with MediaRiffle's built-in defaults under
    /// "MediaRiffle" (the program-folder file needn't list them).
    /// </summary>
    public static JsonObject Baseline(JsonObject? programFolder)
    {
        var baseline = programFolder?.DeepClone().AsObject() ?? new JsonObject();
        var defaults = JsonSerializer.SerializeToNode(new MediaRiffleOptions(), Defaults)!.AsObject();
        if (Find(baseline, MediaRiffleOptions.Section) is JsonObject listed) Overlay(defaults, listed);
        Set(baseline, [MediaRiffleOptions.Section], defaults);
        return baseline;
    }

    /// <summary>The settings in <paramref name="old"/> that aren't <paramref name="baseline"/>'s, by path ("MediaRiffle:Google:ClientId").</summary>
    public static List<(string[] Path, JsonNode? Value)> Changed(JsonObject old, JsonObject baseline)
    {
        var changed = new List<(string[], JsonNode?)>();
        Walk(old, [], (path, value) =>
        {
            var was = Get(baseline, path);
            if (was is null && value is null) return;
            if (was is null || !JsonNode.DeepEquals(value, was)) changed.Add((path, value));
        });
        return changed;
    }

    /// <summary>
    /// Puts the changed settings into the settings file's object where it doesn't set them, or sets them only to the
    /// default (as its template does, e.g. TrustedProxies 127.0.0.1); returns the ones put in. A setting both files
    /// changed keeps the settings file's.
    /// </summary>
    public static List<string> Merge(JsonObject settings, List<(string[] Path, JsonNode? Value)> changed, JsonObject? baseline = null)
    {
        var added = new List<string>();
        foreach (var (path, value) in changed)
        {
            if (Has(settings, path) && !(baseline is not null && Get(baseline, path) is { } d && JsonNode.DeepEquals(Get(settings, path), d)))
                continue;
            Set(settings, path, value?.DeepClone());
            added.Add(string.Join(':', path));
        }
        return added;
    }

    /// <summary>
    /// <c>--carry-settings</c>: from the saved old program-folder file into the settings file (made if there isn't one),
    /// backing the settings file up first. Prints what it did, for the installer's log. Returns the exit code.
    /// </summary>
    public static int Run(string savedOld, string programFolderFile, string settingsFile)
    {
        try
        {
            if (!File.Exists(savedOld)) { Console.WriteLine($"No old settings at {savedOld}; nothing to carry."); return 0; }
            var old = Parse(File.ReadAllText(savedOld));
            var current = File.Exists(programFolderFile) ? Parse(File.ReadAllText(programFolderFile)) : null;
            var baseline = Baseline(current);
            var changed = Changed(old, baseline);
            if (changed.Count == 0) { Console.WriteLine("The old program folder's appsettings.json had no settings of its own."); return 0; }

            var exists = File.Exists(settingsFile);
            var settings = exists ? Parse(File.ReadAllText(settingsFile)) : new JsonObject();
            var added = Merge(settings, changed, baseline);
            if (added.Count == 0) { Console.WriteLine($"{settingsFile} already has every setting the old program folder had."); return 0; }

            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            if (exists)
            {
                // Rewriting drops the file's comments: the original stays beside it
                var backup = $"{settingsFile}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Copy(settingsFile, backup);
                Console.WriteLine($"Backed up {settingsFile} to {backup}");
            }
            File.WriteAllText(settingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Console.WriteLine($"Carried these settings from the old program folder into {settingsFile}: {string.Join(", ", added)}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Couldn't carry the old settings over: {ex.Message}. They're in {savedOld}; copy them into {settingsFile} by hand.");
            return 1;
        }
    }

    /// <summary>The program folder's own settings that aren't defaults: lost at the next upgrade, so the log says so.</summary>
    public static List<string> OwnSettings(string programFolderFile)
    {
        if (!File.Exists(programFolderFile)) return [];
        try
        {
            var file = Parse(File.ReadAllText(programFolderFile));
            if (Find(file, MediaRiffleOptions.Section) is not JsonObject section) return [];
            var only = new JsonObject { [MediaRiffleOptions.Section] = section.DeepClone() };
            return Changed(only, Baseline(null)).Select(c => string.Join(':', c.Path)).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: Lenient)?.AsObject() ?? new JsonObject();

    // ── Paths into JSON, by name ignoring case, as .NET's configuration reads them ──

    private static void Walk(JsonObject obj, string[] prefix, Action<string[], JsonNode?> leaf)
    {
        foreach (var (key, value) in obj)
        {
            string[] path = [.. prefix, key];
            if (value is JsonObject child) Walk(child, path, leaf);
            else leaf(path, value);
        }
    }

    private static JsonNode? Find(JsonObject obj, string key) =>
        obj.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonNode? Get(JsonObject obj, string[] path)
    {
        JsonNode? node = obj;
        foreach (var key in path)
        {
            if (node is not JsonObject o) return null;
            node = Find(o, key);
        }
        return node;
    }

    private static bool Has(JsonObject obj, string[] path)
    {
        JsonNode? node = obj;
        foreach (var key in path)
        {
            if (node is not JsonObject o) return false;
            var match = o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null) return false;
            node = match.Value;
        }
        return true;
    }

    private static void Set(JsonObject obj, string[] path, JsonNode? value)
    {
        var node = obj;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var existing = node.FirstOrDefault(kv => string.Equals(kv.Key, path[i], StringComparison.OrdinalIgnoreCase));
            if (existing.Value is JsonObject child) { node = child; continue; }
            var made = new JsonObject();
            if (existing.Key is not null) node.Remove(existing.Key);
            node[path[i]] = made;
            node = made;
        }
        var last = node.FirstOrDefault(kv => string.Equals(kv.Key, path[^1], StringComparison.OrdinalIgnoreCase)).Key;
        if (last is not null) node.Remove(last);
        node[path[^1]] = value;
    }

    // Copies over's leaves onto target
    private static void Overlay(JsonObject target, JsonObject over) =>
        Walk(over, [], (path, value) => Set(target, path, value?.DeepClone()));
}
