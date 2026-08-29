using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Nodes;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

using Color = SharpDX.Color;

namespace InventoryItemAnalyzer;

public partial class InventoryItemAnalyzer : BaseSettingsPlugin<Settings>
{
    public InventoryItemAnalyzer()
    {
        // ExileAPI uses this display name in its plugin menu. Keep the legacy
        // namespace/assembly identity so existing saved settings continue to
        // load after the LootLens rebrand.
        Name = "LootLens";
        Description = "PoE1 modifier rolls, tiers, defenses and configurable loot ratings.";
    }

    private HoverItemIcon _hoverItemIcon;
    private Entity _lastHoverItem;
    private SharpDX.RectangleF _lastHoverRect;
    private Vector2 _lastHoverMousePosition;
    private bool _lastHoverWasEquipped;
    private SharpDX.RectangleF _lastNativeTooltipAnchor;
    private long _lastNativeTooltipItemAddress;

    // A full stash tab can contain well over one hundred items. Refresh the
    // rating/geometry snapshot periodically, then draw that inexpensive
    // snapshot every frame so stash stars remain visible without requiring a
    // hover or re-analyzing the entire tab at the game's frame rate.
    private readonly List<StashStarMarker> _visibleStashStars = new();
    private long _nextStashStarRefreshTicks;
    private bool _stashStarSnapshotValid;

    // Analysis is expensive (modifier/stat parsing). Cache results by item
    // address so Render() only does the expensive work when an item/settings
    // actually changes.
    private readonly Dictionary<long, CachedItemAnalysis> _analysisCache = new();
    private readonly Dictionary<string, (int Tier, int TotalTiers)> _modTierCache = new(StringComparer.Ordinal);
    private long _analysisSettingsSignature;

    private sealed class CachedItemAnalysis
    {
        public int Rating;
        public int SpecialStars;
        public List<string> SpecialModNames = new();
        public long Fingerprint;
        public List<string> CompactRows;
        public List<string> FullRows;
        public UniquePerfectionResult UniquePerfection;
    }

    private sealed class UniquePerfectionResult
    {
        public int Percentage = -1;
        public int VariableRollCount;
        public int VariableModCount;
    }

    private sealed class StashStarMarker
    {
        public SharpDX.RectangleF Rect;
        public int Rating;
        public int SpecialStars;
    }

    public override bool Initialise()
    {
        _itemDatabase = Poe1ItemDatabase.Load(DirectoryFullName);

        // V43 shipped with only the recommended Boots rules. Upgrade that
        // untouched legacy default automatically so existing users receive
        // the new all-slot rule set without deleting their settings file.
        var currentSpecialRules = (Settings.SpecialModifierRules ?? string.Empty)
            .Replace("\r", string.Empty)
            .Trim();
        if (string.Equals(currentSpecialRules,
                global::InventoryItemAnalyzer.Settings.LegacyBootSpecialModifierRules,
                StringComparison.Ordinal))
        {
            Settings.SpecialModifierRules =
                global::InventoryItemAnalyzer.Settings.DefaultSpecialModifierRules;
        }
        else if (currentSpecialRules.Contains("# BOOTS", StringComparison.Ordinal) &&
                 currentSpecialRules.Contains("# SHIELD", StringComparison.Ordinal) &&
                 currentSpecialRules.Split('\n').Any(line =>
                     string.Equals(line.Trim(), "Boots|Stun", StringComparison.OrdinalIgnoreCase)))
        {
            Settings.SpecialModifierRules = string.Join("\n",
                currentSpecialRules.Split('\n').Where(line =>
                    !string.Equals(line.Trim(), "Boots|Stun",
                        StringComparison.OrdinalIgnoreCase)));
        }

        // Upgrade the old shared 100-ES defaults only when all three values
        // are still untouched. Custom ES thresholds are preserved.
        if (Settings.Star_Helmet_EnergyShield.Value == 100 &&
            Settings.Star_BodyArmour_EnergyShield.Value == 100 &&
            Settings.Star_Shield_EnergyShield.Value == 100)
        {
            Settings.Star_Helmet_EnergyShield.Value = 160;
            Settings.Star_BodyArmour_EnergyShield.Value = 400;
            Settings.Star_Shield_EnergyShield.Value = 140;
        }

        _analysisSettingsSignature = ComputeAnalysisSettingsSignature();
        _analysisCache.Clear();
        _modTierCache.Clear();
        return true;
    }

    public override Job Tick()
    {
        if (!Initialized)
            return null;

        // Settings are checked once per plugin tick, not once per inventory
        // item. This keeps cache invalidation cheap even with a full inventory.
        RefreshAnalysisCacheIfSettingsChanged();

        var hoverItemIcon = GameController?.Game?.IngameState?.UIHover?.AsObject<HoverItemIcon>();
        _hoverItemIcon = hoverItemIcon != null && hoverItemIcon.IsValid
            ? hoverItemIcon
            : null;

        return null;
    }

    public override void Render()
    {
        if (!Settings.Enable)
            return;

        try
        {
            var inventoryHandled = false;
            var inventoryHoverActive = false;
            long inventoryHoveredItemAddress = 0;

            // Existing, known-good inventory path.
            var inventoryPanel = GameController.IngameState.IngameUi.InventoryPanel;
            if (inventoryPanel != null && inventoryPanel.IsVisible)
            {
                var sourceItems =
                    GameController.IngameState.ServerData.PlayerInventories[0]
                        .Inventory.InventorySlotItems;

                if (sourceItems != null)
                {
                    foreach (var inventoryItem in sourceItems)
                    {
                        if (inventoryItem?.Item == null)
                            continue;

                        var rect = inventoryItem.GetClientRect();
                        if (rect.Width <= 0 || rect.Height <= 0)
                            continue;

                        var mouseOverItem = IsMouseOver(rect);
                        if (mouseOverItem)
                        {
                            inventoryHoverActive = true;
                            inventoryHoveredItemAddress = unchecked(
                                (long)inventoryItem.Item.Address);
                        }

                        var starRating = GetCachedRating(inventoryItem.Item);
                        var specialStars = GetCachedSpecialStars(inventoryItem.Item);
                        if (starRating > 0 || specialStars > 0)
                            DrawQualityStars(rect, starRating, specialStars);

                        if (Settings.ShowItemInfo && mouseOverItem &&
                            IsItemInfoPopupVisible())
                        {
                            DrawItemInfoOverlay(inventoryItem.Item, rect);
                            inventoryHandled = true;
                        }
                    }
                }
            }

            DrawVisibleStashStars();

            if (inventoryHandled || !Settings.ShowItemInfo)
                return;

            // Non-inventory item hovers surface through ExileCore's
            // UIHover.HoverItemIcon. Read it again during Render so the live
            // value is preferred while the Tick value remains a safe fallback.
            var hover = GetLiveHoverItemIcon();

            // An overlay above the inventory can share the same screen
            // coordinates. Only let the inventory path consume the hover when
            // UIHover agrees that the inventory entity is active.
            if (inventoryHoverActive)
            {
                var activeHoverItem = hover?.Item;
                if (activeHoverItem == null || activeHoverItem.Address == 0 ||
                    unchecked((long)activeHoverItem.Address) ==
                    inventoryHoveredItemAddress)
                    return;
            }

            // If Alt/the configured key causes the game's tooltip to rebuild and
            // UIHover briefly disappears, reuse the last valid equipped item
            // for this same key hold.
            if ((hover == null || !hover.IsValid) && IsItemInfoPopupVisible() &&
                _lastHoverItem != null && _lastHoverItem.IsValid &&
                _lastHoverItem.GetComponent<Mods>() != null &&
                (!_lastHoverWasEquipped || Settings.ItemInfoShowEquippedItems.Value) &&
                IsMouseNear(_lastHoverMousePosition, 12f))
            {
                DrawItemInfoOverlay(_lastHoverItem, _lastHoverRect);
                return;
            }

            if (hover == null || !hover.IsValid)
                return;

            // Chat-linked item hovers expose an invalid Entity pointer in this
            // ExileAPI build. LootLens intentionally does not analyze them.
            if (hover.ToolTipType == ToolTipType.ItemInChat)
            {
                _lastHoverItem = null;
                _lastHoverWasEquipped = false;
                return;
            }

            var hoverItem = hover.Item;
            var hasTooltipFrameRect = TryGetHoveredTooltipRect(hover,
                out var tooltipFrameRect);

            // Equipped gear can report ToolTipType.None even though UIHover has
            // a valid item under the cursor. Keep processing the item instead of
            // returning early so equipped gear can use the same analyzer path.

            if (hoverItem == null || hoverItem.Address == 0 ||
                !hoverItem.IsValid || hoverItem.GetComponent<Mods>() == null)
                return;

            // ToolTipType is not a reliable equipped-item signal: current
            // ExileAPI builds can report a normal tooltip type and ItemFrame
            // for equipped gear. Resolve the actual player equipment slot
            // containing this entity instead.
            var equippedHover = TryGetEquippedItemRect(hoverItem,
                out var equippedItemRect);
            if (equippedHover && !Settings.ItemInfoShowEquippedItems.Value)
            {
                _lastHoverItem = null;
                _lastHoverWasEquipped = false;
                return;
            }

            // Resolve the visible source cell independently from the tooltip.
            // Equipment and stash UI geometry is more reliable than the
            // generic Item2DIcon pointer in current ExileAPI builds.
            var stashItemRect = default(SharpDX.RectangleF);
            var hasStashRect = !equippedHover &&
                               TryGetStashItemRect(hoverItem,
                                   out stashItemRect) &&
                               IsUsableHoveredItemRect(stashItemRect);
            var hasHoverIconRect = TryGetHoveredItemIconRect(hover,
                                    out var hoverIconRect) &&
                                   IsUsableHoveredItemRect(hoverIconRect);
            var hasEquippedRect = equippedItemRect.Width > 0f &&
                                  equippedItemRect.Height > 0f &&
                                  IsUsableHoveredItemRect(equippedItemRect);
            var hasSourceItemRect = hasEquippedRect || hasStashRect ||
                                    hasHoverIconRect;
            var sourceItemRect = hasEquippedRect
                ? equippedItemRect
                : hasStashRect
                    ? stashItemRect
                    : hoverIconRect;

            // Inventory is already handled above. UIHover handles equipped,
            // stash, and other item contexts. Equipped items may not expose a
            // normal ItemFrame, so those use the mouse anchor fallback below.
            if (!hasTooltipFrameRect)
            {
                var sourceRating = GetCachedRating(hoverItem);
                var sourceSpecialStars = GetCachedSpecialStars(hoverItem);
                if (hasSourceItemRect &&
                    (sourceRating > 0 || sourceSpecialStars > 0))
                {
                    DrawQualityStars(sourceItemRect, sourceRating,
                        sourceSpecialStars);
                }

                if (IsItemInfoPopupVisible())
                {
                    var mouse = ImGuiNET.ImGui.GetIO().MousePos;
                    var mouseRect = new SharpDX.RectangleF(mouse.X, mouse.Y, 1f, 1f);
                    var fallbackRect = hasSourceItemRect
                        ? sourceItemRect
                        : mouseRect;

                    _lastHoverItem = hoverItem;
                    _lastHoverRect = fallbackRect;
                    _lastHoverMousePosition = mouse;
                    _lastHoverWasEquipped = equippedHover;

                    DrawItemInfoOverlay(hoverItem, fallbackRect);
                }
                return;
            }

            var hoverRect = tooltipFrameRect;

            if (hoverRect.Width <= 0 || hoverRect.Height <= 0)
            {
                var mouse = ImGuiNET.ImGui.GetIO().MousePos;
                hoverRect = new SharpDX.RectangleF(mouse.X, mouse.Y, 1f, 1f);
            }

            // ItemFrame is the game's tooltip frame in non-inventory contexts,
            // not the source cell beneath the cursor. UIHover has already
            // established that this is the hovered item, so testing the mouse
            // against ItemFrame would incorrectly reject stash items.

            // Keep the last valid item/rect only while the cursor is over it so
            // a transient UIHover rebuild does not cause a one-frame flash.
            _lastHoverItem = hoverItem;
            _lastHoverRect = hoverRect;
            _lastHoverMousePosition = ImGuiNET.ImGui.GetIO().MousePos;
            _lastHoverWasEquipped = equippedHover;

            var hoverRating = GetCachedRating(hoverItem);
            var hoverSpecialStars = GetCachedSpecialStars(hoverItem);
            var attachedHoverAnalyzerVisible = IsItemInfoPopupVisible();
            if (hasSourceItemRect &&
                (hoverRating > 0 || hoverSpecialStars > 0))
            {
                // Equipped and stash items retain their source-icon rating
                // even while the attached analyzer is visible.
                DrawQualityStars(sourceItemRect, hoverRating,
                    hoverSpecialStars);
            }
            else if (!attachedHoverAnalyzerVisible &&
                (hoverRating > 0 || hoverSpecialStars > 0))
                DrawQualityStars(hoverRect, hoverRating, hoverSpecialStars);

            if (IsItemInfoPopupVisible())
            {
                DrawItemInfoOverlay(hoverItem, hoverRect);
            }
            else
            {
                _lastHoverItem = null;
                _lastHoverWasEquipped = false;
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"LootLens Render: {ex}");
        }
    }


    // IFL terminology:
    // User-defined star thresholds: 1, 2, and 3 stars.
    //
    // The IFLs are used as the specification. The plugin evaluates the same
    // thresholds directly from the item's explicit mod values.

    private int GetIFLQuality(Entity item)
    {
        var mods = item.GetComponent<Mods>();
        if (mods == null || mods.ItemRarity != ItemRarity.Rare || mods.ItemMods == null)
            return 0;

        var path = item.Path ?? string.Empty;
        var stats = ReadQualityStats(mods);

        // The star system is always user-defined. There is no enable/disable
        // switch and no legacy fallback rating.
        try
        {
            var armour = item.GetComponent<Armour>();
            if (armour != null)
            {
                var finalDefenses = CalculateFinalDefenses(item, mods, armour);
                stats.Armour = finalDefenses.Armour;
                stats.Evasion = finalDefenses.Evasion;
                stats.EnergyShield = finalDefenses.EnergyShield;
            }
        }
        catch
        {
        }

        return EvaluateCustomStars(item, stats, path);
    }

    private int EvaluateCustomStars(Entity item, QualityStats s, string path)
    {
        var slot = GetStarSlot(path);
        if (slot == null)
            return 0;

        var qualifying = CountSlotQualifyingStats(item, s, slot);

        if (qualifying >= slot.ThreeStarRequired) return 3;
        if (qualifying >= slot.TwoStarRequired) return 2;
        if (qualifying >= slot.OneStarRequired) return 1;
        return 0;
    }

    private sealed class StarSlotRules
    {
        public int OneStarRequired;
        public int TwoStarRequired;
        public int ThreeStarRequired;
        public Dictionary<string, int> Thresholds = new Dictionary<string, int>();
    }

    private int CountSlotQualifyingStats(Entity item, QualityStats s, StarSlotRules slot)
    {
        var count = 0;

        foreach (var kv in slot.Thresholds)
        {
            if (kv.Value <= 0) continue;

            var value = 0;
            switch (kv.Key)
            {
                case "Life": value = s.Life; break;
                case "Cold Resistance": value = s.ColdRes + s.AllRes; break;
                case "Fire Resistance": value = s.FireRes + s.AllRes; break;
                case "Lightning Resistance": value = s.LightningRes + s.AllRes; break;
                case "Chaos Resistance": value = s.ChaosRes; break;
                case "Armour": value = s.Armour; break;
                case "Evasion": value = s.Evasion; break;
                case "Energy Shield": value = s.EnergyShield; break;
                case "Spell Suppression": value = s.SpellSuppression; break;
                case "Attributes": value = Math.Max(s.Strength, Math.Max(s.Dexterity, s.Intelligence)); break;
                case "Attack Speed": value = s.AttackSpeed; break;
                case "Cast Speed": value = s.CastSpeed; break;
                case "Critical Strike Chance": value = s.CritChance; break;
                case "Crit Multiplier": value = s.CritMultiplier; break;
                case "Accuracy": value = s.Accuracy; break;
                case "Cooldown Recovery": value = s.CooldownRecovery; break;
                case "DoT Multiplier": value = s.DotMultiplier; break;
                case "Movement Speed": value = s.MoveSpeed; break;
                case "Mana": value = s.Mana; break;
                case "Spell Damage": value = s.SpellDamage; break;
                case "Gem Levels": value = s.GemLevels; break;
                case "Specific Gem Levels": value = s.SpecificGemLevels; break;
                case "Physical DPS":
                    {
                        var mods = item.GetComponent<Mods>();
                        if (mods != null)
                            value = (int)Math.Round(CalculateWeaponDps(item, mods).Physical);
                    }
                    break;

                case "Total Weapon DPS":
                    {
                        var mods = item.GetComponent<Mods>();
                        if (mods != null)
                            value = (int)Math.Round(CalculateWeaponDps(item, mods).Total);
                    }
                    break;
            }

            if (value >= kv.Value) count++;
        }

        return count;
    }

    private StarSlotRules GetStarSlot(string path)
    {
        var p = (path ?? string.Empty).ToLowerInvariant();

        // Match specific equipment classes first. Many PoE armour metadata
        // paths contain the generic word "Armour", so checking that first can
        // incorrectly assign boots/gloves to the Body Armour rules.
        if (p.Contains("helmet") || p.Contains("helm") || p.Contains("circlet")) return BuildHelmetRules();
        if (p.Contains("boot")) return BuildBootsRules();
        if (p.Contains("glove")) return BuildGlovesRules();
        if (p.Contains("belt")) return BuildBeltRules();
        if (p.Contains("bodyarmour") || p.Contains("bodyarmours") ||
            p.Contains("body_armour") || p.Contains("chest"))
            return BuildBodyArmourRules();
        if (p.Contains("ring")) return BuildRingRules();
        if (p.Contains("amulet")) return BuildAmuletRules();
        if (p.Contains("shield") || p.Contains("buckler")) return BuildShieldRules();
        if (p.Contains("quiver")) return BuildQuiverRules();
        if (IsBow(path)) return BuildBowRules();
        if (IsCasterWeapon(path)) return BuildCasterWeaponRules();
        if (IsWeaponLike(path)) return BuildWeaponRules();

        return null;
    }

