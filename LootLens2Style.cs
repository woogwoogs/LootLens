using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpDX;

using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace InventoryItemAnalyzer;

public partial class InventoryItemAnalyzer
{
    private Poe1ItemDatabase _itemDatabase = Poe1ItemDatabase.Empty;

    private sealed class Poe1DisplayMod
    {
        public string Affix = string.Empty;
        public string Text = "Unknown modifier";
        public string RangeText = string.Empty;
        public string RangeLabel = string.Empty;
        public int Tier;
        public int TotalTiers;
        public int Perfection = -1;
        public bool IsUnique;
        public bool IsImplicit;
        public bool IsCrafted;
        public bool IsContinuation;
    }

    private sealed class Poe1RollInfo
    {
        public int Perfection = -1;
        public int VariableRolls;
        public string RangeText = string.Empty;
        public string RangeLabel = string.Empty;
    }

    private void DrawLootLens2StyleFullOverlay(SharpDX.RectangleF tooltipRect,
        Entity item, Mods mods, List<string> rows)
    {
        var scale = GetMinimalUiScale();
        var draw = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        var baseFont = ImGui.GetFontSize();
        var display = ImGui.GetIO().DisplaySize;
        var analyzedMods = BuildPoe1DisplayMods(item, mods);

        var uniquePerfection = mods.ItemRarity == ItemRarity.Unique &&
                               Settings.ItemInfoShowOverallPerfection.Value
            ? GetCachedAnalysis(item, mods).UniquePerfection
            : null;
        var summaryMetrics = BuildPoe1SummaryMetrics(item, mods);
        var showRareMetadata = mods.ItemRarity != ItemRarity.Unique;
        var explicitModCount = analyzedMods.Count(mod =>
            !mod.IsImplicit && !mod.IsContinuation);
        var uniqueTier = mods.ItemRarity == ItemRarity.Unique
            ? GetPoe1UniqueTierLabel(item, mods)
            : string.Empty;

        var width = Math.Max(tooltipRect.Width,
            Math.Min(Settings.ItemInfoWidth.Value * scale,
                display.X - 16f));
        width = Math.Clamp(width, 340f * scale,
            Math.Max(340f * scale, display.X - 16f));

        var topPadding = 3f * scale;
        var contextStripHeight = summaryMetrics.Count > 0 || showRareMetadata
            ? 21f * scale
            : 0f;
        var modifierHeaderHeight = analyzedMods.Count > 0 ? 18f * scale : 0f;
        var modRowHeight = 22f * scale;
        var perfectionFooter = uniquePerfection?.Percentage >= 0
            ? 51f * scale : 0f;
        var height = topPadding + contextStripHeight + modifierHeaderHeight +
                     analyzedMods.Count * modRowHeight +
                     perfectionFooter + 4f * scale;

        var position = GetAnalyzerPanelPosition(tooltipRect, width, height, true);
        var topLeft = position;
        var bottomRight = position + new Vector2(width, height);
        DrawLootLens2StyleBackground(draw, topLeft, bottomRight, scale);
        draw.PushClipRect(topLeft + Vector2.One, bottomRight - Vector2.One, true);

        var subdued = ToImGuiColor(new Color(145, 149, 158, 255));
        var mutedLine = ToImGuiColor(new Color(105, 107, 112, 125));
        var cy = position.Y + topPadding;

        if (contextStripHeight > 0f)
        {
            DrawPoe1AnalysisHeader(draw, font, baseFont, position.X, cy,
                width, scale, mods, summaryMetrics, explicitModCount,
                subdued, mutedLine);
            cy += contextStripHeight;
        }

        if (modifierHeaderHeight > 0f)
        {
            DrawPoe1ModifierColumnHeader(draw, font, baseFont, position.X,
                cy, width, scale, subdued, mutedLine);
            cy += modifierHeaderHeight;
        }

        foreach (var displayMod in analyzedMods)
        {
            DrawPoe1ModifierRow(draw, font, baseFont, position.X, cy,
                width, modRowHeight, scale, displayMod, subdued);
            cy += modRowHeight;
        }

        if (perfectionFooter > 0f)
        {
            DrawPoe1PerfectionFooter(draw, font, baseFont, position.X, cy,
                width, scale, uniquePerfection, uniqueTier, subdued,
                mutedLine);
            cy += perfectionFooter;
        }

        draw.PopClipRect();
    }

