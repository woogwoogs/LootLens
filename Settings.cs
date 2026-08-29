using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

using Color = SharpDX.Color;

namespace InventoryItemAnalyzer;

public class Settings : ISettings
{
    public const string LegacyBootSpecialModifierRules =
        "Boots|Tailwind\n" +
        "Boots|Onslaught\n" +
        "Boots|Cannot be Frozen\n" +
        "Boots|Cannot be Chilled\n" +
        "Boots|Cannot be Shocked\n" +
        "Boots|Cannot be Ignited\n" +
        "Boots|Phasing\n" +
        "Boots|Stun";

    public const string DefaultSpecialModifierRules =
        "# HELMET\n" +
        "Helmet|Nearby Enemies have\n" +
        "Helmet|Nearby Enemies take\n" +
        "Helmet|Mana Reservation Efficiency of Skills\n" +
        "Helmet|Physical Damage from Hits taken as\n" +
        "Helmet|Socketed Gems are Supported by\n" +
        "# BODY ARMOUR\n" +
        "Body Armour|Enemies you Kill Explode\n" +
        "Body Armour|additional Curse\n" +
        "Body Armour|Attacks have\n" +
        "Body Armour|Spells have\n" +
        "Body Armour|Frenzy Charge on Hit\n" +
        "Body Armour|Physical Damage from Hits taken as\n" +
        "Body Armour|Socketed Gems are Supported by\n" +
        "# GLOVES\n" +
        "Gloves|Strike Skills target\n" +
        "Gloves|Culling Strike\n" +
        "Gloves|Unnerve\n" +
        "Gloves|Intimidate\n" +
        "Gloves|Exposure on Hit\n" +
        "Gloves|Socketed Gems are Supported by\n" +
        "# BOOTS\n" +
        "Boots|Tailwind\n" +
        "Boots|Onslaught\n" +
        "Boots|Cannot be Frozen\n" +
        "Boots|Cannot be Chilled\n" +
        "Boots|Cannot be Shocked\n" +
        "Boots|Cannot be Ignited\n" +
        "Boots|Phasing\n" +
        "# SHIELD\n" +
        "Shield|Life when you Block\n" +
        "Shield|Energy Shield when you Block\n" +
        "Shield|to maximum Fire Resistance\n" +
        "Shield|to maximum Cold Resistance\n" +
        "Shield|to maximum Lightning Resistance\n" +
        "Shield|Socketed Gems are Supported by\n" +
        "# BELT\n" +
        "Belt|increased Flask Effect\n" +
        "Belt|Cooldown Recovery Rate\n" +
        "Belt|increased maximum Life\n" +
        "Belt|increased Attributes\n" +
        "# RING\n" +
        "Ring|Curse Enemies with\n" +
        "Ring|minimum Frenzy Charge\n" +
        "Ring|minimum Endurance Charge\n" +
        "Ring|minimum Power Charge\n" +
        "Ring|Non-Channelling Skills have\n" +
        "Ring|Channeling Skills have\n" +
        "# AMULET\n" +
        "Amulet|Level of all Skill Gems\n" +
        "Amulet|Level of all Physical Skill Gems\n" +
        "Amulet|Level of all Fire Skill Gems\n" +
        "Amulet|Level of all Cold Skill Gems\n" +
        "Amulet|Level of all Lightning Skill Gems\n" +
        "Amulet|Level of all Chaos Skill Gems\n" +
        "Amulet|Mana Reservation Efficiency of Skills\n" +
        "Amulet|additional Curse\n" +
        "# QUIVER\n" +
        "Quiver|additional Arrow\n" +
        "Quiver|maximum Frenzy Charge\n" +
        "Quiver|Physical Damage as Extra Chaos Damage\n" +
        "# MELEE WEAPON\n" +
        "Melee Weapon|Hits can't be Evaded\n" +
        "Melee Weapon|Chance to deal Double Damage\n" +
        "Melee Weapon|Culling Strike\n" +
        "Melee Weapon|Socketed Gems are Supported by\n" +
        "# BOW\n" +
        "Bow|additional Arrow\n" +
        "Bow|Hits can't be Evaded\n" +
        "Bow|Chance to deal Double Damage\n" +
        "Bow|Socketed Gems are Supported by\n" +
        "# CASTER WEAPON\n" +
        "Caster Weapon|Level of all Spell Skill Gems\n" +
        "Caster Weapon|Trigger a Socketed Spell\n" +
        "Caster Weapon|Culling Strike\n" +
        "Caster Weapon|Chance to deal Double Damage\n" +
        "Caster Weapon|Socketed Gems are Supported by";