    private StarSlotRules BuildHelmetRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Helmet_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Helmet_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Helmet_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Helmet_Life.Value,
                ["Cold Resistance"] = Settings.Star_Helmet_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Helmet_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Helmet_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Helmet_ChaosResistance.Value,
                ["Armour"] = Settings.Star_Helmet_Armour.Value,
                ["Evasion"] = Settings.Star_Helmet_Evasion.Value,
                ["Energy Shield"] = Settings.Star_Helmet_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_Helmet_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_Helmet_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildBodyArmourRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_BodyArmour_GoodRequired.Value,
            TwoStarRequired = Settings.Star_BodyArmour_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_BodyArmour_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_BodyArmour_Life.Value,
                ["Cold Resistance"] = Settings.Star_BodyArmour_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_BodyArmour_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_BodyArmour_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_BodyArmour_ChaosResistance.Value,
                ["Armour"] = Settings.Star_BodyArmour_Armour.Value,
                ["Evasion"] = Settings.Star_BodyArmour_Evasion.Value,
                ["Energy Shield"] = Settings.Star_BodyArmour_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_BodyArmour_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_BodyArmour_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildGlovesRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Gloves_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Gloves_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Gloves_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Gloves_Life.Value,
                ["Cold Resistance"] = Settings.Star_Gloves_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Gloves_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Gloves_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Gloves_ChaosResistance.Value,
                ["Armour"] = Settings.Star_Gloves_Armour.Value,
                ["Evasion"] = Settings.Star_Gloves_Evasion.Value,
                ["Energy Shield"] = Settings.Star_Gloves_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_Gloves_SpellSuppression.Value,
                ["Attack Speed"] = Settings.Star_Gloves_AttackSpeed.Value,
                ["Cast Speed"] = Settings.Star_Gloves_CastSpeed.Value,
                ["Critical Strike Chance"] = Settings.Star_Gloves_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_Gloves_CritMultiplier.Value,
                ["Accuracy"] = Settings.Star_Gloves_Accuracy.Value,
                ["DoT Multiplier"] = Settings.Star_Gloves_DotMultiplier.Value,
                ["Attributes"] = Settings.Star_Gloves_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildBootsRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Boots_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Boots_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Boots_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Boots_Life.Value,
                ["Cold Resistance"] = Settings.Star_Boots_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Boots_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Boots_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Boots_ChaosResistance.Value,
                ["Armour"] = Settings.Star_Boots_Armour.Value,
                ["Evasion"] = Settings.Star_Boots_Evasion.Value,
                ["Energy Shield"] = Settings.Star_Boots_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_Boots_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_Boots_Attributes.Value,
                ["Movement Speed"] = Settings.Star_Boots_MovementSpeed.Value,
                ["Cooldown Recovery"] = Settings.Star_Boots_CooldownRecovery.Value,
            }
        };
    }

    private StarSlotRules BuildBeltRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Belt_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Belt_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Belt_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Belt_Life.Value,
                ["Cold Resistance"] = Settings.Star_Belt_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Belt_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Belt_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Belt_ChaosResistance.Value,
                ["Attributes"] = Settings.Star_Belt_Attributes.Value,
                ["Mana"] = Settings.Star_Belt_Mana.Value,
                ["Cooldown Recovery"] = Settings.Star_Belt_CooldownRecovery.Value,
            }
        };
    }

    private StarSlotRules BuildRingRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Ring_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Ring_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Ring_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Ring_Life.Value,
                ["Cold Resistance"] = Settings.Star_Ring_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Ring_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Ring_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Ring_ChaosResistance.Value,
                ["Attributes"] = Settings.Star_Ring_Attributes.Value,
                ["Mana"] = Settings.Star_Ring_Mana.Value,
                ["Attack Speed"] = Settings.Star_Ring_AttackSpeed.Value,
                ["Cast Speed"] = Settings.Star_Ring_CastSpeed.Value,
                ["Critical Strike Chance"] = Settings.Star_Ring_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_Ring_CritMultiplier.Value,
                ["Accuracy"] = Settings.Star_Ring_Accuracy.Value,
                ["DoT Multiplier"] = Settings.Star_Ring_DotMultiplier.Value,
            }
        };
    }

    private StarSlotRules BuildAmuletRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Amulet_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Amulet_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Amulet_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Amulet_Life.Value,
                ["Cold Resistance"] = Settings.Star_Amulet_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Amulet_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Amulet_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Amulet_ChaosResistance.Value,
                ["Attributes"] = Settings.Star_Amulet_Attributes.Value,
                ["Energy Shield"] = Settings.Star_Amulet_EnergyShield.Value,
                ["Gem Levels"] = Settings.Star_Amulet_GemLevels.Value,
                ["Specific Gem Levels"] = Settings.Star_Amulet_SpecificGemLevels.Value,
                ["Critical Strike Chance"] = Settings.Star_Amulet_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_Amulet_CritMultiplier.Value,
                ["DoT Multiplier"] = Settings.Star_Amulet_DotMultiplier.Value,
            }
        };
    }

    private StarSlotRules BuildShieldRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Shield_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Shield_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Shield_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Shield_Life.Value,
                ["Cold Resistance"] = Settings.Star_Shield_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Shield_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Shield_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Shield_ChaosResistance.Value,
                ["Armour"] = Settings.Star_Shield_Armour.Value,
                ["Evasion"] = Settings.Star_Shield_Evasion.Value,
                ["Energy Shield"] = Settings.Star_Shield_EnergyShield.Value,
                ["Spell Suppression"] = Settings.Star_Shield_SpellSuppression.Value,
                ["Attributes"] = Settings.Star_Shield_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildQuiverRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Quiver_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Quiver_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Quiver_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Life"] = Settings.Star_Quiver_Life.Value,
                ["Cold Resistance"] = Settings.Star_Quiver_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Quiver_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Quiver_LightningResistance.Value,
                ["Chaos Resistance"] = Settings.Star_Quiver_ChaosResistance.Value,
                ["Attack Speed"] = Settings.Star_Quiver_AttackSpeed.Value,
                ["Critical Strike Chance"] = Settings.Star_Quiver_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_Quiver_CritMultiplier.Value,
                ["Accuracy"] = Settings.Star_Quiver_Accuracy.Value,
                ["DoT Multiplier"] = Settings.Star_Quiver_DotMultiplier.Value,
                ["Attributes"] = Settings.Star_Quiver_Attributes.Value,
            }
        };
    }

    private StarSlotRules BuildWeaponRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Weapon_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Weapon_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Weapon_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                [Settings.Star_Weapon_DpsMode.Value == 0 ? "Physical DPS" : "Total Weapon DPS"] =
                    Settings.Star_Weapon_WeaponDps.Value,
                ["Attack Speed"] = Settings.Star_Weapon_AttackSpeed.Value,
                ["Critical Strike Chance"] = Settings.Star_Weapon_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_Weapon_CritMultiplier.Value,
                ["Accuracy"] = Settings.Star_Weapon_Accuracy.Value,
                ["DoT Multiplier"] = Settings.Star_Weapon_DotMultiplier.Value,
                ["Cold Resistance"] = Settings.Star_Weapon_ColdResistance.Value,
                ["Fire Resistance"] = Settings.Star_Weapon_FireResistance.Value,
                ["Lightning Resistance"] = Settings.Star_Weapon_LightningResistance.Value,
                ["Attributes"] = Settings.Star_Weapon_Attributes.Value,
                ["Gem Levels"] = Settings.Star_Weapon_GemLevels.Value,
                ["Specific Gem Levels"] = Settings.Star_Weapon_SpecificGemLevels.Value,
            }
        };
    }

    private StarSlotRules BuildBowRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_Bow_GoodRequired.Value,
            TwoStarRequired = Settings.Star_Bow_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_Bow_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                [Settings.Star_Bow_DpsMode.Value == 0 ? "Physical DPS" : "Total Weapon DPS"] =
                    Settings.Star_Bow_WeaponDps.Value,
                ["Attack Speed"] = Settings.Star_Bow_AttackSpeed.Value,
                ["Critical Strike Chance"] = Settings.Star_Bow_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_Bow_CritMultiplier.Value,
                ["Accuracy"] = Settings.Star_Bow_Accuracy.Value,
                ["DoT Multiplier"] = Settings.Star_Bow_DotMultiplier.Value,
                ["Gem Levels"] = Settings.Star_Bow_GemLevels.Value,
            }
        };
    }

    private StarSlotRules BuildCasterWeaponRules()
    {
        return new StarSlotRules
        {
            OneStarRequired = Settings.Star_CasterWeapon_GoodRequired.Value,
            TwoStarRequired = Settings.Star_CasterWeapon_GreatRequired.Value,
            ThreeStarRequired = Settings.Star_CasterWeapon_ExcellentRequired.Value,
            Thresholds = new Dictionary<string, int>
            {
                ["Spell Damage"] = Settings.Star_CasterWeapon_SpellDamage.Value,
                ["Cast Speed"] = Settings.Star_CasterWeapon_CastSpeed.Value,
                ["Critical Strike Chance"] = Settings.Star_CasterWeapon_CritChance.Value,
                ["Crit Multiplier"] = Settings.Star_CasterWeapon_CritMultiplier.Value,
                ["DoT Multiplier"] = Settings.Star_CasterWeapon_DotMultiplier.Value,
                ["Mana"] = Settings.Star_CasterWeapon_Mana.Value,
                ["Gem Levels"] = Settings.Star_CasterWeapon_GemLevels.Value,
                ["Specific Gem Levels"] = Settings.Star_CasterWeapon_SpecificGemLevels.Value,
            }
        };
    }

    private int EvaluateBoots(QualityStats s)
    {
        // Excellent:
        // 30 MS + 90 Life + 2 strong res
        // OR 35 MS + 80 Life + 2 strong res
        if ((s.MoveSpeed >= 30 && StrongPoolCount(s) >= 2) ||
            (s.MoveSpeed >= 35 && StrongPoolCount(s) >= 2))
            return 3;

        // Great: 30 MS + 2 medium qualifying stats
        if (s.MoveSpeed >= 30 && MediumPoolCount(s) >= 2)
            return 2;

        // Good: 25 MS + 1 qualifying stat
        if (s.MoveSpeed >= 25 && BasicPoolCount(s) >= 1)
            return 1;

        return 0;
    }

    private int EvaluateGloves(QualityStats s)
    {
        if (StrongPoolCount(s) >= 3)
            return 3;

        if (MediumPoolCount(s) >= 2)
            return 2;

        if (BasicPoolCount(s) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateHelmet(QualityStats s)
    {
        if (StrongPoolCount(s) >= 3)
            return 3;

        if (MediumPoolCount(s) >= 2)
            return 2;

        if (BasicPoolCount(s) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateBodyArmour(QualityStats s)
    {
        if (StrongPoolCount(s) >= 3)
            return 3;

        if (MediumPoolCount(s) >= 2)
            return 2;

        if (BasicPoolCount(s) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateJewellery(QualityStats s)
    {
        if (CountTrue(
                s.Life >= 90,
                s.FireRes >= 40,
                s.ColdRes >= 40,
                s.LightningRes >= 40,
                s.ChaosRes >= 30,
                s.AllRes >= 12,
                s.Strength >= 35) >= 3)
            return 3;

        if (CountTrue(
                s.Life >= 80,
                s.FireRes >= 35,
                s.ColdRes >= 35,
                s.LightningRes >= 35,
                s.ChaosRes >= 25,
                s.AllRes >= 12,
                s.Strength >= 30) >= 2)
            return 2;

        if (CountTrue(
                s.Life >= 60,
                s.FireRes >= 30,
                s.ColdRes >= 30,
                s.LightningRes >= 30,
                s.ChaosRes >= 20,
                s.AllRes >= 10,
                s.Strength >= 25) >= 2)
            return 1;

        return 0;
    }

    private int EvaluateTwoHandAxe(Mods mods)
    {
        var addedPhys = HasMod(mods, "AddedPhysicalDamage");
        var increasedPhys = HasMod(mods, "IncreasedPhysicalDamagePercent");
        var localPhys = HasMod(mods, "LocalIncreasedPhysicalDamagePercentAndAccuracyRating");
        var attackSpeed = HasMod(mods, "LocalIncreasedAttackSpeed");

        var core = CountTrue(addedPhys, increasedPhys, localPhys);

        // Excellent: all four.
        if (core >= 3 && attackSpeed)
            return 3;

        // Great: 3 core OR 2 core + attack speed.
        if (core >= 3 || (core >= 2 && attackSpeed))
            return 2;

        // Good: 2 core.
        if (core >= 2)
            return 1;

        return 0;
    }

    private sealed class QualityStats
    {
        public int Life;
        public int FireRes;
        public int ColdRes;
        public int LightningRes;
        public int ChaosRes;
        public int AllRes;
        public int MoveSpeed;
        public int Strength;
        public int Dexterity;
        public int Intelligence;
        public int IncreasedLife;
        public int SpecificGemLevels;
        public int SpellSuppression;
        public int AttackSpeed;
        public int CastSpeed;
        public int CritMultiplier;
        public int CritChance;
        public int Accuracy;
        public int Mana;
        public int SpellDamage;
        public int CooldownRecovery;
        public int LifeRegeneration;
        public int DotMultiplier;
        public int GemLevels;
        public int Armour;
        public int Evasion;
        public int EnergyShield;
        public int IncreasedEnergyShield;
        public int EsRechargeRate;
        public int FasterEsRechargeStart;
    }

    private static QualityStats ReadQualityStats(Mods mods)
    {
        var s = new QualityStats();

        if (mods?.ItemMods == null)
            return s;

        foreach (var mod in mods.ItemMods)
        {
            if (mod == null)
                continue;

            // Different ExileCore builds expose the useful stat identity in
            // slightly different members. Use all of the stable identifiers
            // instead of relying on RawName alone.
            var key = string.Join(" ", new[]
            {
                GetMemberString(mod, "RawName"),
                GetMemberString(mod, "Name"),
                GetMemberString(mod, "DisplayName"),
                GetMemberString(mod, "Group")
            });

            var values = GetMemberValues(mod);
            var value = FirstModValue(mod);

            // The user's runtime can expose the stat identity in a form that
            // does not contain the obvious RawName token. Build a second,
            // human-readable classification string from the same translation
            // used by the overlay. This fixes cases such as "+56 to Maximum Life"
            // where the visible mod is correct but the raw key is opaque.
            var readable = GetReadableModText(mod, GetMemberString(mod, "RawName"), values);
            var statKey = key + " " + readable;
            var isMaximumResistance =
                Contains(statKey, "Maximum") &&
                Contains(statKey, "Resistance");

            if (value == 0 && values.Count > 0)
                value = ParseInt(values[0]);

            // Multi-stat mods can contain several values in one ItemMod.
            // FirstModValue() returns the first value, which is wrong for a
            // later clause such as "+26 to Armour +22 to Maximum Life".
            if ((Contains(statKey, "MaximumLife") ||
                 Contains(statKey, "MaxLife") ||
                 Contains(statKey, "Maximum Life")) &&
                readable.IndexOf("Maximum Life", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var lifeMatch = System.Text.RegularExpressions.Regex.Match(
                    readable,
                    @"([+-]?\d+)\s+to\s+Maximum\s+Life",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (lifeMatch.Success)
                    value = ParseInt(lifeMatch.Groups[1].Value);
            }

            if (Contains(statKey, "MaximumLife") || Contains(statKey, "MaxLife") ||
                Contains(statKey, "Maximum Life"))
                s.Life += value;

            if (Contains(statKey, "Increased Maximum Life") ||
                Contains(statKey, "Increased Maximum Life"))
                s.IncreasedLife += value;

            if (Contains(statKey, "Dexterity") || Contains(statKey, "Dex"))
                s.Dexterity += value;

            if (Contains(statKey, "Intelligence") || Contains(statKey, "Int"))
                s.Intelligence += value;

            if (!isMaximumResistance &&
                (Contains(statKey, "FireDamageResistancePct") ||
                 Contains(statKey, "FireResist") ||
                 Contains(statKey, "Fire Resistance")))
                s.FireRes += value;

            if (!isMaximumResistance &&
                (Contains(statKey, "ColdDamageResistancePct") ||
                 Contains(statKey, "ColdResist") ||
                 Contains(statKey, "Cold Resistance")))
                s.ColdRes += value;

            if (!isMaximumResistance &&
                (Contains(statKey, "LightningDamageResistancePct") ||
                 Contains(statKey, "LightningResist") ||
                 Contains(statKey, "Lightning Resistance")))
                s.LightningRes += value;

            if (!isMaximumResistance &&
                (Contains(statKey, "ChaosDamageResistancePct") ||
                 Contains(statKey, "ChaosResist") ||
                 Contains(statKey, "Chaos Resistance")))
                s.ChaosRes += value;

            if (!isMaximumResistance &&
                (Contains(statKey, "ResistAllElementsPct") ||
                 Contains(statKey, "AllResist") ||
                 Contains(statKey, "All Elemental Resistances")))
                s.AllRes += value;

            if (Contains(statKey, "MovementVelocityPct") ||
                Contains(statKey, "MovementSpeed") ||
                Contains(statKey, "Movement Speed"))
                s.MoveSpeed += value;

            if (Contains(statKey, "Strength"))
                s.Strength += value;

            // Some PoE attribute mods are exposed as combined text such as
            // "Strength and Dexterity" or "all Attributes". Make sure the
            // user-defined Attributes threshold can see those mods too.
            if (Contains(statKey, "all Attributes") || Contains(statKey, "All Attributes"))
            {
                s.Strength += value;
                s.Dexterity += value;
                s.Intelligence += value;
            }
            else if (Contains(statKey, "Strength and Dexterity") ||
                     Contains(statKey, "Strength & Dexterity"))
            {
                s.Strength += value;
                s.Dexterity += value;
            }
            else if (Contains(statKey, "Strength and Intelligence") ||
                     Contains(statKey, "Strength & Intelligence"))
            {
                s.Strength += value;
                s.Intelligence += value;
            }
            else if (Contains(statKey, "Dexterity and Intelligence") ||
                     Contains(statKey, "Dexterity & Intelligence"))
            {
                s.Dexterity += value;
                s.Intelligence += value;
            }

            if (Contains(statKey, "SpellSuppression") ||
                Contains(statKey, "Spell Suppression"))
                s.SpellSuppression += value;

            if (Contains(statKey, "AttackSpeed") ||
                Contains(statKey, "Attack Speed"))
                s.AttackSpeed += value;

            if (Contains(statKey, "CastSpeed") ||
                Contains(statKey, "Cast Speed"))
                s.CastSpeed += value;

            if (Contains(statKey, "CriticalStrikeMultiplier") ||
                Contains(statKey, "Critical Strike Multiplier") ||
                Contains(statKey, "CritMultiplier"))
                s.CritMultiplier += value;

            if (Contains(statKey, "CriticalStrikeChance") ||
                Contains(statKey, "Critical Strike Chance") ||
                Contains(statKey, "CritChance"))
                s.CritChance += value;

            if (Contains(statKey, "AccuracyRating") ||
                Contains(statKey, "Accuracy Rating"))
                s.Accuracy += value;

            if (Contains(statKey, "Mana"))
                s.Mana += value;

            if ((Contains(statKey, "SpellDamage") ||
                 Contains(statKey, "Spell Damage")) &&
                !Contains(statKey, "Suppress"))
                s.SpellDamage += value;

            if (Contains(statKey, "CooldownRecovery") ||
                Contains(statKey, "Cooldown Recovery"))
                s.CooldownRecovery += value;

            if (Contains(statKey, "LifeRegeneration") ||
                Contains(statKey, "Life Regeneration"))
                s.LifeRegeneration += value;

            if (Contains(statKey, "DamageOverTimeMultiplier") ||
                Contains(statKey, "Damage over Time Multiplier") ||
                Contains(statKey, "DamageOverTime"))
                s.DotMultiplier += value;

            if (Contains(statKey, "GemLevel") ||
                Contains(statKey, "Gem Level"))
            {
                s.GemLevels += value;

                // Specific gem-level modifiers contain a skill/gem name in the
                // readable text. Keep a separate counter so generic +1 to all
                // skill gems and +1 to a specific gem can be configured
                // independently.
                if (readable.IndexOf("to Level of", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    readable.IndexOf("to Level of", StringComparison.OrdinalIgnoreCase) >= 0)
                    s.SpecificGemLevels += value;
            }


            if (Contains(statKey, "EnergyShield") ||
                Contains(statKey, "Energy Shield") ||
                Contains(statKey, "maximum Energy Shield") ||
                Contains(statKey, "maximum Energy"))
            {
                if (Contains(statKey, "Recharge") || Contains(statKey, "Recharge Rate"))
                    s.EsRechargeRate += value;
                else if (Contains(statKey, "Faster") || Contains(statKey, "Start"))
                    s.FasterEsRechargeStart += value;
                else if (Contains(statKey, "Increased") ||
                         Contains(statKey, "increased Energy Shield") ||
                         Contains(statKey, "% increased Energy Shield") ||
                         Contains(statKey, "+%"))
                    s.IncreasedEnergyShield += value;
                else
                    s.EnergyShield += value;
            }

            if (Contains(statKey, "EnergyShieldRechargeRate") ||
                Contains(statKey, "Energy Shield Recharge Rate"))
                s.EsRechargeRate += value;

            if (Contains(statKey, "FasterStartOfEnergyShieldRecharge") ||
                Contains(statKey, "Faster Start of Energy Shield Recharge"))
                s.FasterEsRechargeStart += value;
        }

        return s;
    }

    private static int FirstModValue(object mod)
    {
        try
        {
            var values = GetMemberValues(mod);
            if (values.Count > 0)
            {
                var parsed = ParseInt(values[0]);
                if (parsed != 0)
                    return parsed;
            }

            var direct = GetMemberObject(mod, "Value");
            if (direct != null)
            {
                var parsed = ParseInt(direct.ToString());
                if (parsed != 0)
                    return parsed;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static int ParseInt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var match = System.Text.RegularExpressions.Regex.Match(text, @"-?\d+");
        return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
    }

    private static bool HasMod(Mods mods, string text)
    {
        foreach (var mod in mods.ItemMods)
        {
            if (mod != null &&
                (mod.RawName ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static bool Contains(string value, string search)
    {
        return value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int BasicResCount(QualityStats s)
    {
        return CountTrue(
            s.FireRes >= 30,
            s.ColdRes >= 30,
            s.LightningRes >= 30,
            s.ChaosRes >= 20,
            s.AllRes >= 10);
    }

    private static int MediumResCount(QualityStats s)
    {
        return CountTrue(
            s.FireRes >= 35,
            s.ColdRes >= 35,
            s.LightningRes >= 35,
            s.ChaosRes >= 25,
            s.AllRes >= 12);
    }

    private static int StrongResCount(QualityStats s)
    {
        return CountTrue(
            s.FireRes >= 40,
            s.ColdRes >= 40,
            s.LightningRes >= 40,
            s.ChaosRes >= 30,
            s.AllRes >= 12);
    }

    private static int BasicPoolCount(QualityStats s)
    {
        return CountTrue(
            s.Life >= 60,
            s.FireRes >= 30,
            s.ColdRes >= 30,
            s.LightningRes >= 30,
            s.ChaosRes >= 20,
            s.AllRes >= 10);
    }

    private static int MediumPoolCount(QualityStats s)
    {
        return CountTrue(
            s.Life >= 80,
            s.FireRes >= 35,
            s.ColdRes >= 35,
            s.LightningRes >= 35,
            s.ChaosRes >= 25,
            s.AllRes >= 12);
    }

    private static int StrongPoolCount(QualityStats s)
    {
        return CountTrue(
            s.Life >= 90,
            s.FireRes >= 40,
            s.ColdRes >= 40,
            s.LightningRes >= 40,
            s.ChaosRes >= 30,
            s.AllRes >= 12);
    }

    private static int CountTrue(params bool[] values)
    {
        var count = 0;
        foreach (var value in values)
            if (value)
                count++;

        return count;
    }

    private static bool IsBoot(string path)
    {
        return path.IndexOf("/Boots/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Boot/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGloves(string path)
    {
        return path.IndexOf("/Gloves/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHelmet(string path)
    {
        return path.IndexOf("/Helmets/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Helmet/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsBodyArmour(string path)
    {
        return path.IndexOf("/BodyArmours/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/BodyArmour/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsJewellery(string path)
    {
        return path.IndexOf("/Rings/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Amulets/", StringComparison.OrdinalIgnoreCase) >= 0;
    }


    private static bool IsWeaponLike(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var p = path.ToLowerInvariant();

        return p.Contains("/weapons/") ||
               p.Contains("/onehand") ||
               p.Contains("/twohand") ||
               p.Contains("/bows/") ||
               p.Contains("/staves/") ||
               p.Contains("/wands/") ||
               p.Contains("/sceptres/") ||
               p.Contains("/swords/") ||
               p.Contains("/axes/") ||
               p.Contains("/maces/") ||
               p.Contains("/claws/") ||
               p.Contains("/daggers/") ||
               p.Contains("/quarterstaves/");
    }

    private static bool IsBow(string path)
    {
        return !string.IsNullOrEmpty(path) &&
               path.IndexOf("/Bows/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCasterWeapon(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        return path.IndexOf("/Wands/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Sceptres/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Staves/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/Daggers/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTwoHandAxe(string path)
    {
        return path.IndexOf("/TwoHandAxes/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("/TwoHandedAxes/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void DrawQualityStars(SharpDX.RectangleF rect, int rating, int specialStars)
    {
        var drawList = ImGuiNET.ImGui.GetForegroundDrawList();
        var gold = Settings.StarColor.Value;
        var goldColor = (uint)(gold.R | (gold.G << 8) | (gold.B << 16) | (gold.A << 24));
        var red = Settings.SpecialStarColor.Value;
        var redColor = (uint)(red.R | (red.G << 8) | (red.B << 16) | (red.A << 24));

        const float gap = 2f;
        var starCount = Math.Max(0, rating) + Math.Max(0, specialStars);
        if (starCount <= 0)
            return;

        var heightSize = Math.Min(16f, Math.Max(8f, rect.Height - 4f));
        var widthSize = (rect.Width - Math.Max(0, starCount - 1) * gap) / starCount;
        var size = Math.Max(5f, Math.Min(heightSize, widthSize));
        var total = starCount * size + Math.Max(0, starCount - 1) * gap;
        var start = rect.Left + (rect.Width - total) / 2f;
        var cy = rect.Top + rect.Height / 2f;

        for (var i = 0; i < rating; i++)
            DrawFilledStar(drawList, start + size / 2f + i * (size + gap), cy, size, goldColor);

        for (var i = 0; i < specialStars; i++)
        {
            var position = rating + i;
            DrawFilledStar(drawList, start + size / 2f + position * (size + gap), cy, size, redColor);
        }
    }

    private static void DrawFilledStar(
        ImGuiNET.ImDrawListPtr list,
        float cx,
        float cy,
        float size,
        uint color)
    {
        var outer = size / 2f;
        var inner = outer * .45f;
        var points = new System.Numerics.Vector2[10];

        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            var angle = -MathF.PI / 2f + i * MathF.PI / 5f;

            points[i] = new System.Numerics.Vector2(
                cx + MathF.Cos(angle) * radius,
                cy + MathF.Sin(angle) * radius);
        }

        var center = new System.Numerics.Vector2(cx, cy);

        for (var i = 0; i < 10; i++)
        {
            var next = (i + 1) % 10;
            list.AddTriangleFilled(center, points[i], points[next], color);
        }
    }


    private bool IsMouseOver(SharpDX.RectangleF rect)
    {
        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        return mouse.X >= rect.Left && mouse.X <= rect.Right &&
               mouse.Y >= rect.Top && mouse.Y <= rect.Bottom;
    }

    private static bool IsMouseNear(Vector2 position, float tolerance)
    {
        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        return Math.Abs(mouse.X - position.X) <= tolerance &&
               Math.Abs(mouse.Y - position.Y) <= tolerance;
    }

    private static bool IsUsableHoveredItemRect(SharpDX.RectangleF rect)
    {
        if (rect.Width <= 0f || rect.Height <= 0f ||
            rect.Width > 320f || rect.Height > 320f)
            return false;

        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        const float tolerance = 4f;
        return mouse.X >= rect.Left - tolerance &&
               mouse.X <= rect.Right + tolerance &&
               mouse.Y >= rect.Top - tolerance &&
               mouse.Y <= rect.Bottom + tolerance;
    }

    private HoverItemIcon GetLiveHoverItemIcon()
    {
        try
        {
            var liveHover = GameController?.Game?.IngameState?.UIHover?
                .AsObject<HoverItemIcon>();
            if (liveHover != null && liveHover.IsValid)
            {
                _hoverItemIcon = liveHover;
                return liveHover;
            }
        }
        catch
        {
        }

        return _hoverItemIcon != null && _hoverItemIcon.IsValid
            ? _hoverItemIcon
            : null;
    }

    private static bool TryGetHoveredTooltipRect(HoverItemIcon hover,
        out SharpDX.RectangleF tooltipRect)
    {
        tooltipRect = default;
        if (hover == null || !hover.IsValid)
            return false;

        // ExileAPI uses different tooltip members for inventory, stash,
        // ground, and equipped items. Reflection keeps this
        // compatible with builds that do not expose every member.
        foreach (var memberName in new[]
                 {
                     "ItemFrame",
                     "InventoryItemTooltip",
                     "Tooltip",
                     "ToolTipOnGround"
                 })
        {
            var element = GetMemberObject(hover, memberName);
            if (TryGetElementClientRect(element, out tooltipRect))
                return true;
        }

        return false;
    }

    private static bool TryGetElementClientRect(object element,
        out SharpDX.RectangleF elementRect)
    {
        elementRect = default;
        if (element == null)
            return false;

        try
        {
            var method = element.GetType().GetMethod("GetClientRect",
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var result = method?.Invoke(element, null);
            if (result is not SharpDX.RectangleF rect ||
                rect.Width <= 0f || rect.Height <= 0f)
                return false;

            elementRect = rect;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetEquippedItemRect(Entity item,
        out SharpDX.RectangleF itemRect)
    {
        itemRect = default;
        if (item == null || item.Address == 0)
            return false;

        try
        {
            var inventoryPanel = GameController?.IngameState?.IngameUi?
                .InventoryPanel;
            if (inventoryPanel == null)
                return false;

            // InventoryIndex 6 through 18 are the actual equipment slots:
            // helm, amulet, body, weapons/swaps, rings, gloves, belt and boots.
            // PlayerInventory begins at 19 and is deliberately excluded.
            for (var slotIndex = (int)InventoryIndex.Helm;
                 slotIndex <= (int)InventoryIndex.Boots;
                 slotIndex++)
            {
                try
                {
                    var slotInventory = inventoryPanel[(InventoryIndex)slotIndex];
                    var slotItems = slotInventory?.ServerInventory?
                        .InventorySlotItems;
                    if (slotItems == null)
                        continue;

                    foreach (var slotItem in slotItems)
                    {
                        var equippedItem = slotItem?.Item;
                        if (equippedItem == null ||
                            equippedItem.Address != item.Address)
                            continue;

                        // Equipment InventSlotItem.GetClientRect() uses grid
                        // coordinates and can be projected into the main
                        // backpack. The slot InventoryUIElement is the real
                        // visible equipment-cell rectangle.
                        var inventoryRect = slotInventory.InventoryUIElement?
                            .GetClientRect() ?? default;
                        if (inventoryRect.Width > 0f &&
                            inventoryRect.Height > 0f)
                        {
                            itemRect = inventoryRect;
                        }
                        else
                        {
                            var slotRect = slotItem.GetClientRect();
                            if (slotRect.Width > 0f && slotRect.Height > 0f)
                                itemRect = slotRect;
                        }

                        return true;
                    }
                }
                catch
                {
                    // Some optional equipment indices are absent in older
                    // ExileAPI layouts. Continue checking the remaining slots.
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private bool TryGetStashItemRect(Entity item,
        out SharpDX.RectangleF itemRect)
    {
        itemRect = default;
        if (item == null || item.Address == 0)
            return false;

        try
        {
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            var stashInventory = stash?.VisibleStash;
            if (stash == null || !stash.IsVisible || stashInventory == null)
                return false;

            try
            {
                var slotItems = stashInventory.ServerInventory?
                    .InventorySlotItems;
                if (slotItems == null)
                    return false;

                foreach (var slotItem in slotItems)
                {
                    var stashItem = slotItem?.Item;
                    if (stashItem == null || stashItem.Address != item.Address)
                        continue;

                    var inventoryRect = stashInventory.InventoryUIElement?
                        .GetClientRect() ?? default;
                    var columns = stashInventory.TotalBoxesInInventoryRow;
                    if (inventoryRect.Width > 0f &&
                        inventoryRect.Height > 0f && columns > 0)
                    {
                        var cellSize = inventoryRect.Width / columns;
                        if (cellSize <= 0f)
                            continue;

                        itemRect = new SharpDX.RectangleF(
                            inventoryRect.Left + slotItem.PosX * cellSize,
                            inventoryRect.Top + slotItem.PosY * cellSize,
                            Math.Max(cellSize, slotItem.SizeX * cellSize),
                            Math.Max(cellSize, slotItem.SizeY * cellSize));
                        return true;
                    }
                }
            }
            catch
            {
            }
        }
        catch
        {
        }

        return false;
    }

    private void DrawVisibleStashStars()
    {
        try
        {
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            var stashInventory = stash?.VisibleStash;
            if (stash == null || !stash.IsVisible || stashInventory == null)
            {
                _visibleStashStars.Clear();
                _stashStarSnapshotValid = false;
                _nextStashStarRefreshTicks = 0;
                return;
            }

            var now = DateTime.UtcNow.Ticks;
            if (!_stashStarSnapshotValid || now >= _nextStashStarRefreshTicks)
            {
                _visibleStashStars.Clear();

                var inventoryRect = stashInventory.InventoryUIElement?
                    .GetClientRect() ?? default;
                var columns = stashInventory.TotalBoxesInInventoryRow;
                var slotItems = stashInventory.ServerInventory?
                    .InventorySlotItems;

                if (inventoryRect.Width > 0f &&
                    inventoryRect.Height > 0f && columns > 0 &&
                    slotItems != null)
                {
                    var cellSize = inventoryRect.Width / columns;
                    if (cellSize > 0f)
                    {
                        foreach (var slotItem in slotItems)
                        {
                            var stashItem = slotItem?.Item;
                            if (stashItem == null || stashItem.Address == 0 ||
                                !stashItem.IsValid ||
                                stashItem.GetComponent<Mods>() == null)
                                continue;

                            var rating = GetCachedRating(stashItem);
                            var specialStars = GetCachedSpecialStars(stashItem);
                            if (rating <= 0 && specialStars <= 0)
                                continue;

                            _visibleStashStars.Add(new StashStarMarker
                            {
                                Rect = new SharpDX.RectangleF(
                                    inventoryRect.Left +
                                    slotItem.PosX * cellSize,
                                    inventoryRect.Top +
                                    slotItem.PosY * cellSize,
                                    Math.Max(cellSize,
                                        slotItem.SizeX * cellSize),
                                    Math.Max(cellSize,
                                        slotItem.SizeY * cellSize)),
                                Rating = rating,
                                SpecialStars = specialStars
                            });
                        }
                    }
                }

                _stashStarSnapshotValid = true;
                _nextStashStarRefreshTicks = now +
                    TimeSpan.TicksPerMillisecond * 250;
            }

            foreach (var marker in _visibleStashStars)
                DrawQualityStars(marker.Rect, marker.Rating,
                    marker.SpecialStars);
        }
        catch
        {
            _visibleStashStars.Clear();
            _stashStarSnapshotValid = false;
            _nextStashStarRefreshTicks = 0;
        }
    }

    private static bool TryGetHoveredItemIconRect(HoverItemIcon hover,
        out SharpDX.RectangleF itemRect)
    {
        itemRect = default;
        if (hover == null || !hover.IsValid)
            return false;

        try
        {
            var itemIcon = hover.Item2DIcon;
            if (itemIcon == null || !itemIcon.IsValid)
                return false;

            var iconRect = itemIcon.GetClientRect();
            if (iconRect.Width <= 0f || iconRect.Height <= 0f)
                return false;

            itemRect = iconRect;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<string> BuildCompactInfoRows(Entity item, Mods mods)
    {
        var rating = mods.ItemRarity == ItemRarity.Rare ? GetIFLQuality(item) : 0;
        var specialMods = FindSpecialModifierMatches(item, mods);
        var uniquePerfection = mods.ItemRarity == ItemRarity.Unique
            ? CalculateUniquePerfection(mods)
            : null;
        return BuildCompactInfoRowsUncached(item, mods, rating, specialMods,
            uniquePerfection);
    }

    private bool IsFullAnalyzerHotkeyHeld()
    {
        var key = (int)Settings.ItemInfoHotkey.Value;
        if (key == 0)
            return false;

        return (GetAsyncKeyState(key & 0xFF) & 0x8000) != 0;
    }

    private bool IsCompactAnalyzerModeActive()
    {
        // Compact mode is the normal/default view. Holding the configured
        // analyzer hotkey temporarily reveals the complete non-compact view.
        return Settings.ItemInfoCompactMode.Value && !IsFullAnalyzerHotkeyHeld();
    }

    private List<string> BuildNormalInfoRows(Entity item, Mods mods)
    {
        var cached = GetCachedAnalysis(item, mods);

        // Compact mode is the lightweight default and can safely use cached
        // rows. The full analyzer is deliberately rebuilt only when the user
        // explicitly asks for it, preserving the original full-display path
        // and avoiding stale/colliding cached popup data.
        if (IsCompactAnalyzerModeActive())
            return cached.CompactRows;

        // The full analyzer contains the expensive ModRecord/tier work. Build
        // it once for this exact item state, then reuse it until the item or a
        // rating setting changes.
        cached.FullRows ??= BuildFullInfoRowsUncached(item, mods, cached.Rating,
            cached.SpecialModNames, cached.UniquePerfection);
        return cached.FullRows;
    }

    private List<string> FindSpecialModifierMatches(Entity item, Mods mods)
    {
        var matches = new List<string>();
        if (!Settings.ShowSpecialStars.Value || item == null || mods?.ItemMods == null)
            return matches;

        var slotName = GetTierPreviewSlotName(item.Path);
        if (string.IsNullOrWhiteSpace(slotName))
            return matches;

        var rules = (Settings.SpecialModifierRules ?? string.Empty)
            .Replace("\r", string.Empty)
            .Split('\n');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ruleText in rules)
        {
            var line = (ruleText ?? string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var separator = line.IndexOf('|');
            var ruleSlot = separator >= 0 ? line.Substring(0, separator).Trim() : "Any";
            var keyword = separator >= 0 ? line.Substring(separator + 1).Trim() : line;

            if (keyword.Length == 0)
                continue;

            var slotMatches = string.Equals(ruleSlot, "Any", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(ruleSlot, slotName, StringComparison.OrdinalIgnoreCase) ||
                              (string.Equals(ruleSlot, "Weapon", StringComparison.OrdinalIgnoreCase) &&
                               slotName.EndsWith("Weapon", StringComparison.OrdinalIgnoreCase));
            if (!slotMatches)
                continue;

            foreach (var mod in mods.ItemMods)
            {
                if (mod == null)
                    continue;

                var values = GetMemberValues(mod);
                var rawName = GetMemberString(mod, "RawName");
                var searchable = string.Join(" ", new[]
                {
                    rawName,
                    GetMemberString(mod, "Name"),
                    GetMemberString(mod, "DisplayName"),
                    GetMemberString(mod, "Group"),
                    GetReadableModText(mod, rawName, values)
                });

                if (searchable.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (seen.Add(keyword))
                    matches.Add(keyword);
                break;
            }
        }

        return matches;
    }

    private int GetCachedRating(Entity item)
    {
        if (item == null)
            return 0;

        var key = unchecked((long)item.Address);
        var mods = item.GetComponent<Mods>();
        var fingerprint = mods == null ? 0 : ComputeItemFingerprint(item, mods);

        if (_analysisCache.TryGetValue(key, out var cached) &&
            (fingerprint == 0 || cached.Fingerprint == fingerprint))
            return cached.Rating;

        var rating = GetIFLQuality(item);
        var specialMods = FindSpecialModifierMatches(item, mods);
        _analysisCache[key] = new CachedItemAnalysis
        {
            Rating = rating,
            SpecialStars = specialMods.Count,
            SpecialModNames = specialMods,
            Fingerprint = fingerprint
        };

        if (_analysisCache.Count > 512)
            _analysisCache.Clear();

        return rating;
    }

    private int GetCachedSpecialStars(Entity item)
    {
        if (item == null)
            return 0;

        var key = unchecked((long)item.Address);
        if (!_analysisCache.TryGetValue(key, out var cached))
        {
            GetCachedRating(item);
            _analysisCache.TryGetValue(key, out cached);
        }

        return cached?.SpecialStars ?? 0;
    }

    private CachedItemAnalysis GetCachedAnalysis(Entity item, Mods mods = null)
    {
        if (item == null)
            return new CachedItemAnalysis
            {
                Rating = 0,
                SpecialStars = 0,
                SpecialModNames = new List<string>(),
                CompactRows = new List<string>(),
                FullRows = new List<string>(),
                UniquePerfection = null
            };

        var key = unchecked((long)item.Address);

        _analysisCache.TryGetValue(key, out var cached);

        mods ??= item.GetComponent<Mods>();
        if (mods == null)
        {
            cached ??= new CachedItemAnalysis
            {
                Rating = 0,
                SpecialStars = 0,
                SpecialModNames = new List<string>()
            };
            _analysisCache[key] = cached;
            return cached;
        }

        var fingerprint = ComputeItemFingerprint(item, mods);

        // Item addresses can be reused by the client. Do not let a reused
        // address inherit another item's popup rows or rating.
        if (cached != null && cached.Fingerprint != 0 && cached.Fingerprint != fingerprint)
            cached = null;

        // A rating-only cache entry is enough for inventory star drawing.
        // Only build the expensive popup rows when the popup is actually
        // requested for this item.
        if (cached != null && cached.CompactRows != null && cached.Fingerprint == fingerprint)
            return cached;

        var rating = cached?.Fingerprint == fingerprint
            ? cached.Rating
            : GetIFLQuality(item);

        var specialMods = cached?.Fingerprint == fingerprint
            ? cached.SpecialModNames
            : FindSpecialModifierMatches(item, mods);

        var uniquePerfection = cached?.Fingerprint == fingerprint
            ? cached.UniquePerfection
            : null;
        if (mods.ItemRarity == ItemRarity.Unique && uniquePerfection == null)
            uniquePerfection = CalculateUniquePerfection(mods);

        cached = new CachedItemAnalysis
        {
            Rating = rating,
            SpecialStars = specialMods.Count,
            SpecialModNames = specialMods,
            Fingerprint = fingerprint,
            CompactRows = BuildCompactInfoRowsUncached(item, mods, rating, specialMods,
                uniquePerfection),
            FullRows = null,
            UniquePerfection = uniquePerfection
        };

        _analysisCache[key] = cached;

        if (_analysisCache.Count > 512)
            _analysisCache.Clear();

        return cached;
    }

    private static long ComputeItemFingerprint(Entity item, Mods mods)
    {
        unchecked
        {
            long hash = 1469598103934665603L;

            void Add(int value)
            {
                hash ^= value;
                hash *= 1099511628211L;
            }

            Add((item?.Path ?? string.Empty).GetHashCode());
            Add(mods?.ItemLevel ?? 0);
            Add(mods == null ? 0 : (int)mods.ItemRarity);
            Add(mods?.ItemMods?.Count ?? 0);

            if (mods?.ItemMods != null)
            {
                foreach (var mod in mods.ItemMods)
                {
                    if (mod == null)
                        continue;

                    Add((mod.RawName ?? mod.Name ?? string.Empty).GetHashCode());
                    foreach (var value in mod.Values)
                        Add(value);
                }
            }

            return hash;
        }
    }

    private List<string> BuildCompactInfoRowsUncached(Entity item, Mods mods, int rating,
        List<string> specialMods, UniquePerfectionResult uniquePerfection = null)
    {
        var rows = new List<string>();

        var details = new List<string>();
        if (rating > 0)
        {
            foreach (var detail in BuildUserDefinedRatingDetails(item, rating))
            {
                if (!string.IsNullOrWhiteSpace(detail))
                    details.Add(detail);
            }
        }

        if (Settings.ItemInfoShowOverallPerfection.Value &&
            uniquePerfection?.Percentage >= 0)
        {
            rows.Add($"UNIQUE PERFECTION|{uniquePerfection.Percentage}|" +
                     $"{uniquePerfection.VariableRollCount}|{uniquePerfection.VariableModCount}");
        }

        if (details.Count == 0 && (specialMods == null || specialMods.Count == 0) &&
            (!Settings.ItemInfoShowOverallPerfection.Value ||
             uniquePerfection?.Percentage < 0))
            return rows;

        if (rating > 0 && details.Count > 0)
        {
            rows.Add($"LOOT RATING|{rating}");
            rows.Add("DETAIL|QUALIFYING STATS");

            foreach (var detail in details)
                rows.Add($"DETAIL|{detail}");
        }

        if (specialMods != null && specialMods.Count > 0)
        {
            if (rows.Count > 0)
                rows.Add(string.Empty);
            rows.Add($"SPECIAL MODS|{specialMods.Count}");
            foreach (var specialMod in specialMods)
                rows.Add($"SPECIAL|{specialMod}");
        }

        return rows;
    }

    private List<string> BuildFullInfoRowsUncached(Entity item, Mods mods, int cachedRating,
        List<string> specialMods, UniquePerfectionResult uniquePerfection = null)
    {
        if (IsCompactAnalyzerModeActive())
            return BuildCompactInfoRows(item, mods);

        var rows = new List<string>();

        // Compact top section: put the item identity, rarity, affix counts,
        // item level, and defense into a small number of high-information rows.
        var displayName = GetItemDisplayName(item);
        if (mods.ItemRarity == ItemRarity.Unique)
        {
            var uniqueName = GetMemberString(mods, "UniqueName", "Name");
            if (IsMeaningfulName(uniqueName))
                displayName = uniqueName;
        }

        // Some runtime name sources can already contain our UI row protocol.
        // Never let HEADER| or its trailing metadata leak into the visible name.
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var headerIndex = displayName.IndexOf("HEADER|", StringComparison.OrdinalIgnoreCase);
            if (headerIndex >= 0)
                displayName = displayName.Substring(headerIndex + "HEADER|".Length);

            var pipeIndex = displayName.IndexOf('|');
            if (pipeIndex >= 0)
                displayName = displayName.Substring(0, pipeIndex);

            displayName = displayName.Trim();
        }

        var rarity = mods.ItemRarity.ToString();
        var affixCounts = GetAffixCounts(mods);
        var rating = cachedRating;

        if (!string.IsNullOrWhiteSpace(displayName))
            rows.Add($"HEADER|{displayName}|{rarity}|{Math.Max(0, Math.Min(3, rating))}|{specialMods?.Count ?? 0}");

        var metaParts = new List<string>();

        if (true)
        {
            var ilvl = GetMemberString(item, "ItemLevel", "ItemLvl", "Level");
            if (!string.IsNullOrWhiteSpace(ilvl))
                metaParts.Add($"ilvl {ilvl}");
        }

        if (mods.ItemRarity != ItemRarity.Unique)
        {
            metaParts.Add($"{affixCounts.Prefixes} Prefixes");
            metaParts.Add($"{affixCounts.Suffixes} Suffixes");

            if (affixCounts.Unknown > 0)
                metaParts.Add($"{affixCounts.Unknown} Unknown");
        }

        if (metaParts.Count > 0)
            rows.Add("META|" + string.Join("  |  ", metaParts));

        var itemSummary = BuildItemSummary(item, mods);
        foreach (var summaryRow in itemSummary)
            rows.Add(summaryRow);

        

        rows.Add(string.Empty);
        rows.Add("MODIFIERS");

        foreach (var row in BuildModRows(item, mods))
            rows.Add(row);

        if (Settings.ItemInfoShowOverallPerfection.Value &&
            uniquePerfection?.Percentage >= 0)
        {
            rows.Add(string.Empty);
            rows.Add($"UNIQUE PERFECTION|{uniquePerfection.Percentage}|" +
                     $"{uniquePerfection.VariableRollCount}|{uniquePerfection.VariableModCount}");
        }

        if (mods.ItemRarity == ItemRarity.Rare)
        {
            if (rating > 0)
            {
                rows.Add(string.Empty);
                rows.Add($"LOOT RATING|{rating}");
                rows.Add("DETAIL|QUALIFYING STATS");

                if (true)
                {
                    foreach (var detail in BuildUserDefinedRatingDetails(item, rating))
                        rows.Add($"DETAIL|{detail}");
                }
            }
        }

        if (specialMods != null && specialMods.Count > 0)
        {
            rows.Add(string.Empty);
            rows.Add($"SPECIAL MODS|{specialMods.Count}");
            foreach (var specialMod in specialMods)
                rows.Add($"SPECIAL|{specialMod}");
        }

        return rows;
    }


    private void RefreshAnalysisCacheIfSettingsChanged()
    {
        var signature = ComputeAnalysisSettingsSignature();

        if (signature == _analysisSettingsSignature)
            return;

        _analysisSettingsSignature = signature;
        _analysisCache.Clear();
        _visibleStashStars.Clear();
        _stashStarSnapshotValid = false;
        _nextStashStarRefreshTicks = 0;
    }

    private long ComputeAnalysisSettingsSignature()
    {
        unchecked
        {
            long hash = 1469598103934665603L;

            void Add(int value)
            {
                hash ^= value;
                hash *= 1099511628211L;
            }

            Add(Settings.Enable.Value ? 1 : 0);
            Add(Settings.ShowItemInfo.Value ? 1 : 0);
            Add(Settings.AlwaysShowItemInfo.Value ? 1 : 0);
            Add(Settings.ItemInfoCompactMode.Value ? 1 : 0);
            Add(Settings.ItemInfoDebugMode.Value ? 1 : 0);
            Add(Settings.ItemInfoShowEquippedItems.Value ? 1 : 0);
            Add(Settings.ItemInfoMinimalScale.Value);
            Add(Settings.ItemInfoWidth.Value);

            Add(Settings.Star_Weapon_DpsMode.Value);
            Add(Settings.Star_Bow_DpsMode.Value);
            Add(Settings.ShowSpecialStars.Value ? 1 : 0);
            Add((Settings.SpecialModifierRules ?? string.Empty).GetHashCode());

            // Include all rating threshold nodes without reflection.
            Add(Settings.Star_Helmet_GoodRequired.Value);
            Add(Settings.Star_Helmet_GreatRequired.Value);
            Add(Settings.Star_Helmet_ExcellentRequired.Value);
            Add(Settings.Star_Helmet_Life.Value);
            Add(Settings.Star_Helmet_ColdResistance.Value);
            Add(Settings.Star_Helmet_FireResistance.Value);
            Add(Settings.Star_Helmet_LightningResistance.Value);
            Add(Settings.Star_Helmet_ChaosResistance.Value);
            Add(Settings.Star_Helmet_Armour.Value);
            Add(Settings.Star_Helmet_Evasion.Value);
            Add(Settings.Star_Helmet_EnergyShield.Value);
            Add(Settings.Star_Helmet_SpellSuppression.Value);
            Add(Settings.Star_Helmet_Attributes.Value);

            Add(Settings.Star_BodyArmour_GoodRequired.Value);
            Add(Settings.Star_BodyArmour_GreatRequired.Value);
            Add(Settings.Star_BodyArmour_ExcellentRequired.Value);
            Add(Settings.Star_BodyArmour_Life.Value);
            Add(Settings.Star_BodyArmour_ColdResistance.Value);
            Add(Settings.Star_BodyArmour_FireResistance.Value);
            Add(Settings.Star_BodyArmour_LightningResistance.Value);
            Add(Settings.Star_BodyArmour_ChaosResistance.Value);
            Add(Settings.Star_BodyArmour_Armour.Value);
            Add(Settings.Star_BodyArmour_Evasion.Value);
            Add(Settings.Star_BodyArmour_EnergyShield.Value);
            Add(Settings.Star_BodyArmour_SpellSuppression.Value);
            Add(Settings.Star_BodyArmour_Attributes.Value);

            Add(Settings.Star_Gloves_GoodRequired.Value);
            Add(Settings.Star_Gloves_GreatRequired.Value);
            Add(Settings.Star_Gloves_ExcellentRequired.Value);
            Add(Settings.Star_Gloves_Life.Value);
            Add(Settings.Star_Gloves_ColdResistance.Value);
            Add(Settings.Star_Gloves_FireResistance.Value);
            Add(Settings.Star_Gloves_LightningResistance.Value);
            Add(Settings.Star_Gloves_ChaosResistance.Value);
            Add(Settings.Star_Gloves_Armour.Value);
            Add(Settings.Star_Gloves_Evasion.Value);
            Add(Settings.Star_Gloves_EnergyShield.Value);
            Add(Settings.Star_Gloves_SpellSuppression.Value);
            Add(Settings.Star_Gloves_AttackSpeed.Value);
            Add(Settings.Star_Gloves_CastSpeed.Value);
            Add(Settings.Star_Gloves_CritChance.Value);
            Add(Settings.Star_Gloves_CritMultiplier.Value);
            Add(Settings.Star_Gloves_Accuracy.Value);
            Add(Settings.Star_Gloves_DotMultiplier.Value);
            Add(Settings.Star_Gloves_Attributes.Value);

            Add(Settings.Star_Boots_GoodRequired.Value);
            Add(Settings.Star_Boots_GreatRequired.Value);
            Add(Settings.Star_Boots_ExcellentRequired.Value);
            Add(Settings.Star_Boots_Life.Value);
            Add(Settings.Star_Boots_ColdResistance.Value);
            Add(Settings.Star_Boots_FireResistance.Value);
            Add(Settings.Star_Boots_LightningResistance.Value);
            Add(Settings.Star_Boots_ChaosResistance.Value);
            Add(Settings.Star_Boots_Armour.Value);
            Add(Settings.Star_Boots_Evasion.Value);
            Add(Settings.Star_Boots_EnergyShield.Value);
            Add(Settings.Star_Boots_SpellSuppression.Value);
            Add(Settings.Star_Boots_Attributes.Value);
            Add(Settings.Star_Boots_MovementSpeed.Value);
            Add(Settings.Star_Boots_CooldownRecovery.Value);

            Add(Settings.Star_Belt_GoodRequired.Value);
            Add(Settings.Star_Belt_GreatRequired.Value);
            Add(Settings.Star_Belt_ExcellentRequired.Value);
            Add(Settings.Star_Belt_Life.Value);
            Add(Settings.Star_Belt_ColdResistance.Value);
            Add(Settings.Star_Belt_FireResistance.Value);
            Add(Settings.Star_Belt_LightningResistance.Value);
            Add(Settings.Star_Belt_ChaosResistance.Value);
            Add(Settings.Star_Belt_Attributes.Value);
            Add(Settings.Star_Belt_Mana.Value);
            Add(Settings.Star_Belt_CooldownRecovery.Value);

            Add(Settings.Star_Ring_GoodRequired.Value);
            Add(Settings.Star_Ring_GreatRequired.Value);
            Add(Settings.Star_Ring_ExcellentRequired.Value);
            Add(Settings.Star_Ring_Life.Value);
            Add(Settings.Star_Ring_ColdResistance.Value);
            Add(Settings.Star_Ring_FireResistance.Value);
            Add(Settings.Star_Ring_LightningResistance.Value);
            Add(Settings.Star_Ring_ChaosResistance.Value);
            Add(Settings.Star_Ring_Attributes.Value);
            Add(Settings.Star_Ring_Mana.Value);
            Add(Settings.Star_Ring_AttackSpeed.Value);
            Add(Settings.Star_Ring_CastSpeed.Value);
            Add(Settings.Star_Ring_CritChance.Value);
            Add(Settings.Star_Ring_CritMultiplier.Value);
            Add(Settings.Star_Ring_Accuracy.Value);
            Add(Settings.Star_Ring_DotMultiplier.Value);

            Add(Settings.Star_Amulet_GoodRequired.Value);
            Add(Settings.Star_Amulet_GreatRequired.Value);
            Add(Settings.Star_Amulet_ExcellentRequired.Value);
            Add(Settings.Star_Amulet_Life.Value);
            Add(Settings.Star_Amulet_ColdResistance.Value);
            Add(Settings.Star_Amulet_FireResistance.Value);
            Add(Settings.Star_Amulet_LightningResistance.Value);
            Add(Settings.Star_Amulet_ChaosResistance.Value);
            Add(Settings.Star_Amulet_Attributes.Value);
            Add(Settings.Star_Amulet_EnergyShield.Value);
            Add(Settings.Star_Amulet_GemLevels.Value);
            Add(Settings.Star_Amulet_SpecificGemLevels.Value);
            Add(Settings.Star_Amulet_CritChance.Value);
            Add(Settings.Star_Amulet_CritMultiplier.Value);
            Add(Settings.Star_Amulet_DotMultiplier.Value);

            Add(Settings.Star_Shield_GoodRequired.Value);
            Add(Settings.Star_Shield_GreatRequired.Value);
            Add(Settings.Star_Shield_ExcellentRequired.Value);
            Add(Settings.Star_Shield_Life.Value);
            Add(Settings.Star_Shield_ColdResistance.Value);
            Add(Settings.Star_Shield_FireResistance.Value);
            Add(Settings.Star_Shield_LightningResistance.Value);
            Add(Settings.Star_Shield_ChaosResistance.Value);
            Add(Settings.Star_Shield_Armour.Value);
            Add(Settings.Star_Shield_Evasion.Value);
            Add(Settings.Star_Shield_EnergyShield.Value);
            Add(Settings.Star_Shield_SpellSuppression.Value);
            Add(Settings.Star_Shield_Attributes.Value);

            Add(Settings.Star_Quiver_GoodRequired.Value);
            Add(Settings.Star_Quiver_GreatRequired.Value);
            Add(Settings.Star_Quiver_ExcellentRequired.Value);
            Add(Settings.Star_Quiver_Life.Value);
            Add(Settings.Star_Quiver_ColdResistance.Value);
            Add(Settings.Star_Quiver_FireResistance.Value);
            Add(Settings.Star_Quiver_LightningResistance.Value);
            Add(Settings.Star_Quiver_ChaosResistance.Value);
            Add(Settings.Star_Quiver_AttackSpeed.Value);
            Add(Settings.Star_Quiver_CritChance.Value);
            Add(Settings.Star_Quiver_CritMultiplier.Value);
            Add(Settings.Star_Quiver_Accuracy.Value);
            Add(Settings.Star_Quiver_DotMultiplier.Value);
            Add(Settings.Star_Quiver_Attributes.Value);

            Add(Settings.Star_Weapon_GoodRequired.Value);
            Add(Settings.Star_Weapon_GreatRequired.Value);
            Add(Settings.Star_Weapon_ExcellentRequired.Value);
            Add(Settings.Star_Weapon_WeaponDps.Value);
            Add(Settings.Star_Weapon_AttackSpeed.Value);
            Add(Settings.Star_Weapon_CritChance.Value);
            Add(Settings.Star_Weapon_CritMultiplier.Value);
            Add(Settings.Star_Weapon_Accuracy.Value);
            Add(Settings.Star_Weapon_DotMultiplier.Value);
            Add(Settings.Star_Weapon_ColdResistance.Value);
            Add(Settings.Star_Weapon_FireResistance.Value);
            Add(Settings.Star_Weapon_LightningResistance.Value);
            Add(Settings.Star_Weapon_Attributes.Value);
            Add(Settings.Star_Weapon_GemLevels.Value);
            Add(Settings.Star_Weapon_SpecificGemLevels.Value);

            Add(Settings.Star_Bow_GoodRequired.Value);
            Add(Settings.Star_Bow_GreatRequired.Value);
            Add(Settings.Star_Bow_ExcellentRequired.Value);
            Add(Settings.Star_Bow_WeaponDps.Value);
            Add(Settings.Star_Bow_AttackSpeed.Value);
            Add(Settings.Star_Bow_CritChance.Value);
            Add(Settings.Star_Bow_CritMultiplier.Value);
            Add(Settings.Star_Bow_Accuracy.Value);
            Add(Settings.Star_Bow_DotMultiplier.Value);
            Add(Settings.Star_Bow_GemLevels.Value);

            Add(Settings.Star_CasterWeapon_GoodRequired.Value);
            Add(Settings.Star_CasterWeapon_GreatRequired.Value);
            Add(Settings.Star_CasterWeapon_ExcellentRequired.Value);
            Add(Settings.Star_CasterWeapon_SpellDamage.Value);
            Add(Settings.Star_CasterWeapon_CastSpeed.Value);
            Add(Settings.Star_CasterWeapon_CritChance.Value);
            Add(Settings.Star_CasterWeapon_CritMultiplier.Value);
            Add(Settings.Star_CasterWeapon_DotMultiplier.Value);
            Add(Settings.Star_CasterWeapon_Mana.Value);
            Add(Settings.Star_CasterWeapon_GemLevels.Value);
            Add(Settings.Star_CasterWeapon_SpecificGemLevels.Value);

            return hash;
        }
    }

    private List<string> BuildItemSummary(Entity item, Mods mods)
    {
        var rows = new List<string>();

        // Read the authoritative item components instead of scraping the
        // rendered tooltip. This is the same data path used by the original
        // AdvancedTooltip weapon-DPS implementation.
        var quality = item?.GetComponent<Quality>();
        var armour = item?.GetComponent<Armour>();

        var itemLevel = mods?.ItemLevel ?? 0;
        var qualityValue = quality?.ItemQuality ?? -1;

        if (itemLevel > 0 || qualityValue >= 0)
        {
            var parts = new List<string>();
            if (itemLevel > 0)
                parts.Add($"iLvl {itemLevel}");
            if (qualityValue > 0)
                parts.Add($"Quality {qualityValue}%");
            rows.Add(string.Join("  |  ", parts));
        }

        if (armour != null)
        {
            var defenses = CalculateFinalDefenses(item, mods, armour);
            var parts = new List<string>();

            if (defenses.Armour > 0)
                parts.Add($"ARM {defenses.Armour}");
            if (defenses.Evasion > 0)
                parts.Add($"EVA {defenses.Evasion}");
            if (defenses.EnergyShield > 0)
                parts.Add($"ES {defenses.EnergyShield}");

            if (parts.Count > 0)
                rows.Add("DEFENSES|" + string.Join("  •  ", parts));
        }

        if (IsWeaponPath(item?.Path))
        {
            var dps = CalculateWeaponDps(item, mods);
            if (dps.Total > 0)
            {
                rows.Add($"DPSROW|PDPS {dps.Physical:0.0}|EDPS {dps.Elemental:0.0}|DPS {dps.Total:0.0}");
            }
        }

        return rows;
    }

    private (int Armour, int Evasion, int EnergyShield) CalculateFinalDefenses(
        Entity item,
        Mods mods,
        Armour armour)
    {
        try
        {
            // ArmourScore/EvasionScore/ESScore are the raw base rolls exposed
            // by the Armour component. The client stores positive base defence
            // rolls one point below the value shown by the native tooltip, so
            // normalise them before applying local modifiers. Since PoE 3.25,
            // item quality is a separate multiplicative "more" modifier:
            // (base + local flat) * (1 + local increased) * (1 + quality).
            double ar = armour.ArmourScore > 0 ? armour.ArmourScore + 1 : 0;
            double ev = armour.EvasionScore > 0 ? armour.EvasionScore + 1 : 0;
            double es = armour.EnergyShieldScore > 0 ?
                armour.EnergyShieldScore + 1 : 0;

            var quality = item?.GetComponent<Quality>();
            var qualityPct = quality?.ItemQuality ?? 0;

            double arInc = 0;
            double evInc = 0;
            double esInc = 0;

            double arFlat = 0;
            double evFlat = 0;
            double esFlat = 0;

            foreach (var mod in mods?.ItemMods ?? Enumerable.Empty<ItemMod>())
            {
                if (mod == null || string.IsNullOrEmpty(mod.RawName))
                    continue;

                ModsDat.ModRecord record;
                try
                {
                    record = GameController.Files.Mods.records[mod.RawName];
                }
                catch
                {
                    continue;
                }

                if (record == null)
                    continue;

                var values = GetMemberValues(mod)
                    .Select(ParseDouble)
                    .ToList();

                var pairs = record.StatNames
                    .Zip(values, (stat, value) => new { stat, value });

                foreach (var pair in pairs)
                {
                    if (pair.stat == null)
                        continue;

                    var stat = pair.stat.Key ?? string.Empty;
                    var value = pair.value;

                    if (value == 0)
                        continue;

                    // Flat local defense additions.
                    if (stat == "local_base_physical_damage_reduction_rating")
                    {
                        arFlat += value;
                        continue;
                    }

                    if (stat == "local_base_evasion_rating")
                    {
                        evFlat += value;
                        continue;
                    }

                    if (stat == "local_energy_shield")
                    {
                        esFlat += value;
                        continue;
                    }

                    // Hybrid flat Evasion + ES.
                    if (stat == "local_evasion_rating_and_energy_shield")
                    {
                        evFlat += value;
                        esFlat += value;
                        continue;
                    }

                    // Local increased defense percentages.
                    switch (stat)
                    {
                        case "local_physical_damage_reduction_rating_+%":
                            arInc += value;
                            break;

                        case "local_evasion_rating_+%":
                            evInc += value;
                            break;

                        case "local_energy_shield_+%":
                            esInc += value;
                            break;

                        case "local_armour_and_evasion_+%":
                            arInc += value;
                            evInc += value;
                            break;

                        case "local_armour_and_energy_shield_+%":
                            arInc += value;
                            esInc += value;
                            break;

                        case "local_evasion_and_energy_shield_+%":
                            evInc += value;
                            esInc += value;
                            break;

                        case "local_armour_and_evasion_and_energy_shield_+%":
                            arInc += value;
                            evInc += value;
                            esInc += value;
                            break;
                    }
                }
            }

            var qualityMultiplier = 1.0 + qualityPct / 100.0;
            var finalAr = Math.Round((ar + arFlat) *
                                     (1.0 + arInc / 100.0) * qualityMultiplier);
            var finalEv = Math.Round((ev + evFlat) *
                                     (1.0 + evInc / 100.0) * qualityMultiplier);
            var finalEs = Math.Round((es + esFlat) *
                                     (1.0 + esInc / 100.0) * qualityMultiplier);

            return ((int)finalAr, (int)finalEv, (int)finalEs);
        }
        catch
        {
            // Never let the display-only calculation break the item tooltip.
            return (
                armour?.ArmourScore ?? 0,
                armour?.EvasionScore ?? 0,
                armour?.EnergyShieldScore ?? 0);
        }
    }

    private (double Physical, double Elemental, double Total) CalculateWeaponDps(Entity item, Mods mods)
    {
        try
        {
            var weapon = item?.GetComponent<Weapon>();
            if (weapon == null || weapon.AttackTime <= 0)
                return (0, 0, 0);

            // This follows the actual AdvancedTooltip implementation:
            // Weapon.DamageMin/Max + AttackTime, then local weapon mods.
            // Match the native tooltip/AdvancedTooltip display path: base APS
            // is rounded to two decimals, local attack speed is applied, then
            // final APS is rounded again before DPS is calculated.
            var attacksPerSecond = Math.Round(1000.0 / weapon.AttackTime, 2);
            double physicalLow = weapon.DamageMin;
            double physicalHigh = weapon.DamageMax;

            var elemental = new double[(int)DamageType.Chaos + 1];
            var physicalMultiplier = 1.0;

            foreach (var mod in mods?.ItemMods ?? Enumerable.Empty<ItemMod>())
            {
                if (mod == null || string.IsNullOrEmpty(mod.RawName))
                    continue;

                ModsDat.ModRecord record;
                try
                {
                    record = GameController.Files.Mods.records[mod.RawName];
                }
                catch
                {
                    continue;
                }

                if (record == null)
                    continue;

                // StatValues are exposed as strings in this ExileCore build.
                // AdvancedTooltip gets its numeric values from ModValue.StatValue;
                // we reproduce that conversion here.
                var rawValues = GetMemberValues(mod);
                var numericValues = rawValues
                    .Select(ParseDouble)
                    .ToList();

                foreach (var pair in record.StatNames
                    .Zip(record.StatRange, (stat, range) => new { stat, range })
                    .Zip(numericValues, (pair, value) => new { pair.stat, pair.range, value }))
                {
                    var stat = pair.stat;
                    var range = pair.range;
                    var value = pair.value;

                    if (stat == null)
                        continue;

                    if (range.Min == 0 && range.Max == 0)
                        continue;

                    if (value <= -1000)
                        continue;

                    switch (stat.Key)
                    {
                        case "physical_damage_+%":
                        case "local_physical_damage_+%":
                            physicalMultiplier += value / 100.0;
                            break;

                        case "local_attack_speed_+%":
                            attacksPerSecond *= (100.0 + value) / 100.0;
                            break;

                        case "local_minimum_added_physical_damage":
                            physicalLow += value;
                            break;

                        case "local_maximum_added_physical_damage":
                            physicalHigh += value;
                            break;

                        case "local_minimum_added_fire_damage":
                        case "local_maximum_added_fire_damage":
                        case "unique_local_minimum_added_fire_damage_when_in_main_hand":
                        case "unique_local_maximum_added_fire_damage_when_in_main_hand":
                            elemental[(int)DamageType.Fire] += value;
                            break;

                        case "local_minimum_added_cold_damage":
                        case "local_maximum_added_cold_damage":
                        case "unique_local_minimum_added_cold_damage_when_in_off_hand":
                        case "unique_local_maximum_added_cold_damage_when_in_off_hand":
                            elemental[(int)DamageType.Cold] += value;
                            break;

                        case "local_minimum_added_lightning_damage":
                        case "local_maximum_added_lightning_damage":
                            elemental[(int)DamageType.Lightning] += value;
                            break;

                        case "unique_local_minimum_added_chaos_damage_when_in_off_hand":
                        case "unique_local_maximum_added_chaos_damage_when_in_off_hand":
                        case "local_minimum_added_chaos_damage":
                        case "local_maximum_added_chaos_damage":
                            elemental[(int)DamageType.Chaos] += value;
                            break;
                    }
                }
            }

            var quality = item.GetComponent<Quality>();
            if (quality == null)
                return (0, 0, 0);

            var qualityMultiplier = 1.0 + quality.ItemQuality / 100.0;

            physicalLow = Math.Round(physicalLow * physicalMultiplier *
                                     qualityMultiplier);
            physicalHigh = Math.Round(physicalHigh * physicalMultiplier *
                                      qualityMultiplier);

            attacksPerSecond = Math.Round(attacksPerSecond, 2);

            var physicalDps = ((physicalLow + physicalHigh) / 2.0) * attacksPerSecond;

            double elementalDps = 0;
            for (var i = (int)DamageType.Fire; i <= (int)DamageType.Chaos && i < elemental.Length; i++)
                elementalDps += (elemental[i] / 2.0) * attacksPerSecond;

            return (physicalDps, elementalDps, physicalDps + elementalDps);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static double ParseDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        value = value.Trim().Replace(",", "");

        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
    }


    private List<string> GetCurrentTooltipLines(Entity item)
    {
        var result = new List<string>();

        try
        {
            var icon = _hoverItemIcon;
            var tooltip = icon?.ItemFrame;

            if (tooltip == null || !tooltip.IsValid)
                return result;

            if (icon.Item == null || item == null || icon.Item.Address != item.Address)
                return result;

            // Avoid version-specific ExileCore helper methods here. Walk the
            // tooltip's Children collection through the same reflection helpers
            // already used elsewhere in this plugin.
            CollectTooltipText(tooltip, result, 0, new HashSet<object>());
        }
        catch
        {
        }

        return result;
    }

    private void CollectTooltipText(object obj, List<string> result, int depth, HashSet<object> visited)
    {
        if (obj == null || depth > 8 || result == null)
            return;

        if (!visited.Add(obj))
            return;

        var text = GetMemberString(obj, "Text");
        if (!string.IsNullOrWhiteSpace(text) && !result.Contains(text.Trim()))
            result.Add(text.Trim());

        var children = GetMemberObject(obj, "Children");
        if (children is System.Collections.IEnumerable enumerable && !(children is string))
        {
            foreach (var child in enumerable)
                CollectTooltipText(child, result, depth + 1, visited);
        }
    }


    private static int FindFirstNumber(IEnumerable<string> lines, params string[] patterns)
    {
        if (lines == null)
            return -1;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    line,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!match.Success)
                    continue;

                var raw = match.Groups[1].Value.Replace(",", "");
                if (int.TryParse(raw, out var value))
                    return value;
            }
        }

        return -1;
    }

    private static bool TryGetDamageRange(string line, out double min, out double max)
    {
        min = 0;
        max = 0;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        var match = System.Text.RegularExpressions.Regex.Match(
            line,
            @"(?:Damage|damage)\s*:\s*([\d,]+)\s*-\s*([\d,]+)");

        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups[1].Value.Replace(",", ""),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out min))
            return false;

        if (!double.TryParse(match.Groups[2].Value.Replace(",", ""),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out max))
            return false;

        return true;
    }

    private static double GetWeaponDps(IEnumerable<string> lines)
    {
        if (lines == null)
            return 0;

        double totalAverageHit = 0;
        double attacksPerSecond = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var aps = System.Text.RegularExpressions.Regex.Match(
                line,
                @"Attacks?\s+per\s+Second:\s*([0-9]+(?:\.[0-9]+)?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (aps.Success)
            {
                double.TryParse(
                    aps.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out attacksPerSecond);
                continue;
            }

            if (TryGetDamageRange(line, out var min, out var max))
                totalAverageHit += (min + max) / 2.0;
        }

        if (attacksPerSecond <= 0 || totalAverageHit <= 0)
            return 0;

        return totalAverageHit * attacksPerSecond;
    }

    private static bool IsWeaponPath(string path)
    {
        return !string.IsNullOrEmpty(path) &&
               path.IndexOf("/Weapons/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private List<string> BuildDebugRows(Entity item, Mods mods)
    {
        var rows = new List<string>
        {
            "ITEM INFO DEBUG",
            $"Item type: {item.GetType().FullName}",
            $"Mods type: {mods.GetType().FullName}",
            $"Path: {item.Path ?? "<null>"}",
            $"Rarity: {mods.ItemRarity}",
            $"ItemMods count: {(mods.ItemMods == null ? 0 : mods.ItemMods.Count)}",
            string.Empty,
            "ITEM SELECTED MEMBERS"
        };

        AddSelectedMembers(rows, item, new[]
        {
            "RenderName", "Name", "DisplayName", "Text", "ItemLevel", "ItemLvl", "Level",
            "Path", "Metadata", "Address", "EntityId", "Id", "Type", "ClientRect"
        });

        rows.Add($"Item available members: {GetMemberNames(item, 500)}");
        rows.Add(string.Empty);
        rows.Add("MOD COLLECTION SELECTED MEMBERS");
        AddSelectedMembers(rows, mods, new[]
        {
            "ItemRarity", "ItemMods", "Prefixes", "Suffixes", "ImplicitMods", "ExplicitMods"
        });
        rows.Add($"Mods available members: {GetMemberNames(mods, 500)}");

        if (mods.ItemMods != null)
        {
            var index = 0;
            foreach (var mod in mods.ItemMods)
            {
                if (mod == null)
                    continue;

                rows.Add(string.Empty);
                rows.Add($"MOD {index}: {mod.GetType().FullName}");
                AddSelectedMembers(rows, mod, new[]
                {
                    "RawName", "Name", "DisplayName", "Text", "Tier", "TierName", "TierText",
                    "Values", "Value", "Range", "Stat", "Stats", "Mod", "Id", "Key",
                    "Domain", "Group", "GenerationType", "AffixName"
                });
                rows.Add($"Mod available members: {GetMemberNames(mod, 900)}");
                index++;

                // Four mods is enough to identify the runtime's mod object without
                // making the debug panel impossibly tall.
                if (index >= 4)
                {
                    if (mods.ItemMods.Count > 4)
                        rows.Add($"... {mods.ItemMods.Count - 4} more mods omitted ...");
                    break;
                }
            }
        }

        rows.Add(string.Empty);
        rows.Add("TIP: send this screenshot; DEBUG can be turned off afterward");
        return rows;
    }

    private static string GetMemberNames(object obj, int maxLength)
    {
        if (obj == null)
            return "<null>";

        try
        {
            var type = obj.GetType();
            var names = new List<string>();

            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                names.Add("P:" + prop.Name);

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                names.Add("F:" + field.Name);

            names = names.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return TrimDebug(string.Join(", ", names), maxLength);
        }
        catch (Exception ex)
        {
            return $"<error {ex.GetType().Name}>";
        }
    }

    private static void AddSelectedMembers(List<string> rows, object obj, IEnumerable<string> names)
    {
        if (obj == null)
            return;

        foreach (var name in names)
        {
            var found = false;
            try
            {
                var type = obj.GetType();
                var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    rows.Add($"{name}: {FormatDebugValue(value)}");
                    found = true;
                }

                if (!found)
                {
                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        var value = field.GetValue(obj);
                        rows.Add($"{name}: {FormatDebugValue(value)}");
                        found = true;
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add($"{name}: <error {ex.GetType().Name}>");
            }
        }
    }

    private static string FormatDebugValue(object value)
    {
        if (value == null)
            return "<null>";

        try
        {
            if (value is string text)
                return TrimDebug(text, 180);

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                var parts = new List<string>();
                var count = 0;
                foreach (var entry in enumerable)
                {
                    parts.Add(entry?.ToString() ?? "<null>");
                    count++;
                    if (count >= 8)
                    {
                        parts.Add("...");
                        break;
                    }
                }
                return TrimDebug("[" + string.Join(", ", parts) + "]", 180);
            }

            return TrimDebug(value.ToString() ?? "<null>", 180);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string TrimDebug(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text.Substring(0, max - 3) + "...";
    }

    private bool TryResolveNativeTooltipAnchor(Entity item,
        SharpDX.RectangleF fallback, out SharpDX.RectangleF anchor)
    {
        anchor = fallback;
        if (item == null || item.Address == 0)
            return false;

        var itemAddress = unchecked((long)item.Address);

        try
        {
            var hover = GetLiveHoverItemIcon();
            var hoverItem = hover?.Item;
            if (hoverItem != null && item != null &&
                hoverItem.Address == item.Address &&
                TryGetHoveredTooltipRect(hover, out var frameRect))
            {
                if (frameRect.Width > 40f && frameRect.Height > 40f)
                {
                    anchor = frameRect;
                    _lastNativeTooltipAnchor = frameRect;
                    _lastNativeTooltipItemAddress = itemAddress;
                    return true;
                }
            }
        }
        catch
        {
        }

        // Non-inventory callers already pass the native ItemFrame rectangle.
        // Inventory cells and the equipped mouse fallback are much smaller.
        if (fallback.Width >= 220f && fallback.Height >= 120f)
        {
            _lastNativeTooltipAnchor = fallback;
            _lastNativeTooltipItemAddress = itemAddress;
            return true;
        }

        // Pressing Alt can rebuild the native tooltip for a frame and make
        // UIHover temporarily report only the source cell. Reuse the last
        // verified frame for this exact item so the full analyzer remains
        // attached instead of falling back to the retired floating layout.
        if (_lastNativeTooltipItemAddress == itemAddress &&
            _lastNativeTooltipAnchor.Width >= 220f &&
            _lastNativeTooltipAnchor.Height >= 120f)
        {
            anchor = _lastNativeTooltipAnchor;
            return true;
        }

        return false;
    }

    private static Vector2 GetAnalyzerPanelPosition(
        SharpDX.RectangleF anchor, float width, float height, bool attachBelow)
    {
        var screen = ImGuiNET.ImGui.GetIO().DisplaySize;
        const float margin = 4f;

        if (attachBelow)
        {
            var attachedX = Math.Clamp(anchor.Left, margin,
                Math.Max(margin, screen.X - width - margin));
            var belowY = anchor.Bottom;
            if (belowY + height <= screen.Y - margin)
                return new Vector2(attachedX, Math.Max(margin, belowY));

            var aboveY = anchor.Top - height;
            if (aboveY >= margin)
                return new Vector2(attachedX, aboveY);
        }

        var x = anchor.Right + 12f;
        var y = anchor.Top;
        if (x + width > screen.X - margin)
            x = anchor.Left - width - 12f;
        x = Math.Clamp(x, margin, Math.Max(margin, screen.X - width - margin));

        if (y + height > screen.Y - margin)
            y = Math.Max(margin, screen.Y - height - margin);

        return new Vector2(x, y);
    }

    private void DrawItemInfoOverlay(Entity item, SharpDX.RectangleF itemRect)
    {
        var mods = item.GetComponent<Mods>();
        if (mods == null)
            return;

        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var rows = Settings.ItemInfoDebugMode.Value
            ? BuildDebugRows(item, mods)
            : BuildNormalInfoRows(item, mods);

        var compactAnalyzer = IsCompactAnalyzerModeActive() &&
                              !Settings.ItemInfoDebugMode.Value;
        // LootLens is now attached-only. If the game has not exposed a native
        // tooltip frame yet, wait for it rather than drawing a detached panel.
        if (!TryResolveNativeTooltipAnchor(item, itemRect,
                out var overlayAnchor))
            return;

        const bool attachedToTooltip = true;

        // Compact mode can legitimately return no rows when the item has a
        // rating but no qualifying-stat details to display. Do not create an
        // empty background panel in that case.
        if (compactAnalyzer && rows.Count == 0)
            return;

        if (compactAnalyzer)
        {
            DrawMinimalCompactOverlay(overlayAnchor, rows,
                GetCachedAnalysis(item, mods).Rating, attachedToTooltip);
            return;
        }

        if (!Settings.ItemInfoDebugMode.Value)
        {
            DrawLootLens2StyleFullOverlay(overlayAnchor, item, mods, rows);
            return;
        }

        var font = ImGuiNET.ImGui.GetFont();
        var fontSize = ImGuiNET.ImGui.GetFontSize();
        var glyphHeight = Math.Max(1f, ImGuiNET.ImGui.CalcTextSize("Ag").Y);

        const float rowGap = 9f;
        const float blankGap = 10f;
        var rowStep = glyphHeight + rowGap;
        var padding = IsCompactAnalyzerModeActive() ? 9f : 10f;

        // Sanitize every displayed row to one physical line.
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.IsNullOrEmpty(rows[i]))
                rows[i] = rows[i].Replace("\r", " ").Replace("\n", " ");
        }

        // Width is based on the longest actual row, with a sensible cap.
        var maxWidth = 0f;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row))
                continue;

            var measure = row;
            if (measure.StartsWith("DETAIL|", StringComparison.Ordinal))
                measure = measure.Substring("DETAIL|".Length);
            else if (measure.StartsWith("SPECIAL|", StringComparison.Ordinal))
                measure = measure.Substring("SPECIAL|".Length);
            else if (measure.StartsWith("SPECIAL MODS|", StringComparison.Ordinal))
                measure = "SPECIAL MODS";
            else if (measure.StartsWith("SUMMARY|", StringComparison.Ordinal))
                measure = measure.Substring("SUMMARY|".Length).Replace("|", "  |  ");
            else if (measure.StartsWith("DPSROW|", StringComparison.Ordinal))
                measure = measure.Substring("DPSROW|".Length).Replace("|", "  |  ");

            maxWidth = Math.Max(maxWidth, ImGuiNET.ImGui.CalcTextSize(measure).X);
        }

        var width = IsCompactAnalyzerModeActive()
            ? Math.Max(235f, (int)Math.Ceiling(maxWidth + padding * 2f + 12f))
            : Math.Max(Settings.ItemInfoWidth.Value,
                (int)Math.Ceiling(maxWidth + padding * 2f + 12f));
        width = Math.Min(width, 760);
        if (attachedToTooltip)
            width = Math.Max(width, overlayAnchor.Width);

        var height = padding * 2f;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row))
                height += blankGap;
            else
                height += rowStep;
        }

        var panelPosition = GetAnalyzerPanelPosition(overlayAnchor, width, height,
            attachedToTooltip);
        var x = panelPosition.X;
        var y = panelPosition.Y;

        var bg = ToImGuiColor(GetColor(Settings.ItemInfoBackground,
            new Color(0, 0, 0, 236)));
        var border = ToImGuiColor(GetColor(Settings.ItemInfoBorder,
            new Color(0, 0, 0, 0)));
        var tier = ToImGuiColor(GetColor(Settings.ItemInfoTierColor, Color.Gold));

        draw.AddRectFilled(new Vector2(x, y), new Vector2(x + width, y + height), bg, 5f);
        draw.AddRect(new Vector2(x, y), new Vector2(x + width, y + height),
            border, 5f, ImGuiNET.ImDrawFlags.None, 1.5f);

        // Hard clip to the panel. A long modifier can never draw over the next
        // row or outside the overlay.
        draw.PushClipRect(new Vector2(x + 1f, y + 1f),
            new Vector2(x + width - 1f, y + height - 1f), true);

        var cy = y + padding;

        var isFirstRow = true;
        var currentRating = GetCachedAnalysis(item, mods).Rating;

        foreach (var raw in rows)
        {
            if (string.IsNullOrEmpty(raw))
            {
                cy += blankGap;
                isFirstRow = false;
                continue;
            }

            var text = raw;

            if (text.StartsWith("S ", StringComparison.Ordinal) && text.Contains(" | P "))
            {
                var parts = text.Split(new[] { " | " }, StringSplitOptions.None);
                var sText = parts[0];
                var pText = parts.Length > 1 ? parts[1] : "";

                var sColor = ToImGuiColor(new Color(190,75,230,255));
                var pColor = ToImGuiColor(new Color(70,150,255,255));
                var gray = ToImGuiColor(new Color(150,150,160,255));

                draw.AddText(font, fontSize, new Vector2(x + padding, cy), sColor, sText);
                var sx = x + padding + ImGuiNET.ImGui.CalcTextSize(sText).X + 8f;
                draw.AddText(font, fontSize, new Vector2(sx, cy), gray, "|");
                sx += ImGuiNET.ImGui.CalcTextSize("|").X + 8f;
                draw.AddText(font, fontSize, new Vector2(sx, cy), pColor, pText);

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("HEADER|", StringComparison.Ordinal))
            {
                var parts = text.Substring("HEADER|".Length).Split('|');
                var name = parts.Length > 0 ? parts[0] : "Item";
                var rarity = parts.Length > 1 ? parts[1] : "";
                var rating = 0;
                if (parts.Length > 2)
                    int.TryParse(parts[2], out rating);
                var specialStars = 0;
                if (parts.Length > 3)
                    int.TryParse(parts[3], out specialStars);

                var titleColor = ToImGuiColor(new Color(225, 225, 230, 255));
                var rarityColor = ToImGuiColor(new Color(205, 205, 212, 255));

                var sx = x + padding;

                draw.AddText(font, fontSize * 1.02f,
                    new Vector2(sx, cy), titleColor, name);
                sx += ImGuiNET.ImGui.CalcTextSize(name).X + 10f;

                draw.AddText(font, fontSize * 1.02f,
                    new Vector2(sx, cy), rarityColor, "|");
                sx += ImGuiNET.ImGui.CalcTextSize("|").X + 10f;

                draw.AddText(font, fontSize * 1.02f,
                    new Vector2(sx, cy), rarityColor, rarity);
                sx += ImGuiNET.ImGui.CalcTextSize(rarity).X + 14f;

                // Always show all three rating stars in the header. Filled
                // stars are gold; empty stars are subtle hollow outlines.
                for (var star = 0; star < 3; star++)
                {
                    var center = new Vector2(
                        sx + star * 16f + 5f,
                        cy + glyphHeight * .5f);

                    if (star < Math.Min(3, Math.Max(0, rating)))
                    {
                        DrawFivePointStar(draw, center, 4.8f,
                            ToImGuiColor(new Color(245, 195, 60, 255)));
                    }
                    else
                    {
                        DrawFivePointStarOutline(draw, center, 4.8f,
                            ToImGuiColor(new Color(90, 90, 100, 255)));
                    }
                }

                sx += 3 * 16f + 4f;
                for (var star = 0; star < Math.Max(0, specialStars); star++)
                {
                    var center = new Vector2(
                        sx + star * 16f + 5f,
                        cy + glyphHeight * .5f);
                    DrawFivePointStar(draw, center, 4.8f,
                        ToImGuiColor(Settings.SpecialStarColor.Value));
                }

                cy += rowStep * .95f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("META|", StringComparison.Ordinal))
            {
                var meta = text.Substring("META|".Length);
                draw.AddText(font, fontSize * .78f,
                    new Vector2(x + padding, cy),
                    ToImGuiColor(new Color(145, 145, 155, 255)), meta);

                cy += rowStep * .88f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DEFENSES|", StringComparison.Ordinal))
            {
                var defense = text.Substring("DEFENSES|".Length);
                draw.AddText(font, fontSize * .84f,
                    new Vector2(x + padding, cy),
                    ToImGuiColor(new Color(195, 200, 210, 255)), defense);

                cy += rowStep * .9f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("SUMMARY|", StringComparison.Ordinal))
            {
                var parts = text.Substring("SUMMARY|".Length).Split('|');
                var sx = x + padding;

                foreach (var part in parts)
                {
                    var c = ToImGuiColor(new Color(190,195,205,255));
                    if (part.StartsWith("Quality", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(220,205,120,255));
                    else if (part.StartsWith("ARM", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(180,195,205,255));
                    else if (part.StartsWith("EVA", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(70,210,105,255));
                    else if (part.StartsWith("ES", StringComparison.Ordinal))
                        c = ToImGuiColor(new Color(70,220,240,255));

                    draw.AddText(font, fontSize * .88f, new Vector2(sx, cy), c, part);
                    sx += ImGuiNET.ImGui.CalcTextSize(part).X + 18f;
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("LOOT RATING|", StringComparison.Ordinal))
            {
                var ratingText = text.Substring("LOOT RATING|".Length);
                int.TryParse(ratingText, out var rating);

                var headingColor = ToImGuiColor(new Color(245, 205, 70, 255));
                draw.AddText(font, fontSize, new Vector2(x + padding, cy),
                    headingColor, "LOOT RATING");

                var headingWidth = ImGuiNET.ImGui.CalcTextSize("LOOT RATING").X;
                var sx = x + padding + headingWidth + 16f;
                var starColor = rating >= 3
                    ? ToImGuiColor(new Color(255, 215, 60, 255))
                    : rating == 2
                        ? ToImGuiColor(new Color(100, 215, 125, 255))
                        : ToImGuiColor(new Color(90, 170, 240, 255));

                for (var star = 0; star < 3; star++)
                {
                    var filled = star < Math.Min(3, Math.Max(0, rating));
                    var center = new Vector2(sx + star * 17f, cy + glyphHeight * .5f);
                    if (filled)
                        DrawFivePointStar(draw, center, 6.5f, starColor);
                    else
                        draw.AddCircle(center, 4.2f,
                            ToImGuiColor(new Color(75, 75, 88, 255)), 10, 1.2f);
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("SPECIAL MODS|", StringComparison.Ordinal))
            {
                var countText = text.Substring("SPECIAL MODS|".Length);
                int.TryParse(countText, out var count);
                var redColor = ToImGuiColor(Settings.SpecialStarColor.Value);

                draw.AddText(font, fontSize, new Vector2(x + padding, cy),
                    redColor, "SPECIAL MODS");
                var headingWidth = ImGuiNET.ImGui.CalcTextSize("SPECIAL MODS").X;
                var sx = x + padding + headingWidth + 16f;

                for (var star = 0; star < Math.Max(0, count); star++)
                {
                    var center = new Vector2(sx + star * 17f, cy + glyphHeight * .5f);
                    DrawFivePointStar(draw, center, 6.5f, redColor);
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DEFENSES|", StringComparison.Ordinal))
            {
                var parts = text.Substring("DEFENSES|".Length).Split('|');
                var sx = x + padding;

                foreach (var part in parts)
                {
                    var isArm = part.StartsWith("ARM ", StringComparison.Ordinal);
                    var isEva = part.StartsWith("EVA ", StringComparison.Ordinal);
                    var isEs = part.StartsWith("ES ", StringComparison.Ordinal);

                    var valueText = isArm
                        ? part.Substring(4).Trim()
                        : isEva
                            ? part.Substring(4).Trim()
                            : part.Substring(3).Trim();

                    var c = isArm
                        ? ToImGuiColor(new Color(190, 200, 210, 255))
                        : isEva
                            ? ToImGuiColor(new Color(90, 205, 115, 255))
                            : ToImGuiColor(new Color(90, 215, 235, 255));

                    var center = new Vector2(sx + 7f, cy + glyphHeight * 0.5f);

                    if (isArm)
                    {
                        // Shield/armor crest.
                        var pts = new Vector2[]
                        {
                            new Vector2(center.X, center.Y - 7f),
                            new Vector2(center.X + 6f, center.Y - 4f),
                            new Vector2(center.X + 5f, center.Y + 3f),
                            new Vector2(center.X, center.Y + 7f),
                            new Vector2(center.X - 5f, center.Y + 3f),
                            new Vector2(center.X - 6f, center.Y - 4f)
                        };
                        draw.AddPolyline(ref pts[0], pts.Length, c, ImGuiNET.ImDrawFlags.Closed, 1.7f);
                    }
                    else if (isEva)
                    {
                        // Small green leaf/wing shape.
                        draw.AddLine(new Vector2(center.X - 6f, center.Y + 4f),
                                     new Vector2(center.X + 5f, center.Y - 5f), c, 1.8f);
                        draw.AddLine(new Vector2(center.X - 1f, center.Y + 4f),
                                     new Vector2(center.X + 5f, center.Y - 5f), c, 1.4f);
                    }
                    else if (isEs)
                    {
                        // Cyan energy diamond.
                        var pts = new Vector2[]
                        {
                            new Vector2(center.X, center.Y - 7f),
                            new Vector2(center.X + 6f, center.Y),
                            new Vector2(center.X, center.Y + 7f),
                            new Vector2(center.X - 6f, center.Y)
                        };
                        draw.AddPolyline(ref pts[0], pts.Length, c, ImGuiNET.ImDrawFlags.Closed, 1.7f);
                    }

                    draw.AddText(font, fontSize, new Vector2(sx + 17f, cy), c, valueText);
                    sx += ImGuiNET.ImGui.CalcTextSize(valueText).X + 44f;
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DPSROW|", StringComparison.Ordinal))
            {
                var parts = text.Substring("DPSROW|".Length).Split('|');
                var sx = x + padding;

                foreach (var part in parts)
                {
                    var isPdps = part.StartsWith("PDPS", StringComparison.Ordinal);
                    var isEdps = part.StartsWith("EDPS", StringComparison.Ordinal);
                    var numberText = isPdps
                        ? part.Substring(4).Trim()
                        : isEdps
                            ? part.Substring(4).Trim()
                            : part.Substring(3).Trim();

                    var c = ToImGuiColor(new Color(225, 225, 230, 255));
                    var center = new Vector2(sx + 7f, cy + glyphHeight * 0.5f);

                    if (isPdps)
                    {
                        // Crossed swords / physical damage.
                        draw.AddLine(new Vector2(center.X - 5f, center.Y + 5f),
                                     new Vector2(center.X + 5f, center.Y - 5f), c, 2.0f);
                        draw.AddLine(new Vector2(center.X - 5f, center.Y - 5f),
                                     new Vector2(center.X + 5f, center.Y + 5f), c, 1.4f);
                    }
                    else if (isEdps)
                    {
                        // Elemental spark.
                        draw.AddCircleFilled(center, 4.5f, c);
                        draw.AddLine(new Vector2(center.X, center.Y - 7f),
                                     new Vector2(center.X, center.Y + 7f), c, 1.4f);
                        draw.AddLine(new Vector2(center.X - 6f, center.Y),
                                     new Vector2(center.X + 6f, center.Y), c, 1.4f);
                    }
                    else
                    {
                        // Total DPS: compact target/burst.
                        draw.AddCircle(center, 5.5f, c, 10, 1.5f);
                        draw.AddCircleFilled(center, 2.2f, c);
                    }

                    draw.AddText(font, fontSize, new Vector2(sx + 17f, cy), c, numberText);
                    sx += ImGuiNET.ImGui.CalcTextSize(numberText).X + 44f;
                }

                cy += rowStep;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("DETAIL|", StringComparison.Ordinal))
            {
                var detail = text.Substring("DETAIL|".Length).Trim();

                if (string.Equals(detail, "QUALIFYING STATS", StringComparison.Ordinal))
                {
                    var c = ToImGuiColor(new Color(185, 195, 205, 255));
                    draw.AddText(font, fontSize * .90f,
                        new Vector2(x + padding, cy), c, "QUALIFYING STATS");
                    cy += rowStep;
                    isFirstRow = false;
                    continue;
                }

                var cDetail = ToImGuiColor(new Color(205, 205, 212, 255));
                var iconCenter = new Vector2(
                    x + padding + 7f,
                    cy + glyphHeight * .5f);

                draw.AddCircleFilled(iconCenter, 3.2f,
                    ToImGuiColor(new Color(125, 125, 135, 255)));

                draw.AddText(font, fontSize * .94f,
                    new Vector2(x + padding + 18f, cy),
                    cDetail,
                    detail);

                cy += rowStep * 1.05f;
                isFirstRow = false;
                continue;
            }

            if (text.StartsWith("SPECIAL|", StringComparison.Ordinal))
            {
                var special = text.Substring("SPECIAL|".Length).Trim();
                var redColor = ToImGuiColor(Settings.SpecialStarColor.Value);
                var iconCenter = new Vector2(x + padding + 7f, cy + glyphHeight * .5f);
                DrawFivePointStar(draw, iconCenter, 5f, redColor);
                draw.AddText(font, fontSize * .94f,
                    new Vector2(x + padding + 18f, cy), redColor, special);

                cy += rowStep * 1.05f;
                isFirstRow = false;
                continue;
            }

            // Keep the existing modifier text, but pull [Tn] into a
            // right-aligned tag without changing the underlying data.
            if (text != "MODIFIERS" && text.Contains("[T") && text.EndsWith("]"))
            {
                var tagStart = text.LastIndexOf(" [T", StringComparison.Ordinal);
                if (tagStart > 0)
                {
                    var modText = text.Substring(0, tagStart);
                    var tag = text.Substring(tagStart + 1);

                    // T1 is special: highlight the entire modifier and its
                    // tier tag in gold. Every other tier remains neutral.
                    var isTierOne = string.Equals(tag, "[T1]", StringComparison.OrdinalIgnoreCase);
                    var modColor = isTierOne
                        ? ToImGuiColor(new Color(245, 205, 70, 255))
                        : ToImGuiColor(new Color(225, 225, 230, 255));

                    draw.AddText(font, fontSize, new Vector2(x + padding, cy), modColor, modText);

                    var tagColor = GetTierTagColor(tag);
                    var tagWidth = ImGuiNET.ImGui.CalcTextSize(tag).X;
                    draw.AddText(font, fontSize, new Vector2(x + width - padding - tagWidth, cy), tagColor, tag);

                    cy += rowStep;
                    isFirstRow = false;
                    continue;
                }
            }

            var summaryColor = ToImGuiColor(new Color(225, 225, 230, 255));

            var color = text == "MODIFIERS" || text.StartsWith("LOOT RATING:", StringComparison.Ordinal)
                ? tier
                : summaryColor;

            // Normal modifier lines are neutral; T1 modifiers are gold. Loot Rating
            // remains the primary star-based evaluation element.
            if (text != "MODIFIERS" &&
                !text.StartsWith("LOOT RATING:", StringComparison.Ordinal) &&
                !text.StartsWith("DEFENSES|", StringComparison.Ordinal) &&
                !text.StartsWith("WEAPON DPS:", StringComparison.Ordinal))
            {
                color = ToImGuiColor(new Color(225, 225, 230, 255));
            }

            draw.AddText(font, fontSize, new Vector2(x + padding, cy), color, text);
            cy += rowStep;
            isFirstRow = false;
        }
        draw.PopClipRect();
    }

    private void DrawPolishedFullOverlay(
        SharpDX.RectangleF itemRect, List<string> rows, bool attachBelow)
    {
        var itemName = "ITEM";
        var rarity = string.Empty;
        var rating = 0;
        var uniquePerfection = -1;
        var uniqueRollCount = 0;
        var uniqueModCount = 0;
        var metaParts = new List<string>();
        var modRows = new List<(string Affix, string Tier, string Text)>();
        var details = new List<string>();
        var specialMods = new List<string>();
        var beforeModifiers = true;

        foreach (var raw in rows)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (raw.StartsWith("HEADER|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("HEADER|".Length).Split('|');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                    itemName = parts[0].Trim();
                if (parts.Length > 1)
                    rarity = parts[1].Trim();
                if (parts.Length > 2)
                    int.TryParse(parts[2], out rating);
                continue;
            }

            if (raw == "MODIFIERS")
            {
                beforeModifiers = false;
                continue;
            }

            if (raw.StartsWith("META|", StringComparison.Ordinal))
            {
                var meta = raw.Substring("META|".Length)
                    .Replace("  |  ", " | ", StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(meta))
                    metaParts.Add(meta);
                continue;
            }

            if (raw.StartsWith("DEFENSES|", StringComparison.Ordinal))
            {
                metaParts.Add(raw.Substring("DEFENSES|".Length)
                    .Replace("  •  ", " | ", StringComparison.Ordinal));
                continue;
            }

            if (raw.StartsWith("DPSROW|", StringComparison.Ordinal))
            {
                metaParts.Add(raw.Substring("DPSROW|".Length)
                    .Replace("|", " | ", StringComparison.Ordinal));
                continue;
            }

            if (raw.StartsWith("MODROW|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("MODROW|".Length).Split('|');
                var affix = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                var tier = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                var modText = parts.Length > 2
                    ? string.Join("/", parts.Skip(2)).Trim()
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(modText))
                    modRows.Add((affix, tier, modText));
                continue;
            }

            if (raw.StartsWith("LOOT RATING|", StringComparison.Ordinal))
            {
                int.TryParse(raw.Substring("LOOT RATING|".Length), out rating);
                continue;
            }

            if (raw.StartsWith("UNIQUE PERFECTION|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("UNIQUE PERFECTION|".Length).Split('|');
                if (parts.Length > 0)
                    int.TryParse(parts[0], out uniquePerfection);
                if (parts.Length > 1)
                    int.TryParse(parts[1], out uniqueRollCount);
                if (parts.Length > 2)
                    int.TryParse(parts[2], out uniqueModCount);
                continue;
            }

            if (raw.StartsWith("DETAIL|", StringComparison.Ordinal))
            {
                var detail = raw.Substring("DETAIL|".Length).Trim();
                if (!string.Equals(detail, "QUALIFYING STATS", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(detail))
                    details.Add(detail);
                continue;
            }

            if (raw.StartsWith("SPECIAL|", StringComparison.Ordinal))
            {
                var special = raw.Substring("SPECIAL|".Length).Trim();
                if (!string.IsNullOrWhiteSpace(special))
                    specialMods.Add(special);
                continue;
            }

            if (raw.StartsWith("SPECIAL MODS|", StringComparison.Ordinal))
                continue;

            if (beforeModifiers)
                metaParts.Add(raw.Trim());
        }

        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        var fontSize = ImGuiNET.ImGui.GetFontSize();

        var longest = ImGuiNET.ImGui.CalcTextSize(itemName.ToUpperInvariant()).X;
        foreach (var mod in modRows)
            longest = Math.Max(longest, ImGuiNET.ImGui.CalcTextSize(mod.Text).X);
        foreach (var detail in details)
        {
            if (TryParseCompactDetail(detail, out var label, out _, out _, out _))
                longest = Math.Max(longest,
                    ImGuiNET.ImGui.CalcTextSize(label.ToUpperInvariant()).X + 110f);
        }

        var width = Math.Max(430f,
            Math.Max(Settings.ItemInfoWidth.Value, longest + 145f));
        width = Math.Min(width, 650f);
        if (attachBelow)
            width = Math.Max(width, itemRect.Width);

        const float headerHeight = 66f;
        const float metaHeight = 28f;
        const float sectionHeight = 25f;
        const float modRowHeight = 29f;
        const float ratingHeaderHeight = 38f;
        const float perfectionHeight = 72f;
        const float detailRowHeight = 28f;
        const float specialRowHeight = 25f;
        const float bottomPadding = 10f;

        var hasMeta = metaParts.Count > 0;
        var height = headerHeight + (hasMeta ? metaHeight : 0f) +
                     sectionHeight + modRows.Count * modRowHeight + bottomPadding;
        if (uniquePerfection >= 0)
            height += perfectionHeight;
        if (details.Count > 0)
            height += ratingHeaderHeight + sectionHeight + details.Count * detailRowHeight;
        if (specialMods.Count > 0)
            height += sectionHeight + specialMods.Count * specialRowHeight;

        var panelPosition = GetAnalyzerPanelPosition(itemRect, width, height,
            attachBelow);
        var x = panelPosition.X;
        var y = panelPosition.Y;

        var topLeft = new Vector2(x, y);
        var bottomRight = new Vector2(x + width, y + height);
        var shadow = ToImGuiColor(new Color(0, 0, 0, 180));
        var outer = ToImGuiColor(new Color(7, 8, 11, 249));
        var inner = ToImGuiColor(new Color(14, 15, 19, 247));
        var steel = ToImGuiColor(new Color(72, 75, 84, 255));
        var mutedSteel = ToImGuiColor(new Color(42, 44, 51, 255));
        var gold = ToImGuiColor(GetColor(Settings.ItemInfoTierColor,
            new Color(214, 168, 66, 255)));
        var mutedGold = ToImGuiColor(new Color(150, 119, 61, 255));
        var normalText = ToImGuiColor(new Color(224, 222, 216, 255));
        var subdued = ToImGuiColor(new Color(145, 147, 155, 255));

        draw.AddRectFilled(topLeft + new Vector2(4f, 5f),
            bottomRight + new Vector2(4f, 5f), shadow, 4f);
        draw.AddRectFilled(topLeft, bottomRight, outer, 4f);
        draw.AddRectFilled(topLeft + new Vector2(3f, 3f),
            bottomRight - new Vector2(3f, 3f), inner, 3f);
        draw.AddRect(topLeft, bottomRight, mutedGold, 4f,
            ImGuiNET.ImDrawFlags.None, 1.5f);
        draw.AddRect(topLeft + new Vector2(3f, 3f),
            bottomRight - new Vector2(3f, 3f), steel, 3f,
            ImGuiNET.ImDrawFlags.None, 1f);
        draw.AddLine(new Vector2(x + 10f, y + 3f),
            new Vector2(x + width - 10f, y + 3f), gold, 1.5f);

        DrawCompactPanelCorner(draw, new Vector2(x + 6f, y + 6f), mutedGold);
        DrawCompactPanelCorner(draw, new Vector2(x + width - 6f, y + 6f), mutedGold);
        DrawCompactPanelCorner(draw, new Vector2(x + 6f, y + height - 6f), mutedGold);
        DrawCompactPanelCorner(draw, new Vector2(x + width - 6f, y + height - 6f), mutedGold);

        draw.PushClipRect(topLeft + new Vector2(2f, 2f),
            bottomRight - new Vector2(2f, 2f), true);

        draw.AddText(font, fontSize * .72f, new Vector2(x + 14f, y + 8f),
            mutedGold, "LOOTLENS  |  FULL ANALYSIS");

        var titleY = y + 28f;
        draw.AddText(font, fontSize * 1.05f, new Vector2(x + 14f, titleY),
            normalText, itemName.ToUpperInvariant());

        var rarityColor = GetFullRarityColor(rarity);
        var rarityWidth = ImGuiNET.ImGui.CalcTextSize(rarity.ToUpperInvariant()).X * .82f;
        var rightContentX = x + width - 14f;

        if (uniquePerfection >= 0)
        {
            var scoreText = $"{uniquePerfection}%";
            var scoreWidth = ImGuiNET.ImGui.CalcTextSize(scoreText).X * 1.05f;
            var scoreX = x + width - 15f - scoreWidth;
            draw.AddText(font, fontSize * 1.05f,
                new Vector2(scoreX, titleY), GetPerfectionColor(uniquePerfection),
                scoreText);
            rightContentX = scoreX - 15f;
        }
        else if (!string.Equals(rarity, "Unique", StringComparison.OrdinalIgnoreCase))
        {
            var starStart = x + width - 70f;
            for (var star = 0; star < 3; star++)
            {
                var center = new Vector2(starStart + star * 19f,
                    titleY + fontSize * .5f);
                if (star < Math.Min(3, Math.Max(0, rating)))
                    DrawFivePointStar(draw, center, 6.5f, gold);
                else
                    DrawFivePointStarOutline(draw, center, 6.5f,
                        ToImGuiColor(new Color(84, 84, 90, 255)));
            }
            rightContentX = starStart - 16f;
        }

        draw.AddText(font, fontSize * .82f,
            new Vector2(rightContentX - rarityWidth, titleY + 2f),
            rarityColor, rarity.ToUpperInvariant());

        var cy = y + headerHeight;
        draw.AddLine(new Vector2(x + 8f, cy - 4f),
            new Vector2(x + width - 8f, cy - 4f), mutedGold, 1f);

        if (hasMeta)
        {
            var meta = string.Join(" | ", metaParts);
            draw.AddRectFilled(new Vector2(x + 8f, cy),
                new Vector2(x + width - 8f, cy + metaHeight - 3f),
                ToImGuiColor(new Color(18, 19, 24, 225)), 2f);
            draw.AddText(font, fontSize * .78f,
                new Vector2(x + 14f, cy + 5f), subdued, meta);
            cy += metaHeight;
        }

        draw.AddText(font, fontSize * .80f, new Vector2(x + 14f, cy + 5f),
            gold, "MODIFIERS");
        cy += sectionHeight;

        for (var index = 0; index < modRows.Count; index++)
        {
            var mod = modRows[index];
            var rowTop = cy + index * modRowHeight;
            var rowBottom = rowTop + modRowHeight - 2f;
            var rowBg = index % 2 == 0
                ? ToImGuiColor(new Color(20, 21, 26, 232))
                : ToImGuiColor(new Color(16, 17, 21, 222));
            draw.AddRectFilled(new Vector2(x + 8f, rowTop),
                new Vector2(x + width - 8f, rowBottom), rowBg, 2f);
            draw.AddLine(new Vector2(x + 9f, rowBottom),
                new Vector2(x + width - 9f, rowBottom), mutedSteel, 1f);

            var affixColor = mod.Affix == "P"
                ? ToImGuiColor(new Color(75, 145, 245, 255))
                : mod.Affix == "S"
                    ? ToImGuiColor(new Color(185, 90, 225, 255))
                    : mod.Affix == "U"
                        ? ToImGuiColor(new Color(220, 125, 48, 255))
                    : subdued;

            if (!string.IsNullOrEmpty(mod.Affix))
            {
                draw.AddRectFilled(new Vector2(x + 14f, rowTop + 5f),
                    new Vector2(x + 34f, rowTop + 23f),
                    ToImGuiColor(new Color(25, 26, 32, 255)), 2f);
                draw.AddRect(new Vector2(x + 14f, rowTop + 5f),
                    new Vector2(x + 34f, rowTop + 23f), affixColor, 2f,
                    ImGuiNET.ImDrawFlags.None, 1f);
                draw.AddText(font, fontSize * .72f,
                    new Vector2(x + 20f, rowTop + 7f), affixColor, mod.Affix);
            }

            var statColor = GetCompactStatColor(mod.Text);
            DrawCompactStatIcon(draw, new Vector2(x + 47f, rowTop + 13f),
                mod.Text, statColor);

            var tierText = mod.Tier.Trim('[', ']');
            var tierColor = GetFullTierColor(tierText);
            var tierWidth = string.IsNullOrEmpty(tierText) ? 0f : 39f;
            var tierX = x + width - 15f - tierWidth;

            if (!string.IsNullOrEmpty(tierText))
            {
                draw.AddRectFilled(new Vector2(tierX, rowTop + 5f),
                    new Vector2(tierX + tierWidth, rowTop + 23f),
                    ToImGuiColor(new Color(24, 25, 30, 255)), 2f);
                draw.AddRect(new Vector2(tierX, rowTop + 5f),
                    new Vector2(tierX + tierWidth, rowTop + 23f), tierColor, 2f,
                    ImGuiNET.ImDrawFlags.None, 1f);
                var tierTextWidth = ImGuiNET.ImGui.CalcTextSize(tierText).X * .72f;
                draw.AddText(font, fontSize * .72f,
                    new Vector2(tierX + (tierWidth - tierTextWidth) * .5f,
                        rowTop + 7f), tierColor, tierText);
            }

            draw.PushClipRect(new Vector2(x + 60f, rowTop),
                new Vector2(tierX - 6f, rowBottom), true);
            draw.AddText(font, fontSize * .88f,
                new Vector2(x + 61f, rowTop + 5f), statColor, mod.Text);
            draw.PopClipRect();
        }
        cy += modRows.Count * modRowHeight;

        if (uniquePerfection >= 0)
        {
            draw.AddLine(new Vector2(x + 8f, cy + 3f),
                new Vector2(x + width - 8f, cy + 3f), mutedGold, 1f);

            var perfectionColor = GetPerfectionColor(uniquePerfection);
            var perfectionY = cy + 12f;
            draw.AddText(font, fontSize * .86f,
                new Vector2(x + 14f, perfectionY), normalText, "ITEM PERFECTION");

            var percentText = $"{uniquePerfection}%";
            var percentWidth = ImGuiNET.ImGui.CalcTextSize(percentText).X * .96f;
            draw.AddText(font, fontSize * .96f,
                new Vector2(x + width - 15f - percentWidth, perfectionY - 1f),
                perfectionColor, percentText);

            var barLeft = x + 14f;
            var barRight = x + width - 14f;
            var barTop = perfectionY + 24f;
            var barBottom = barTop + 9f;
            draw.AddRectFilled(new Vector2(barLeft, barTop),
                new Vector2(barRight, barBottom),
                ToImGuiColor(new Color(30, 31, 37, 255)), 3f);
            var fillRight = barLeft + (barRight - barLeft) *
                Math.Clamp(uniquePerfection / 100f, 0f, 1f);
            if (fillRight > barLeft)
                draw.AddRectFilled(new Vector2(barLeft, barTop),
                    new Vector2(fillRight, barBottom), perfectionColor, 3f);
            draw.AddRect(new Vector2(barLeft, barTop),
                new Vector2(barRight, barBottom), mutedSteel, 3f,
                ImGuiNET.ImDrawFlags.None, 1f);

            for (var marker = 1; marker < 4; marker++)
            {
                var markerX = barLeft + (barRight - barLeft) * marker / 4f;
                draw.AddLine(new Vector2(markerX, barTop + 2f),
                    new Vector2(markerX, barBottom - 2f),
                    ToImGuiColor(new Color(10, 10, 13, 150)), 1f);
            }

            var rollLabel = uniqueRollCount == 1
                ? "1 VARIABLE ROLL"
                : $"{uniqueRollCount} VARIABLE ROLLS";
            var modLabel = uniqueModCount == 1
                ? "1 MODIFIER"
                : $"{uniqueModCount} MODIFIERS";
            draw.AddText(font, fontSize * .70f,
                new Vector2(x + 14f, barBottom + 6f), subdued,
                $"{rollLabel}  |  {modLabel}  |  FIXED ROLLS EXCLUDED");

            cy += perfectionHeight;
        }

        if (details.Count > 0)
        {
            draw.AddLine(new Vector2(x + 8f, cy + 3f),
                new Vector2(x + width - 8f, cy + 3f), mutedGold, 1f);
            var ratingY = cy + 12f;
            draw.AddText(font, fontSize * .90f, new Vector2(x + 14f, ratingY),
                normalText, "LOOT RATING");
            var bottomStarStart = x + width - 70f;
            for (var star = 0; star < 3; star++)
            {
                var center = new Vector2(bottomStarStart + star * 19f,
                    ratingY + fontSize * .45f);
                if (star < rating)
                    DrawFivePointStar(draw, center, 6.2f, gold);
                else
                    DrawFivePointStarOutline(draw, center, 6.2f,
                        ToImGuiColor(new Color(84, 84, 90, 255)));
            }
            cy += ratingHeaderHeight;

            draw.AddText(font, fontSize * .78f, new Vector2(x + 14f, cy + 5f),
                subdued, "QUALIFYING STATS");
            cy += sectionHeight;

            for (var index = 0; index < details.Count; index++)
            {
                if (!TryParseCompactDetail(details[index], out var label,
                        out var actualValue, out var requiredValue, out var suffix))
                    continue;

                var rowTop = cy + index * detailRowHeight;
                var rowBottom = rowTop + detailRowHeight - 2f;
                var rowBg = index % 2 == 0
                    ? ToImGuiColor(new Color(20, 21, 26, 230))
                    : ToImGuiColor(new Color(16, 17, 21, 220));
                draw.AddRectFilled(new Vector2(x + 8f, rowTop),
                    new Vector2(x + width - 8f, rowBottom), rowBg, 2f);
                var statColor = GetCompactStatColor(label);
                DrawCompactStatIcon(draw,
                    new Vector2(x + 23f, rowTop + 13f), label, statColor);
                draw.AddText(font, fontSize * .86f,
                    new Vector2(x + 40f, rowTop + 5f), statColor,
                    label.ToUpperInvariant());

                var actualText = actualValue + suffix;
                var requiredText = requiredValue + suffix;
                var requiredWidth = ImGuiNET.ImGui.CalcTextSize(requiredText).X * .86f;
                var actualWidth = ImGuiNET.ImGui.CalcTextSize(actualText).X * .86f;
                var requiredX = x + width - 17f - requiredWidth;
                var comparatorX = requiredX - 27f;
                var actualX = comparatorX - 12f - actualWidth;
                draw.AddText(font, fontSize * .86f,
                    new Vector2(actualX, rowTop + 5f), normalText, actualText);
                draw.AddText(font, fontSize * .76f,
                    new Vector2(comparatorX, rowTop + 6f), subdued, ">=");
                draw.AddText(font, fontSize * .86f,
                    new Vector2(requiredX, rowTop + 5f), normalText, requiredText);
            }
            cy += details.Count * detailRowHeight;
        }

        if (specialMods.Count > 0)
        {
            var red = ToImGuiColor(Settings.SpecialStarColor.Value);
            draw.AddText(font, fontSize * .78f, new Vector2(x + 14f, cy + 5f),
                red, "SPECIAL MODIFIERS");
            cy += sectionHeight;
            for (var index = 0; index < specialMods.Count; index++)
            {
                var rowY = cy + index * specialRowHeight;
                DrawFivePointStar(draw,
                    new Vector2(x + 22f, rowY + specialRowHeight * .48f),
                    5f, red);
                draw.AddText(font, fontSize * .86f,
                    new Vector2(x + 37f, rowY + 4f), red,
                    specialMods[index].ToUpperInvariant());
            }
        }

        draw.PopClipRect();
    }

    private static uint GetFullRarityColor(string rarity)
    {
        if (string.Equals(rarity, "Rare", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(255, 215, 80, 255));
        if (string.Equals(rarity, "Unique", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(210, 115, 45, 255));
        if (string.Equals(rarity, "Magic", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(105, 135, 255, 255));
        return ToImGuiColor(new Color(205, 205, 212, 255));
    }

    private static uint GetFullTierColor(string tier)
    {
        if (!string.IsNullOrWhiteSpace(tier) &&
            tier.EndsWith("%", StringComparison.Ordinal) &&
            int.TryParse(tier.TrimEnd('%'), out var percentage))
            return GetPerfectionColor(percentage);

        if (string.Equals(tier, "T1", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(245, 205, 70, 255));
        if (string.Equals(tier, "T2", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(95, 215, 125, 255));
        if (string.Equals(tier, "T3", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(90, 170, 245, 255));
        if (string.Equals(tier, "C", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(90, 210, 225, 255));
        if (string.Equals(tier, "I", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(195, 160, 85, 255));
        return ToImGuiColor(new Color(155, 157, 166, 255));
    }

    private static uint GetPerfectionColor(int percentage)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        if (percentage >= 100)
            return ToImGuiColor(new Color(115, 225, 220, 255));
        if (percentage >= 90)
            return ToImGuiColor(new Color(105, 220, 125, 255));
        if (percentage >= 70)
            return ToImGuiColor(new Color(245, 205, 70, 255));
        if (percentage >= 40)
            return ToImGuiColor(new Color(225, 145, 55, 255));
        return ToImGuiColor(new Color(220, 85, 60, 255));
    }

    private void DrawPolishedCompactOverlay(
        SharpDX.RectangleF itemRect, List<string> rows, int rating,
        bool attachBelow)
    {
        var details = new List<string>();
        var specialMods = new List<string>();
        var uniquePerfection = -1;
        var uniqueRollCount = 0;
        var uniqueModCount = 0;

        foreach (var raw in rows)
        {
            if (raw.StartsWith("UNIQUE PERFECTION|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("UNIQUE PERFECTION|".Length).Split('|');
                if (parts.Length > 0)
                    int.TryParse(parts[0], out uniquePerfection);
                if (parts.Length > 1)
                    int.TryParse(parts[1], out uniqueRollCount);
                if (parts.Length > 2)
                    int.TryParse(parts[2], out uniqueModCount);
            }
            else if (raw.StartsWith("DETAIL|", StringComparison.Ordinal))
            {
                var detail = raw.Substring("DETAIL|".Length).Trim();
                if (!string.Equals(detail, "QUALIFYING STATS", StringComparison.Ordinal))
                    details.Add(detail);
            }
            else if (raw.StartsWith("SPECIAL|", StringComparison.Ordinal))
            {
                var special = raw.Substring("SPECIAL|".Length).Trim();
                if (!string.IsNullOrWhiteSpace(special))
                    specialMods.Add(special);
            }
        }

        if (details.Count == 0 && specialMods.Count == 0 && uniquePerfection < 0)
            return;

        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        var fontSize = ImGuiNET.ImGui.GetFontSize();

        var longestLabel = 0f;
        foreach (var detail in details)
        {
            if (TryParseCompactDetail(detail, out var label, out _, out _, out _))
                longestLabel = Math.Max(longestLabel,
                    ImGuiNET.ImGui.CalcTextSize(label.ToUpperInvariant()).X);
        }
        foreach (var special in specialMods)
            longestLabel = Math.Max(longestLabel,
                ImGuiNET.ImGui.CalcTextSize(special.ToUpperInvariant()).X);

        var width = Math.Max(340f, Math.Max(Settings.ItemInfoWidth.Value + 30f,
            longestLabel + 188f));
        width = Math.Min(width, 520f);
        if (attachBelow)
            width = Math.Max(width, itemRect.Width);

        const float headerHeight = 58f;
        const float sectionHeight = 24f;
        const float detailRowHeight = 28f;
        const float specialRowHeight = 25f;
        const float uniqueSectionHeight = 47f;
        const float bottomPadding = 10f;

        var height = headerHeight + bottomPadding;
        if (uniquePerfection >= 0)
            height += uniqueSectionHeight;
        if (details.Count > 0)
            height += sectionHeight + details.Count * detailRowHeight;
        if (specialMods.Count > 0)
            height += sectionHeight + specialMods.Count * specialRowHeight;

        var panelPosition = GetAnalyzerPanelPosition(itemRect, width, height,
            attachBelow);
        var x = panelPosition.X;
        var y = panelPosition.Y;

        var topLeft = new Vector2(x, y);
        var bottomRight = new Vector2(x + width, y + height);
        var shadow = ToImGuiColor(new Color(0, 0, 0, 175));
        var outer = ToImGuiColor(new Color(7, 8, 11, 248));
        var inner = ToImGuiColor(new Color(14, 15, 19, 246));
        var steel = ToImGuiColor(new Color(72, 75, 84, 255));
        var mutedSteel = ToImGuiColor(new Color(42, 44, 51, 255));
        var gold = ToImGuiColor(GetColor(Settings.ItemInfoTierColor,
            new Color(214, 168, 66, 255)));
        var mutedGold = ToImGuiColor(new Color(150, 119, 61, 255));
        var text = ToImGuiColor(new Color(224, 222, 216, 255));
        var subdued = ToImGuiColor(new Color(145, 147, 155, 255));

        draw.AddRectFilled(topLeft + new Vector2(4f, 5f),
            bottomRight + new Vector2(4f, 5f), shadow, 4f);
        draw.AddRectFilled(topLeft, bottomRight, outer, 4f);
        draw.AddRectFilled(topLeft + new Vector2(3f, 3f),
            bottomRight - new Vector2(3f, 3f), inner, 3f);
        draw.AddRect(topLeft, bottomRight, mutedGold, 4f,
            ImGuiNET.ImDrawFlags.None, 1.5f);
        draw.AddRect(topLeft + new Vector2(3f, 3f),
            bottomRight - new Vector2(3f, 3f), steel, 3f,
            ImGuiNET.ImDrawFlags.None, 1f);
        draw.AddLine(new Vector2(x + 10f, y + 3f),
            new Vector2(x + width - 10f, y + 3f), gold, 1.5f);

        DrawCompactPanelCorner(draw, new Vector2(x + 6f, y + 6f), mutedGold);
        DrawCompactPanelCorner(draw, new Vector2(x + width - 6f, y + 6f), mutedGold);
        DrawCompactPanelCorner(draw, new Vector2(x + 6f, y + height - 6f), mutedGold);
        DrawCompactPanelCorner(draw, new Vector2(x + width - 6f, y + height - 6f), mutedGold);

        draw.PushClipRect(topLeft + new Vector2(2f, 2f),
            bottomRight - new Vector2(2f, 2f), true);

        draw.AddText(font, fontSize * .72f, new Vector2(x + 14f, y + 8f),
            mutedGold, "LOOTLENS");

        var ratingY = y + 27f;
        var ratingTitle = uniquePerfection >= 0
            ? "ITEM PERFECTION"
            : rating > 0 ? "LOOT RATING" : "SPECIAL MODS";
        draw.AddText(font, fontSize * .96f, new Vector2(x + 14f, ratingY),
            text, ratingTitle);

        if (rating > 0)
        {
            var starsWidth = 55f;
            var starStart = x + width - 17f - starsWidth;

            for (var star = 0; star < 3; star++)
            {
                var center = new Vector2(starStart + star * 19f + 6f,
                    ratingY + fontSize * .48f);
                if (star < Math.Min(3, rating))
                    DrawFivePointStar(draw, center, 7f, gold);
                else
                    DrawFivePointStarOutline(draw, center, 7f,
                        ToImGuiColor(new Color(84, 84, 90, 255)));
            }
        }
        else if (uniquePerfection >= 0)
        {
            var percentText = $"{uniquePerfection}%";
            var percentWidth = ImGuiNET.ImGui.CalcTextSize(percentText).X * 1.08f;
            draw.AddText(font, fontSize * 1.08f,
                new Vector2(x + width - 16f - percentWidth, ratingY - 1f),
                GetPerfectionColor(uniquePerfection), percentText);
        }
        else
        {
            var red = ToImGuiColor(Settings.SpecialStarColor.Value);
            var start = x + width - 22f - specialMods.Count * 18f;
            for (var star = 0; star < specialMods.Count; star++)
                DrawFivePointStar(draw,
                    new Vector2(start + star * 18f, ratingY + fontSize * .48f),
                    6.5f, red);
        }

        var dividerY = y + headerHeight - 3f;
        draw.AddLine(new Vector2(x + 8f, dividerY),
            new Vector2(x + width - 8f, dividerY), mutedGold, 1f);

        var cy = y + headerHeight;
        if (uniquePerfection >= 0)
        {
            var perfectionColor = GetPerfectionColor(uniquePerfection);
            var barLeft = x + 14f;
            var barRight = x + width - 14f;
            var barTop = cy + 8f;
            var barBottom = barTop + 10f;

            draw.AddRectFilled(new Vector2(barLeft, barTop),
                new Vector2(barRight, barBottom),
                ToImGuiColor(new Color(30, 31, 37, 255)), 3f);
            var fillRight = barLeft + (barRight - barLeft) *
                Math.Clamp(uniquePerfection / 100f, 0f, 1f);
            if (fillRight > barLeft)
                draw.AddRectFilled(new Vector2(barLeft, barTop),
                    new Vector2(fillRight, barBottom), perfectionColor, 3f);
            draw.AddRect(new Vector2(barLeft, barTop),
                new Vector2(barRight, barBottom), mutedSteel, 3f,
                ImGuiNET.ImDrawFlags.None, 1f);

            var rollText = uniqueRollCount == 1
                ? "1 VARIABLE ROLL"
                : $"{uniqueRollCount} VARIABLE ROLLS";
            var modText = uniqueModCount == 1
                ? "1 MODIFIER"
                : $"{uniqueModCount} MODIFIERS";
            draw.AddText(font, fontSize * .70f,
                new Vector2(x + 14f, barBottom + 7f), subdued,
                $"{rollText}  |  {modText}  |  FIXED ROLLS EXCLUDED");
            cy += uniqueSectionHeight;
        }

        if (details.Count > 0)
        {
            draw.AddText(font, fontSize * .78f, new Vector2(x + 14f, cy + 5f),
                subdued, "QUALIFYING STATS");
            cy += sectionHeight;

            for (var index = 0; index < details.Count; index++)
            {
                if (!TryParseCompactDetail(details[index], out var label,
                        out var actualValue, out var requiredValue, out var suffix))
                    continue;

                var rowTop = cy + index * detailRowHeight;
                var rowBottom = rowTop + detailRowHeight - 2f;
                var rowBg = index % 2 == 0
                    ? ToImGuiColor(new Color(20, 21, 26, 230))
                    : ToImGuiColor(new Color(16, 17, 21, 220));
                draw.AddRectFilled(new Vector2(x + 8f, rowTop),
                    new Vector2(x + width - 8f, rowBottom), rowBg, 2f);
                draw.AddLine(new Vector2(x + 9f, rowBottom),
                    new Vector2(x + width - 9f, rowBottom), mutedSteel, 1f);

                var statColor = GetCompactStatColor(label);
                var center = new Vector2(x + 23f,
                    rowTop + (detailRowHeight - 2f) * .5f);
                DrawCompactStatIcon(draw, center, label, statColor);

                draw.AddText(font, fontSize * .88f,
                    new Vector2(x + 40f, rowTop + 5f), statColor,
                    label.ToUpperInvariant());

                var actualText = actualValue + suffix;
                var requiredText = requiredValue + suffix;
                var requiredWidth = ImGuiNET.ImGui.CalcTextSize(requiredText).X * .88f;
                var actualWidth = ImGuiNET.ImGui.CalcTextSize(actualText).X * .88f;
                var requiredX = x + width - 17f - requiredWidth;
                var comparatorX = requiredX - 27f;
                var actualX = comparatorX - 12f - actualWidth;

                draw.AddText(font, fontSize * .88f,
                    new Vector2(actualX, rowTop + 5f), text, actualText);
                draw.AddText(font, fontSize * .78f,
                    new Vector2(comparatorX, rowTop + 6f), subdued, ">=");
                draw.AddText(font, fontSize * .88f,
                    new Vector2(requiredX, rowTop + 5f), text, requiredText);
            }

            cy += details.Count * detailRowHeight;
        }

        if (specialMods.Count > 0)
        {
            var red = ToImGuiColor(Settings.SpecialStarColor.Value);
            draw.AddText(font, fontSize * .78f, new Vector2(x + 14f, cy + 5f),
                red, "SPECIAL MODIFIERS");
            cy += sectionHeight;

            for (var index = 0; index < specialMods.Count; index++)
            {
                var rowY = cy + index * specialRowHeight;
                DrawFivePointStar(draw,
                    new Vector2(x + 22f, rowY + specialRowHeight * .48f),
                    5f, red);
                draw.AddText(font, fontSize * .86f,
                    new Vector2(x + 37f, rowY + 4f), red,
                    specialMods[index].ToUpperInvariant());
            }
        }

        draw.PopClipRect();
    }

    private float GetMinimalUiScale()
    {
        return Math.Clamp(Settings.ItemInfoMinimalScale.Value / 100f, .80f, 1.80f);
    }

    private void DrawMinimalCardBackground(ImGuiNET.ImDrawListPtr draw,
        Vector2 topLeft, Vector2 bottomRight, bool attached)
    {
        var configuredBackground = GetColor(Settings.ItemInfoBackground,
            new Color(0, 0, 0, 236));
        var configuredBorder = GetColor(Settings.ItemInfoBorder,
            new Color(0, 0, 0, 0));
        // Respect the configured RGBA values exactly. The old implementation
        // forced a visible alpha onto the border even when the user selected a
        // fully transparent border.
        var background = ToImGuiColor(configuredBackground);
        var border = ToImGuiColor(configuredBorder);
        var shadow = ToImGuiColor(new Color(0, 0, 0, 135));
        var rounding = attached ? 1f : 7f;

        if (!attached)
        {
            draw.AddRectFilled(topLeft + new Vector2(3f, 4f),
                bottomRight + new Vector2(3f, 4f), shadow, rounding);
            draw.AddRectFilled(topLeft, bottomRight, background, rounding);
            if (configuredBorder.A > 0)
                draw.AddRect(topLeft, bottomRight, border, rounding,
                    ImGuiNET.ImDrawFlags.None, 1f);
            return;
        }

        // Keep the attached strip seamless. A transparent configured border
        // now truly means no outer frame.
        draw.AddRectFilled(topLeft, bottomRight, background, rounding);
        if (configuredBorder.A > 0)
        {
            draw.AddLine(topLeft, new Vector2(topLeft.X, bottomRight.Y), border, 1f);
            draw.AddLine(new Vector2(topLeft.X, bottomRight.Y), bottomRight, border, 1f);
            draw.AddLine(new Vector2(bottomRight.X, topLeft.Y), bottomRight, border, 1f);
        }
    }

    private static string GetMinimalQualifierLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        return label.Trim() switch
        {
            "Cold Resistance" => "Cold Res",
            "Fire Resistance" => "Fire Res",
            "Lightning Resistance" => "Lightning Res",
            "Chaos Resistance" => "Chaos Res",
            "Movement Speed" => "Move Speed",
            "Spell Suppression" => "Suppression",
            "Critical Strike Chance" => "Crit Chance",
            "Crit Multiplier" => "Crit Multi",
            "Cooldown Recovery" => "Cooldown",
            "Energy Shield" => "Energy Shield",
            _ => label.Trim()
        };
    }

    private static string GetAttachedQualifierLabel(string label, bool narrow)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        if (narrow)
        {
            return label.Trim() switch
            {
                "Cold Resistance" => "Cold Res",
                "Fire Resistance" => "Fire Res",
                "Lightning Resistance" => "Ltng Res",
                "Chaos Resistance" => "Chaos Res",
                "Movement Speed" => "Move Speed",
                "Spell Suppression" => "Suppress",
                "Critical Strike Chance" => "Crit Chance",
                "Crit Multiplier" => "Crit Multi",
                "Attack Speed" => "Atk Speed",
                "Cast Speed" => "Cast Speed",
                "Cooldown Recovery" => "Cooldown",
                "Energy Shield" => "ES",
                "Total Weapon DPS" => "Weapon DPS",
                "Physical DPS" => "Phys DPS",
                _ => label.Trim()
            };
        }

        return label.Trim() switch
        {
            "Cold Resistance" => "Cold Res",
            "Fire Resistance" => "Fire Res",
            "Lightning Resistance" => "Lightning Res",
            "Chaos Resistance" => "Chaos Res",
            "Movement Speed" => "Move Speed",
            "Spell Suppression" => "Suppression",
            "Critical Strike Chance" => "Crit Chance",
            "Crit Multiplier" => "Crit Multi",
            "Attack Speed" => "Atk Speed",
            "Cast Speed" => "Cast Speed",
            "Cooldown Recovery" => "Cooldown",
            "Energy Shield" => "Energy Shield",
            "Total Weapon DPS" => "Weapon DPS",
            "Physical DPS" => "Phys DPS",
            _ => label.Trim()
        };
    }

    private static string GetMinimalMetaText(IEnumerable<string> metaParts)
    {
        var parts = new List<string>();
        foreach (var raw in metaParts ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var split = raw.Replace(" | ", "|", StringComparison.Ordinal)
                .Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var value in split)
            {
                var part = value.Trim()
                    .Replace(" Prefixes", "P", StringComparison.OrdinalIgnoreCase)
                    .Replace(" Suffixes", "S", StringComparison.OrdinalIgnoreCase)
                    .Replace("ilvl ", "i", StringComparison.OrdinalIgnoreCase)
                    .Replace("Armour ", "AR ", StringComparison.OrdinalIgnoreCase)
                    .Replace("Evasion ", "EV ", StringComparison.OrdinalIgnoreCase)
                    .Replace("Energy Shield ", "ES ", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part);
            }
        }

        return string.Join(" · ", parts);
    }

    private static string EllipsizeMinimalText(string text, float maxWidth,
        float scale)
    {
        text ??= string.Empty;
        if (maxWidth <= 12f)
            return string.Empty;

        float Measure(string value) =>
            ImGuiNET.ImGui.CalcTextSize(value).X * Math.Max(.1f, scale);

        if (Measure(text) <= maxWidth)
            return text;

        const string ellipsis = "...";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Measure(text.Substring(0, mid).TrimEnd() + ellipsis) <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return text.Substring(0, Math.Max(0, low)).TrimEnd() + ellipsis;
    }

    private static void DrawMinimalBadge(ImGuiNET.ImDrawListPtr draw,
        ImGuiNET.ImFontPtr font, float fontSize, Vector2 position,
        string text, uint color, float width, float height)
    {
        draw.AddRectFilled(position, position + new Vector2(width, height),
            ToImGuiColor(new Color(24, 25, 30, 238)), 3f);
        draw.AddRect(position, position + new Vector2(width, height), color, 3f,
            ImGuiNET.ImDrawFlags.None, 1f);
        var textWidth = ImGuiNET.ImGui.CalcTextSize(text).X *
                        (fontSize / Math.Max(1f, ImGuiNET.ImGui.GetFontSize()));
        draw.AddText(font, fontSize,
            position + new Vector2(Math.Max(2f, (width - textWidth) * .5f), 2f),
            color, text);
    }

    private void DrawMinimalAttachedCompactStrip(SharpDX.RectangleF itemRect,
        List<string> details, List<string> specialMods, int rating,
        int uniquePerfection, int uniqueRollCount, int uniqueModCount)
    {
        var scale = GetMinimalUiScale();
        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        var baseFontSize = ImGuiNET.ImGui.GetFontSize();
        var width = Math.Max(250f, itemRect.Width);
        var gold = ToImGuiColor(GetColor(Settings.ItemInfoTierColor,
            new Color(235, 190, 45, 255)));
        var normal = ToImGuiColor(new Color(232, 233, 236, 255));
        var subdued = ToImGuiColor(new Color(145, 149, 158, 255));
        // Jewelry tooltips vary in width depending on the base name and the
        // native modifier text. Keep compact labels available for the wider
        // ring/amulet variants too; the actual column count is decided below
        // from the space available rather than from one width cutoff.
        var narrowAttachedLayout = width < 540f;

        if (uniquePerfection >= 0)
        {
            var uniqueStripHeight = 43f * scale;
            var pos = GetAnalyzerPanelPosition(itemRect, width,
                uniqueStripHeight, true);
            var topLeft = pos;
            var bottomRight = pos + new Vector2(width, uniqueStripHeight);
            DrawMinimalCardBackground(draw, topLeft, bottomRight, true);
            draw.PushClipRect(topLeft + Vector2.One,
                bottomRight - Vector2.One, true);

            var label = "ITEM PERFECTION";
            draw.AddText(font, baseFontSize * .68f * scale,
                new Vector2(pos.X + 12f * scale, pos.Y + 7f * scale),
                subdued, label);

            var rollText = $"{FormatCountLabel(uniqueRollCount, "ROLL", "ROLLS")} · " +
                           FormatCountLabel(uniqueModCount, "MOD", "MODS");
            draw.AddText(font, baseFontSize * .56f * scale,
                new Vector2(pos.X + 12f * scale, pos.Y + 24f * scale),
                subdued, rollText);

            draw.AddLine(new Vector2(topLeft.X, topLeft.Y + .5f),
                new Vector2(bottomRight.X, topLeft.Y + .5f),
                ToImGuiColor(new Color(178, 180, 186, 45)), 1f);

            var percent = $"{uniquePerfection}%";
            var percentWidth = ImGuiNET.ImGui.CalcTextSize(percent).X *
                               .86f * scale;
            var percentX = pos.X + width - 12f * scale - percentWidth;
            draw.AddText(font, baseFontSize * .86f * scale,
                new Vector2(percentX, pos.Y + 12f * scale),
                GetPerfectionColor(uniquePerfection), percent);

            var barLeft = pos.X + 130f * scale;
            var barRight = percentX - 12f * scale;
            if (barRight > barLeft + 30f * scale)
            {
                var barTop = pos.Y + 19f * scale;
                var barHeight = 5f * scale;
                draw.AddRectFilled(new Vector2(barLeft, barTop),
                    new Vector2(barRight, barTop + barHeight),
                    ToImGuiColor(new Color(50, 52, 59, 255)), 2f * scale);
                draw.AddRectFilled(new Vector2(barLeft, barTop),
                    new Vector2(barLeft + (barRight - barLeft) *
                        Math.Clamp(uniquePerfection / 100f, 0f, 1f),
                        barTop + barHeight),
                    GetPerfectionColor(uniquePerfection), 2f * scale);
            }

            draw.PopClipRect();
            return;
        }

        var qualifiers = new List<(string Label, string Actual,
            string Required, string Suffix, uint Color)>();
        foreach (var detail in details)
        {
            if (!TryParseCompactDetail(detail, out var label,
                    out var actual, out var required, out var suffix))
                continue;

            qualifiers.Add((GetAttachedQualifierLabel(label,
                    narrowAttachedLayout), actual,
                required, suffix, GetCompactStatColor(label)));
        }

        var cellCount = qualifiers.Count + specialMods.Count;
        if (cellCount == 0)
            return;

        // Scale the text for readability, but only gently scale the layout.
        // Otherwise a larger UI scale makes three useful stats wrap even when
        // the native tooltip has plenty of horizontal room.
        var layoutScale = 1f + (scale - 1f) * .25f;
        // Reserve a distinct rating lane so stars never compete with the last
        // qualifier value or its threshold.
        var scoreWidth = 70f * layoutScale;
        var leftPadding = 10f * layoutScale;
        var rightPadding = 7f * layoutScale;
        var contentWidth = Math.Max(80f,
            width - leftPadding - rightPadding - scoreWidth);
        var maximumColumns = Math.Min(3, cellCount);
        // A two-line compact cell remains readable at 96 px. Use that as the
        // true fit requirement for every tooltip, then let wide armour and
        // weapon cards naturally receive larger cells from contentWidth.
        var minimumCellWidth = 96f;
        var columns = Math.Clamp((int)Math.Floor(contentWidth /
            minimumCellWidth),
            1, maximumColumns);
        var rowCount = (int)Math.Ceiling(cellCount / (double)columns);
        var rowHeight = 38f * scale;
        var verticalPadding = 7f * layoutScale;
        var qualifierStripHeight = Math.Max(50f * scale,
            verticalPadding * 2f + rowCount * rowHeight);
        var panelPos = GetAnalyzerPanelPosition(itemRect, width,
            qualifierStripHeight, true);
        var panelTopLeft = panelPos;
        var panelBottomRight = panelPos +
                               new Vector2(width, qualifierStripHeight);
        DrawMinimalCardBackground(draw, panelTopLeft, panelBottomRight, true);
        draw.PushClipRect(panelTopLeft + Vector2.One,
            panelBottomRight - Vector2.One, true);

        // A restrained joining line gives the strip a crisp top edge without
        // bringing back an outer box. Internal separators are deliberately
        // faint so the stat colors remain the primary structure.
        var topSeparator = ToImGuiColor(new Color(178, 180, 186, 45));
        var internalSeparator = ToImGuiColor(new Color(125, 128, 136, 34));
        draw.AddLine(new Vector2(panelTopLeft.X, panelTopLeft.Y + .5f),
            new Vector2(panelBottomRight.X, panelTopLeft.Y + .5f),
            topSeparator, 1f);

        var cellWidth = contentWidth / columns;
        var contentLeft = panelPos.X + leftPadding;
        var contentTop = panelPos.Y + verticalPadding;
        var contentBottom = contentTop + rowCount * rowHeight;
        for (var divider = 1; divider < columns; divider++)
        {
            var dividerX = contentLeft + divider * cellWidth;
            draw.AddLine(new Vector2(dividerX, contentTop + 3f * layoutScale),
                new Vector2(dividerX,
                    Math.Min(contentBottom - 3f * layoutScale,
                        panelBottomRight.Y - 6f * layoutScale)),
                internalSeparator, 1f);
        }
        for (var rowDivider = 1; rowDivider < rowCount; rowDivider++)
        {
            var dividerY = contentTop + rowDivider * rowHeight;
            draw.AddLine(new Vector2(contentLeft + 5f * layoutScale, dividerY),
                new Vector2(contentLeft + contentWidth - 5f * layoutScale,
                    dividerY), internalSeparator, 1f);
        }

        var scoreDividerX = panelPos.X + width - rightPadding - scoreWidth;
        draw.AddLine(new Vector2(scoreDividerX,
                panelTopLeft.Y + 8f * layoutScale),
            new Vector2(scoreDividerX,
                panelBottomRight.Y - 8f * layoutScale),
            internalSeparator, 1f);

        var cellIndex = 0;
        foreach (var qualifier in qualifiers)
        {
            var column = cellIndex % columns;
            var row = cellIndex / columns;
            var cellX = panelPos.X + leftPadding + column * cellWidth;
            var rowY = panelPos.Y + verticalPadding + row * rowHeight;
            draw.AddRectFilled(new Vector2(cellX, rowY + 3f * layoutScale),
                new Vector2(cellX + 2f * scale,
                    rowY + rowHeight - 3f * layoutScale), qualifier.Color, 1f);

            var labelWidth = Math.Max(20f, cellWidth - 18f * layoutScale);
            var label = EllipsizeMinimalText(
                qualifier.Label.ToUpperInvariant(), labelWidth, .66f * scale);
            draw.AddText(font, baseFontSize * .66f * scale,
                new Vector2(cellX + 8f * layoutScale, rowY + 1f * scale),
                qualifier.Color, label);

            var actualText = $"{qualifier.Actual}{qualifier.Suffix}";
            var minimumText = $"MIN {qualifier.Required}{qualifier.Suffix}";
            var minimumWidth = ImGuiNET.ImGui.CalcTextSize(minimumText).X *
                               .52f * scale;
            draw.AddText(font, baseFontSize * .84f * scale,
                new Vector2(cellX + 8f * layoutScale, rowY + 17f * scale),
                normal, actualText);
            draw.AddText(font, baseFontSize * .52f * scale,
                new Vector2(cellX + cellWidth - 8f * layoutScale - minimumWidth,
                    rowY + 21f * scale), subdued, minimumText);
            cellIndex++;
        }

        var specialColor = ToImGuiColor(Settings.SpecialStarColor.Value);
        foreach (var special in specialMods)
        {
            var column = cellIndex % columns;
            var row = cellIndex / columns;
            var cellX = panelPos.X + leftPadding + column * cellWidth;
            var rowY = panelPos.Y + verticalPadding + row * rowHeight;
            draw.AddRectFilled(new Vector2(cellX, rowY + 3f * layoutScale),
                new Vector2(cellX + 2f * scale,
                    rowY + rowHeight - 3f * layoutScale), specialColor, 1f);
            var text = EllipsizeMinimalText(special.ToUpperInvariant(),
                cellWidth - 18f * layoutScale, .64f * scale);
            draw.AddText(font, baseFontSize * .64f * scale,
                new Vector2(cellX + 8f * layoutScale, rowY + 1f * scale),
                specialColor, text);
            DrawFivePointStar(draw,
                new Vector2(cellX + 11f * layoutScale, rowY + 27f * scale),
                4.2f * scale, specialColor);
            draw.AddText(font, baseFontSize * .52f * scale,
                new Vector2(cellX + 20f * layoutScale, rowY + 21f * scale),
                subdued, "SPECIAL MOD");
            cellIndex++;
        }

        var starSpacing = 16f * scale;
        var starLaneCenter = scoreDividerX + scoreWidth * .5f;
        var starStart = starLaneCenter - starSpacing;
        var starY = panelPos.Y + qualifierStripHeight * .5f;
        for (var star = 0; star < 3; star++)
        {
            var center = new Vector2(starStart + star * starSpacing, starY);
            if (star < Math.Clamp(rating, 0, 3))
                DrawFivePointStar(draw, center, 5.4f * scale, gold);
            else
                DrawFivePointStarOutline(draw, center, 5.4f * scale,
                    ToImGuiColor(new Color(100, 103, 112, 255)));
        }

        draw.PopClipRect();
    }

    private void DrawMinimalCompactOverlay(SharpDX.RectangleF itemRect,
        List<string> rows, int rating, bool attachBelow)
    {
        var details = new List<string>();
        var specialMods = new List<string>();
        var uniquePerfection = -1;
        var uniqueRollCount = 0;
        var uniqueModCount = 0;

        foreach (var raw in rows)
        {
            if (raw.StartsWith("UNIQUE PERFECTION|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("UNIQUE PERFECTION|".Length).Split('|');
                if (parts.Length > 0) int.TryParse(parts[0], out uniquePerfection);
                if (parts.Length > 1) int.TryParse(parts[1], out uniqueRollCount);
                if (parts.Length > 2) int.TryParse(parts[2], out uniqueModCount);
            }
            else if (raw.StartsWith("DETAIL|", StringComparison.Ordinal))
            {
                var detail = raw.Substring("DETAIL|".Length).Trim();
                if (!string.Equals(detail, "QUALIFYING STATS", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(detail))
                    details.Add(detail);
            }
            else if (raw.StartsWith("SPECIAL|", StringComparison.Ordinal))
            {
                var special = raw.Substring("SPECIAL|".Length).Trim();
                if (!string.IsNullOrWhiteSpace(special))
                    specialMods.Add(special);
            }
        }

        if (details.Count == 0 && specialMods.Count == 0 && uniquePerfection < 0)
            return;

        if (attachBelow)
        {
            DrawMinimalAttachedCompactStrip(itemRect, details, specialMods,
                rating, uniquePerfection, uniqueRollCount, uniqueModCount);
            return;
        }

        var scale = GetMinimalUiScale();
        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        var baseFontSize = ImGuiNET.ImGui.GetFontSize();
        var width = attachBelow
            ? Math.Max(230f * scale, itemRect.Width)
            : Math.Max(250f * scale, Math.Min(360f * scale,
                Settings.ItemInfoWidth.Value * scale));
        var headerHeight = 30f * scale;
        var rowHeight = 23f * scale;
        var specialRowHeight = 20f * scale;
        var uniqueHeight = uniquePerfection >= 0 ? 35f * scale : 0f;
        var height = 8f * scale + headerHeight + uniqueHeight +
                     details.Count * rowHeight + specialMods.Count * specialRowHeight;

        var pos = GetAnalyzerPanelPosition(itemRect, width, height, attachBelow);
        var x = pos.X;
        var y = pos.Y;
        var topLeft = new Vector2(x, y);
        var bottomRight = new Vector2(x + width, y + height);
        DrawMinimalCardBackground(draw, topLeft, bottomRight, attachBelow);
        draw.PushClipRect(topLeft + Vector2.One,
            bottomRight - Vector2.One, true);

        var normal = ToImGuiColor(new Color(232, 233, 236, 255));
        var subdued = ToImGuiColor(new Color(145, 149, 158, 255));
        var gold = ToImGuiColor(GetColor(Settings.ItemInfoTierColor,
            new Color(235, 190, 45, 255)));
        var cy = y + 8f * scale;

        var headerText = uniquePerfection >= 0
            ? "ITEM PERFECTION"
            : details.Count > 0 ? $"{details.Count} MATCHES" : "SPECIAL MODS";
        draw.AddText(font, baseFontSize * .76f * scale,
            new Vector2(x + 11f * scale, cy), subdued, headerText);

        if (uniquePerfection >= 0)
        {
            var percent = $"{uniquePerfection}%";
            var percentWidth = ImGuiNET.ImGui.CalcTextSize(percent).X * .90f * scale;
            draw.AddText(font, baseFontSize * .90f * scale,
                new Vector2(x + width - 11f * scale - percentWidth, cy - 1f * scale),
                GetPerfectionColor(uniquePerfection), percent);
        }
        else if (rating > 0)
        {
            var starStart = x + width - 53f * scale;
            for (var star = 0; star < 3; star++)
            {
                var center = new Vector2(starStart + star * 16f * scale,
                    cy + 7f * scale);
                if (star < Math.Clamp(rating, 0, 3))
                    DrawFivePointStar(draw, center, 5.4f * scale, gold);
                else
                    DrawFivePointStarOutline(draw, center, 5.4f * scale,
                        ToImGuiColor(new Color(100, 103, 112, 255)));
            }
        }

        cy += headerHeight;
        if (uniquePerfection >= 0)
        {
            var barLeft = x + 11f * scale;
            var barRight = x + width - 11f * scale;
            var barTop = cy + 1f * scale;
            var barHeight = 6f * scale;
            draw.AddRectFilled(new Vector2(barLeft, barTop),
                new Vector2(barRight, barTop + barHeight),
                ToImGuiColor(new Color(50, 52, 59, 255)), 3f * scale);
            draw.AddRectFilled(new Vector2(barLeft, barTop),
                new Vector2(barLeft + (barRight - barLeft) *
                    Math.Clamp(uniquePerfection / 100f, 0f, 1f), barTop + barHeight),
                GetPerfectionColor(uniquePerfection), 3f * scale);
            var rollText = $"{FormatCountLabel(uniqueRollCount, "VARIABLE ROLL", "VARIABLE ROLLS")} · " +
                           $"{FormatCountLabel(uniqueModCount, "MODIFIER", "MODIFIERS")} · FIXED EXCLUDED";
            rollText = EllipsizeMinimalText(rollText,
                barRight - barLeft, .62f * scale);
            draw.AddText(font, baseFontSize * .62f * scale,
                new Vector2(barLeft, barTop + 10f * scale), subdued, rollText);
            cy += uniqueHeight;
        }

        for (var index = 0; index < details.Count; index++)
        {
            if (!TryParseCompactDetail(details[index], out var label,
                    out var actualValue, out var requiredValue, out var suffix))
                continue;

            var rowTop = cy + index * rowHeight;
            var statColor = GetCompactStatColor(label);
            draw.AddRectFilled(new Vector2(x + 11f * scale, rowTop + 4f * scale),
                new Vector2(x + 13f * scale, rowTop + rowHeight - 4f * scale),
                statColor, 1f);
            var minimalLabel = GetMinimalQualifierLabel(label);
            draw.AddText(font, baseFontSize * .76f * scale,
                new Vector2(x + 20f * scale, rowTop + 3f * scale), normal,
                minimalLabel);

            var valueText = $"{actualValue}{suffix} >= {requiredValue}{suffix}";
            var valueSize = baseFontSize * .73f * scale;
            var valueWidth = ImGuiNET.ImGui.CalcTextSize(valueText).X * .73f * scale;
            draw.AddText(font, valueSize,
                new Vector2(x + width - 11f * scale - valueWidth,
                    rowTop + 3f * scale), subdued, valueText);
        }
        cy += details.Count * rowHeight;

        var specialColor = ToImGuiColor(Settings.SpecialStarColor.Value);
        for (var index = 0; index < specialMods.Count; index++)
        {
            var rowTop = cy + index * specialRowHeight;
            DrawFivePointStar(draw, new Vector2(x + 13f * scale,
                    rowTop + 8f * scale), 4.2f * scale, specialColor);
            var text = EllipsizeMinimalText(specialMods[index].ToUpperInvariant(),
                width - 35f * scale, .72f * scale);
            draw.AddText(font, baseFontSize * .72f * scale,
                new Vector2(x + 22f * scale, rowTop + 1f * scale),
                specialColor, text);
        }

        draw.PopClipRect();
    }

    private void DrawMinimalFullOverlay(SharpDX.RectangleF itemRect,
        List<string> rows, bool attachBelow)
    {
        var itemName = "ITEM";
        var rarity = string.Empty;
        var rating = 0;
        var uniquePerfection = -1;
        var uniqueRollCount = 0;
        var uniqueModCount = 0;
        var metaParts = new List<string>();
        var modRows = new List<(string Affix, string Tier, string Text)>();
        var details = new List<string>();
        var specialMods = new List<string>();

        foreach (var raw in rows)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (raw.StartsWith("HEADER|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("HEADER|".Length).Split('|');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                    itemName = parts[0].Trim();
                if (parts.Length > 1) rarity = parts[1].Trim();
                if (parts.Length > 2) int.TryParse(parts[2], out rating);
            }
            else if (raw.StartsWith("META|", StringComparison.Ordinal))
                metaParts.Add(raw.Substring("META|".Length));
            else if (raw.StartsWith("DEFENSES|", StringComparison.Ordinal))
                metaParts.Add(raw.Substring("DEFENSES|".Length)
                    .Replace("  •  ", " | ", StringComparison.Ordinal));
            else if (raw.StartsWith("DPSROW|", StringComparison.Ordinal))
                metaParts.Add(raw.Substring("DPSROW|".Length)
                    .Replace("|", " | ", StringComparison.Ordinal));
            else if (raw.StartsWith("MODROW|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("MODROW|".Length).Split('|');
                var affix = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                var tier = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                var text = parts.Length > 2
                    ? string.Join("/", parts.Skip(2)).Trim()
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    modRows.Add((affix, tier, text));
            }
            else if (raw.StartsWith("LOOT RATING|", StringComparison.Ordinal))
                int.TryParse(raw.Substring("LOOT RATING|".Length), out rating);
            else if (raw.StartsWith("UNIQUE PERFECTION|", StringComparison.Ordinal))
            {
                var parts = raw.Substring("UNIQUE PERFECTION|".Length).Split('|');
                if (parts.Length > 0) int.TryParse(parts[0], out uniquePerfection);
                if (parts.Length > 1) int.TryParse(parts[1], out uniqueRollCount);
                if (parts.Length > 2) int.TryParse(parts[2], out uniqueModCount);
            }
            else if (raw.StartsWith("DETAIL|", StringComparison.Ordinal))
            {
                var detail = raw.Substring("DETAIL|".Length).Trim();
                if (!string.Equals(detail, "QUALIFYING STATS", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(detail))
                    details.Add(detail);
            }
            else if (raw.StartsWith("SPECIAL|", StringComparison.Ordinal))
            {
                var special = raw.Substring("SPECIAL|".Length).Trim();
                if (!string.IsNullOrWhiteSpace(special))
                    specialMods.Add(special);
            }
        }

        var scale = GetMinimalUiScale();
        var draw = ImGuiNET.ImGui.GetForegroundDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        var baseFontSize = ImGuiNET.ImGui.GetFontSize();
        var width = attachBelow
            ? Math.Max(300f * scale, itemRect.Width)
            : Math.Max(340f * scale, Settings.ItemInfoWidth.Value * scale);
        width = Math.Min(width, 560f * scale);
        var headerHeight = 63f * scale;
        var metaHeight = string.IsNullOrWhiteSpace(GetMinimalMetaText(metaParts))
            ? 0f : 23f * scale;
        var modRowHeight = 25f * scale;
        var footerHeaderHeight = 27f * scale;
        var detailRowHeight = 38f * scale;
        var specialRowHeight = 20f * scale;
        var perfectionFooterHeight = 42f * scale;
        var qualifierColumns = Math.Min(3, Math.Max(1, details.Count));
        var qualifierRows = details.Count > 0
            ? (int)Math.Ceiling(details.Count / (double)qualifierColumns)
            : 0;
        var footerHeight = uniquePerfection >= 0
            ? perfectionFooterHeight
            : details.Count > 0 ? footerHeaderHeight + qualifierRows * detailRowHeight : 0f;
        var height = headerHeight + metaHeight + modRows.Count * modRowHeight +
                     specialMods.Count * specialRowHeight + footerHeight + 10f * scale;

        var pos = GetAnalyzerPanelPosition(itemRect, width, height, attachBelow);
        var x = pos.X;
        var y = pos.Y;
        var topLeft = new Vector2(x, y);
        var bottomRight = new Vector2(x + width, y + height);
        DrawMinimalCardBackground(draw, topLeft, bottomRight, attachBelow);
        draw.PushClipRect(topLeft + Vector2.One,
            bottomRight - Vector2.One, true);

        var normal = ToImGuiColor(new Color(234, 235, 238, 255));
        var subdued = ToImGuiColor(new Color(145, 149, 158, 255));
        var mutedLine = ToImGuiColor(new Color(65, 68, 76, 160));
        var gold = ToImGuiColor(GetColor(Settings.ItemInfoTierColor,
            new Color(235, 190, 45, 255)));
        var specialColor = ToImGuiColor(Settings.SpecialStarColor.Value);

        var titleMaxWidth = width - 130f * scale;
        var title = EllipsizeMinimalText(itemName.ToUpperInvariant(),
            titleMaxWidth, 1.02f * scale);
        draw.AddText(font, baseFontSize * 1.02f * scale,
            new Vector2(x + 12f * scale, y + 10f * scale), normal, title);
        draw.AddText(font, baseFontSize * .70f * scale,
            new Vector2(x + 12f * scale, y + 35f * scale),
            GetFullRarityColor(rarity), rarity.ToUpperInvariant());

        if (uniquePerfection >= 0)
        {
            var scoreText = $"{uniquePerfection}%";
            var scoreWidth = ImGuiNET.ImGui.CalcTextSize(scoreText).X *
                             1.12f * scale;
            draw.AddText(font, baseFontSize * 1.12f * scale,
                new Vector2(x + width - 12f * scale - scoreWidth,
                    y + 10f * scale), GetPerfectionColor(uniquePerfection),
                scoreText);
            var label = "PERFECTION";
            var labelWidth = ImGuiNET.ImGui.CalcTextSize(label).X * .62f * scale;
            draw.AddText(font, baseFontSize * .62f * scale,
                new Vector2(x + width - 12f * scale - labelWidth,
                    y + 37f * scale), subdued, label);
        }
        else
        {
            var starStart = x + width - 54f * scale;
            // Full rare analyzers move their gold rating into the compact
            // qualifier strip below. Keep header stars only when there is no
            // qualifier footer, avoiding the same score in two places.
            if (details.Count == 0)
            {
                for (var star = 0; star < 3; star++)
                {
                    var center = new Vector2(starStart + star * 16f * scale,
                        y + 20f * scale);
                    if (star < Math.Clamp(rating, 0, 3))
                        DrawFivePointStar(draw, center, 5.6f * scale, gold);
                    else
                        DrawFivePointStarOutline(draw, center, 5.6f * scale,
                            ToImGuiColor(new Color(100, 103, 112, 255)));
                }
            }

            var specialStart = details.Count > 0
                ? x + width - 18f * scale
                : starStart;
            for (var index = 0; index < Math.Min(2, specialMods.Count); index++)
                DrawFivePointStar(draw,
                    new Vector2(specialStart - (index + 1) * 15f * scale,
                        y + 20f * scale), 4.8f * scale, specialColor);
        }

        var cy = y + headerHeight;
        var meta = GetMinimalMetaText(metaParts);
        if (!string.IsNullOrWhiteSpace(meta))
        {
            var clippedMeta = EllipsizeMinimalText(meta,
                width - 24f * scale, .68f * scale);
            draw.AddText(font, baseFontSize * .68f * scale,
                new Vector2(x + 12f * scale, cy + 2f * scale), subdued,
                clippedMeta);
            cy += metaHeight;
        }

        var isUnique = string.Equals(rarity, "Unique",
            StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < modRows.Count; index++)
        {
            var mod = modRows[index];
            var rowTop = cy + index * modRowHeight;
            var statColor = GetCompactStatColor(mod.Text);
            draw.AddRectFilled(new Vector2(x + 12f * scale, rowTop + 4f * scale),
                new Vector2(x + 14f * scale, rowTop + modRowHeight - 4f * scale),
                statColor, 1f);

            var tierText = mod.Tier.Trim('[', ']');
            var fixedUnique = isUnique && string.Equals(mod.Affix, "U",
                StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(tierText);
            var badgeWidth = fixedUnique ? 40f * scale :
                string.IsNullOrEmpty(tierText) ? 0f : 36f * scale;
            var textRight = x + width - 12f * scale -
                            (badgeWidth > 0f ? badgeWidth + 8f * scale : 0f);
            var text = EllipsizeMinimalText(mod.Text,
                textRight - (x + 22f * scale), .79f * scale);
            draw.AddText(font, baseFontSize * .79f * scale,
                new Vector2(x + 22f * scale, rowTop + 3f * scale),
                statColor, text);

            if (fixedUnique)
            {
                var fixedText = "FIXED";
                draw.AddText(font, baseFontSize * .60f * scale,
                    new Vector2(x + width - 12f * scale - badgeWidth,
                        rowTop + 5f * scale), subdued, fixedText);
            }
            else if (!string.IsNullOrEmpty(tierText))
            {
                var badgeColor = GetFullTierColor(tierText);
                DrawMinimalBadge(draw, font, baseFontSize * .62f * scale,
                    new Vector2(x + width - 12f * scale - badgeWidth,
                        rowTop + 2f * scale), tierText, badgeColor,
                    badgeWidth, 19f * scale);
            }
        }
        cy += modRows.Count * modRowHeight;

        for (var index = 0; index < specialMods.Count; index++)
        {
            var rowTop = cy + index * specialRowHeight;
            draw.AddRectFilled(new Vector2(x + 12f * scale, rowTop + 4f * scale),
                new Vector2(x + 14f * scale,
                    rowTop + specialRowHeight - 4f * scale), specialColor, 1f);
            var specialText = EllipsizeMinimalText(
                specialMods[index].ToUpperInvariant(), width - 36f * scale,
                .68f * scale);
            draw.AddText(font, baseFontSize * .68f * scale,
                new Vector2(x + 22f * scale, rowTop + 2f * scale),
                specialColor, specialText);
        }
        cy += specialMods.Count * specialRowHeight;

        if (uniquePerfection >= 0)
        {
            draw.AddLine(new Vector2(x + 12f * scale, cy + 2f * scale),
                new Vector2(x + width - 12f * scale, cy + 2f * scale),
                mutedLine, 1f);
            draw.AddText(font, baseFontSize * .67f * scale,
                new Vector2(x + 12f * scale, cy + 8f * scale), subdued,
                "ITEM PERFECTION");
            var barLeft = x + 12f * scale;
            var barRight = x + width - 12f * scale;
            var barTop = cy + 25f * scale;
            var barHeight = 6f * scale;
            draw.AddRectFilled(new Vector2(barLeft, barTop),
                new Vector2(barRight, barTop + barHeight),
                ToImGuiColor(new Color(50, 52, 59, 255)), 3f * scale);
            draw.AddRectFilled(new Vector2(barLeft, barTop),
                new Vector2(barLeft + (barRight - barLeft) *
                    Math.Clamp(uniquePerfection / 100f, 0f, 1f),
                    barTop + barHeight), GetPerfectionColor(uniquePerfection),
                3f * scale);
            var rollText = $"{FormatCountLabel(uniqueRollCount, "VARIABLE ROLL", "VARIABLE ROLLS")} · " +
                           $"{FormatCountLabel(uniqueModCount, "MODIFIER", "MODIFIERS")} · FIXED EXCLUDED";
            var rollWidth = ImGuiNET.ImGui.CalcTextSize(rollText).X * .56f * scale;
            if (rollWidth < width - 24f * scale)
                draw.AddText(font, baseFontSize * .56f * scale,
                    new Vector2(x + width - 12f * scale - rollWidth,
                        cy + 7f * scale), subdued, rollText);
        }
        else if (details.Count > 0)
        {
            draw.AddLine(new Vector2(x + 12f * scale, cy + 2f * scale),
                new Vector2(x + width - 12f * scale, cy + 2f * scale),
                mutedLine, 1f);
            draw.AddText(font, baseFontSize * .67f * scale,
                new Vector2(x + 12f * scale, cy + 8f * scale), subdued,
                $"{details.Count} MATCHES");
            cy += footerHeaderHeight;

            var scoreWidth = 66f * scale;
            var leftPadding = 12f * scale;
            var rightPadding = 8f * scale;
            var contentWidth = width - leftPadding - rightPadding - scoreWidth;
            var cellWidth = contentWidth / qualifierColumns;
            var footerTop = cy;
            var footerBottom = cy + qualifierRows * detailRowHeight;
            var faintLine = ToImGuiColor(new Color(125, 128, 136, 34));

            for (var divider = 1; divider < qualifierColumns; divider++)
            {
                var dividerX = x + leftPadding + divider * cellWidth;
                draw.AddLine(new Vector2(dividerX, footerTop + 4f * scale),
                    new Vector2(dividerX, footerBottom - 4f * scale),
                    faintLine, 1f);
            }

            for (var rowDivider = 1; rowDivider < qualifierRows; rowDivider++)
            {
                var dividerY = footerTop + rowDivider * detailRowHeight;
                draw.AddLine(new Vector2(x + leftPadding, dividerY),
                    new Vector2(x + leftPadding + contentWidth, dividerY),
                    faintLine, 1f);
            }

            var scoreDividerX = x + width - rightPadding - scoreWidth;
            draw.AddLine(new Vector2(scoreDividerX, footerTop + 4f * scale),
                new Vector2(scoreDividerX, footerBottom - 4f * scale),
                faintLine, 1f);

            for (var index = 0; index < details.Count; index++)
            {
                if (!TryParseCompactDetail(details[index], out var label,
                        out var actualValue, out var requiredValue, out var suffix))
                    continue;

                var column = index % qualifierColumns;
                var row = index / qualifierColumns;
                var cellX = x + leftPadding + column * cellWidth;
                var rowTop = cy + row * detailRowHeight;
                var statColor = GetCompactStatColor(label);
                draw.AddRectFilled(new Vector2(cellX, rowTop + 4f * scale),
                    new Vector2(cellX + 2f * scale,
                        rowTop + detailRowHeight - 4f * scale), statColor, 1f);

                var compactLabel = GetAttachedQualifierLabel(label, true)
                    .ToUpperInvariant();
                compactLabel = EllipsizeMinimalText(compactLabel,
                    cellWidth - 16f * scale, .61f * scale);
                draw.AddText(font, baseFontSize * .61f * scale,
                    new Vector2(cellX + 8f * scale, rowTop + 1f * scale),
                    statColor, compactLabel);

                var actualText = $"{actualValue}{suffix}";
                var minimumText = $"MIN {requiredValue}{suffix}";
                var minimumWidth = ImGuiNET.ImGui.CalcTextSize(minimumText).X *
                                   .48f * scale;
                draw.AddText(font, baseFontSize * .79f * scale,
                    new Vector2(cellX + 8f * scale, rowTop + 17f * scale),
                    normal, actualText);
                draw.AddText(font, baseFontSize * .48f * scale,
                    new Vector2(cellX + cellWidth - 7f * scale - minimumWidth,
                        rowTop + 21f * scale), subdued, minimumText);
            }

            var starSpacing = 16f * scale;
            var starCenterX = scoreDividerX + scoreWidth * .5f;
            var starY = footerTop + (footerBottom - footerTop) * .5f;
            for (var star = 0; star < 3; star++)
            {
                var center = new Vector2(starCenterX - starSpacing +
                    star * starSpacing, starY);
                if (star < Math.Clamp(rating, 0, 3))
                    DrawFivePointStar(draw, center, 5.4f * scale, gold);
                else
                    DrawFivePointStarOutline(draw, center, 5.4f * scale,
                        ToImGuiColor(new Color(100, 103, 112, 255)));
            }
        }

        draw.PopClipRect();
    }

    private static bool TryParseCompactDetail(string detail, out string label,
        out string actualValue, out string requiredValue, out string suffix)
    {
        label = detail ?? string.Empty;
        actualValue = string.Empty;
        requiredValue = string.Empty;
        suffix = string.Empty;

        var colon = label.IndexOf(':');
        if (colon <= 0)
            return false;

        var slash = label.IndexOf('/', colon + 1);
        if (slash < 0)
            return false;

        var original = label;
        label = original.Substring(0, colon).Trim();
        var actualPart = original.Substring(colon + 1, slash - colon - 1);
        var requiredPart = original.Substring(slash + 1);

        if (!TryReadLeadingNumber(actualPart, out actualValue) ||
            !TryReadLeadingNumber(requiredPart, out requiredValue))
            return false;

        suffix = actualPart.IndexOf('%') >= 0 || requiredPart.IndexOf('%') >= 0
            ? "%"
            : string.Empty;
        return label.Length > 0;
    }

    private static bool TryReadLeadingNumber(string text, out string number)
    {
        number = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        var end = start;
        if (end < text.Length && (text[end] == '+' || text[end] == '-'))
            end++;

        var digitStart = end;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;

        if (end == digitStart)
            return false;

        number = text.Substring(start, end - start);
        return true;
    }

    private static uint GetCompactStatColor(string stat)
    {
        if (Contains(stat, "Cold Resistance") || Contains(stat, "Cold Res")) return ToImGuiColor(new Color(90, 190, 255, 255));
        if (Contains(stat, "Fire Resistance") || Contains(stat, "Fire Res")) return ToImGuiColor(new Color(255, 112, 58, 255));
        if (Contains(stat, "Lightning Resistance") || Contains(stat, "Lightning Res")) return ToImGuiColor(new Color(245, 210, 70, 255));
        if (Contains(stat, "Chaos Resistance") || Contains(stat, "Chaos Res")) return ToImGuiColor(new Color(190, 105, 225, 255));
        if (Contains(stat, "Resistance") || Contains(stat, " Res")) return ToImGuiColor(new Color(175, 185, 225, 255));
        if (Contains(stat, "Life")) return ToImGuiColor(new Color(235, 90, 105, 255));
        if (Contains(stat, "Armour") || Contains(stat, "Physical Damage Reduction") ||
            Contains(stat, "Defences")) return ToImGuiColor(new Color(205, 170, 120, 255));
        if (Contains(stat, "Evasion")) return ToImGuiColor(new Color(85, 210, 110, 255));
        if (Contains(stat, "Energy Shield") || Contains(stat, " ES")) return ToImGuiColor(new Color(75, 215, 238, 255));
        if (Contains(stat, "Suppression")) return ToImGuiColor(new Color(80, 205, 175, 255));
        if (Contains(stat, "Mana")) return ToImGuiColor(new Color(90, 135, 245, 255));
        if (Contains(stat, "Block") || Contains(stat, "Stun")) return ToImGuiColor(new Color(125, 165, 215, 255));
        if (Contains(stat, "Movement") || Contains(stat, "Move Speed")) return ToImGuiColor(new Color(105, 220, 95, 255));
        if (Contains(stat, "Cast Speed")) return ToImGuiColor(new Color(165, 125, 245, 255));
        if (Contains(stat, "Spell Damage")) return ToImGuiColor(new Color(125, 145, 255, 255));
        if (Contains(stat, "Attack Speed")) return ToImGuiColor(new Color(240, 155, 70, 255));
        if (Contains(stat, "Action Speed") || Contains(stat, "Speed")) return ToImGuiColor(new Color(150, 175, 225, 255));
        if (Contains(stat, "Critical") || Contains(stat, "Crit ")) return ToImGuiColor(new Color(245, 190, 70, 255));
        if (Contains(stat, "Accuracy")) return ToImGuiColor(new Color(220, 210, 165, 255));
        if (Contains(stat, "Cooldown")) return ToImGuiColor(new Color(90, 205, 225, 255));
        if (Contains(stat, "DoT") || Contains(stat, "Damage over Time")) return ToImGuiColor(new Color(150, 210, 90, 255));
        if (Contains(stat, "Gem")) return ToImGuiColor(new Color(80, 220, 190, 255));
        if (Contains(stat, "DPS")) return ToImGuiColor(new Color(235, 120, 80, 255));
        if (Contains(stat, "Attribute")) return ToImGuiColor(new Color(220, 180, 90, 255));
        if (Contains(stat, "Strength") || Contains(stat, " Str")) return ToImGuiColor(new Color(230, 115, 90, 255));
        if (Contains(stat, "Dexterity") || Contains(stat, " Dex")) return ToImGuiColor(new Color(100, 205, 115, 255));
        if (Contains(stat, "Intelligence") || Contains(stat, " Int")) return ToImGuiColor(new Color(100, 155, 240, 255));
        if (Contains(stat, "Fire Damage") || Contains(stat, "Fire Dmg") || Contains(stat, "Ignite")) return ToImGuiColor(new Color(245, 115, 55, 255));
        if (Contains(stat, "Cold Damage") || Contains(stat, "Cold Dmg") || Contains(stat, "Freeze") ||
            Contains(stat, "Chill")) return ToImGuiColor(new Color(90, 185, 245, 255));
        if (Contains(stat, "Lightning Damage") || Contains(stat, "Lightning Dmg") || Contains(stat, "Shock")) return ToImGuiColor(new Color(240, 205, 70, 255));
        if (Contains(stat, "Chaos Damage") || Contains(stat, "Chaos Dmg")) return ToImGuiColor(new Color(190, 105, 225, 255));
        if (Contains(stat, "Physical Damage") || Contains(stat, "Phys Dmg")) return ToImGuiColor(new Color(225, 145, 105, 255));
        if (Contains(stat, "Elemental Damage") || Contains(stat, "Ele Dmg")) return ToImGuiColor(new Color(225, 175, 90, 255));
        if (Contains(stat, "Poison")) return ToImGuiColor(new Color(120, 195, 90, 255));
        if (Contains(stat, "Bleed")) return ToImGuiColor(new Color(215, 75, 80, 255));
        if (Contains(stat, "Minion")) return ToImGuiColor(new Color(180, 125, 225, 255));
        if (Contains(stat, "Projectile")) return ToImGuiColor(new Color(100, 190, 215, 255));
        if (Contains(stat, "Area of Effect") || Contains(stat, "Area Damage")) return ToImGuiColor(new Color(225, 160, 90, 255));
        if (Contains(stat, "Flask")) return ToImGuiColor(new Color(90, 195, 180, 255));
        if (Contains(stat, "Endurance Charge")) return ToImGuiColor(new Color(220, 105, 70, 255));
        if (Contains(stat, "Frenzy Charge")) return ToImGuiColor(new Color(95, 210, 110, 255));
        if (Contains(stat, "Power Charge")) return ToImGuiColor(new Color(145, 135, 240, 255));
        if (Contains(stat, "Charge")) return ToImGuiColor(new Color(215, 185, 100, 255));
        if (Contains(stat, "Rarity") || Contains(stat, "Quantity")) return ToImGuiColor(new Color(225, 190, 105, 255));
        if (Contains(stat, "Regeneration") || Contains(stat, "Regen")) return ToImGuiColor(new Color(120, 195, 150, 255));
        if (Contains(stat, "Leech")) return ToImGuiColor(new Color(210, 105, 145, 255));
        if (Contains(stat, "Curse")) return ToImGuiColor(new Color(175, 115, 220, 255));
        if (Contains(stat, "Aura") || Contains(stat, "Reservation")) return ToImGuiColor(new Color(85, 195, 180, 255));
        if (Contains(stat, "Duration")) return ToImGuiColor(new Color(120, 185, 190, 255));
        if (Contains(stat, "Damage") || Contains(stat, "Dmg")) return ToImGuiColor(new Color(205, 140, 105, 255));
        return ToImGuiColor(new Color(190, 195, 205, 255));
    }

    private static void DrawCompactPanelCorner(
        ImGuiNET.ImDrawListPtr draw, Vector2 center, uint color)
    {
        draw.AddCircleFilled(center, 3.2f, color, 8);
        draw.AddLine(center + new Vector2(-5f, 0f),
            center + new Vector2(5f, 0f), color, 1f);
        draw.AddLine(center + new Vector2(0f, -5f),
            center + new Vector2(0f, 5f), color, 1f);
    }

    private static void DrawCompactStatIcon(ImGuiNET.ImDrawListPtr draw,
        Vector2 center, string stat, uint color)
    {
        if (Contains(stat, "Cold Resistance") || Contains(stat, "Cold Damage") ||
            Contains(stat, "Freeze") || Contains(stat, "Chill"))
        {
            for (var i = 0; i < 3; i++)
            {
                var angle = i * MathF.PI / 3f;
                var d = new Vector2(MathF.Cos(angle) * 7f, MathF.Sin(angle) * 7f);
                draw.AddLine(center - d, center + d, color, 1.6f);
            }
            return;
        }

        if (Contains(stat, "Fire Resistance") || Contains(stat, "Fire Damage") ||
            Contains(stat, "Ignite"))
        {
            draw.AddTriangleFilled(center + new Vector2(0f, -8f),
                center + new Vector2(6f, 5f), center + new Vector2(-6f, 5f), color);
            draw.AddCircleFilled(center + new Vector2(0f, 3f), 3.2f,
                ToImGuiColor(new Color(255, 195, 85, 255)));
            return;
        }

        if (Contains(stat, "Lightning Resistance") ||
            Contains(stat, "Lightning Damage") || Contains(stat, "Shock"))
        {
            draw.AddLine(center + new Vector2(3f, -8f),
                center + new Vector2(-4f, 0f), color, 2.4f);
            draw.AddLine(center + new Vector2(-4f, 0f),
                center + new Vector2(1f, 0f), color, 2.4f);
            draw.AddLine(center + new Vector2(1f, 0f),
                center + new Vector2(-2f, 8f), color, 2.4f);
            return;
        }

        if (Contains(stat, "Life"))
        {
            draw.AddCircleFilled(center + new Vector2(-3f, -2f), 4f, color);
            draw.AddCircleFilled(center + new Vector2(3f, -2f), 4f, color);
            draw.AddTriangleFilled(center + new Vector2(-7f, 0f),
                center + new Vector2(7f, 0f), center + new Vector2(0f, 8f), color);
            return;
        }

        if (Contains(stat, "Movement"))
        {
            draw.AddLine(center + new Vector2(-7f, 5f),
                center + new Vector2(5f, 5f), color, 2f);
            draw.AddLine(center + new Vector2(-2f, -7f),
                center + new Vector2(-2f, 4f), color, 3f);
            draw.AddLine(center + new Vector2(-2f, 1f),
                center + new Vector2(6f, 4f), color, 3f);
            draw.AddLine(center + new Vector2(-6f, -4f),
                center + new Vector2(-2f, -1f), color, 1.4f);
            return;
        }

        if (Contains(stat, "Armour") || Contains(stat, "Suppression") ||
            Contains(stat, "Physical Damage Reduction") ||
            Contains(stat, "Defences") || Contains(stat, "Block") ||
            Contains(stat, "Stun"))
        {
            var shield = new[]
            {
                center + new Vector2(0f, -8f), center + new Vector2(6f, -5f),
                center + new Vector2(5f, 3f), center + new Vector2(0f, 8f),
                center + new Vector2(-5f, 3f), center + new Vector2(-6f, -5f)
            };
            draw.AddPolyline(ref shield[0], shield.Length, color,
                ImGuiNET.ImDrawFlags.Closed, 1.7f);
            if (Contains(stat, "Suppression"))
                draw.AddLine(center + new Vector2(-4f, 4f),
                    center + new Vector2(4f, -4f), color, 1.5f);
            return;
        }

        if (Contains(stat, "Evasion"))
        {
            draw.AddLine(center + new Vector2(-7f, 6f),
                center + new Vector2(6f, -6f), color, 2f);
            draw.AddLine(center + new Vector2(-2f, 5f),
                center + new Vector2(6f, -6f), color, 1.3f);
            draw.AddLine(center + new Vector2(1f, 1f),
                center + new Vector2(7f, 0f), color, 1.3f);
            return;
        }

        if (Contains(stat, "Energy Shield") || Contains(stat, "Gem"))
        {
            var diamond = new[]
            {
                center + new Vector2(0f, -8f), center + new Vector2(7f, 0f),
                center + new Vector2(0f, 8f), center + new Vector2(-7f, 0f)
            };
            draw.AddPolyline(ref diamond[0], diamond.Length, color,
                ImGuiNET.ImDrawFlags.Closed, 1.7f);
            draw.AddCircleFilled(center, 2.3f, color);
            return;
        }

        if (Contains(stat, "Accuracy") || Contains(stat, "Critical") ||
            Contains(stat, "Crit "))
        {
            draw.AddCircle(center, 6.5f, color, 12, 1.5f);
            draw.AddCircleFilled(center, 2.2f, color);
            draw.AddLine(center + new Vector2(-9f, 0f),
                center + new Vector2(-5f, 0f), color, 1.2f);
            draw.AddLine(center + new Vector2(5f, 0f),
                center + new Vector2(9f, 0f), color, 1.2f);
            return;
        }

        if (Contains(stat, "Cooldown"))
        {
            draw.AddCircle(center, 7f, color, 12, 1.5f);
            draw.AddLine(center, center + new Vector2(0f, -5f), color, 1.5f);
            draw.AddLine(center, center + new Vector2(4f, 2f), color, 1.5f);
            return;
        }

        if (Contains(stat, "Attack Speed") || Contains(stat, "DPS") ||
            Contains(stat, "Physical Damage") || Contains(stat, "Elemental Damage") ||
            Contains(stat, "Chaos Damage") || Contains(stat, "Attack Damage") ||
            Contains(stat, "Melee Damage") || Contains(stat, "Projectile Damage"))
        {
            draw.AddLine(center + new Vector2(-6f, 7f),
                center + new Vector2(6f, -7f), color, 2f);
            draw.AddLine(center + new Vector2(-6f, -7f),
                center + new Vector2(6f, 7f), color, 1.5f);
            return;
        }

        if (Contains(stat, "Mana") || Contains(stat, "Spell") ||
            Contains(stat, "Cast Speed"))
        {
            draw.AddCircleFilled(center, 5f, color);
            draw.AddLine(center + new Vector2(0f, -8f),
                center + new Vector2(0f, 8f), color, 1.2f);
            draw.AddLine(center + new Vector2(-8f, 0f),
                center + new Vector2(8f, 0f), color, 1.2f);
            return;
        }

        if (Contains(stat, "DoT") || Contains(stat, "Damage over Time"))
        {
            draw.AddTriangleFilled(center + new Vector2(0f, -8f),
                center + new Vector2(6f, 3f), center + new Vector2(-6f, 3f), color);
            draw.AddCircleFilled(center + new Vector2(0f, 3f), 5f, color);
            return;
        }

        if (Contains(stat, "Attribute") || Contains(stat, "Strength") ||
            Contains(stat, "Dexterity") || Contains(stat, "Intelligence"))
        {
            draw.AddCircleFilled(center + new Vector2(0f, -5f), 2.8f, color);
            draw.AddCircleFilled(center + new Vector2(-5f, 4f), 2.8f, color);
            draw.AddCircleFilled(center + new Vector2(5f, 4f), 2.8f, color);
            draw.AddLine(center + new Vector2(0f, -3f),
                center + new Vector2(-4f, 2f), color, 1.3f);
            draw.AddLine(center + new Vector2(0f, -3f),
                center + new Vector2(4f, 2f), color, 1.3f);
            return;
        }

        var points = new[]
        {
            center + new Vector2(0f, -6f), center + new Vector2(6f, 0f),
            center + new Vector2(0f, 6f), center + new Vector2(-6f, 0f)
        };
        draw.AddPolyline(ref points[0], points.Length, color,
            ImGuiNET.ImDrawFlags.Closed, 1.5f);
    }


    private static void DrawFivePointStar(ImGuiNET.ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var points = new Vector2[10];
        const double start = -Math.PI / 2.0;

        for (var i = 0; i < 10; i++)
        {
            var r = (i % 2 == 0) ? radius : radius * 0.45f;
            var a = start + i * Math.PI / 5.0;
            points[i] = new Vector2(
                center.X + (float)Math.Cos(a) * r,
                center.Y + (float)Math.Sin(a) * r);
        }

        draw.AddConvexPolyFilled(ref points[0], points.Length, color);
    }

    private static void DrawFivePointStarOutline(
        ImGuiNET.ImDrawListPtr draw, Vector2 center, float radius, uint color)
    {
        var points = new Vector2[10];
        const double start = -Math.PI / 2.0;

        for (var i = 0; i < 10; i++)
        {
            var r = (i % 2 == 0) ? radius : radius * 0.45f;
            var a = start + i * Math.PI / 5.0;
            points[i] = new Vector2(
                center.X + (float)Math.Cos(a) * r,
                center.Y + (float)Math.Sin(a) * r);
        }

        for (var i = 0; i < 10; i++)
        {
            var next = (i + 1) % 10;
            draw.AddLine(points[i], points[next], color, 1.1f);
        }
    }

    private uint GetTierTagColor(string tag)
    {
        if (string.Equals(tag, "[T1]", StringComparison.OrdinalIgnoreCase))
            return ToImGuiColor(new Color(245, 205, 70, 255));

        return ToImGuiColor(new Color(175, 175, 185, 255));
    }

    private static bool IsRatingDetail(string text)
    {
        return Contains(text, "Life:") ||
               Contains(text, "Fire Resistance:") ||
               Contains(text, "Cold Resistance:") ||
               Contains(text, "Lightning Resistance:") ||
               Contains(text, "Chaos Resistance:") ||
               Contains(text, "All Resistances:");
    }

    private void DrawStatIcon(ImGuiNET.ImDrawListPtr draw, Vector2 center, string text)
    {
        var color = GetItemInfoTextColor(text, ToImGuiColor(SharpDX.Color.White));
        var radius = 5.5f;

        if (Contains(text, "Life:"))
        {
            // Simple heart-like diamond/chevron built from filled geometry.
            draw.AddCircleFilled(new Vector2(center.X - 3f, center.Y - 1f), radius * .65f, color);
            draw.AddCircleFilled(new Vector2(center.X + 3f, center.Y - 1f), radius * .65f, color);
            draw.AddTriangleFilled(
                new Vector2(center.X - 7f, center.Y),
                new Vector2(center.X + 7f, center.Y),
                new Vector2(center.X, center.Y + 8f), color);
            return;
        }

        if (Contains(text, "Cold Resistance:"))
        {
            for (var i = 0; i < 6; i++)
            {
                var a = i * (float)Math.PI / 3f;
                var dx = (float)Math.Cos(a) * 7f;
                var dy = (float)Math.Sin(a) * 7f;
                draw.AddLine(center, new Vector2(center.X + dx, center.Y + dy), color, 2f);
            }
            return;
        }

        if (Contains(text, "Lightning Resistance:"))
        {
            var p1 = new Vector2(center.X + 2f, center.Y - 8f);
            var p2 = new Vector2(center.X - 5f, center.Y + 1f);
            var p3 = new Vector2(center.X, center.Y + 1f);
            var p4 = new Vector2(center.X - 2f, center.Y + 8f);
            draw.AddLine(p1, p2, color, 2.5f);
            draw.AddLine(p2, p3, color, 2.5f);
            draw.AddLine(p3, p4, color, 2.5f);
            return;
        }

        if (Contains(text, "Fire Resistance:"))
        {
            draw.AddCircleFilled(center, 6f, color);
            draw.AddTriangleFilled(
                new Vector2(center.X - 5f, center.Y + 3f),
                new Vector2(center.X + 5f, center.Y + 3f),
                new Vector2(center.X, center.Y - 8f), color);
            return;
        }

        // Chaos / all-res fallback icon.
        draw.AddCircle(center, 6f, color, 12, 2f);
        draw.AddCircle(center, 2.5f, color, 8, 2f);
    }


    /// <summary>
    /// Color-codes the Life/Resistance stats in the Item Info overlay while
    /// leaving unrelated modifiers white. Life is intentionally red so it is
    /// visually distinct from Fire Resistance, which uses orange.
    ///
    /// Colors:
    ///   Life        = red
    ///   Fire        = orange
    ///   Cold        = blue
    ///   Lightning   = yellow
    ///   Chaos       = purple
    /// </summary>
    private uint GetItemInfoTextColor(string text, uint fallback)
    {
        return fallback;
    }


    private (int Prefixes, int Suffixes, int Unknown) GetAffixCounts(Mods mods)
    {
        if (mods?.ItemMods == null)
            return (0, 0, 0);

        var prefixes = 0;
        var suffixes = 0;
        var unknown = 0;

        foreach (var mod in mods.ItemMods)
        {
            if (mod == null || string.IsNullOrEmpty(mod.RawName))
                continue;

            try
            {
                // Authoritative ExileCore data path used by AdvancedTooltipPlus:
                // ItemMod.RawName -> GameController.Files.Mods.records[RawName]
                // -> ModsDat.ModRecord.AffixType.
                ModsDat.ModRecord record = GameController.Files.Mods.records[mod.RawName];

                if (record == null)
                {
                    unknown++;
                    continue;
                }

                if (record.AffixType == ModType.Prefix)
                    prefixes++;
                else if (record.AffixType == ModType.Suffix)
                    suffixes++;
            }
            catch
            {
                unknown++;
            }
        }

        return (prefixes, suffixes, unknown);
    }


    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
private bool IsItemInfoPopupVisible()
    {
        if (Settings.AlwaysShowItemInfo)
            return true;

        var key = (int)Settings.ItemInfoHotkey.Value;
        if (key == 0)
            return false;

        return (GetAsyncKeyState(key & 0xFF) & 0x8000) != 0;
    }

    private bool IsCraftedMod(ItemMod mod)
    {
        if (mod == null)
            return false;

        try
        {
            // Different ExileCore versions expose crafted state differently,
            // so check the common boolean/property names without taking a
            // compile-time dependency on one particular version.
            foreach (var name in new[] { "IsCrafted", "Crafted", "IsCraftedMod" })
            {
                var value = GetMemberObject(mod, name);
                if (value is bool b && b)
                    return true;
            }

            // Some versions expose the source/type as a string or enum.
            foreach (var name in new[] { "Source", "ModSource", "Type", "ModType" })
            {
                var value = GetMemberObject(mod, name);
                if (value != null &&
                    value.ToString().IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Final data-driven fallback: crafted mod IDs in PoE's mod data
            // commonly carry the crafted marker in their raw key/name.
            var raw = mod.RawName ?? string.Empty;
            if (raw.IndexOf("crafted", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            object record = GetMemberObject(mod, "ModRecord");
            var recordDomain = GetMemberString(record, "Domain", "ModDomain", "Source");
            if (!string.IsNullOrEmpty(recordDomain) &&
                recordDomain.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // The user's ExileAPI build does not expose the crafted flag on
            // ItemMod, but the authoritative ModsDat record still carries its
            // Crafted domain. Resolve RawName through the same table used for
            // affix and tier detection.
            if (!string.IsNullOrWhiteSpace(mod.RawName))
            {
                try
                {
                    var dataRecord = GameController.Files.Mods.records[mod.RawName];
                    if (dataRecord != null)
                    {
                        record = dataRecord;
                        recordDomain = GetMemberString(dataRecord,
                            "Domain", "ModDomain", "Source");
                        if (!string.IsNullOrEmpty(recordDomain) &&
                            recordDomain.IndexOf("craft",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
                catch
                {
                }
            }

            var recordName = GetMemberString(record, "Key", "Name", "Id", "RawName");
            if (!string.IsNullOrEmpty(recordName) &&
                recordName.IndexOf("crafted", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Resolves the real PoE affix tier using the same ModsDat.recordsByTier
    /// path used by AdvancedTooltipPlus. The current mod record is matched
    /// against the tier records for its Group + AffixType, with the item's
    /// base-item tags and item level used to filter eligible tiers.
    /// </summary>
    private (int Tier, int TotalTiers) GetAuthoritativeModTier(Entity item, Mods mods, ItemMod mod)
    {
        if (item == null || mods == null || mod == null || string.IsNullOrEmpty(mod.RawName))
            return (0, 0);

        try
        {
            var record = GameController.Files.Mods.records[mod.RawName];
            if (record == null)
                return (0, 0);

            if (record.AffixType != ModType.Prefix && record.AffixType != ModType.Suffix)
                return (0, 0);

            // Crafted/implicit mods are not normal affix tiers.
            if (mods.ImplicitMods != null &&
                mods.ImplicitMods.Any(x => x != null && x.RawName == mod.RawName))
                return (0, 0);

            var cacheKey = $"{item.Path}\u001f{mod.RawName}";
            if (_modTierCache.TryGetValue(cacheKey, out var cachedTier))
                return cachedTier;

            (int Tier, int TotalTiers) Cache((int Tier, int TotalTiers) value)
            {
                if (_modTierCache.Count > 4096)
                    _modTierCache.Clear();

                _modTierCache[cacheKey] = value;
                return value;
            }

            var baseItem = GameController.Files.BaseItemTypes.Translate(item.Path);
            if (baseItem == null)
                return Cache(ParseRecordTier(record));

            if (!GameController.Files.Mods.recordsByTier.TryGetValue(
                    Tuple.Create(record.Group, record.AffixType), out var allTiers))
                return Cache(ParseRecordTier(record));

            var recordLetters = new string(record.Key.Where(char.IsLetter).ToArray());
            var totalTiers = 0;
            var tier = 0;

            foreach (var candidate in allTiers)
            {
                if (candidate == null)
                    continue;

                if (!candidate.Key.StartsWith(recordLetters, StringComparison.Ordinal))
                    continue;

                var candidateLetters = new string(candidate.Key.Where(char.IsLetter).ToArray());
                if (!candidateLetters.SequenceEqual(recordLetters))
                    continue;

                if (!IsTierCandidateEligible(candidate, baseItem))
                    continue;

                totalTiers++;

                if (candidate.Equals(record))
                    tier = totalTiers;
            }

            // Some records expose their tier directly; use that as a safe
            // fallback if recordsByTier could not resolve the ordinal.
            if (tier <= 0)
            {
                var parsed = ParseRecordTier(record);
                if (parsed.Tier > 0)
                    return Cache((parsed.Tier, Math.Max(parsed.TotalTiers, totalTiers)));
            }

            return Cache((tier, totalTiers));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool IsTierCandidateEligible(ModsDat.ModRecord candidate, BaseItemType baseItem)
    {
        if (candidate == null || baseItem == null || candidate.TagChances == null)
            return false;

        var baseChance = -1;
        var defaultChance = 0;
        var tagChance = -1;
        var moreTagChance = -1;

        var classTag = (baseItem.ClassName ?? string.Empty)
            .ToLowerInvariant()
            .Replace(' ', '_');
        if (!string.IsNullOrEmpty(classTag) &&
            candidate.TagChances.TryGetValue(classTag, out var bc))
            baseChance = bc;

        if (candidate.TagChances.TryGetValue("default", out var dc))
            defaultChance = dc;

        foreach (var tag in baseItem.Tags ?? Enumerable.Empty<string>())
        {
            if (candidate.TagChances.TryGetValue(tag, out var chance))
                tagChance = chance;
        }

        foreach (var tag in baseItem.MoreTagsFromPath ?? Enumerable.Empty<string>())
        {
            if (candidate.TagChances.TryGetValue(tag, out var chance))
                moreTagChance = chance;
        }

        return baseChance > 0 ||
               (baseChance == -1 && tagChance > 0) ||
               (baseChance == -1 && tagChance == -1 && moreTagChance > 0) ||
               (baseChance == -1 && tagChance == -1 && moreTagChance == -1 && defaultChance > 0);
    }

    private static (int Tier, int TotalTiers) ParseRecordTier(ModsDat.ModRecord record)
    {
        if (record == null || string.IsNullOrEmpty(record.Tier))
            return (0, 0);

        var digits = new string(record.Tier.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var tier))
            return (tier, tier > 0 ? Math.Max(tier, 1) : 0);

        return (0, 0);
    }

#if false
    // Retired V44 slider-tier reference implementation. Kept disabled for one
    // release so the stable per-item analyzer tier path remains easy to compare.
    private (string Label, string Range, string Tooltip) GetSliderTierHint(
        string slotName, string statName, int threshold)
    {
        if (threshold <= 0)
            return ("OFF", string.Empty,
                "This stat is disabled and does not count toward the star rating.");

        if (IsLocalDefenseSlider(slotName, statName))
        {
            return ("TOTAL", string.Empty,
                "This threshold uses the item's final local defence: base value + quality + flat local modifiers + local percentage modifiers. It does not correspond to one affix tier.");
        }

        if (statName == "Weapon DPS")
        {
            return ("TOTAL", string.Empty,
                "Weapon DPS is calculated from the weapon's base damage, quality, local damage modifiers, and attack speed. It does not correspond to one affix tier.");
        }

        var cacheKey = $"{slotName}\u001f{statName}\u001f{threshold}";
        if (_sliderTierHintCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var familyRecord = GetCanonicalSliderTierRecord(slotName, statName);
        if (familyRecord == null)
        {
            var unavailable = ("N/A", string.Empty,
                $"No ordinary explicit {slotName} affix family could be resolved for this stat.");
            _sliderTierHintCache[cacheKey] = unavailable;
            return unavailable;
        }

        var thresholdTier = GetThresholdTierInfo(slotName, familyRecord, statName, threshold);

        string label;
        string rangeText;
        string explanation;

        if (thresholdTier.Tier > 0)
        {
            label = $"T{thresholdTier.Tier}+";
            rangeText = FormatTierRange(statName, thresholdTier.Minimum, thresholdTier.Maximum);
            explanation = $"A T{thresholdTier.Tier} or better roll can reach the configured threshold of {threshold}.";
            if (!string.IsNullOrEmpty(rangeText))
                explanation += $"\nT{thresholdTier.Tier} roll range: {rangeText}.";
        }
        else if (thresholdTier.Tier < 0)
        {
            label = "ABOVE T1";
            rangeText = string.Empty;
            explanation = $"The configured threshold of {threshold} is above the normal T1 range for this affix.";
        }
        else
        {
            var unavailable = ("N/A", string.Empty,
                "A normal affix family was found, but its tier ranges could not be resolved for this base type.");
            _sliderTierHintCache[cacheKey] = unavailable;
            return unavailable;
        }

        var tooltip = $"Standard {slotName} affix reference\n{explanation}";

        var result = (label, rangeText, tooltip);
        if (_sliderTierHintCache.Count > 1024)
            _sliderTierHintCache.Clear();
        _sliderTierHintCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Finds the ordinary explicit affix family that represents a settings
    /// slider from the complete modifier pool for that equipment tab. No live
    /// or hovered item is required.
    /// </summary>
    private ModsDat.ModRecord GetCanonicalSliderTierRecord(string slotName, string statName)
    {
        if (string.IsNullOrWhiteSpace(slotName) ||
            string.IsNullOrWhiteSpace(statName))
            return null;

        var cacheKey = $"{slotName}\u001f{statName}";
        if (_sliderTierFamilyCache.TryGetValue(cacheKey, out var cached))
            return cached;

        BuildCanonicalSliderTierCache(slotName);
        return _sliderTierFamilyCache.TryGetValue(cacheKey, out cached)
            ? cached
            : null;
    }

    /// <summary>
    /// Builds every slider-family reference for one settings slot in a single
    /// database pass. Opening a settings tab therefore performs one cached
    /// lookup rather than rescanning the full modifier database per slider.
    /// </summary>
    private void BuildCanonicalSliderTierCache(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName) ||
            _sliderTierSlotsBuilt.Contains(slotName))
            return;

        if (_sliderTierFamilyCache.Count > 4096)
        {
            _sliderTierFamilyCache.Clear();
            _sliderTierSlotsBuilt.Clear();
        }

        try
        {
            if (!SliderTierSlotTags.ContainsKey(slotName))
            {
                _sliderTierSlotsBuilt.Add(slotName);
                foreach (var statName in SliderTierReferenceStats)
                    _sliderTierFamilyCache[$"{slotName}\u001f{statName}"] = null;
                return;
            }

            // ExileAPI can draw the settings window while its game-data files
            // are still loading. Leave this slot uncached in that brief state
            // so the next frame can resolve the tier references normally.
            if (GameController.Files?.Mods?.recordsByTier == null ||
                !GameController.Files.Mods.recordsByTier.Any())
                return;

            _sliderTierSlotsBuilt.Add(slotName);

            var bestRecords = new Dictionary<string, ModsDat.ModRecord>(StringComparer.Ordinal);
            var bestScores = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var statName in SliderTierReferenceStats)
            {
                bestRecords[statName] = null;
                bestScores[statName] = int.MinValue;
            }

            foreach (var tierGroup in GameController.Files.Mods.recordsByTier)
            {
                if (tierGroup.Value == null)
                    continue;

                var seenFamilies = new HashSet<string>(StringComparer.Ordinal);

                foreach (var seed in tierGroup.Value)
                {
                    if (seed == null ||
                        (seed.AffixType != ModType.Prefix && seed.AffixType != ModType.Suffix))
                        continue;

                    var recordLetters = GetTierFamilyLetters(seed);
                    if (string.IsNullOrEmpty(recordLetters))
                        continue;

                    var familyKey = $"{seed.Group}\u001f{seed.AffixType}\u001f{recordLetters}";
                    if (!seenFamilies.Add(familyKey))
                        continue;

                    var eligibleCandidates = tierGroup.Value.Where(candidate =>
                        candidate != null &&
                        string.Equals(GetTierFamilyLetters(candidate), recordLetters,
                            StringComparison.Ordinal) &&
                        IsTierCandidateEligibleForSlot(candidate, slotName) &&
                        IsOrdinaryExplicitTierRecord(candidate)).ToList();

                    if (eligibleCandidates.Count == 0)
                        continue;

                    foreach (var statName in SliderTierReferenceStats)
                    {
                        ModsDat.ModRecord representative = null;
                        var matchingTiers = 0;
                        var fewestStats = int.MaxValue;
                        var bestKeyScore = -1;

                        foreach (var candidate in eligibleCandidates)
                        {
                            if (FindCanonicalConfiguredStatIndex(candidate, statName) < 0)
                                continue;

                            matchingTiers++;
                            var keyScore = GetCanonicalStatKeyScore(candidate, statName);
                            var statCount = candidate.StatNames == null
                                ? int.MaxValue
                                : candidate.StatNames.Count();

                            if (representative == null ||
                                keyScore > bestKeyScore ||
                                (keyScore == bestKeyScore && statCount < fewestStats))
                            {
                                representative = candidate;
                                fewestStats = statCount;
                                bestKeyScore = keyScore;
                            }
                        }

                        if (representative == null || matchingTiers == 0)
                            continue;

                        // Normal PoE affixes generally have a full tier ladder.
                        // Prefer that over a one-off special family, then prefer
                        // a clean single-stat family over hybrids sharing a group.
                        // An exact PoE stat key is the strongest signal. This
                        // prevents conditional, maximum-resistance, jewel, or
                        // hybrid families with similar translated wording from
                        // beating the ordinary equipment affix ladder.
                        var score = bestKeyScore * 1000000 +
                                    matchingTiers * 1000 +
                                    eligibleCandidates.Count * 100;
                        if (fewestStats == 1)
                            score += 50000;
                        else if (fewestStats != int.MaxValue)
                            score -= Math.Max(0, fewestStats - 1) * 5000;

                        score += GetCanonicalFamilyNameScore(representative, statName);

                        if (score <= bestScores[statName])
                            continue;

                        bestScores[statName] = score;
                        bestRecords[statName] = representative;
                    }
                }
            }

            foreach (var statName in SliderTierReferenceStats)
                _sliderTierFamilyCache[$"{slotName}\u001f{statName}"] = bestRecords[statName];
        }
        catch
        {
            // Cache misses too. A malformed/unsupported data record should not
            // cause a full database rescan on every rendered settings frame.
            foreach (var statName in SliderTierReferenceStats)
                _sliderTierFamilyCache[$"{slotName}\u001f{statName}"] = null;
        }
    }

    private static string GetTierFamilyLetters(ModsDat.ModRecord record)
    {
        return record == null || string.IsNullOrEmpty(record.Key)
            ? string.Empty
            : new string(record.Key.Where(char.IsLetter).ToArray());
    }

    private static bool IsOrdinaryExplicitTierRecord(ModsDat.ModRecord record)
    {
        if (record == null ||
            (record.AffixType != ModType.Prefix && record.AffixType != ModType.Suffix))
            return false;

        try
        {
            var descriptor = GetModRecordDescriptor(record);
            foreach (var marker in new[]
                     {
                         "crafted", "master", "essence", "delve", "incursion", "temple",
                         "veiled", "chosen", "shaper", "elder", "crusader", "redeemer",
                         "warlord", "hunter", "synthesis", "implicit", "corrupt",
                         "enchant", "eldritch", "exarch", "eater of worlds"
                     })
            {
                if (descriptor.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindCanonicalConfiguredStatIndex(
        ModsDat.ModRecord record, string statName)
    {
        if (record?.StatNames == null)
            return -1;

        // Prefer the exact internal PoE stat id before considering translated
        // wording. A hybrid record can contain both maximum Fire Resistance
        // and ordinary Fire Resistance; returning the first broad text match
        // would read the tiny maximum-resistance range for the normal slider.
        if (CanonicalSliderStatKeys.TryGetValue(statName, out var expectedKeys))
        {
            var exactIndex = 0;
            foreach (var stat in record.StatNames)
            {
                var key = GetMemberString(stat, "Key");
                if (expectedKeys.Any(expected =>
                        string.Equals(key, expected, StringComparison.OrdinalIgnoreCase)))
                    return exactIndex;

                exactIndex++;
            }
        }

        var index = 0;
        foreach (var stat in record.StatNames)
        {
            if (stat != null)
            {
                var text = $"{GetMemberString(stat, "Key", "Name")} {stat}";
                if (CanonicalStatTextMatches(text, statName))
                    return index;
            }

            index++;
        }

        return -1;
    }

    private static int GetCanonicalStatKeyScore(
        ModsDat.ModRecord record, string statName)
    {
        if (record?.StatNames == null ||
            !CanonicalSliderStatKeys.TryGetValue(statName, out var expectedKeys))
            return 0;

        foreach (var stat in record.StatNames)
        {
            var key = GetMemberString(stat, "Key");
            if (expectedKeys.Any(expected =>
                    string.Equals(key, expected, StringComparison.OrdinalIgnoreCase)))
                return 2;
        }

        return 0;
    }

    private static bool CanonicalStatTextMatches(string text, string statName)
    {
        if (!TextMatchesConfiguredStat(text, statName))
            return false;

        text ??= string.Empty;

        switch (statName)
        {
            case "Life":
                return !Contains(text, "Percent") && !Contains(text, "Regener") &&
                       !Contains(text, "Pct") && !Contains(text, "%") &&
                       !Contains(text, "Increased") && !Contains(text, "Leech") &&
                       !Contains(text, "Minion");

            case "Cold Resistance":
            case "Fire Resistance":
            case "Lightning Resistance":
            case "Chaos Resistance":
                return !Contains(text, "Maximum") && !Contains(text, "Penetrat") &&
                       !Contains(text, "Against");

            case "Attributes":
                var attributeCount = (Contains(text, "Strength") ? 1 : 0) +
                                     (Contains(text, "Dexterity") ? 1 : 0) +
                                     (Contains(text, "Intelligence") ? 1 : 0);
                return attributeCount == 1 && !Contains(text, "Percent") &&
                       !Contains(text, "Pct") && !Contains(text, "%") &&
                       !Contains(text, "Requirement");

            case "Movement Speed":
                return !Contains(text, "Action Speed") && !Contains(text, "Recently") &&
                       !Contains(text, "While") && !Contains(text, "If");

            case "Spell Suppression":
                return !Contains(text, "Recently") && !Contains(text, "While") &&
                       !Contains(text, "If");

            default:
                return true;
        }
    }

    private static int GetCanonicalFamilyNameScore(
        ModsDat.ModRecord record, string statName)
    {
        var descriptor = GetModRecordDescriptor(record);
        var score = 0;

        if (statName == "Life" && Contains(descriptor, "MaximumLife")) score += 100;
        if (statName == "Cold Resistance" && Contains(descriptor, "Cold")) score += 50;
        if (statName == "Fire Resistance" && Contains(descriptor, "Fire")) score += 50;
        if (statName == "Lightning Resistance" && Contains(descriptor, "Lightning")) score += 50;
        if (statName == "Chaos Resistance" && Contains(descriptor, "Chaos")) score += 50;
        if (statName == "Movement Speed" && Contains(descriptor, "Movement")) score += 75;
        if (statName == "Spell Suppression" && Contains(descriptor, "Suppress")) score += 75;

        return score;
    }

    private static string GetModRecordDescriptor(ModsDat.ModRecord record)
    {
        if (record == null)
            return string.Empty;

        var statText = record.StatNames == null
            ? string.Empty
            : string.Join(" ", record.StatNames.Select(stat =>
                stat == null ? string.Empty : $"{GetMemberString(stat, "Key", "Name")} {stat}"));

        return string.Join(" ", new[]
        {
            record.Key,
            record.Group,
            record.Tier,
            GetMemberString(record, "Name"),
            GetMemberString(record, "UserFriendlyName"),
            GetMemberString(record, "Domain"),
            GetMemberString(record, "GenerationType"),
            GetMemberString(record, "Source"),
            statText
        });
    }

    private (int Tier, int Minimum, int Maximum) GetThresholdTierInfo(
        string slotName, ModsDat.ModRecord familyRecord, string statName, int threshold)
    {
        try
        {
            if (familyRecord == null ||
                (familyRecord.AffixType != ModType.Prefix && familyRecord.AffixType != ModType.Suffix))
                return (0, 0, 0);

            if (!GameController.Files.Mods.recordsByTier.TryGetValue(
                    Tuple.Create(familyRecord.Group, familyRecord.AffixType), out var allTiers))
                return (0, 0, 0);

            var recordLetters = GetTierFamilyLetters(familyRecord);
            var sourceStatIndex = FindCanonicalConfiguredStatIndex(familyRecord, statName);
            var tierNumber = 0;
            var thresholdTier = 0;
            var thresholdMinimum = 0;
            var thresholdMaximum = 0;
            var sawRange = false;

            foreach (var candidate in allTiers)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.Key) ||
                    !candidate.Key.StartsWith(recordLetters, StringComparison.Ordinal))
                    continue;

                var candidateLetters = new string(candidate.Key.Where(char.IsLetter).ToArray());
                if (!candidateLetters.SequenceEqual(recordLetters) ||
                    !IsTierCandidateEligibleForSlot(candidate, slotName) ||
                    !IsOrdinaryExplicitTierRecord(candidate))
                    continue;

                tierNumber++;

                var statIndex = FindCanonicalConfiguredStatIndex(candidate, statName);
                if (statIndex < 0)
                    statIndex = sourceStatIndex;

                if (candidate.StatRange == null)
                    continue;

                var ranges = candidate.StatRange.ToArray();
                if (statIndex < 0 || statIndex >= ranges.Length)
                    continue;

                var range = ranges[statIndex];
                var minimum = Math.Min(range.Min, range.Max);
                var maximum = Math.Max(range.Min, range.Max);
                sawRange = true;

                // recordsByTier is already in PoE tier order. Keep updating
                // the answer so the badge represents the lowest tier whose
                // best roll can still meet the user's minimum threshold.
                if (maximum >= threshold)
                {
                    thresholdTier = tierNumber;
                    thresholdMinimum = minimum;
                    thresholdMaximum = maximum;
                }
            }

            if (thresholdTier > 0)
                return (thresholdTier, thresholdMinimum, thresholdMaximum);

            return sawRange && tierNumber > 0 ? (-1, 0, 0) : (0, 0, 0);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static bool IsLocalDefenseSlider(string slotName, string statName)
    {
        if (statName != "Armour" && statName != "Evasion" && statName != "Energy Shield")
            return false;

        return slotName == "Helmet" || slotName == "Body Armour" ||
               slotName == "Gloves" || slotName == "Boots" || slotName == "Shield";
    }

    private static string FormatTierRange(string statName, int minimum, int maximum)
    {
        var suffix = IsPercentageStat(statName) ? "%" : string.Empty;
        return minimum == maximum
            ? $"{minimum}{suffix}"
            : $"{minimum}-{maximum}{suffix}";
    }

#endif

    private static int FindConfiguredStatIndex(ModsDat.ModRecord record, string statName)
    {
        if (record?.StatNames == null)
            return -1;

        var i = 0;
        foreach (var stat in record.StatNames)
        {
            if (stat == null)
            {
                i++;
                continue;
            }

            var text = $"{GetMemberString(stat, "Key", "Name")} {stat}";
            if (TextMatchesConfiguredStat(text, statName))
                return i;

            i++;
        }

        return -1;
    }

    private static bool ModMatchesConfiguredStat(ItemMod mod, string statName)
    {
        if (mod == null)
            return false;

        var values = GetMemberValues(mod);
        var rawName = GetMemberString(mod, "RawName");
        var text = string.Join(" ", new[]
        {
            rawName,
            GetMemberString(mod, "Name", "DisplayName", "Group"),
            GetReadableModText(mod, rawName, values)
        });

        return TextMatchesConfiguredStat(text, statName);
    }

    private static bool TextMatchesConfiguredStat(string text, string statName)
    {
        text ??= string.Empty;
        var compactText = new string(text.Where(char.IsLetterOrDigit).ToArray());
        var compactStatName = new string((statName ?? string.Empty)
            .Where(char.IsLetterOrDigit).ToArray());

        switch (statName)
        {
            case "Life":
                return (Contains(text, "Maximum Life") || Contains(compactText, "MaximumLife") || Contains(compactText, "MaxLife")) &&
                       !Contains(text, "Increased Maximum Life") && !Contains(text, "Regeneration");
            case "Cold Resistance": return Contains(text, "Cold") && Contains(text, "Resist");
            case "Fire Resistance": return Contains(text, "Fire") && Contains(text, "Resist");
            case "Lightning Resistance": return Contains(text, "Lightning") && Contains(text, "Resist");
            case "Chaos Resistance": return Contains(text, "Chaos") && Contains(text, "Resist");
            case "Energy Shield":
                return (Contains(text, "Energy Shield") || Contains(compactText, "EnergyShield")) && !Contains(text, "Increased") &&
                       !Contains(text, "Recharge") && !Contains(text, "Faster");
            case "Spell Suppression": return Contains(text, "Spell") && Contains(text, "Suppress");
            case "Attributes":
                return Contains(text, "Attribute") || Contains(text, "Strength") ||
                       Contains(text, "Dexterity") || Contains(text, "Intelligence");
            case "Attack Speed": return Contains(text, "Attack") && Contains(text, "Speed");
            case "Cast Speed": return Contains(text, "Cast") && Contains(text, "Speed");
            case "Crit Multiplier": return Contains(text, "Critical") && Contains(text, "Multiplier");
            case "Critical Strike Chance":
                return Contains(text, "Critical") && Contains(text, "Chance") && !Contains(text, "Multiplier");
            case "Movement Speed": return Contains(text, "Movement") && Contains(text, "Speed");
            case "Mana": return Contains(text, "Maximum Mana") || Contains(compactText, "MaximumMana");
            case "Gem Levels": return Contains(text, "Gem") && Contains(text, "Level");
            case "Specific Gem Levels": return Contains(text, "Gem") && Contains(text, "Level");
            case "Accuracy": return Contains(text, "Accuracy");
            case "Cooldown Recovery": return Contains(text, "Cooldown") && Contains(text, "Recovery");
            case "DoT Multiplier":
                return (Contains(text, "Damage over Time") || Contains(compactText, "DamageOverTime")) &&
                       Contains(text, "Multiplier");
            default:
                return Contains(text, statName) ||
                       (!string.IsNullOrEmpty(compactStatName) && Contains(compactText, compactStatName));
        }
    }

    private static string GetTierPreviewSlotName(string path)
    {
        var p = (path ?? string.Empty).ToLowerInvariant();
        if (p.Contains("helmet") || p.Contains("helm") || p.Contains("circlet")) return "Helmet";
        if (p.Contains("boot")) return "Boots";
        if (p.Contains("glove")) return "Gloves";
        if (p.Contains("belt")) return "Belt";
        if (p.Contains("bodyarmour") || p.Contains("bodyarmours") || p.Contains("body_armour") || p.Contains("chest"))
            return "Body Armour";
        if (p.Contains("ring")) return "Ring";
        if (p.Contains("amulet")) return "Amulet";
        if (p.Contains("shield") || p.Contains("buckler")) return "Shield";
        if (p.Contains("quiver")) return "Quiver";
        if (IsBow(path)) return "Bow";
        if (IsCasterWeapon(path)) return "Caster Weapon";
        if (IsWeaponLike(path)) return "Melee Weapon";
        return string.Empty;
    }

    private UniquePerfectionResult CalculateUniquePerfection(Mods mods)
    {
        var result = new UniquePerfectionResult();
        if (mods?.ItemRarity != ItemRarity.Unique || mods.ItemMods == null)
            return result;

        double weightedTotal = 0;

        foreach (var mod in mods.ItemMods)
        {
            if (!TryGetUniqueModPerfection(mod, out var modPercentage,
                    out var variableRolls))
                continue;

            weightedTotal += modPercentage * variableRolls;
            result.VariableRollCount += variableRolls;
            result.VariableModCount++;
        }

        if (result.VariableRollCount > 0)
        {
            result.Percentage = Math.Clamp((int)Math.Round(
                weightedTotal / result.VariableRollCount,
                MidpointRounding.AwayFromZero), 0, 100);
        }

        return result;
    }

    private bool TryGetUniqueModPerfection(ItemMod mod, out double percentage,
        out int variableRolls)
    {
        percentage = 0;
        variableRolls = 0;

        if (mod == null || string.IsNullOrWhiteSpace(mod.RawName) || mod.Values == null)
            return false;

        try
        {
            var record = GameController.Files.Mods.records[mod.RawName];
            if (record == null || record.AffixType != ModType.Unique ||
                record.StatNames == null || record.StatRange == null)
                return false;

            double total = 0;

            foreach (var (stat, range, value) in
                     record.StatNames.Zip(record.StatRange, mod.Values))
            {
                if (stat == null || value <= -1000)
                    continue;

                var minimum = Math.Min(range.Min, range.Max);
                var maximum = Math.Max(range.Min, range.Max);
                if (minimum == maximum)
                    continue;

                var position = Math.Clamp(
                    (value - minimum) / (double)(maximum - minimum), 0d, 1d);

                var statKey = GetMemberString(stat, "Key", "Name");
                var statText = stat.ToString() ?? string.Empty;
                if (minimum >= 0 && IsLowerUniqueRollBetter(statKey, statText))
                    position = 1d - position;

                total += position * 100d;
                variableRolls++;
            }

            if (variableRolls <= 0)
                return false;

            percentage = total / variableRolls;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLowerUniqueRollBetter(string statKey, string statText)
    {
        var text = ((statKey ?? string.Empty) + " " + (statText ?? string.Empty))
            .ToLowerInvariant();

        // Negative-valued drawbacks already score correctly because a value
        // closer to zero is the range maximum. These checks cover the common
        // positive-valued drawbacks where the numerically smaller roll is the
        // desirable one.
        return text.Contains("damage_taken_+%", StringComparison.Ordinal) ||
               text.Contains("increased damage taken", StringComparison.Ordinal) ||
               text.Contains("charges used", StringComparison.Ordinal) ||
               text.Contains("charges_used", StringComparison.Ordinal) ||
               text.Contains("souls per use", StringComparison.Ordinal) ||
               text.Contains("souls_per_use", StringComparison.Ordinal) ||
               text.Contains("requirements_+%", StringComparison.Ordinal) ||
               text.Contains("increased requirements", StringComparison.Ordinal) ||
               text.Contains("skill_cost_+%", StringComparison.Ordinal) ||
               text.Contains("mana_cost_+%", StringComparison.Ordinal) ||
               text.Contains("life_cost_+%", StringComparison.Ordinal) ||
               text.Contains("curse_effect_on_self", StringComparison.Ordinal) ||
               text.Contains("effect of curses on you", StringComparison.Ordinal);
    }

    private List<string> BuildModRows(Entity item, Mods mods)
    {
        var result = new List<string>();

        if (mods?.ItemMods == null)
            return result;

        // The overlay's current item is supplied by the caller through the
        // per-render cache below. If unavailable, the normal mod text still
        // renders exactly as before.
        foreach (var mod in mods.ItemMods)
        {
            if (mod == null)
                continue;

            var values = GetMemberValues(mod);
            var rawName = GetMemberString(mod, "RawName");
            var modRows = GetAuthoritativeStatRows(mod);
            var affixCode = string.Empty;
            var isUniqueMod = false;
            var isImplicitMod = mods.ImplicitMods != null &&
                mods.ImplicitMods.Any(x => x != null &&
                    string.Equals(x.RawName, mod.RawName,
                        StringComparison.Ordinal));

            try
            {
                var record = GameController.Files.Mods.records[mod.RawName];
                if (record != null)
                {
                    if (record.AffixType == ModType.Prefix)
                        affixCode = "P";
                    else if (record.AffixType == ModType.Suffix)
                        affixCode = "S";
                    else if (record.AffixType == ModType.Unique)
                    {
                        affixCode = "U";
                        isUniqueMod = true;
                    }
                }
            }
            catch
            {
            }

            if (modRows.Count == 0)
            {
                var fallback = GetReadableModText(mod, rawName, values);
                if (string.IsNullOrWhiteSpace(fallback))
                    fallback = string.IsNullOrWhiteSpace(rawName) ? "Unknown modifier" : rawName;

                modRows.Add(fallback);
            }

            for (var rowIndex = 0; rowIndex < modRows.Count; rowIndex++)
                modRows[rowIndex] = modRows[rowIndex].Replace("\r", " ").Replace("\n", " ").Trim();

            var tag = string.Empty;

            // Some rare-item implicits use records whose AffixType is Unique.
            // Identify them from the item's actual ImplicitMods collection
            // before applying unique-roll perfection, otherwise a misleading
            // percentage badge appears on rare items.
            if (mods.ItemRarity != ItemRarity.Unique && isImplicitMod)
            {
                affixCode = "I";
                tag = "[I]";
            }
            else if (isUniqueMod && TryGetUniqueModPerfection(mod,
                    out var modPerfection, out _))
            {
                tag = $"[{Math.Clamp((int)Math.Round(modPerfection,
                    MidpointRounding.AwayFromZero), 0, 100)}%]";
            }
            else if (item != null)
            {
                // Crafted mods are not natural T1/T2/T3 affixes. Show a compact
                // "C" marker instead so the advanced display makes the distinction
                // immediately obvious.
                if (IsCraftedMod(mod))
                {
                    tag = "[C]";
                }
                else
                {
                    var tierInfo = GetAuthoritativeModTier(item, mods, mod);

                    // Only show a tier when the mod has multiple legitimate tiers.
                    // This avoids falsely labeling one-off/non-tiered affixes as T1.
                    if (tierInfo.Tier > 0 && tierInfo.TotalTiers > 1)
                        tag = $"[T{tierInfo.Tier}]";
                }
            }

            for (var rowIndex = 0; rowIndex < modRows.Count; rowIndex++)
            {
                var text = modRows[rowIndex];
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Preserve structure for the polished full renderer. Hybrid
                // continuation lines stay visually grouped under one P/S and
                // tier badge instead of pretending to be separate affixes.
                var rowAffix = rowIndex == 0 ? affixCode : string.Empty;
                var rowTag = rowIndex == 0 ? tag : string.Empty;
                if (rowIndex > 0)
                    text = "+ " + text;

                result.Add($"MODROW|{rowAffix}|{rowTag}|{text.Replace("|", "/")}");
            }
        }

        return result;
    }

    private static List<string> CombineAddedAttackDamageRows(
        List<string> rows)
    {
        if (rows == null || rows.Count < 2)
            return rows ?? new List<string>();

        var combined = new List<string>(rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            if (index + 1 < rows.Count &&
                TryParseAddedAttackDamageBound(rows[index],
                    out var firstValue, out var firstBound,
                    out var firstDamageType, out var firstAppliesToAttacks) &&
                TryParseAddedAttackDamageBound(rows[index + 1],
                    out var secondValue, out var secondBound,
                    out var secondDamageType, out var secondAppliesToAttacks) &&
                !string.Equals(firstBound, secondBound,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(firstDamageType, secondDamageType,
                    StringComparison.OrdinalIgnoreCase) &&
                firstAppliesToAttacks == secondAppliesToAttacks)
            {
                var minimum = string.Equals(firstBound, "Minimum",
                    StringComparison.OrdinalIgnoreCase)
                    ? firstValue
                    : secondValue;
                var maximum = string.Equals(firstBound, "Maximum",
                    StringComparison.OrdinalIgnoreCase)
                    ? firstValue
                    : secondValue;

                combined.Add($"Adds {minimum.TrimStart('+')} to " +
                             $"{maximum.TrimStart('+')} {firstDamageType} " +
                             "Damage" +
                             (firstAppliesToAttacks ? " to Attacks" : string.Empty));
                index++;
                continue;
            }

            combined.Add(rows[index]);
        }

        return combined;
    }

    private static bool TryParseAddedAttackDamageBound(string row,
        out string value, out string bound, out string damageType,
        out bool appliesToAttacks)
    {
        value = string.Empty;
        bound = string.Empty;
        damageType = string.Empty;
        appliesToAttacks = false;

        var match = System.Text.RegularExpressions.Regex.Match(
            row ?? string.Empty,
            @"^([+-]?\d+(?:\.\d+)?)\s+(?:(Attack)\s+)?" +
            @"(Minimum|Maximum)\s+Added\s+" +
            @"(Physical|Fire|Cold|Lightning|Chaos)\s+Damage$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        value = match.Groups[1].Value;
        appliesToAttacks = match.Groups[2].Success;
        bound = match.Groups[3].Value;
        damageType = System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(match.Groups[4].Value.ToLowerInvariant());
        return true;
    }

    private List<string> GetAuthoritativeStatRows(ItemMod mod)
    {
        var result = new List<string>();
        if (mod == null || string.IsNullOrEmpty(mod.RawName))
            return result;

        try
        {
            var record = GameController.Files.Mods.records[mod.RawName];
            if (record?.StatNames == null || record.StatRange == null || mod.Values == null)
                return result;

            foreach (var (stat, range, value) in record.StatNames.Zip(record.StatRange, mod.Values))
            {
                if (stat == null || value <= -1000)
                    continue;

                // Match AdvancedTooltip's authoritative display path: the
                // localized stat label comes from ModRecord.StatNames and the
                // rolled number comes from ItemMod.Values.
                var valueText = stat.ValueToString(value)?.Trim() ?? string.Empty;
                var statText = stat.ToString()?.Trim() ?? string.Empty;
                var statKey = GetMemberString(stat, "Key", "Name");

                if (range.Min == 0 && range.Max == 0 && string.IsNullOrWhiteSpace(statText))
                    continue;

                var line = FormatAuthoritativeStatLine(statKey, valueText, statText);
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ");

                if (!string.IsNullOrWhiteSpace(line) &&
                    !result.Contains(line, StringComparer.OrdinalIgnoreCase))
                    result.Add(line);
            }
        }
        catch
        {
            // Translation/reflection fallback below will still display the mod.
        }

        return CombineAddedAttackDamageRows(result);
    }

    private static string FormatAuthoritativeStatLine(
        string statKey, string valueText, string statText)
    {
        statKey ??= string.Empty;
        valueText ??= string.Empty;
        statText ??= string.Empty;

        var key = statKey.ToLowerInvariant();
        var signed = valueText.StartsWith("+", StringComparison.Ordinal)
            ? valueText
            : "+" + valueText;
        var unsigned = valueText.TrimStart('+');

        // Several PoE stat records store display values in internal units.
        // Translate those units before building the player-facing line.
        if (key.Contains("life_regeneration_rate_per_minute",
                StringComparison.Ordinal) &&
            TryParseDisplayNumber(valueText, out var regenerationPerMinute))
        {
            return $"{FormatDisplayNumber(regenerationPerMinute / 60d, 1)} " +
                   "Life Regenerated per Second";
        }

        if (key.Contains("life_leech_from_physical_attack_damage_permyriad",
                StringComparison.Ordinal) &&
            TryParseDisplayNumber(valueText, out var leechPermyriad))
        {
            return $"{FormatDisplayNumber(leechPermyriad / 100d, 2)}% of Physical " +
                   "Attack Damage Leeched as Life";
        }

        switch (key)
        {
            case "base_fire_damage_resistance_%": return $"{signed} to Fire Resistance";
            case "base_cold_damage_resistance_%": return $"{signed} to Cold Resistance";
            case "base_lightning_damage_resistance_%": return $"{signed} to Lightning Resistance";
            case "base_chaos_damage_resistance_%": return $"{signed} to Chaos Resistance";
            case "base_maximum_fire_damage_resistance_%": return $"{signed} to maximum Fire Resistance";
            case "base_maximum_cold_damage_resistance_%": return $"{signed} to maximum Cold Resistance";
            case "base_maximum_lightning_damage_resistance_%": return $"{signed} to maximum Lightning Resistance";
            case "base_maximum_chaos_damage_resistance_%": return $"{signed} to maximum Chaos Resistance";
            case "base_maximum_life": return $"{signed} to Maximum Life";
            case "base_maximum_mana": return $"{signed} to Maximum Mana";
            case "base_movement_velocity_+%": return $"{unsigned} increased Movement Speed";
            case "local_physical_damage_reduction_rating_+%": return $"{unsigned} increased Armour";
            case "local_evasion_rating_+%": return $"{unsigned} increased Evasion Rating";
            case "local_energy_shield_+%": return $"{unsigned} increased Energy Shield";
            case "local_base_physical_damage_reduction_rating": return $"{signed} to Armour";
            case "local_base_evasion_rating": return $"{signed} to Evasion Rating";
            case "local_base_maximum_energy_shield": return $"{signed} to Maximum Energy Shield";
            case "base_spell_suppression_chance_%": return $"{signed} chance to Suppress Spell Damage";
            case "additional_strength": return $"{signed} to Strength";
            case "additional_dexterity": return $"{signed} to Dexterity";
            case "additional_intelligence": return $"{signed} to Intelligence";
            case "attack_speed_+%":
            case "local_attack_speed_+%": return $"{unsigned} increased Attack Speed";
            case "base_cast_speed_+%":
            case "cast_speed_+%": return $"{unsigned} increased Cast Speed";
            case "spell_damage_+%": return $"{unsigned} increased Spell Damage";
            case "critical_strike_chance_+%":
            case "local_critical_strike_chance_+%": return $"{unsigned} increased Critical Strike Chance";
            case "base_critical_strike_multiplier_+":
            case "critical_strike_multiplier_+": return $"{signed} to Critical Strike Multiplier";
            case "accuracy_rating":
            case "local_accuracy_rating": return $"{signed} to Accuracy Rating";
            case "base_cooldown_speed_+%":
            case "cooldown_recovery_+%": return $"{unsigned} increased Cooldown Recovery Rate";
            case "damage_over_time_multiplier_+%": return $"{signed} to Damage over Time Multiplier";
            case "impale_on_hit_%_chance":
            case "attacks_impale_on_hit_%_chance": return $"{unsigned} chance to Impale Enemies on Hit with Attacks";
        }

        var looksInternal = statText.IndexOf('_') >= 0 ||
                            string.Equals(statText, statKey,
                                StringComparison.OrdinalIgnoreCase);
        if (!looksInternal && !string.IsNullOrWhiteSpace(statText))
            return (valueText + " " + statText).Trim();

        var human = HumanizeInternalStatKey(statKey);
        return string.IsNullOrWhiteSpace(human)
            ? (valueText + " " + statText).Trim()
            : (valueText + " " + human).Trim();
    }

    private static bool TryParseDisplayNumber(string text, out double value)
    {
        value = 0d;
        var match = System.Text.RegularExpressions.Regex.Match(text ?? string.Empty,
            @"[-+]?\d+(?:\.\d+)?");
        if (!match.Success)
            return false;

        return double.TryParse(match.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string FormatDisplayNumber(double value, int maximumDecimals)
    {
        var format = maximumDecimals <= 0
            ? "0"
            : "0." + new string('#', maximumDecimals);
        return value.ToString(format,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatCountLabel(int count, string singular,
        string plural)
    {
        return $"{count} {(count == 1 ? singular : plural)}";
    }

    private static string HumanizeInternalStatKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var text = key
            .Replace("local_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("base_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_+%", string.Empty, StringComparison.Ordinal)
            .Replace("_%", string.Empty, StringComparison.Ordinal)
            .Replace("_+", string.Empty, StringComparison.Ordinal)
            .Replace('_', ' ')
            .Trim();

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(text);
    }


    private static string GetReadableModText(object mod, string rawName, List<string> values)
    {
        // The debug build proved the user's ItemMod exposes Translation and
        // ModRecord. Prefer a real translated stat template whenever the
        // runtime provides one.
        var translated = TryGetTranslationText(mod);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            var formatted = ApplyModValues(translated, values);
            if (!string.IsNullOrWhiteSpace(formatted))
                return formatted;
        }

        // Fallback for common PoE stat keys. This also makes the overlay useful
        // if Translation is unavailable for a particular mod.
        var fallback = TranslateKnownMod(rawName, values);
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;

        // Last resort: use the human affix name, but always show the values so
        // we never silently lose information.
        var affix = GetMemberString(mod, "DisplayName", "Name");
        if (!string.IsNullOrWhiteSpace(affix))
            return values.Count > 0 ? $"{affix} [{string.Join(", ", values)}]" : affix;

        return rawName;
    }

    private static string TryGetTranslationText(object mod)
    {
        try
        {
            foreach (var owner in new[] { mod, GetMemberObject(mod, "ModRecord") })
            {
                if (owner == null)
                    continue;

                var direct = GetMemberObject(owner, "Translation");
                var text = ExtractTranslationString(direct, 0);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;

                text = GetMemberString(owner, "Translation", "Text", "Description", "Template");
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractTranslationString(object value, int depth)
    {
        if (value == null || depth > 2)
            return string.Empty;

        if (value is string s)
            return s;

        foreach (var name in new[] { "Text", "Translation", "Description", "Template", "String" })
        {
            var nested = GetMemberObject(value, name);
            if (nested is string ns && !string.IsNullOrWhiteSpace(ns))
                return ns;

            var recursive = ExtractTranslationString(nested, depth + 1);
            if (!string.IsNullOrWhiteSpace(recursive))
                return recursive;
        }

        return string.Empty;
    }

    private static string ApplyModValues(string template, List<string> values)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var result = template;
        for (var i = 0; i < values.Count; i++)
        {
            result = result.Replace("{" + i + "}", values[i], StringComparison.Ordinal);
        }

        // A few PoE translation templates use # placeholders. Replace them in
        // order without touching text that contains no placeholder.
        foreach (var value in values)
        {
            var hash = result.IndexOf('#');
            if (hash < 0)
                break;
            result = result.Remove(hash, 1).Insert(hash, value);
        }

        result = result.Trim();
        if (values.Count > 0 && result.IndexOf(values[0], StringComparison.Ordinal) < 0 &&
            result.IndexOf("#", StringComparison.Ordinal) < 0 &&
            result.IndexOf("{0}", StringComparison.Ordinal) < 0)
        {
            // Do not lose the numeric roll when a translation is only an affix
            // label (for example "OF THE THUNDERHEAD").
            result += values.Count == 1
                ? $" ({values[0]})"
                : $" ({string.Join(", ", values)})";
        }

        return result;
    }

    private static string TranslateKnownMod(string rawName, List<string> values)
    {
        if (string.IsNullOrWhiteSpace(rawName) || values == null || values.Count == 0)
            return string.Empty;

        var v0 = values[0];

        if (Contains(rawName, "MaximumLife") || Contains(rawName, "MaxLife"))
            return $"+{v0} to Maximum Life";
        if (Contains(rawName, "FireResist"))
            return $"+{v0}% to Fire Resistance";
        if (Contains(rawName, "ColdResist"))
            return $"+{v0}% to Cold Resistance";
        if (Contains(rawName, "LightningResist"))
            return $"+{v0}% to Lightning Resistance";
        if (Contains(rawName, "ChaosResist"))
            return $"+{v0}% to Chaos Resistance";
        if (Contains(rawName, "ResistAllElementsPct") || Contains(rawName, "AllResist"))
            return $"+{v0}% to all Elemental Resistances";
        if (Contains(rawName, "MovementVelocityPct") || Contains(rawName, "MovementSpeed"))
            return $"{v0}% increased Movement Speed";
        if (Contains(rawName, "Strength"))
            return $"+{v0} to Strength";
        if (Contains(rawName, "Dexterity"))
            return $"+{v0} to Dexterity";
        if (Contains(rawName, "Intelligence"))
            return $"+{v0} to Intelligence";
        if (Contains(rawName, "LocalIncreasedAttackSpeed"))
            return $"{v0}% increased Attack Speed";
        if (Contains(rawName, "IncreasedPhysicalDamagePercent"))
            return $"{v0}% increased Physical Damage";
        if (Contains(rawName, "AddedPhysicalDamage"))
        {
            var hi = values.Count > 1 ? values[1] : v0;
            return $"Adds {v0} to {hi} Physical Damage";
        }

        return string.Empty;
    }

    private static object GetMemberObject(object obj, string name)
    {
        if (obj == null)
            return null;

        try
        {
            var type = obj.GetType();
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
                return prop.GetValue(obj);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private string GetLootRatingReason(Entity item, Mods mods)
    {
        if (item == null || mods == null || mods.ItemRarity != ItemRarity.Rare)
            return string.Empty;

        var path = item.Path ?? string.Empty;

        if (IsBoot(path))
        {
            foreach (var mod in mods.ItemMods ?? new List<ItemMod>())
            {
                if (mod == null)
                    continue;

                var key = string.Join(" ", new[]
                {
                    GetMemberString(mod, "RawName"),
                    GetMemberString(mod, "Name"),
                    GetMemberString(mod, "DisplayName")
                }).ToLowerInvariant();

                if (key.Contains("movement") && key.Contains("speed"))
                    return string.Empty;
            }

            return "Movement Speed";
        }

        return string.Empty;
    }

    private List<string> BuildUserDefinedRatingDetails(Entity item, int rating)
    {
        var result = new List<string>();
        if (item == null || rating <= 0)
            return result;

        var mods = item.GetComponent<Mods>();
        if (mods == null)
            return result;

        var path = item.Path ?? string.Empty;
        var slot = GetStarSlot(path);
        if (slot == null)
            return result;

        var stats = ReadQualityStats(mods);

        // Keep ES consistent with the actual final post-mod defense value
        // displayed in the analyzer.
        try
        {
            var armour = item.GetComponent<Armour>();
            if (armour != null)
            {
                var finalDefenses = CalculateFinalDefenses(item, mods, armour);
                stats.Armour = finalDefenses.Armour;
                stats.Evasion = finalDefenses.Evasion;
                stats.EnergyShield = finalDefenses.EnergyShield;
            }
        }
        catch
        {
        }

        foreach (var kv in slot.Thresholds)
        {
            var threshold = kv.Value;
            if (threshold <= 0)
                continue;

            var value = GetConfiguredStatValue(item, stats, kv.Key);
            if (value < threshold)
                continue;

            var label = kv.Key;
            var suffix = IsPercentageStat(label) ? "%" : "";

            // This is deliberately the user's configured threshold, not the
            // old hard-coded Good/Great/Excellent values. Include the amount
            // above the user's threshold so the result is immediately readable.
            var above = value - threshold;
            result.Add($"{label}: {value}{suffix} / {threshold}{suffix}  +{above}{suffix}");
        }

        return result;
    }

    private int GetConfiguredStatValue(Entity item, QualityStats s, string key)
    {
        switch (key)
        {
            case "Life": return s.Life;
            case "Cold Resistance": return s.ColdRes + s.AllRes;
            case "Fire Resistance": return s.FireRes + s.AllRes;
            case "Lightning Resistance": return s.LightningRes + s.AllRes;
            case "Chaos Resistance": return s.ChaosRes;
            case "Armour": return s.Armour;
            case "Evasion": return s.Evasion;
            case "Energy Shield": return s.EnergyShield;
            case "Spell Suppression": return s.SpellSuppression;
            case "Attributes": return Math.Max(s.Strength, Math.Max(s.Dexterity, s.Intelligence));
            case "Attack Speed": return s.AttackSpeed;
            case "Cast Speed": return s.CastSpeed;
            case "Critical Strike Chance": return s.CritChance;
            case "Crit Multiplier": return s.CritMultiplier;
            case "Accuracy": return s.Accuracy;
            case "Cooldown Recovery": return s.CooldownRecovery;
            case "DoT Multiplier": return s.DotMultiplier;
            case "Movement Speed": return s.MoveSpeed;
            case "Mana": return s.Mana;
            case "Spell Damage": return s.SpellDamage;
            case "Gem Levels": return s.GemLevels;
            case "Specific Gem Levels": return s.SpecificGemLevels;
            case "Physical DPS":
                {
                    var mods = item?.GetComponent<Mods>();
                    return mods == null ? 0 : (int)Math.Round(CalculateWeaponDps(item, mods).Physical);
                }

            case "Total Weapon DPS":
            case "Weapon DPS":
                {
                    var mods = item?.GetComponent<Mods>();
                    return mods == null ? 0 : (int)Math.Round(CalculateWeaponDps(item, mods).Total);
                }

            default:
                return 0;
        }
    }

    private static bool IsPercentageStat(string key)
    {
        key = (key ?? string.Empty).Trim();
        return key.EndsWith("Resistance", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Spell Suppression", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Attack Speed", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Spell Damage", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Cast Speed", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Critical Strike Chance", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Crit Multiplier", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Cooldown Recovery", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("DoT Multiplier", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("Movement Speed", StringComparison.OrdinalIgnoreCase);
    }

    private static string QualificationSuffix(int value, int good, int great, int excellent)
    {
        if (value >= excellent) return "  [EXCELLENT]";
        if (value >= great) return "  [GREAT]";
        if (value >= good) return "  [GOOD]";
        return string.Empty;
    }

    private static string RatingText(int rating)
    {
        return rating switch
        {
            3 => "★★★",
            2 => "★★",
            1 => "★",
            _ => "—"
        };
    }

    private string GetItemDisplayName(Entity item)
    {
        if (item == null)
            return "Item";

        // RenderName was observed as EMPTY in the user's runtime, so only use
        // it if it contains meaningful text.
        var direct = GetMemberString(item, "RenderName");
        if (IsMeaningfulName(direct))
            return direct;

        // Try common metadata/name members without assuming a specific
        // ExileCore version.
        direct = GetMemberString(item, "Name", "DisplayName", "Text");
        if (IsMeaningfulName(direct))
            return direct;

        var metadata = GetMemberObject(item, "Metadata");
        direct = GetMemberString(metadata, "Name", "DisplayName", "BaseName", "Text");
        if (IsMeaningfulName(direct))
            return direct;

        // BaseItemTypes is more useful than the metadata path when the entity
        // itself only exposes its broad class (for example "Boots"). Read it
        // through reflection to remain compatible with ExileAPI model changes.
        try
        {
            var baseItem = GameController.Files.BaseItemTypes.Translate(item.Path);
            direct = GetMemberString(baseItem,
                "BaseName", "Name", "DisplayName", "ItemName", "TypeName");
            if (IsMeaningfulName(direct))
                return direct;
        }
        catch
        {
        }

        // Last fallback: convert a metadata path such as GlovesStr8 into a
        // readable base-type label rather than showing EMPTY.
        return PrettyPathLeaf(item.Path);
    }

    private static bool IsMeaningfulName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value.Trim(), "EMPTY", StringComparison.OrdinalIgnoreCase) &&
               !value.StartsWith("Metadata/", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrettyPathLeaf(string path)
    {
        var leaf = GetPathLeaf(path);
        if (string.IsNullOrWhiteSpace(leaf) || string.Equals(leaf, "Item", StringComparison.OrdinalIgnoreCase))
            return "Item";

        // Remove the common base-type strength suffix used by PoE metadata.
        var name = System.Text.RegularExpressions.Regex.Replace(leaf, "(?:Str|Dex|Int|StrDex|StrInt|DexInt)\\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        return name.Replace("  ", " ").Trim();
    }

    private static string GetPathLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Item";

        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length
            ? path.Substring(slash + 1)
            : path;
    }

    private static string GetMemberString(object obj, params string[] names)
    {
        if (obj == null)
            return string.Empty;

        foreach (var name in names)
        {
            try
            {
                var type = obj.GetType();
                var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    if (value != null)
                        return value.ToString();
                }

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    var value = field.GetValue(obj);
                    if (value != null)
                        return value.ToString();
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static List<string> GetMemberValues(object mod)
    {
        var result = new List<string>();
        if (mod == null)
            return result;

        try
        {
            var type = mod.GetType();
            var prop = type.GetProperty("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = prop?.GetValue(mod);

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var v in enumerable)
                    result.Add(v?.ToString() ?? string.Empty);
            }
        }
        catch
        {
        }

        return result;
    }

    private static uint ToImGuiColor(SharpDX.Color c)
    {
        return (uint)(c.R | (c.G << 8) | (c.B << 16) | (c.A << 24));
    }


    private static SharpDX.Color GetColor(ColorNode node, SharpDX.Color fallback)
    {
        if (node == null)
            return fallback;

        try { return node.Value; }
        catch { return fallback; }
    }
}
