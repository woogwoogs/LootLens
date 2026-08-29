using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace InventoryItemAnalyzer;

public partial class InventoryItemAnalyzer
{
    public override void DrawSettings()
    {
        ImGui.TextColored(new Vector4(0.20f, 0.75f, 1.00f, 1f), "LOOTLENS");
        ImGui.TextDisabled("Item inspection, modifier tiers, defenses and configurable loot ratings.");
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##analyzer_main_tabs"))
            return;

        if (ImGui.BeginTabItem("Analyzer"))
        {
            DrawAnalyzerSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Custom Stars"))
        {
            DrawCustomStarsSettings();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Made by woogo");
        ImGui.SameLine(0f, 4f);

        var heartPos = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();
        DrawFooterHeart(ImGui.GetWindowDrawList(),
            new Vector2(heartPos.X + 6f, heartPos.Y + lineHeight * .48f));
        ImGui.Dummy(new Vector2(13f, lineHeight));

        ImGui.SameLine(0f, 4f);
        ImGui.TextDisabled(
            "please feel free to message me on Discord with any errors or suggestions.");
    }

    private static void DrawFooterHeart(ImDrawListPtr draw, Vector2 center)
    {
        var color = Col(.88f, .30f, .40f, .88f);
        draw.AddCircleFilled(center + new Vector2(-2.6f, -1.8f), 3.5f, color, 12);
        draw.AddCircleFilled(center + new Vector2(2.6f, -1.8f), 3.5f, color, 12);
        draw.AddTriangleFilled(center + new Vector2(-6f, 0f),
            center + new Vector2(6f, 0f), center + new Vector2(0f, 7f), color);
    }

    private void DrawAnalyzerSettings()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("ITEM ANALYZER");
        ImGui.Separator();
        Toggle("Enable Plugin", Settings.Enable);
        DrawItemInfoModeSelector();

        if (Settings.ShowItemInfo.Value)
        {
            DrawDefaultAnalyzerViewSelector();

            ImGui.Spacing();
            ImGui.TextDisabled("PLACEMENT & SOURCES");
            ImGui.Separator();
            ImGui.TextDisabled(
                "LootLens is attached directly to the native item tooltip.");
            Toggle("Show analyzer on equipped items",
                Settings.ItemInfoShowEquippedItems);
            ImGui.TextDisabled(
                "Inventory and stash analysis remain enabled when this is off.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("APPEARANCE");
        ImGui.Separator();
        Slider("Width", Settings.ItemInfoWidth);
        Slider("UI Scale", Settings.ItemInfoMinimalScale);
        ImGui.TextDisabled(
            "Attached compact mode automatically uses the native tooltip width.");
        ColorNodeEditor("Background", Settings.ItemInfoBackground);
        ColorNodeEditor("Border", Settings.ItemInfoBorder);
        ColorNodeEditor("Tier Highlight", Settings.ItemInfoTierColor);

        ImGui.Spacing();
        ImGui.TextDisabled("MODIFIER ANALYSIS");
        ImGui.Separator();
        DrawPerfectionRangeSelector();
        DrawFixedUniqueModsSelector();
        Toggle("Full stat colors", Settings.ItemInfoFullStatColors);
        ImGui.TextDisabled(
            "Colors modifier text as well as its accent and roll track.");
        Toggle("Show implicit modifiers", Settings.ItemInfoShowImplicitModifiers);
        Toggle("Show unique item perfection",
            Settings.ItemInfoShowOverallPerfection);

        if (ImGui.Button("Reset Analyzer Appearance"))
        {
            Settings.ItemInfoWidth.Value = 310;
            Settings.ItemInfoMinimalScale.Value = 140;
            Settings.ItemInfoBackground.Value = new SharpDX.Color(0, 0, 0, 236);
            Settings.ItemInfoBorder.Value = new SharpDX.Color(0, 0, 0, 0);
            Settings.ItemInfoTierColor.Value = SharpDX.Color.Gold;
            Settings.ItemInfoFullStatColors.Value = false;
        }

        if (ShouldShowAnalyzerKeybind())
        {
            ImGui.Spacing();
            ImGui.TextDisabled("KEYBIND");
            ImGui.Separator();
            DrawFullAnalyzerKeybind();
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("ADVANCED"))
        {
            Toggle("Debug Mode", Settings.ItemInfoDebugMode);
            ImGui.TextDisabled("Shows diagnostic item-member data in the full analyzer.");
            ImGui.TextDisabled($"PoE1 database: {_itemDatabase.BaseCount} bases | " +
                               $"{_itemDatabase.ModCount} mods | " +
                               $"{_itemDatabase.UniqueCount} uniques");
        }
    }

    private void DrawPerfectionRangeSelector()
    {
        var mode = Math.Clamp(Settings.ItemInfoPerfectionRange.Value, 0, 2);
        ImGui.Text("Roll comparison");
        ImGui.SameLine();
        if (ImGui.RadioButton("Current Tier##roll_range", mode == 0))
        {
            Settings.ItemInfoPerfectionRange.Value = 0;
            mode = 0;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("All Valid Tiers##roll_range", mode == 1))
        {
            Settings.ItemInfoPerfectionRange.Value = 1;
            mode = 1;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Item-Level Reachable##roll_range", mode == 2))
        {
            Settings.ItemInfoPerfectionRange.Value = 2;
            mode = 2;
        }

        ImGui.TextDisabled(mode switch
        {
            1 => "Compares the roll against every tier valid for this PoE1 base.",
            2 => "Compares the roll against base-valid tiers reachable at this item level.",
            _ => "Compares the roll only against its current affix tier."
        });
    }

    private void DrawFixedUniqueModsSelector()
    {
        var mode = Math.Clamp(Settings.ItemInfoFixedUniqueMods.Value, 0, 2);
        ImGui.Text("Fixed unique mods");
        ImGui.SameLine();
        if (ImGui.RadioButton("Full##fixed_unique", mode == 0))
        {
            Settings.ItemInfoFixedUniqueMods.Value = 0;
            mode = 0;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Dimmed##fixed_unique", mode == 1))
        {
            Settings.ItemInfoFixedUniqueMods.Value = 1;
            mode = 1;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Hidden##fixed_unique", mode == 2))
        {
            Settings.ItemInfoFixedUniqueMods.Value = 2;
            mode = 2;
        }

        ImGui.TextDisabled(mode switch
        {
            0 => "Shows fixed unique modifiers at normal brightness.",
            2 => "Hides fixed unique modifiers and emphasizes variable rolls only.",
            _ => "Dims fixed unique modifiers so variable roll percentages stand out."
        });
    }

    private void DrawItemInfoModeSelector()
    {
        var mode = !Settings.ShowItemInfo.Value
            ? 0
            : Settings.AlwaysShowItemInfo.Value ? 1 : 2;

        ImGui.Text("Item Info Mode");
        ImGui.SameLine();

        if (ImGui.RadioButton("Off##item_info_mode", mode == 0))
        {
            Settings.ShowItemInfo.Value = false;
            mode = 0;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Show on Hover##item_info_mode", mode == 1))
        {
            Settings.ShowItemInfo.Value = true;
            Settings.AlwaysShowItemInfo.Value = true;
            mode = 1;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Hold Key##item_info_mode", mode == 2))
        {
            Settings.ShowItemInfo.Value = true;
            Settings.AlwaysShowItemInfo.Value = false;
            mode = 2;
        }

        if (mode == 0)
            ImGui.TextDisabled("Gold and red inventory stars remain available; the item analyzer panel is hidden.");
        else if (mode == 1)
            ImGui.TextDisabled("Shows the analyzer automatically while an inventory item is hovered.");
        else
            ImGui.TextDisabled("Shows the analyzer only while its key is held over an inventory item.");
    }

    private void DrawDefaultAnalyzerViewSelector()
    {
        var compact = Settings.ItemInfoCompactMode.Value;

        ImGui.Text("Default View");
        ImGui.SameLine();
        if (ImGui.RadioButton("Compact##analyzer_view", compact))
        {
            Settings.ItemInfoCompactMode.Value = true;
            compact = true;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Full Analysis (Recommended)##analyzer_view", !compact))
        {
            Settings.ItemInfoCompactMode.Value = false;
            compact = false;
        }

        ImGui.TextDisabled(compact
            ? "Uses the original small PoE1 rating strip; hold the analyzer key for full modifier details."
            : "Uses a compact analysis strip with JSON-backed roll quality, tiers, and perfection.");
    }

    private bool ShouldShowAnalyzerKeybind()
    {
        return Settings.ShowItemInfo.Value &&
               (!Settings.AlwaysShowItemInfo.Value || Settings.ItemInfoCompactMode.Value);
    }

    private bool _capturingFullAnalyzerKey;

    private void DrawFullAnalyzerKeybind()
    {
        var showOnlyWhileHeld = !Settings.AlwaysShowItemInfo.Value;
        ImGui.Text(showOnlyWhileHeld
            ? "Hold Key to Show Analyzer"
            : "Hold Key for Full Analyzer");
        ImGui.SameLine();

        var currentKey = (int)Settings.ItemInfoHotkey.Value;
        var label = GetKeyDisplayName(currentKey);

        if (ImGui.Button(_capturingFullAnalyzerKey ? "PRESS A KEY..." : label,
            new Vector2(220f, 0f)))
        {
            _capturingFullAnalyzerKey = true;
        }

        if (_capturingFullAnalyzerKey)
        {
            for (var key = 1; key < 256; key++)
            {
                if ((GetAsyncKeyState(key) & 0x8000) == 0)
                    continue;

                // Ignore mouse buttons and modifier combinations here; the
                // analyzer uses the same virtual-key code path as its runtime
                // visibility check.
                if (key == 1 || key == 2 || key == 4 || key == 5 || key == 6)
                    continue;

                Settings.ItemInfoHotkey.Value = (System.Windows.Forms.Keys)key;
                _capturingFullAnalyzerKey = false;
                break;
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled(showOnlyWhileHeld
            ? "The analyzer is visible while this key is held."
            : "Temporarily expands the compact panel to its full details.");
    }

    private static string GetKeyDisplayName(int key)
    {
        if (key <= 0)
            return "NONE";

        try
        {
            var name = Enum.GetName(typeof(System.Windows.Forms.Keys), key);
            if (!string.IsNullOrWhiteSpace(name))
                return name.Replace("ControlKey", "CTRL");
        }
        catch
        {
        }

        return $"VK {key}";
    }

    private void DrawCustomStarsSettings()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("Set how many of your configured valuable stats an item needs to reach each star level. A stat threshold of 0 means it does not count.");
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##iro_star_tabs"))
            return;

        DrawStarTab("Helmet", 0);
        DrawStarTab("Body Armour", 1);
        DrawStarTab("Gloves", 2);
        DrawStarTab("Boots", 3);
        DrawStarTab("Shield", 4);
        DrawStarTab("Belt", 5);
        DrawStarTab("Ring", 6);
        DrawStarTab("Amulet", 7);
        DrawStarTab("Quiver", 8);
        DrawStarTab("Melee Weapon", 9);
        DrawStarTab("Bow", 10);
        DrawStarTab("Caster Weapon", 11);
        DrawSpecialModsTab();

        ImGui.EndTabBar();
    }

    private void DrawStarTab(string name, int index)
    {
        if (!ImGui.BeginTabItem(name + $"##star_slot_{index}"))
            return;

        var slot = BuildStarSlot(index);

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.20f, 0.75f, 1f, 1f),
            name.ToUpperInvariant());
        ImGui.SameLine();
        ImGui.TextDisabled("- USER-DEFINED STAR RULES");

        if (name == "Melee Weapon" || name == "Bow")
        {
            ImGui.Spacing();
            ImGui.TextDisabled("WEAPON QUALIFIER");
            ImGui.Separator();

            var dpsNode = name == "Bow"
                ? Settings.Star_Bow_DpsMode
                : Settings.Star_Weapon_DpsMode;
            var dpsMode = dpsNode.Value;
            ImGui.Text("Qualify weapons by:");
            ImGui.SameLine();
            if (ImGui.RadioButton("Physical DPS", dpsMode == 0))
            {
                dpsNode.Value = 0;
                dpsMode = 0;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Total Weapon DPS", dpsMode == 1))
            {
                dpsNode.Value = 1;
                dpsMode = 1;
            }

            ImGui.TextDisabled(
                dpsMode == 0
                    ? "Physical damage only."
                    : "Physical + elemental damage.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("STAR THRESHOLDS");
        ImGui.Separator();

        StarThresholdSlider(slot.Name, 1, slot.OneStar);
        StarThresholdSlider(slot.Name, 2, slot.TwoStar);
        StarThresholdSlider(slot.Name, 3, slot.ThreeStar);

        ImGui.Spacing();
        ImGui.TextDisabled("VALUABLE STATS");
        ImGui.Separator();

        foreach (var stat in slot.Stats)
        {
            var displayName = stat.Name;
            if ((name == "Melee Weapon" || name == "Bow") && stat.Name == "Weapon DPS")
            {
                var dpsMode = name == "Bow"
                    ? Settings.Star_Bow_DpsMode.Value
                    : Settings.Star_Weapon_DpsMode.Value;
                displayName = dpsMode == 0
                    ? "Physical DPS"
                    : "Total Weapon DPS";
            }

            DrawConfiguredStatSlider(displayName, stat.Name, stat.Node);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("The item receives the highest star tier whose required-stat count is met.");

        ImGui.Spacing();
        if (ImGui.Button($"Reset {name}"))
            ResetStarSlot(index);

        ImGui.EndTabItem();
    }

    private void DrawSpecialModsTab()
    {
        if (!ImGui.BeginTabItem("Special Mods##special_mod_stars"))
            return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.95f, 0.25f, 0.30f, 1f),
            "SPECIAL MODS - RED STARS");
        ImGui.TextDisabled("Each matched rule adds one red star. Red stars are separate from gold stars.");
        ImGui.Separator();

        Toggle("Show Special Mod Stars", Settings.ShowSpecialStars);
        ColorNodeEditor("Red Star Color", Settings.SpecialStarColor);

        ImGui.Spacing();
        ImGui.TextDisabled("RULES (one per line: Slot|modifier text)");
        ImGui.TextDisabled("Use Any|text for all slots. Matching is case-insensitive.");

        var rules = Settings.SpecialModifierRules ?? string.Empty;
        if (ImGui.InputTextMultiline("##special_mod_rules", ref rules, 16384,
            new Vector2(-1f, 300f)))
            Settings.SpecialModifierRules = rules;

        if (ImGui.Button("Restore All Recommended Rules"))
            Settings.SpecialModifierRules =
                global::InventoryItemAnalyzer.Settings.DefaultSpecialModifierRules;

        ImGui.Spacing();
        ImGui.TextDisabled("Includes recommended Helmet, Body, Gloves, Boots, Shield, Belt,");
        ImGui.TextDisabled("Ring, Amulet, Quiver, Melee, Bow, and Caster rules.");
        ImGui.EndTabItem();
    }


    private static readonly Dictionary<string, int> DefaultStarValues = new()
    {
        ["Star_Helmet_GoodRequired"] = 2,
        ["Star_Helmet_GreatRequired"] = 3,
        ["Star_Helmet_ExcellentRequired"] = 4,
        ["Star_Helmet_Life"] = 80,
        ["Star_Helmet_ColdResistance"] = 30,
        ["Star_Helmet_FireResistance"] = 30,
        ["Star_Helmet_LightningResistance"] = 30,
        ["Star_Helmet_ChaosResistance"] = 20,
        ["Star_Helmet_Armour"] = 700,
        ["Star_Helmet_Evasion"] = 700,
        ["Star_Helmet_EnergyShield"] = 160,
        ["Star_Helmet_SpellSuppression"] = 12,
        ["Star_Helmet_Attributes"] = 30,
        ["Star_BodyArmour_GoodRequired"] = 2,
        ["Star_BodyArmour_GreatRequired"] = 3,
        ["Star_BodyArmour_ExcellentRequired"] = 4,
        ["Star_BodyArmour_Life"] = 80,
        ["Star_BodyArmour_ColdResistance"] = 30,
        ["Star_BodyArmour_FireResistance"] = 30,
        ["Star_BodyArmour_LightningResistance"] = 30,
        ["Star_BodyArmour_ChaosResistance"] = 20,
        ["Star_BodyArmour_Armour"] = 1600,
        ["Star_BodyArmour_Evasion"] = 1600,
        ["Star_BodyArmour_EnergyShield"] = 400,
        ["Star_BodyArmour_SpellSuppression"] = 12,
        ["Star_BodyArmour_Attributes"] = 30,
        ["Star_Gloves_GoodRequired"] = 2,
        ["Star_Gloves_GreatRequired"] = 3,
        ["Star_Gloves_ExcellentRequired"] = 4,
        ["Star_Gloves_Life"] = 80,
        ["Star_Gloves_ColdResistance"] = 30,
        ["Star_Gloves_FireResistance"] = 30,
        ["Star_Gloves_LightningResistance"] = 30,
        ["Star_Gloves_ChaosResistance"] = 20,
        ["Star_Gloves_Armour"] = 450,
        ["Star_Gloves_Evasion"] = 450,
        ["Star_Gloves_EnergyShield"] = 110,
        ["Star_Gloves_SpellSuppression"] = 12,
        ["Star_Gloves_AttackSpeed"] = 10,
        ["Star_Gloves_CastSpeed"] = 10,
        ["Star_Gloves_CritChance"] = 0,
        ["Star_Gloves_CritMultiplier"] = 20,
        ["Star_Gloves_Accuracy"] = 0,
        ["Star_Gloves_DotMultiplier"] = 0,
        ["Star_Gloves_Attributes"] = 30,
        ["Star_Boots_GoodRequired"] = 2,
        ["Star_Boots_GreatRequired"] = 3,
        ["Star_Boots_ExcellentRequired"] = 4,
        ["Star_Boots_Life"] = 80,
        ["Star_Boots_ColdResistance"] = 30,
        ["Star_Boots_FireResistance"] = 30,
        ["Star_Boots_LightningResistance"] = 30,
        ["Star_Boots_ChaosResistance"] = 20,
        ["Star_Boots_Armour"] = 450,
        ["Star_Boots_Evasion"] = 450,
        ["Star_Boots_EnergyShield"] = 110,
        ["Star_Boots_SpellSuppression"] = 12,
        ["Star_Boots_Attributes"] = 30,
        ["Star_Boots_MovementSpeed"] = 30,
        ["Star_Boots_CooldownRecovery"] = 0,
        ["Star_Belt_GoodRequired"] = 2,
        ["Star_Belt_GreatRequired"] = 3,
        ["Star_Belt_ExcellentRequired"] = 4,
        ["Star_Belt_Life"] = 80,
        ["Star_Belt_ColdResistance"] = 30,
        ["Star_Belt_FireResistance"] = 30,
        ["Star_Belt_LightningResistance"] = 30,
        ["Star_Belt_ChaosResistance"] = 20,
        ["Star_Belt_Attributes"] = 30,
        ["Star_Belt_Mana"] = 50,
        ["Star_Belt_CooldownRecovery"] = 0,
        ["Star_Ring_GoodRequired"] = 2,
        ["Star_Ring_GreatRequired"] = 3,
        ["Star_Ring_ExcellentRequired"] = 4,
        ["Star_Ring_Life"] = 80,
        ["Star_Ring_ColdResistance"] = 30,
        ["Star_Ring_FireResistance"] = 30,
        ["Star_Ring_LightningResistance"] = 30,
        ["Star_Ring_ChaosResistance"] = 20,
        ["Star_Ring_Attributes"] = 30,
        ["Star_Ring_Mana"] = 50,
        ["Star_Ring_AttackSpeed"] = 10,
        ["Star_Ring_CastSpeed"] = 10,
        ["Star_Ring_CritChance"] = 0,
        ["Star_Ring_CritMultiplier"] = 20,
        ["Star_Ring_Accuracy"] = 200,
        ["Star_Ring_DotMultiplier"] = 0,
        ["Star_Amulet_GoodRequired"] = 2,
        ["Star_Amulet_GreatRequired"] = 3,
        ["Star_Amulet_ExcellentRequired"] = 4,
        ["Star_Amulet_Life"] = 80,
        ["Star_Amulet_ColdResistance"] = 30,
        ["Star_Amulet_FireResistance"] = 30,
        ["Star_Amulet_LightningResistance"] = 30,
        ["Star_Amulet_ChaosResistance"] = 20,
        ["Star_Amulet_Attributes"] = 30,
        ["Star_Amulet_EnergyShield"] = 100,
        ["Star_Amulet_GemLevels"] = 1,
        ["Star_Amulet_SpecificGemLevels"] = 0,
        ["Star_Amulet_CritChance"] = 0,
        ["Star_Amulet_CritMultiplier"] = 20,
        ["Star_Amulet_DotMultiplier"] = 0,
        ["Star_Shield_GoodRequired"] = 2,
        ["Star_Shield_GreatRequired"] = 3,
        ["Star_Shield_ExcellentRequired"] = 4,
        ["Star_Shield_Life"] = 80,
        ["Star_Shield_ColdResistance"] = 30,
        ["Star_Shield_FireResistance"] = 30,
        ["Star_Shield_LightningResistance"] = 30,
        ["Star_Shield_ChaosResistance"] = 20,
        ["Star_Shield_Armour"] = 700,
        ["Star_Shield_Evasion"] = 700,
        ["Star_Shield_EnergyShield"] = 140,
        ["Star_Shield_SpellSuppression"] = 12,
        ["Star_Shield_Attributes"] = 30,
        ["Star_Quiver_GoodRequired"] = 2,
        ["Star_Quiver_GreatRequired"] = 3,
        ["Star_Quiver_ExcellentRequired"] = 4,
        ["Star_Quiver_Life"] = 80,
        ["Star_Quiver_ColdResistance"] = 30,
        ["Star_Quiver_FireResistance"] = 30,
        ["Star_Quiver_LightningResistance"] = 30,
        ["Star_Quiver_ChaosResistance"] = 20,
        ["Star_Quiver_AttackSpeed"] = 10,
        ["Star_Quiver_CritChance"] = 0,
        ["Star_Quiver_CritMultiplier"] = 20,
        ["Star_Quiver_Accuracy"] = 0,
        ["Star_Quiver_DotMultiplier"] = 0,
        ["Star_Quiver_Attributes"] = 30,
        ["Star_Weapon_GoodRequired"] = 2,
        ["Star_Weapon_GreatRequired"] = 3,
        ["Star_Weapon_ExcellentRequired"] = 4,
        ["Star_Weapon_WeaponDps"] = 200,
        ["Star_Weapon_DpsMode"] = 1,
        ["Star_Weapon_AttackSpeed"] = 10,
        ["Star_Weapon_CritChance"] = 0,
        ["Star_Weapon_CritMultiplier"] = 20,
        ["Star_Weapon_Accuracy"] = 0,
        ["Star_Weapon_DotMultiplier"] = 0,
        ["Star_Weapon_ColdResistance"] = 30,
        ["Star_Weapon_FireResistance"] = 30,
        ["Star_Weapon_LightningResistance"] = 30,
        ["Star_Weapon_Attributes"] = 30,
        ["Star_Weapon_GemLevels"] = 1,
        ["Star_Weapon_SpecificGemLevels"] = 0,
        ["Star_Bow_GoodRequired"] = 2,
        ["Star_Bow_GreatRequired"] = 3,
        ["Star_Bow_ExcellentRequired"] = 4,
        ["Star_Bow_WeaponDps"] = 250,
        ["Star_Bow_DpsMode"] = 1,
        ["Star_Bow_AttackSpeed"] = 10,
        ["Star_Bow_CritChance"] = 20,
        ["Star_Bow_CritMultiplier"] = 20,
        ["Star_Bow_Accuracy"] = 300,
        ["Star_Bow_DotMultiplier"] = 0,
        ["Star_Bow_GemLevels"] = 0,
        ["Star_CasterWeapon_GoodRequired"] = 2,
        ["Star_CasterWeapon_GreatRequired"] = 3,
        ["Star_CasterWeapon_ExcellentRequired"] = 4,
        ["Star_CasterWeapon_SpellDamage"] = 60,
        ["Star_CasterWeapon_CastSpeed"] = 10,
        ["Star_CasterWeapon_CritChance"] = 0,
        ["Star_CasterWeapon_CritMultiplier"] = 20,
        ["Star_CasterWeapon_DotMultiplier"] = 15,
        ["Star_CasterWeapon_Mana"] = 60,
        ["Star_CasterWeapon_GemLevels"] = 1,
        ["Star_CasterWeapon_SpecificGemLevels"] = 0
    };

    private void ResetStarSlot(int index)
    {
        string[] slotNames =
        {
            "Helmet", "BodyArmour", "Gloves", "Boots", "Shield",
            "Belt", "Ring", "Amulet", "Quiver", "Weapon", "Bow", "CasterWeapon"
        };

        if (index < 0 || index >= slotNames.Length)
            return;

        string prefix = "Star_" + slotNames[index] + "_";

        foreach (var property in typeof(Settings).GetProperties())
        {
            if (!property.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (property.GetValue(Settings) is RangeNode<int> node &&
                DefaultStarValues.TryGetValue(property.Name, out var defaultValue))
            {
                node.Value = defaultValue;
            }
        }
    }

    private StarSlotView BuildStarSlot(int index)
    {
        switch (index)
        {
            case 0:
                return Slot("Helmet",
                    Settings.Star_Helmet_GoodRequired,
                    Settings.Star_Helmet_GreatRequired,
                    Settings.Star_Helmet_ExcellentRequired,
                    ("Life", Settings.Star_Helmet_Life),
                    ("Cold Resistance", Settings.Star_Helmet_ColdResistance),
                    ("Fire Resistance", Settings.Star_Helmet_FireResistance),
                    ("Lightning Resistance", Settings.Star_Helmet_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Helmet_ChaosResistance),
                    ("Armour", Settings.Star_Helmet_Armour),
                    ("Evasion", Settings.Star_Helmet_Evasion),
                    ("Energy Shield", Settings.Star_Helmet_EnergyShield),
                    ("Spell Suppression", Settings.Star_Helmet_SpellSuppression),
                    ("Attributes", Settings.Star_Helmet_Attributes));

            case 1:
                return Slot("Body Armour",
                    Settings.Star_BodyArmour_GoodRequired,
                    Settings.Star_BodyArmour_GreatRequired,
                    Settings.Star_BodyArmour_ExcellentRequired,
                    ("Life", Settings.Star_BodyArmour_Life),
                    ("Cold Resistance", Settings.Star_BodyArmour_ColdResistance),
                    ("Fire Resistance", Settings.Star_BodyArmour_FireResistance),
                    ("Lightning Resistance", Settings.Star_BodyArmour_LightningResistance),
                    ("Chaos Resistance", Settings.Star_BodyArmour_ChaosResistance),
                    ("Armour", Settings.Star_BodyArmour_Armour),
                    ("Evasion", Settings.Star_BodyArmour_Evasion),
                    ("Energy Shield", Settings.Star_BodyArmour_EnergyShield),
                    ("Spell Suppression", Settings.Star_BodyArmour_SpellSuppression),
                    ("Attributes", Settings.Star_BodyArmour_Attributes));

            case 2:
                return Slot("Gloves",
                    Settings.Star_Gloves_GoodRequired,
                    Settings.Star_Gloves_GreatRequired,
                    Settings.Star_Gloves_ExcellentRequired,
                    ("Life", Settings.Star_Gloves_Life),
                    ("Cold Resistance", Settings.Star_Gloves_ColdResistance),
                    ("Fire Resistance", Settings.Star_Gloves_FireResistance),
                    ("Lightning Resistance", Settings.Star_Gloves_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Gloves_ChaosResistance),
                    ("Armour", Settings.Star_Gloves_Armour),
                    ("Evasion", Settings.Star_Gloves_Evasion),
                    ("Energy Shield", Settings.Star_Gloves_EnergyShield),
                    ("Spell Suppression", Settings.Star_Gloves_SpellSuppression),
                    ("Attack Speed", Settings.Star_Gloves_AttackSpeed),
                    ("Cast Speed", Settings.Star_Gloves_CastSpeed),
                    ("Critical Strike Chance", Settings.Star_Gloves_CritChance),
                    ("Crit Multiplier", Settings.Star_Gloves_CritMultiplier),
                    ("Accuracy", Settings.Star_Gloves_Accuracy),
                    ("DoT Multiplier", Settings.Star_Gloves_DotMultiplier),
                    ("Attributes", Settings.Star_Gloves_Attributes));

            case 3:
                return Slot("Boots",
                    Settings.Star_Boots_GoodRequired,
                    Settings.Star_Boots_GreatRequired,
                    Settings.Star_Boots_ExcellentRequired,
                    ("Life", Settings.Star_Boots_Life),
                    ("Cold Resistance", Settings.Star_Boots_ColdResistance),
                    ("Fire Resistance", Settings.Star_Boots_FireResistance),
                    ("Lightning Resistance", Settings.Star_Boots_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Boots_ChaosResistance),
                    ("Armour", Settings.Star_Boots_Armour),
                    ("Evasion", Settings.Star_Boots_Evasion),
                    ("Energy Shield", Settings.Star_Boots_EnergyShield),
                    ("Spell Suppression", Settings.Star_Boots_SpellSuppression),
                    ("Attributes", Settings.Star_Boots_Attributes),
                    ("Movement Speed", Settings.Star_Boots_MovementSpeed),
                    ("Cooldown Recovery", Settings.Star_Boots_CooldownRecovery));

            case 4:
                return Slot("Shield",
                    Settings.Star_Shield_GoodRequired,
                    Settings.Star_Shield_GreatRequired,
                    Settings.Star_Shield_ExcellentRequired,
                    ("Life", Settings.Star_Shield_Life),
                    ("Cold Resistance", Settings.Star_Shield_ColdResistance),
                    ("Fire Resistance", Settings.Star_Shield_FireResistance),
                    ("Lightning Resistance", Settings.Star_Shield_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Shield_ChaosResistance),
                    ("Armour", Settings.Star_Shield_Armour),
                    ("Evasion", Settings.Star_Shield_Evasion),
                    ("Energy Shield", Settings.Star_Shield_EnergyShield),
                    ("Spell Suppression", Settings.Star_Shield_SpellSuppression),
                    ("Attributes", Settings.Star_Shield_Attributes));

            case 5:
                return Slot("Belt",
                    Settings.Star_Belt_GoodRequired,
                    Settings.Star_Belt_GreatRequired,
                    Settings.Star_Belt_ExcellentRequired,
                    ("Life", Settings.Star_Belt_Life),
                    ("Cold Resistance", Settings.Star_Belt_ColdResistance),
                    ("Fire Resistance", Settings.Star_Belt_FireResistance),
                    ("Lightning Resistance", Settings.Star_Belt_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Belt_ChaosResistance),
                    ("Attributes", Settings.Star_Belt_Attributes),
                    ("Mana", Settings.Star_Belt_Mana),
                    ("Cooldown Recovery", Settings.Star_Belt_CooldownRecovery));

            case 6:
                return Slot("Ring",
                    Settings.Star_Ring_GoodRequired,
                    Settings.Star_Ring_GreatRequired,
                    Settings.Star_Ring_ExcellentRequired,
                    ("Life", Settings.Star_Ring_Life),
                    ("Cold Resistance", Settings.Star_Ring_ColdResistance),
                    ("Fire Resistance", Settings.Star_Ring_FireResistance),
                    ("Lightning Resistance", Settings.Star_Ring_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Ring_ChaosResistance),
                    ("Attributes", Settings.Star_Ring_Attributes),
                    ("Mana", Settings.Star_Ring_Mana),
                    ("Attack Speed", Settings.Star_Ring_AttackSpeed),
                    ("Cast Speed", Settings.Star_Ring_CastSpeed),
                    ("Critical Strike Chance", Settings.Star_Ring_CritChance),
                    ("Crit Multiplier", Settings.Star_Ring_CritMultiplier),
                    ("Accuracy", Settings.Star_Ring_Accuracy),
                    ("DoT Multiplier", Settings.Star_Ring_DotMultiplier));

            case 7:
                return Slot("Amulet",
                    Settings.Star_Amulet_GoodRequired,
                    Settings.Star_Amulet_GreatRequired,
                    Settings.Star_Amulet_ExcellentRequired,
                    ("Life", Settings.Star_Amulet_Life),
                    ("Cold Resistance", Settings.Star_Amulet_ColdResistance),
                    ("Fire Resistance", Settings.Star_Amulet_FireResistance),
                    ("Lightning Resistance", Settings.Star_Amulet_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Amulet_ChaosResistance),
                    ("Attributes", Settings.Star_Amulet_Attributes),
                    ("Energy Shield", Settings.Star_Amulet_EnergyShield),
                    ("Gem Levels", Settings.Star_Amulet_GemLevels),
                    ("Specific Gem Levels", Settings.Star_Amulet_SpecificGemLevels),
                    ("Critical Strike Chance", Settings.Star_Amulet_CritChance),
                    ("Crit Multiplier", Settings.Star_Amulet_CritMultiplier),
                    ("DoT Multiplier", Settings.Star_Amulet_DotMultiplier));

            case 8:
                return Slot("Quiver",
                    Settings.Star_Quiver_GoodRequired,
                    Settings.Star_Quiver_GreatRequired,
                    Settings.Star_Quiver_ExcellentRequired,
                    ("Life", Settings.Star_Quiver_Life),
                    ("Cold Resistance", Settings.Star_Quiver_ColdResistance),
                    ("Fire Resistance", Settings.Star_Quiver_FireResistance),
                    ("Lightning Resistance", Settings.Star_Quiver_LightningResistance),
                    ("Chaos Resistance", Settings.Star_Quiver_ChaosResistance),
                    ("Attack Speed", Settings.Star_Quiver_AttackSpeed),
                    ("Critical Strike Chance", Settings.Star_Quiver_CritChance),
                    ("Crit Multiplier", Settings.Star_Quiver_CritMultiplier),
                    ("Accuracy", Settings.Star_Quiver_Accuracy),
                    ("DoT Multiplier", Settings.Star_Quiver_DotMultiplier),
                    ("Attributes", Settings.Star_Quiver_Attributes));

            case 9:
                return Slot("Melee Weapon",
                    Settings.Star_Weapon_GoodRequired,
                    Settings.Star_Weapon_GreatRequired,
                    Settings.Star_Weapon_ExcellentRequired,
                    ("Weapon DPS", Settings.Star_Weapon_WeaponDps),
                    ("Attack Speed", Settings.Star_Weapon_AttackSpeed),
                    ("Critical Strike Chance", Settings.Star_Weapon_CritChance),
                    ("Crit Multiplier", Settings.Star_Weapon_CritMultiplier),
                    ("Accuracy", Settings.Star_Weapon_Accuracy),
                    ("DoT Multiplier", Settings.Star_Weapon_DotMultiplier),
                    ("Cold Resistance", Settings.Star_Weapon_ColdResistance),
                    ("Fire Resistance", Settings.Star_Weapon_FireResistance),
                    ("Lightning Resistance", Settings.Star_Weapon_LightningResistance),
                    ("Attributes", Settings.Star_Weapon_Attributes),
                    ("Gem Levels", Settings.Star_Weapon_GemLevels),
                    ("Specific Gem Levels", Settings.Star_Weapon_SpecificGemLevels));

            case 10:
                return Slot("Bow",
                    Settings.Star_Bow_GoodRequired,
                    Settings.Star_Bow_GreatRequired,
                    Settings.Star_Bow_ExcellentRequired,
                    ("Weapon DPS", Settings.Star_Bow_WeaponDps),
                    ("Attack Speed", Settings.Star_Bow_AttackSpeed),
                    ("Critical Strike Chance", Settings.Star_Bow_CritChance),
                    ("Crit Multiplier", Settings.Star_Bow_CritMultiplier),
                    ("Accuracy", Settings.Star_Bow_Accuracy),
                    ("DoT Multiplier", Settings.Star_Bow_DotMultiplier),
                    ("Gem Levels", Settings.Star_Bow_GemLevels));

            default:
                return Slot("Caster Weapon",
                    Settings.Star_CasterWeapon_GoodRequired,
                    Settings.Star_CasterWeapon_GreatRequired,
                    Settings.Star_CasterWeapon_ExcellentRequired,
                    ("Spell Damage", Settings.Star_CasterWeapon_SpellDamage),
                    ("Cast Speed", Settings.Star_CasterWeapon_CastSpeed),
                    ("Critical Strike Chance", Settings.Star_CasterWeapon_CritChance),
                    ("Crit Multiplier", Settings.Star_CasterWeapon_CritMultiplier),
                    ("DoT Multiplier", Settings.Star_CasterWeapon_DotMultiplier),
                    ("Mana", Settings.Star_CasterWeapon_Mana),
                    ("Gem Levels", Settings.Star_CasterWeapon_GemLevels),
                    ("Specific Gem Levels", Settings.Star_CasterWeapon_SpecificGemLevels));
        }
    }

    private static StarSlotView Slot(string name,
        RangeNode<int> one, RangeNode<int> two, RangeNode<int> three,
        params (string Name, RangeNode<int> Node)[] stats)
    {
        var result = new StarSlotView
        {
            Name = name,
            OneStar = one,
            TwoStar = two,
            ThreeStar = three
        };

        foreach (var stat in stats)
            result.Stats.Add(new StarStatView { Name = stat.Name, Node = stat.Node });

        return result;
    }

    private static void Toggle(string label, ToggleNode node)
    {
        bool value = node.Value;
        if (ImGui.Checkbox(label, ref value))
            node.Value = value;
    }

    private static void StarThresholdSlider(string slotName, int stars, RangeNode<int> node)
    {
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        const float radius = 7f;
        const float gap = 4f;
        var centerY = start.Y + 9f;

        for (var i = 0; i < stars; i++)
        {
            var cx = start.X + radius + i * (radius * 2f + gap);
            DrawStar(draw, new Vector2(cx, centerY), radius, Col(1.00f, 0.78f, 0.20f));
        }

        ImGui.Dummy(new Vector2(stars * (radius * 2f + gap), 18f));
        ImGui.SameLine();
        Slider($"Stats Needed##{slotName}_{stars}", node);
    }

    private static void DrawStar(ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var points = new Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = -MathF.PI / 2f + i * MathF.PI / 5f;
            var r = (i % 2 == 0) ? radius : radius * 0.42f;
            points[i] = new Vector2(
                center.X + MathF.Cos(angle) * r,
                center.Y + MathF.Sin(angle) * r);
        }

        draw.AddConvexPolyFilled(ref points[0], points.Length, color);
    }

    private static uint Col(float r, float g, float b, float a = 1f)
    {
        byte R = (byte)Math.Clamp((int)(r * 255f), 0, 255);
        byte G = (byte)Math.Clamp((int)(g * 255f), 0, 255);
        byte B = (byte)Math.Clamp((int)(b * 255f), 0, 255);
        byte A = (byte)Math.Clamp((int)(a * 255f), 0, 255);
        return (uint)(R | ((uint)G << 8) | ((uint)B << 16) | ((uint)A << 24));
    }

    private static void Slider(string label, RangeNode<int> node)
    {
        int value = node.Value;
        ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X * 0.55f));
        if (ImGui.SliderInt(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private static bool DpsSlider(string id, string label, RangeNode<int> node)
    {
        var width = Math.Max(260f, ImGui.GetContentRegionAvail().X * 0.72f);
        var size = new Vector2(width, 24f);
        var pos = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var mouseX = ImGui.GetIO().MousePos.X;
            var fraction = Math.Clamp((mouseX - pos.X) / Math.Max(1f, width), 0f, 1f);
            var value = node.Min + (int)Math.Round(fraction * (node.Max - node.Min));
            node.Value = Math.Clamp(value, node.Min, node.Max);
        }

        var fractionFilled = node.Max <= node.Min
            ? 0f
            : Math.Clamp((node.Value - node.Min) / (float)(node.Max - node.Min), 0f, 1f);
        var draw = ImGui.GetWindowDrawList();
        var background = hovered || active
            ? Col(0.24f, 0.27f, 0.32f)
            : Col(0.18f, 0.20f, 0.24f);
        var fill = Col(0.20f, 0.62f, 0.88f);
        var border = Col(0.40f, 0.44f, 0.50f);

        draw.AddRectFilled(pos, pos + size, background, 4f);
        draw.AddRectFilled(pos, new Vector2(pos.X + width * fractionFilled, pos.Y + size.Y),
            fill, 4f);
        draw.AddRect(pos, pos + size, border, 4f);

        var handleX = pos.X + width * fractionFilled;
        draw.AddCircleFilled(new Vector2(handleX, pos.Y + size.Y * .5f), 6f,
            Col(0.94f, 0.95f, 0.98f));

        draw.AddText(new Vector2(pos.X + 8f, pos.Y + 4f),
            Col(0.98f, 0.98f, 1f), label);
        var valueText = node.Value.ToString();
        var valueWidth = ImGui.CalcTextSize(valueText).X;
        draw.AddText(new Vector2(pos.X + width - valueWidth - 8f, pos.Y + 4f),
            Col(1f, 1f, 1f), valueText);

        return hovered;
    }

    private void DrawConfiguredStatSlider(
        string displayName, string statKey, RangeNode<int> node)
    {
        bool sliderHovered;
        if (statKey == "Weapon DPS")
            sliderHovered = DpsSlider($"##weapon_dps_{displayName}", displayName, node);
        else
        {
            Slider(displayName, node);
            sliderHovered = ImGui.IsItemHovered();
        }

        if (!sliderHovered)
            return;

        ImGui.BeginTooltip();
        if (statKey == "Weapon DPS")
            ImGui.Text("Click or drag anywhere on the bar to set the DPS threshold.");
        ImGui.Text("Set to 0 to ignore this stat.");
        ImGui.TextDisabled("Threshold = minimum qualifying roll.");

        ImGui.EndTooltip();
    }

    private static void ColorNodeEditor(string label, ColorNode node)
    {
        var c = node.Value;
        var v = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        ImGui.SetNextItemWidth(220f);
        if (ImGui.ColorEdit4(label, ref v,
                ImGuiColorEditFlags.NoInputs |
                ImGuiColorEditFlags.AlphaPreviewHalf |
                ImGuiColorEditFlags.AlphaBar))
        {
            c.R = (byte)Math.Clamp((int)(v.X * 255f), 0, 255);
            c.G = (byte)Math.Clamp((int)(v.Y * 255f), 0, 255);
            c.B = (byte)Math.Clamp((int)(v.Z * 255f), 0, 255);
            c.A = (byte)Math.Clamp((int)(v.W * 255f), 0, 255);
            node.Value = c;
        }
    }

    private sealed class StarSlotView
    {
        public string Name { get; set; } = string.Empty;
        public RangeNode<int> OneStar { get; set; }
        public RangeNode<int> TwoStar { get; set; }
        public RangeNode<int> ThreeStar { get; set; }
        public List<StarStatView> Stats { get; } = new();
    }

    private sealed class StarStatView
    {
        public string Name { get; set; } = string.Empty;
        public RangeNode<int> Node { get; set; }
    }


}