    [Menu("Enable Plugin")]
    public ToggleNode Enable { get; set; } = new(true);
    [Menu("Star Color")]
    public ColorNode StarColor { get; set; } = new(Color.Gold);

    [Menu("Show Special Mod Stars")]
    public ToggleNode ShowSpecialStars { get; set; } = new(true);

    [Menu("Special Mod Star Color")]
    public ColorNode SpecialStarColor { get; set; } = new(new Color(235, 55, 65, 255));

    // One rule per line: Slot|text to find in the modifier. "Any" applies to
    // every equipment slot. Each matched rule earns one independent red star.
    public string SpecialModifierRules { get; set; } = DefaultSpecialModifierRules;

    [Menu("Item Info Overlay")]
    public ToggleNode ShowItemInfo { get; set; } = new(true);

    [Menu("Always Show Item Info")]
    public ToggleNode AlwaysShowItemInfo { get; set; } = new(false);

    [Menu("Item Info - Hold Key for Full Analyzer")]
    public HotkeyNode ItemInfoHotkey { get; set; } = new(Keys.F8);

    [Menu("Item Info - Show Equipped Items")]
    public ToggleNode ItemInfoShowEquippedItems { get; set; } = new(true);

    [Menu("Item Info - Minimal Scale")]
    public RangeNode<int> ItemInfoMinimalScale { get; set; } = new(140, 80, 180);

    // 0 = current affix tier, 1 = all tiers valid for the base,
    // 2 = all base-valid tiers reachable at the item's item level.
    public RangeNode<int> ItemInfoPerfectionRange { get; set; } = new(0, 0, 2);

    // 0 = full brightness, 1 = dimmed, 2 = hidden.
    public RangeNode<int> ItemInfoFixedUniqueMods { get; set; } = new(1, 0, 2);

    // Retained for config compatibility. V74 shows the roll percentage without
    // repeating a textual range beside every modifier.
    public ToggleNode ItemInfoShowTierRanges { get; set; } = new(true);
    public ToggleNode ItemInfoShowImplicitModifiers { get; set; } = new(false);
    public ToggleNode ItemInfoShowOverallPerfection { get; set; } = new(true);
    public ToggleNode ItemInfoFullStatColors { get; set; } = new(false);






