using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace InventoryItemAnalyzer;

/// <summary>
/// Lightweight companion database for the public PoE1 Item Checker exports.
/// ExileAPI's live ModRecord remains authoritative for rolled values and tier
/// ranges; these files provide stable base tags, mod metadata, and unique drop
/// classifications without mixing PoE2 component assumptions into LootLens1.
/// </summary>
internal sealed class Poe1ItemDatabase
{
    private readonly Dictionary<string, BaseEntry> _bases =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModEntry> _mods =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _uniqueDropTiers =
        new(StringComparer.OrdinalIgnoreCase);

    public static Poe1ItemDatabase Empty { get; } = new();

    public bool BasesLoaded { get; private set; }
    public bool ModsLoaded { get; private set; }
    public bool DropTiersLoaded { get; private set; }
    public bool IsLoaded => BasesLoaded && ModsLoaded;
    public int BaseCount => _bases.Count;
    public int ModCount => _mods.Count;
    public int UniqueCount => _uniqueDropTiers.Count;

    public static Poe1ItemDatabase Load(string pluginDirectory)
    {
        var database = new Poe1ItemDatabase();
        var dataDirectory = Path.Combine(pluginDirectory ?? string.Empty, "Data");

        TryLoad(() => database.LoadBases(Path.Combine(dataDirectory,
            "item_bases.json")));
        TryLoad(() => database.LoadMods(Path.Combine(dataDirectory,
            "item_mods.json")));
        TryLoad(() => database.LoadDropTiers(Path.Combine(dataDirectory,
            "item_drop_tiers.json")));
        return database;

        static void TryLoad(Action action)
        {
            try { action(); }
            catch
            {
                // Every file has an independent live-data fallback. A damaged
                // optional database must never prevent the plugin from loading.
            }
        }
    }

    public string GetUniqueDropTier(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return "UNKNOWN";

        var clean = Regex.Replace(itemName.Trim(), "^foulborn\\s+", string.Empty,
            RegexOptions.IgnoreCase);
        return _uniqueDropTiers.TryGetValue(clean, out var tier) &&
               !string.IsNullOrWhiteSpace(tier)
            ? tier.Trim().ToUpperInvariant()
            : "UNKNOWN";
    }

    public string GetItemClass(string baseType)
    {
        return TryGetBase(baseType, out var entry) ? entry.ItemClass : string.Empty;
    }

    public bool IsModEligible(string rawName, string baseType)
    {
        if (!_mods.TryGetValue(rawName ?? string.Empty, out var mod) ||
            mod.Tags.Count == 0 || !TryGetBase(baseType, out var itemBase))
            return true;

        if (mod.Tags.Contains("default"))
            return true;

        return mod.Tags.Overlaps(itemBase.Tags);
    }

    public int GetModLevel(string rawName)
    {
        return _mods.TryGetValue(rawName ?? string.Empty, out var mod)
            ? mod.Level
            : 0;
    }

    private bool TryGetBase(string baseType, out BaseEntry entry)
    {
        var normalized = Normalize(baseType);
        if (_bases.TryGetValue(normalized, out entry))
            return true;

        // Some display sources prepend a temporary decorator. Prefer the
        // longest known base suffix so the lookup remains deterministic.
        var suffix = _bases.Keys
            .Where(key => key.Length >= 4 && normalized.EndsWith(key,
                StringComparison.Ordinal))
            .OrderByDescending(key => key.Length)
            .FirstOrDefault();
        if (suffix != null && _bases.TryGetValue(suffix, out entry))
            return true;

        entry = null;
        return false;
    }

    private void LoadBases(string path)
    {
        var root = JObject.Parse(File.ReadAllText(path));
        foreach (var classProperty in root.Properties())
        {
            if (classProperty.Name.StartsWith("_", StringComparison.Ordinal) ||
                classProperty.Value is not JObject bases)
                continue;

            foreach (var baseProperty in bases.Properties())
            {
                if (baseProperty.Value is not JObject baseObject)
                    continue;

                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var tagArray = baseObject["_tags"] as JArray ??
                               baseObject["tags"] as JArray;
                if (tagArray != null)
                {
                    foreach (var token in tagArray)
                    {
                        var tag = token.Value<string>();
                        if (!string.IsNullOrWhiteSpace(tag))
                            tags.Add(tag);
                    }
                }

                _bases[Normalize(baseProperty.Name)] =
                    new BaseEntry(classProperty.Name, tags);
            }
        }

        BasesLoaded = _bases.Count > 0;
    }

    private void LoadMods(string path)
    {
        var root = JObject.Parse(File.ReadAllText(path));
        foreach (var section in root.Properties())
        {
            if (section.Value is not JObject records)
                continue;

            foreach (var property in records.Properties())
            {
                if (property.Value is not JObject source)
                    continue;

                var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (source["tags"] is JArray tagArray)
                {
                    foreach (var token in tagArray)
                    {
                        var tag = token.Value<string>();
                        if (!string.IsNullOrWhiteSpace(tag))
                            tags.Add(tag);
                    }
                }

                var level = 0;
                int.TryParse(source["level"]?.ToString(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out level);
                _mods[property.Name] = new ModEntry(level, tags);
            }
        }

        ModsLoaded = _mods.Count > 0;
    }

    private void LoadDropTiers(string path)
    {
        var root = JObject.Parse(File.ReadAllText(path));
        foreach (var property in root.Properties())
        {
            var value = property.Value.Value<string>();
            if (!string.IsNullOrWhiteSpace(value))
                _uniqueDropTiers[property.Name] = value;
        }

        DropTiersLoaded = _uniqueDropTiers.Count > 0;
    }

    private static string Normalize(string value) => Regex.Replace(
        (value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", string.Empty);

    private sealed record BaseEntry(string ItemClass, HashSet<string> Tags);
    private sealed record ModEntry(int Level, HashSet<string> Tags);
}