    private List<Poe1DisplayMod> BuildPoe1DisplayMods(Entity item, Mods mods)
    {
        var result = new List<(Poe1DisplayMod Mod, int Order, int Index)>();
        if (mods?.ItemMods == null)
            return new List<Poe1DisplayMod>();

        var sourceIndex = 0;
        foreach (var itemMod in mods.ItemMods)
        {
            if (itemMod == null)
                continue;

            ModsDat.ModRecord record = null;
            try { record = GameController.Files.Mods.records[itemMod.RawName]; }
            catch { }

            var implicitMod = mods.ImplicitMods != null &&
                              mods.ImplicitMods.Any(candidate => candidate != null &&
                                  string.Equals(candidate.RawName, itemMod.RawName,
                                      StringComparison.Ordinal));
            if (implicitMod && !Settings.ItemInfoShowImplicitModifiers.Value)
                continue;

            var uniqueMod = !implicitMod && record?.AffixType == ModType.Unique &&
                            mods.ItemRarity == ItemRarity.Unique;
            var craftedMod = !implicitMod && !uniqueMod && IsCraftedMod(itemMod);
            var affix = implicitMod ? "I" : uniqueMod ? string.Empty : craftedMod ? "C" :
                record?.AffixType == ModType.Prefix ? "P" :
                record?.AffixType == ModType.Suffix ? "S" : string.Empty;
            (int Tier, int TotalTiers) tier =
                !implicitMod && !uniqueMod && !craftedMod
                ? GetAuthoritativeModTier(item, mods, itemMod)
                : (0, 0);
            var roll = GetPoe1RollInfo(item, mods, itemMod, record,
                uniqueMod || craftedMod || implicitMod);
            if (uniqueMod && roll.Perfection < 0 &&
                Math.Clamp(Settings.ItemInfoFixedUniqueMods.Value, 0, 2) == 2)
                continue;
            var textRows = GetAuthoritativeStatRows(itemMod);
            if (textRows.Count == 0)
            {
                var values = GetMemberValues(itemMod);
                var fallback = GetReadableModText(itemMod, itemMod.RawName, values);
                textRows.Add(string.IsNullOrWhiteSpace(fallback)
                    ? itemMod.RawName ?? "Unknown modifier"
                    : fallback);
            }

            var order = implicitMod ? 0 :
                record?.AffixType == ModType.Prefix ? 1 :
                record?.AffixType == ModType.Suffix ? 2 : 3;
            for (var rowIndex = 0; rowIndex < textRows.Count; rowIndex++)
            {
                var text = CleanPoe1ModifierText(textRows[rowIndex]);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                result.Add((new Poe1DisplayMod
                {
                    Affix = rowIndex == 0 ? affix : string.Empty,
                    Text = text,
                    RangeText = rowIndex == 0 ? roll.RangeText : string.Empty,
                    RangeLabel = rowIndex == 0 ? roll.RangeLabel : string.Empty,
                    Tier = rowIndex == 0 ? tier.Tier : 0,
                    TotalTiers = rowIndex == 0 ? tier.TotalTiers : 0,
                    Perfection = rowIndex == 0 ? roll.Perfection : -1,
                    IsUnique = uniqueMod,
                    IsImplicit = implicitMod,
                    IsCrafted = craftedMod,
                    IsContinuation = rowIndex > 0
                }, order, sourceIndex++));
            }
        }

        return result.OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Mod).ToList();
    }

    private Poe1RollInfo GetPoe1RollInfo(Entity item, Mods mods,
        ItemMod itemMod, ModsDat.ModRecord record, bool forceCurrentTier)
    {
        var result = new Poe1RollInfo();
        if (record?.StatNames == null || record.StatRange == null ||
            itemMod?.Values == null)
            return result;

        var statNames = record.StatNames.ToArray();
        var liveRanges = record.StatRange.ToArray();
        var values = itemMod.Values.ToArray();
        var count = Math.Min(values.Length,
            Math.Min(statNames.Length, liveRanges.Length));
        if (count == 0)
            return result;

        var minimum = liveRanges.Select(range =>
            (double)Math.Min(range.Min, range.Max)).ToArray();
        var maximum = liveRanges.Select(range =>
            (double)Math.Max(range.Min, range.Max)).ToArray();

        var rangeMode = forceCurrentTier ? 0 :
            Math.Clamp(Settings.ItemInfoPerfectionRange.Value, 0, 2);
        if (rangeMode != 0 &&
            (record.AffixType == ModType.Prefix ||
             record.AffixType == ModType.Suffix))
        {
            ExpandPoe1RollRange(item, mods, record, statNames,
                rangeMode, minimum, maximum);
        }

        double total = 0d;
        var displayRanges = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var stat = statNames[index];
            if (stat == null || values[index] <= -1000)
                continue;

            var low = minimum[index];
            var high = maximum[index];
            if (Math.Abs(high - low) < .0001d)
                continue;

            var position = Math.Clamp((values[index] - low) /
                                      (high - low), 0d, 1d);
            var statKey = GetMemberString(stat, "Key", "Name");
            var statText = stat.ToString() ?? string.Empty;
            if (low >= 0 && IsLowerUniqueRollBetter(statKey, statText))
                position = 1d - position;

            total += position * 100d;
            result.VariableRolls++;
            displayRanges.Add(FormatPoe1StatRange(stat, low, high));
        }

        if (result.VariableRolls == 0)
            return result;

        result.Perfection = Math.Clamp((int)Math.Round(
            total / result.VariableRolls, MidpointRounding.AwayFromZero),
            0, 100);
        result.RangeText = string.Join(" | ", displayRanges.Distinct());
        result.RangeLabel = rangeMode switch
        {
            1 => "G RANGE",
            2 => "ILVL RANGE",
            _ => "TIER RANGE"
        };
        return result;
    }

    private void ExpandPoe1RollRange(Entity item, Mods mods,
        ModsDat.ModRecord currentRecord, StatsDat.StatRecord[] currentStats,
        int rangeMode, double[] minimum, double[] maximum)
    {
        try
        {
            if (!GameController.Files.Mods.recordsByTier.TryGetValue(
                    Tuple.Create(currentRecord.Group, currentRecord.AffixType),
                    out var records))
                return;

            var baseItem = GameController.Files.BaseItemTypes.Translate(item.Path);
            var baseType = GetPoe1BaseName(item);
            var familyLetters = new string((currentRecord.Key ?? string.Empty)
                .Where(char.IsLetter).ToArray());
            var candidates = records.Where(candidate =>
            {
                if (candidate == null)
                    return false;
                var letters = new string((candidate.Key ?? string.Empty)
                    .Where(char.IsLetter).ToArray());
                if (!string.Equals(letters, familyLetters,
                        StringComparison.Ordinal) ||
                    !IsTierCandidateEligible(candidate, baseItem) ||
                    !_itemDatabase.IsModEligible(candidate.Key, baseType))
                    return false;

                var level = candidate.MinLevel > 0
                    ? candidate.MinLevel
                    : _itemDatabase.GetModLevel(candidate.Key);
                return rangeMode != 2 || level <= Math.Max(1, mods.ItemLevel);
            }).ToList();

            if (candidates.Count == 0)
                return;

            for (var index = 0; index < currentStats.Length; index++)
            {
                var stat = currentStats[index];
                if (stat == null)
                    continue;
                var statKey = GetMemberString(stat, "Key", "Name");
                foreach (var candidate in candidates)
                {
                    if (candidate.StatNames == null || candidate.StatRange == null)
                        continue;
                    var candidateStats = candidate.StatNames.ToArray();
                    var candidateRanges = candidate.StatRange.ToArray();
                    for (var candidateIndex = 0;
                         candidateIndex < candidateStats.Length &&
                         candidateIndex < candidateRanges.Length;
                         candidateIndex++)
                    {
                        if (candidateStats[candidateIndex] == null ||
                            !string.Equals(GetMemberString(
                                    candidateStats[candidateIndex], "Key", "Name"),
                                statKey, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var range = candidateRanges[candidateIndex];
                        minimum[index] = Math.Min(minimum[index],
                            Math.Min(range.Min, range.Max));
                        maximum[index] = Math.Max(maximum[index],
                            Math.Max(range.Min, range.Max));
                        break;
                    }
                }
            }
        }
        catch
        {
            // The live current-tier range remains valid.
        }
    }

    private static string FormatPoe1StatRange(object stat, double minimum,
        double maximum)
    {
        string Format(double value)
        {
            try
            {
                var method = stat.GetType().GetMethod("ValueToString",
                    new[] { typeof(int) });
                if (method?.Invoke(stat, new object[]
                    { (int)Math.Round(value) }) is string text &&
                    !string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
            catch { }

            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        var low = Format(minimum);
        var high = Format(maximum);
        return string.Equals(low, high, StringComparison.Ordinal)
            ? low
            : $"{low}-{high}";
    }

    private static void DrawPoe1ModifierColumnHeader(ImDrawListPtr draw,
        ImFontPtr font, float baseFont, float x, float y, float width,
        float scale, uint subdued, uint mutedLine)
    {
        var right = x + width - 14f * scale;
        var rollRight = right - 55f * scale;
        var rollLeft = x + Math.Max(width * .53f, 190f * scale);
        if (rollRight - rollLeft < 42f * scale)
            rollLeft = rollRight - 42f * scale;

        draw.AddLine(new Vector2(x + 14f * scale, y + 9f * scale),
            new Vector2(rollLeft - 12f * scale, y + 9f * scale),
            mutedLine, Math.Max(1f, scale * .65f));

        var labelScale = .43f * scale;
        const string rollLabel = "ROLL";
        var rollWidth = LootLens2TextWidth(rollLabel, labelScale);
        draw.AddText(font, baseFont * labelScale,
            new Vector2((rollLeft + rollRight - rollWidth) * .5f,
                y + 2f * scale), subdued, rollLabel);

        const string tierLabel = "TIER";
        var tierWidth = LootLens2TextWidth(tierLabel, labelScale);
        draw.AddText(font, baseFont * labelScale,
            new Vector2(right - tierWidth, y + 2f * scale),
            subdued, tierLabel);
    }

    private void DrawPoe1ModifierRow(ImDrawListPtr draw, ImFontPtr font,
        float baseFont, float x, float rowTop, float width, float rowHeight,
        float scale, Poe1DisplayMod mod, uint subdued)
    {
        var statColor = GetCompactStatColor(mod.Text);
        var specialColor = mod.IsUnique
            ? ToImGuiColor(new Color(203, 111, 55, 255))
            : mod.IsCrafted
                ? ToImGuiColor(new Color(79, 197, 212, 255))
                : mod.IsImplicit
                    ? ToImGuiColor(new Color(153, 158, 168, 255))
                    : statColor;
        var textColor = mod.IsUnique
            ? ToImGuiColor(new Color(213, 119, 58, 255))
            : mod.IsCrafted
                ? ToImGuiColor(new Color(92, 207, 220, 255))
                : ToImGuiColor(new Color(218, 220, 224, 255));
        var letterColor = mod.IsUnique || mod.IsCrafted || mod.IsImplicit
            ? specialColor
            : ToImGuiColor(new Color(151, 154, 162, 255));

        if (Settings.ItemInfoFullStatColors.Value)
            textColor = statColor;

        var fixedUnique = mod.IsUnique && mod.Perfection < 0 &&
                          !mod.IsContinuation;
        var dimFactor = mod.IsImplicit
            ? .65f
            : fixedUnique &&
              Math.Clamp(Settings.ItemInfoFixedUniqueMods.Value, 0, 2) == 1
                ? .48f
                : 1f;
        if (dimFactor < 1f)
        {
            statColor = WithAlpha(statColor, dimFactor);
            specialColor = WithAlpha(specialColor, dimFactor);
            textColor = WithAlpha(textColor, dimFactor);
            letterColor = WithAlpha(letterColor, dimFactor);
            subdued = WithAlpha(subdued, dimFactor);
        }

        var uniqueLayout = mod.IsUnique;
        if (!uniqueLayout)
        {
            draw.AddText(font, baseFont * .52f * scale,
                new Vector2(x + 14f * scale, rowTop + 6f * scale),
                letterColor,
                mod.Affix ?? string.Empty);
        }

        var accentX = x + (uniqueLayout ? 16f : 29f) * scale;
        draw.AddLine(new Vector2(accentX, rowTop + 4f * scale),
            new Vector2(accentX, rowTop + rowHeight - 4f * scale),
            statColor, Math.Max(1.25f, 1.35f * scale));

        var right = x + width - 14f * scale;
        var tierText = GetPoe1BadgeText(mod);
        var tierScale = tierText.Length > 4 ? .40f * scale : .53f * scale;
        var tierWidth = LootLens2TextWidth(tierText, tierScale);
        if (!string.IsNullOrEmpty(tierText))
        {
            var tierColor = GetPoe1BadgeColor(mod);
            if (dimFactor < 1f)
                tierColor = WithAlpha(tierColor, dimFactor);
            draw.AddText(font, baseFont * tierScale,
                new Vector2(right - tierWidth, rowTop + 5f * scale),
                tierColor, tierText);
        }

        var rollRight = right - 55f * scale;
        var rollLeft = x + Math.Max(width * .53f, 190f * scale);
        if (rollRight - rollLeft < 42f * scale)
            rollLeft = rollRight - 42f * scale;
        var textLeft = x + (uniqueLayout ? 27f : 40f) * scale;
        var textRight = rollLeft - 12f * scale;
        draw.AddText(font, baseFont * .66f * scale,
            new Vector2(textLeft, rowTop + 4f * scale), textColor,
            EllipsizeLootLens2Text(mod.Text,
                Math.Max(70f, textRight - textLeft), .66f * scale));

        var rollY = rowTop + 11f * scale;
        if (mod.Perfection >= 0)
        {
            var trackColor = WithAlpha(
                ToImGuiColor(new Color(130, 134, 143, 210)), dimFactor);
            draw.AddLine(new Vector2(rollLeft, rollY),
                new Vector2(rollRight, rollY), trackColor,
                Math.Max(1.2f, 1.3f * scale));

            var markerX = rollLeft + (rollRight - rollLeft) *
                mod.Perfection / 100f;
            draw.AddLine(new Vector2(rollLeft, rollY),
                new Vector2(markerX, rollY), statColor,
                Math.Max(1.8f, 2f * scale));

            var markerSize = 4.2f * scale;
            var top = new Vector2(markerX, rollY - markerSize);
            var rightPoint = new Vector2(markerX + markerSize, rollY);
            var bottom = new Vector2(markerX, rollY + markerSize);
            var leftPoint = new Vector2(markerX - markerSize, rollY);
            draw.AddTriangleFilled(top, rightPoint, bottom, statColor);
            draw.AddTriangleFilled(top, bottom, leftPoint, statColor);
            var outline = WithAlpha(
                ToImGuiColor(new Color(238, 239, 242, 235)), dimFactor);
            draw.AddLine(top, rightPoint, outline, Math.Max(1f, scale));
            draw.AddLine(rightPoint, bottom, outline, Math.Max(1f, scale));
            draw.AddLine(bottom, leftPoint, outline, Math.Max(1f, scale));
            draw.AddLine(leftPoint, top, outline, Math.Max(1f, scale));
        }
        else if (!mod.IsContinuation)
        {
            const string fixedMarker = "-";
            var markerScale = .52f * scale;
            var markerWidth = LootLens2TextWidth(fixedMarker, markerScale);
            draw.AddText(font, baseFont * markerScale,
                new Vector2((rollLeft + rollRight - markerWidth) * .5f,
                    rowTop + 4f * scale), subdued, fixedMarker);
        }
    }

    private static string CleanPoe1ModifierText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ");

        void Rewrite(string pattern, string replacement)
        {
            clean = System.Text.RegularExpressions.Regex.Replace(clean,
                pattern, replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Phrase rewrites are intentionally display-only.  The authoritative
        // stat IDs, raw values, tier calculations, and matching text are never
        // modified by this shorthand pass.
        Rewrite(@"^Adds\s+\+?(\d+(?:\.\d+)?)\s+to\s+\+?(\d+(?:\.\d+)?)\s+",
            "+$1-$2 ");
        Rewrite(@"(?<=\d)\s+to\s+(?=[+-]?\d)", "-");
        Rewrite(@"^Gain\s+([+-]?\d+(?:\.\d+)?)\s+(Life|Mana)\s+per\s+Enemy\s+Killed$",
            "+$1 $2 on Kill");
        Rewrite(@"^Recover\s+([+-]?\d+(?:\.\d+)?%)\s+of\s+(Life|Mana)\s+on\s+Kill$",
            "Recover $1 $2 on Kill");
        Rewrite(@"^(?:Regenerate|Regenerates)\s+([+-]?\d+(?:\.\d+)?%?)\s+(?:of\s+)?(Life|Mana)\s+per\s+second$",
            "Regen $1 $2/s");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?)\s+(Life|Mana)\s+Regenerated\s+per\s+Second$",
            "Regen $1 $2/s");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?%)\s+of\s+Life\s+Regenerated\s+per\s+Second$",
            "Regen $1 Life/s");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?%)\s+Maximum\s+(Fire|Cold|Lightning|Chaos)\s+Damage\s+Resistance$",
            "$1 Max $2 Res");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?%)\s+Attacks\s+Impale\s+On\s+Hit\s+Chance$",
            "$1 Impale Chance with Attacks");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?%)\s+Chance\s+to\s+Block\s+Attack\s+Damage$",
            "$1 Attack Block");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?%)\s+Chance\s+to\s+Block\s+Spell\s+Damage$",
            "$1 Spell Block");
        Rewrite(@"Socketed Gems are Supported by Level\s+", "Socketed Gems Supported by Lvl ");
        Rewrite(@"([+-]?\d+)\s+to\s+Level\s+of\s+all\s+([A-Za-z ]+?)\s+Skill\s+Gems",
            "$1 Lvl $2 Gems");
        Rewrite(@"([+-]?\d+)\s+to\s+Level\s+of\s+Socketed\s+Gems",
            "$1 Lvl Socketed Gems");

        Rewrite(@"\bPhysical Damage Reduction Rating\b", "Armour");
        Rewrite(@"\bMovement Velocity\b", "Movement Speed");
        Rewrite(@"\bFire and Cold Resistances\b", "Fire & Cold Res");
        Rewrite(@"\bFire and Lightning Resistances\b", "Fire & Lightning Res");
        Rewrite(@"\bCold and Lightning Resistances\b", "Cold & Lightning Res");
        Rewrite(@"\ball Elemental Resistances\b", "All Ele Res");
        Rewrite(@"\bAdditional All Attributes\b", "All Attributes");
        Rewrite(@"\bto all Attributes\b", "All Attributes");
        Rewrite(@"\bto Maximum Life\b", "Life");
        Rewrite(@"\bto Maximum Mana\b", "Mana");
        Rewrite(@"\bto maximum Energy Shield\b", "ES");
        Rewrite(@"\bto maximum (Fire|Cold|Lightning|Chaos) Resistance\b", "Max $1 Res");
        Rewrite(@"\bto (Fire|Cold|Lightning|Chaos) Resistance\b", "$1 Res");
        Rewrite(@"\bto (Intelligence|Strength|Dexterity)\b", "$1");
        Rewrite(@"\bto (Accuracy|Armour|Evasion|ES|Crit Multi)\b", "$1");
        Rewrite(@"\bto (All Ele Res|Fire & Cold Res|Fire & Lightning Res|Cold & Lightning Res)\b", "$1");
        Rewrite(@"\bincreased Armour and Evasion Rating\b", "Inc Armour & Evasion");
        Rewrite(@"\bincreased Armour and Energy Shield\b", "Inc Armour & ES");
        Rewrite(@"\bincreased Evasion Rating and Energy Shield\b", "Inc Evasion & ES");
        Rewrite(@"\bincreased Movement Speed\b", "Move Speed");
        Rewrite(@"\bincreased Attack and Cast Speed\b", "Attack & Cast Speed");
        Rewrite(@"\bincreased Attack Speed\b", "Attack Speed");
        Rewrite(@"\bincreased Cast Speed\b", "Cast Speed");
        Rewrite(@"\bincreased Critical Strike Chance\b", "Crit Chance");
        Rewrite(@"\bincreased Global Critical Strike Chance\b", "Global Crit Chance");
        Rewrite(@"\bGlobal Critical Strike Multiplier\b", "Crit Multi");
        Rewrite(@"\bCritical Strike Multiplier\b", "Crit Multi");
        Rewrite(@"\bGlobal Critical Strike Chance\b", "Global Crit Chance");
        Rewrite(@"\bto Crit Multi\b", "Crit Multi");
        Rewrite(@"\bElemental Damage with Attack Skills\b", "Ele Dmg with Attacks");
        Rewrite(@"\bincreased Block Recovery\b", "Block Recovery");
        Rewrite(@"\bAccuracy Rating\b", "Accuracy");
        Rewrite(@"\bEvasion Rating\b", "Evasion");
        Rewrite(@"\bMaximum Life\b", "Life");
        Rewrite(@"\bMaximum Mana\b", "Mana");
        Rewrite(@"\bmaximum Energy Shield\b", "ES");
        Rewrite(@"\bEnergy Shield\b", "ES");
        Rewrite(@"\bMovement Speed\b", "Move Speed");
        Rewrite(@"\bIntelligence\b", "Int");
        Rewrite(@"\bStrength\b", "Str");
        Rewrite(@"\bDexterity\b", "Dex");
        Rewrite(@"^([+-]?\d+(?:\.\d+)?%)\s+of\s+((?:Physical|Attack)\b.*Leeched\s+as\s+(?:Life|Mana))$",
            "$1 $2");
        Rewrite(@"\bPhysical\b", "Phys");
        Rewrite(@"\bElemental\b", "Ele");
        Rewrite(@"\bDamage\b", "Dmg");
        Rewrite(@"\bResistances?\b", "Res");
        Rewrite(@"\bLevel\b", "Lvl");
        Rewrite(@"\bAdditional\b", string.Empty);
        Rewrite(@"\bincreased\b", "Inc");
        Rewrite(@"\bper second\b", "/s");
        Rewrite(@"\s+", " ");
        Rewrite(@"\+\s+", "+");
        return clean.Trim();
    }

    private static string GetPoe1BadgeText(Poe1DisplayMod mod)
    {
        if (mod.IsCrafted) return "CRAFTED";
        if (mod.IsImplicit || mod.IsUnique) return string.Empty;
        return mod.Tier > 0 ? $"T{mod.Tier}" : string.Empty;
    }

    private static uint GetPoe1BadgeColor(Poe1DisplayMod mod)
    {
        if (mod.IsUnique) return ToImGuiColor(new Color(203, 111, 55, 255));
        if (mod.IsImplicit) return ToImGuiColor(new Color(153, 158, 168, 255));
        if (mod.IsCrafted) return ToImGuiColor(new Color(79, 197, 212, 255));
        return mod.Tier switch
        {
            1 => ToImGuiColor(new Color(227, 184, 66, 255)),
            2 => ToImGuiColor(new Color(76, 205, 124, 255)),
            3 => ToImGuiColor(new Color(52, 181, 195, 255)),
            4 => ToImGuiColor(new Color(118, 138, 157, 255)),
            _ => ToImGuiColor(new Color(112, 116, 125, 255))
        };
    }

    private static void DrawPoe1AnalysisHeader(ImDrawListPtr draw,
        ImFontPtr font, float baseFont, float x, float y, float width,
        float scale, Mods mods,
        List<(string Label, string Value, uint Color)> metrics,
        int explicitModCount, uint subdued, uint mutedLine)
    {
        var left = x + 14f * scale;
        var right = x + width - 14f * scale;
        var cursor = left;
        var textScale = .48f * scale;
        var separator = "  |  ";

        if (mods.ItemRarity != ItemRarity.Unique)
        {
            var metadata = $"iLvl {mods.ItemLevel}  |  " +
                           $"{explicitModCount} " +
                           (explicitModCount == 1 ? "Mod" : "Mods");
            draw.AddText(font, baseFont * textScale,
                new Vector2(cursor, y + 3f * scale), subdued, metadata);
            cursor += LootLens2TextWidth(metadata, textScale);
            if (metrics.Count > 0)
            {
                draw.AddText(font, baseFont * textScale,
                    new Vector2(cursor, y + 3f * scale), subdued, separator);
                cursor += LootLens2TextWidth(separator, textScale);
            }
        }

        for (var index = 0; index < metrics.Count; index++)
        {
            var metric = metrics[index];
            if (index > 0)
            {
                draw.AddText(font, baseFont * textScale,
                    new Vector2(cursor, y + 3f * scale), subdued, separator);
                cursor += LootLens2TextWidth(separator, textScale);
            }

            var label = metric.Label + " ";
            draw.AddText(font, baseFont * textScale,
                new Vector2(cursor, y + 3f * scale), subdued, label);
            cursor += LootLens2TextWidth(label, textScale);
            var value = metric.Value;
            var valueWidth = LootLens2TextWidth(value, textScale);
            if (cursor + valueWidth > right)
                break;
            draw.AddText(font, baseFont * textScale,
                new Vector2(cursor, y + 3f * scale), metric.Color, value);
            cursor += valueWidth;
        }

        draw.AddLine(new Vector2(left, y + 18f * scale),
            new Vector2(right, y + 18f * scale), mutedLine,
            Math.Max(1f, scale * .65f));
    }

    private List<(string Label, string Value, uint Color)>
        BuildPoe1SummaryMetrics(Entity item, Mods mods)
    {
        var metrics = new List<(string, string, uint)>();
        var dps = CalculateWeaponDps(item, mods);
        if (dps.Total > .01d)
        {
            metrics.Add(("PDPS", dps.Physical.ToString("0.0"),
                ToImGuiColor(new Color(215, 192, 156, 255))));
            metrics.Add(("EDPS", dps.Elemental.ToString("0.0"),
                ToImGuiColor(new Color(112, 165, 235, 255))));
            metrics.Add(("DPS", dps.Total.ToString("0.0"),
                ToImGuiColor(new Color(231, 233, 238, 255))));
            return metrics;
        }

        var armour = item.GetComponent<Armour>();
        if (armour != null)
        {
            var defenses = CalculateFinalDefenses(item, mods, armour);
            if (defenses.Armour > 0)
                metrics.Add(("ARMOUR", defenses.Armour.ToString(),
                    ToImGuiColor(new Color(203, 184, 151, 255))));
            if (defenses.Evasion > 0)
                metrics.Add(("EVASION", defenses.Evasion.ToString(),
                    ToImGuiColor(new Color(83, 215, 125, 255))));
            if (defenses.EnergyShield > 0)
                metrics.Add(("ES", defenses.EnergyShield.ToString(),
                    ToImGuiColor(new Color(112, 165, 235, 255))));
        }

        return metrics;
    }

    private static void DrawPoe1PerfectionFooter(ImDrawListPtr draw,
        ImFontPtr font, float baseFont, float x, float y, float width,
        float scale, UniquePerfectionResult perfection, string uniqueTier,
        uint subdued, uint mutedLine)
    {
        draw.AddLine(new Vector2(x + 14f * scale, y + 2f * scale),
            new Vector2(x + width - 14f * scale, y + 2f * scale),
            mutedLine, Math.Max(1f, scale));
        draw.AddText(font, baseFont * .58f * scale,
            new Vector2(x + 14f * scale, y + 6f * scale), subdued,
            "ITEM PERFECTION");
        var score = $"{perfection.Percentage}%";
        var scoreColor = GetPerfectionColor(perfection.Percentage);
        var scoreScale = .76f * scale;
        var scoreWidth = LootLens2TextWidth(score, scoreScale);
        draw.AddText(font, baseFont * scoreScale,
            new Vector2(x + width - 14f * scale - scoreWidth,
                y + 3f * scale), scoreColor, score);

        var barLeft = x + 14f * scale;
        var barRight = x + width - 14f * scale;
        var barY = y + 26f * scale;
        draw.AddLine(new Vector2(barLeft, barY),
            new Vector2(barRight, barY),
            ToImGuiColor(new Color(112, 115, 122, 205)),
            Math.Max(1f, scale));
        var markerX = barLeft + (barRight - barLeft) *
            perfection.Percentage / 100f;
        draw.AddLine(new Vector2(barLeft, barY),
            new Vector2(markerX, barY), scoreColor, Math.Max(1f, scale));
        draw.AddCircleFilled(new Vector2(markerX, barY), 2.4f * scale,
            scoreColor);
        var rollText = $"{perfection.VariableRollCount} " +
                       (perfection.VariableRollCount == 1 ? "Roll" : "Rolls") +
                       "  |  " +
                       $"{perfection.VariableModCount} Variable " +
                       (perfection.VariableModCount == 1 ? "Mod" : "Mods");
        draw.AddText(font, baseFont * .48f * scale,
            new Vector2(barLeft, y + 35f * scale), subdued, rollText);

        var tierText = string.IsNullOrWhiteSpace(uniqueTier)
            ? "UNIQUE"
            : $"{uniqueTier} UNIQUE";
        var tierScale = .48f * scale;
        var tierWidth = LootLens2TextWidth(tierText, tierScale);
        draw.AddText(font, baseFont * tierScale,
            new Vector2(barRight - tierWidth, y + 35f * scale),
            ToImGuiColor(new Color(196, 104, 46, 210)), tierText);
    }

    private static void DrawPoe1QualificationFooter(ImDrawListPtr draw,
        ImFontPtr font, float baseFont, float x, float y, float width,
        float scale, List<string> details, int rating, uint normal,
        uint subdued, uint mutedLine)
    {
        draw.AddLine(new Vector2(x + 14f * scale, y + 2f * scale),
            new Vector2(x + width - 14f * scale, y + 2f * scale),
            mutedLine, Math.Max(1f, scale));
        draw.AddText(font, baseFont * .55f * scale,
            new Vector2(x + 14f * scale, y + 8f * scale), normal,
            $"{details.Count} MATCHES");

        var gold = ToImGuiColor(new Color(255, 215, 0, 255));
        var starSpacing = 14f * scale;
        var starRight = x + width - 14f * scale;
        for (var star = 0; star < 3; star++)
        {
            var center = new Vector2(starRight - (2 - star) * starSpacing,
                y + 13f * scale);
            if (star < Math.Clamp(rating, 0, 3))
                DrawFivePointStar(draw, center, 4.6f * scale, gold);
            else
                DrawFivePointStarOutline(draw, center, 4.6f * scale,
                    ToImGuiColor(new Color(100, 103, 112, 255)));
        }

        if (details.Count == 0)
            return;
        var columns = Math.Min(4, details.Count);
        var columnWidth = (width - 26f * scale) / columns;
        for (var index = 0; index < details.Count; index++)
        {
            if (!TryParseCompactDetail(details[index], out var label,
                    out var actual, out var required, out var suffix))
                continue;
            var row = index / columns;
            var column = index % columns;
            var left = x + 13f * scale + column * columnWidth;
            var top = y + (22f + row * 18f) * scale;
            var color = GetCompactStatColor(label);
            draw.AddCircleFilled(new Vector2(left + 3f * scale,
                top + 5f * scale), 2.4f * scale, color);
            var compactLabel = GetAttachedQualifierLabel(label, true);
            var value = $"{actual}{suffix}/{required}{suffix}";
            var valueScale = .52f * scale;
            var valueWidth = LootLens2TextWidth(value, valueScale);
            var valueRight = left + columnWidth - 9f * scale;
            var labelLeft = left + 12f * scale;
            draw.AddText(font, baseFont * .48f * scale,
                new Vector2(labelLeft, top), subdued,
                EllipsizeLootLens2Text(compactLabel,
                    Math.Max(20f * scale,
                        valueRight - valueWidth - labelLeft - 7f * scale),
                    .48f * scale));
            draw.AddText(font, baseFont * valueScale,
                new Vector2(valueRight - valueWidth, top), normal, value);
        }
    }

    private void DrawLootLens2StyleBackground(ImDrawListPtr draw,
        Vector2 topLeft, Vector2 bottomRight, float scale)
    {
        draw.AddRectFilled(topLeft, bottomRight,
            ToImGuiColor(GetColor(Settings.ItemInfoBackground,
                new Color(0, 0, 0, 236))), 1f * scale);
        var border = GetColor(Settings.ItemInfoBorder,
            new Color(0, 0, 0, 0));
        if (border.A > 0)
            draw.AddRect(topLeft, bottomRight, ToImGuiColor(border),
                1f * scale, ImDrawFlags.None, Math.Max(1f, scale));
    }

    private static uint WithAlpha(uint color, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        var alpha = (byte)((color >> 24) & 0xFF);
        var adjusted = (uint)Math.Clamp(
            (int)Math.Round(alpha * factor), 0, 255);
        return (color & 0x00FFFFFFu) | (adjusted << 24);
    }

    private string GetPoe1BaseName(Entity item)
    {
        try
        {
            var baseItem = GameController.Files.BaseItemTypes.Translate(item.Path);
            var name = GetMemberString(baseItem,
                "BaseName", "Name", "DisplayName", "ItemName", "TypeName");
            if (IsMeaningfulName(name))
                return name.Trim();
        }
        catch { }
        return GetItemDisplayName(item);
    }

    private string GetPoe1UniqueName(Entity item, Mods mods)
    {
        var name = GetMemberString(mods, "UniqueName", "Name");
        if (IsMeaningfulName(name))
            return name.Trim();
        return GetItemDisplayName(item);
    }

    private string GetPoe1UniqueTierLabel(Entity item, Mods mods)
    {
        var tier = _itemDatabase.GetUniqueDropTier(
            GetPoe1UniqueName(item, mods));
        return string.Equals(tier, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : tier;
    }

    private static uint GetPoe1RarityColor(ItemRarity rarity)
    {
        return rarity switch
        {
            ItemRarity.Rare => ToImGuiColor(new Color(255, 222, 84, 255)),
            ItemRarity.Magic => ToImGuiColor(new Color(136, 136, 255, 255)),
            ItemRarity.Unique => ToImGuiColor(new Color(196, 104, 46, 255)),
            _ => ToImGuiColor(new Color(230, 232, 236, 255))
        };
    }

    private static List<string> ExtractQualificationDetails(
        IEnumerable<string> rows)
    {
        return rows.Where(row => row != null &&
                                 row.StartsWith("DETAIL|", StringComparison.Ordinal))
            .Select(row => row.Substring("DETAIL|".Length).Trim())
            .Where(row => !string.IsNullOrWhiteSpace(row) &&
                          !string.Equals(row, "QUALIFYING STATS",
                              StringComparison.Ordinal))
            .ToList();
    }

    private static List<string> ExtractSpecialDetails(IEnumerable<string> rows)
    {
        return rows.Where(row => row != null &&
                                 row.StartsWith("SPECIAL|", StringComparison.Ordinal))
            .Select(row => row.Substring("SPECIAL|".Length).Trim())
            .Where(row => !string.IsNullOrWhiteSpace(row)).ToList();
    }

    private static string EllipsizeLootLens2Text(string text,
        float maximumWidth, float sizeScale)
    {
        text ??= string.Empty;
        if (maximumWidth <= 0f ||
            LootLens2TextWidth(text, sizeScale) <= maximumWidth)
            return text;

        const string ellipsis = "...";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (LootLens2TextWidth(text.Substring(0, middle) + ellipsis,
                    sizeScale) <= maximumWidth)
                low = middle;
            else
                high = middle - 1;
        }
        return text.Substring(0, low).TrimEnd() + ellipsis;
    }

    private static float LootLens2TextWidth(string text, float sizeScale) =>
        ImGui.CalcTextSize(text ?? string.Empty).X * sizeScale;
}