    // ============================================================
    // CUSTOM STAR SYSTEM - SLOT TABS
    // 0 on a stat slider means "do not count this stat".
    // ============================================================
    public RangeNode<int> Star_Helmet_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Helmet_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Helmet_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Helmet_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Helmet_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Helmet_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Helmet_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Helmet_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Helmet_Armour { get; set; } = new(700, 0, 5000);
    public RangeNode<int> Star_Helmet_Evasion { get; set; } = new(700, 0, 5000);
    public RangeNode<int> Star_Helmet_EnergyShield { get; set; } = new(160, 0, 1500);
    public RangeNode<int> Star_Helmet_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Helmet_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_BodyArmour_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_BodyArmour_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_BodyArmour_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_BodyArmour_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_BodyArmour_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_BodyArmour_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_BodyArmour_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_BodyArmour_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_BodyArmour_Armour { get; set; } = new(1600, 0, 12000);
    public RangeNode<int> Star_BodyArmour_Evasion { get; set; } = new(1600, 0, 12000);
    public RangeNode<int> Star_BodyArmour_EnergyShield { get; set; } = new(400, 0, 5000);
    public RangeNode<int> Star_BodyArmour_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_BodyArmour_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Gloves_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Gloves_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Gloves_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Gloves_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Gloves_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Gloves_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Gloves_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Gloves_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Gloves_Armour { get; set; } = new(450, 0, 4000);
    public RangeNode<int> Star_Gloves_Evasion { get; set; } = new(450, 0, 4000);
    public RangeNode<int> Star_Gloves_EnergyShield { get; set; } = new(110, 0, 1200);
    public RangeNode<int> Star_Gloves_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Gloves_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Gloves_CastSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Gloves_CritChance { get; set; } = new(0, 0, 200);
    public RangeNode<int> Star_Gloves_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Gloves_Accuracy { get; set; } = new(0, 0, 2000);
    public RangeNode<int> Star_Gloves_DotMultiplier { get; set; } = new(0, 0, 100);
    public RangeNode<int> Star_Gloves_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Boots_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Boots_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Boots_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Boots_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Boots_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Boots_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Boots_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Boots_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Boots_Armour { get; set; } = new(450, 0, 4000);
    public RangeNode<int> Star_Boots_Evasion { get; set; } = new(450, 0, 4000);
    public RangeNode<int> Star_Boots_EnergyShield { get; set; } = new(110, 0, 1200);
    public RangeNode<int> Star_Boots_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Boots_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Boots_MovementSpeed { get; set; } = new(30, 0, 50);
    public RangeNode<int> Star_Boots_CooldownRecovery { get; set; } = new(0, 0, 50);
    public RangeNode<int> Star_Belt_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Belt_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Belt_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Belt_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Belt_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Belt_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Belt_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Belt_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Belt_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Belt_Mana { get; set; } = new(50, 0, 300);
    public RangeNode<int> Star_Belt_CooldownRecovery { get; set; } = new(0, 0, 50);
    public RangeNode<int> Star_Ring_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Ring_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Ring_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Ring_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Ring_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Ring_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Ring_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Ring_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Ring_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Ring_Mana { get; set; } = new(50, 0, 300);
    public RangeNode<int> Star_Ring_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Ring_CastSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Ring_CritChance { get; set; } = new(0, 0, 200);
    public RangeNode<int> Star_Ring_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Ring_Accuracy { get; set; } = new(200, 0, 1000);
    public RangeNode<int> Star_Ring_DotMultiplier { get; set; } = new(0, 0, 100);
    public RangeNode<int> Star_Amulet_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Amulet_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Amulet_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Amulet_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Amulet_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Amulet_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Amulet_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Amulet_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Amulet_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Amulet_EnergyShield { get; set; } = new(100, 0, 1000);
    public RangeNode<int> Star_Amulet_GemLevels { get; set; } = new(1, 0, 5);
    public RangeNode<int> Star_Amulet_SpecificGemLevels { get; set; } = new(0, 0, 5);
    public RangeNode<int> Star_Amulet_CritChance { get; set; } = new(0, 0, 200);
    public RangeNode<int> Star_Amulet_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Amulet_DotMultiplier { get; set; } = new(0, 0, 100);
    public RangeNode<int> Star_Shield_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Shield_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Shield_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Shield_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Shield_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Shield_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Shield_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Shield_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Shield_Armour { get; set; } = new(700, 0, 6000);
    public RangeNode<int> Star_Shield_Evasion { get; set; } = new(700, 0, 6000);
    public RangeNode<int> Star_Shield_EnergyShield { get; set; } = new(140, 0, 2000);
    public RangeNode<int> Star_Shield_SpellSuppression { get; set; } = new(12, 0, 60);
    public RangeNode<int> Star_Shield_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Quiver_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Quiver_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Quiver_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Quiver_Life { get; set; } = new(80, 0, 200);
    public RangeNode<int> Star_Quiver_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Quiver_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Quiver_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Quiver_ChaosResistance { get; set; } = new(20, 0, 80);
    public RangeNode<int> Star_Quiver_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Quiver_CritChance { get; set; } = new(0, 0, 200);
    public RangeNode<int> Star_Quiver_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Quiver_Accuracy { get; set; } = new(0, 0, 2000);
    public RangeNode<int> Star_Quiver_DotMultiplier { get; set; } = new(0, 0, 100);
    public RangeNode<int> Star_Quiver_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Weapon_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Weapon_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Weapon_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Weapon_WeaponDps { get; set; } = new(200, 0, 1000);

    // 0 = Physical DPS, 1 = Total Weapon DPS.
    public RangeNode<int> Star_Weapon_DpsMode { get; set; } = new(1, 0, 1);
    public RangeNode<int> Star_Weapon_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Weapon_CritChance { get; set; } = new(0, 0, 200);
    public RangeNode<int> Star_Weapon_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Weapon_Accuracy { get; set; } = new(0, 0, 2000);
    public RangeNode<int> Star_Weapon_DotMultiplier { get; set; } = new(0, 0, 100);
    public RangeNode<int> Star_Weapon_ColdResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Weapon_FireResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Weapon_LightningResistance { get; set; } = new(30, 0, 80);
    public RangeNode<int> Star_Weapon_Attributes { get; set; } = new(30, 0, 200);
    public RangeNode<int> Star_Weapon_GemLevels { get; set; } = new(1, 0, 5);
    public RangeNode<int> Star_Weapon_SpecificGemLevels { get; set; } = new(0, 0, 5);

    // Bows use a separate attack profile so their DPS targets can be tuned
    // independently from melee weapons.
    public RangeNode<int> Star_Bow_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_Bow_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_Bow_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_Bow_WeaponDps { get; set; } = new(250, 0, 1500);
    public RangeNode<int> Star_Bow_DpsMode { get; set; } = new(1, 0, 1);
    public RangeNode<int> Star_Bow_AttackSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_Bow_CritChance { get; set; } = new(20, 0, 200);
    public RangeNode<int> Star_Bow_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_Bow_Accuracy { get; set; } = new(300, 0, 2000);
    public RangeNode<int> Star_Bow_DotMultiplier { get; set; } = new(0, 0, 100);
    public RangeNode<int> Star_Bow_GemLevels { get; set; } = new(0, 0, 5);

    // Wands, sceptres, staves and daggers receive caster-oriented rules rather
    // than being incorrectly judged by weapon DPS alone.
    public RangeNode<int> Star_CasterWeapon_GoodRequired { get; set; } = new(2, 0, 10);
    public RangeNode<int> Star_CasterWeapon_GreatRequired { get; set; } = new(3, 0, 10);
    public RangeNode<int> Star_CasterWeapon_ExcellentRequired { get; set; } = new(4, 0, 10);
    public RangeNode<int> Star_CasterWeapon_SpellDamage { get; set; } = new(60, 0, 300);
    public RangeNode<int> Star_CasterWeapon_CastSpeed { get; set; } = new(10, 0, 50);
    public RangeNode<int> Star_CasterWeapon_CritChance { get; set; } = new(0, 0, 200);
    public RangeNode<int> Star_CasterWeapon_CritMultiplier { get; set; } = new(20, 0, 100);
    public RangeNode<int> Star_CasterWeapon_DotMultiplier { get; set; } = new(15, 0, 100);
    public RangeNode<int> Star_CasterWeapon_Mana { get; set; } = new(60, 0, 300);
    public RangeNode<int> Star_CasterWeapon_GemLevels { get; set; } = new(1, 0, 5);
    public RangeNode<int> Star_CasterWeapon_SpecificGemLevels { get; set; } = new(0, 0, 5);


    [Menu("Item Info - Compact Mode")]
    public ToggleNode ItemInfoCompactMode { get; set; } = new(false);


    [Menu("Item Info - Width")]
    public RangeNode<int> ItemInfoWidth { get; set; } = new(310, 220, 600);

    [Menu("Item Info - Background")]
    public ColorNode ItemInfoBackground { get; set; } = new(new Color(0, 0, 0, 236));

    [Menu("Item Info - Border")]
    public ColorNode ItemInfoBorder { get; set; } = new(new Color(0, 0, 0, 0));

    [Menu("Item Info - Tier Color")]
    public ColorNode ItemInfoTierColor { get; set; } = new(Color.Gold);

    [Menu("Item Info - DEBUG Mode (temporary)")]
    public ToggleNode ItemInfoDebugMode { get; set; } = new(false);

}
