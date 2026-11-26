using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

using static Game;

namespace GameServer.Modules.PlayerBots;

// How a bot plays: the game's own rules, run for a player the server drives.
public static partial class BotPlay
{
    // Conservative starting archetypes: ordinary connected allocations, wearable bases and compatible links.
    // No requirement exemptions or hidden attribute bonuses are used for a controlled character.
    public static CharacterSaves.Save BuildControlledPlayer(CharacterClass characterClass, int level)
    {
        level = Math.Clamp(level, 1, 100);
        var cls = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)characterClass));
        bool spell = characterClass is CharacterClass.Witch or CharacterClass.Templar;
        bool bow = characterClass == CharacterClass.Ranger;
        string skill = characterClass switch
        {
            CharacterClass.Witch or CharacterClass.Templar => "Fireball",
            // The ladder's bow skill (poe.qq.com's s29 ranking, 2026-04-13: lightning arrow leads the ranger's actives).
            // Split Arrow was the beginner gem, with a fraction of its clear.
            // Lightning Arrow's gem drops at level 12; below that a bow user has Split Arrow and nothing else.
            CharacterClass.Ranger => level >= 12 ? "Lightning Arrow" : "Split Arrow",
            CharacterClass.Marauder => "Ground Slam",
            CharacterClass.Shadow => "Frost Blades", _ => "Cleave",
        };
        string weapon = spell ? "Wand" : bow ? "Bow" : characterClass == CharacterClass.Marauder ? "Two Hand Mace"
            : characterClass == CharacterClass.Shadow ? "Claw" : "One Hand Sword";
        var nodes = ControlledNodes(characterClass, level);
        var passiveStats = Program.Passives.Stats(nodes);
        int Attr(uint key, int basis) => basis + passiveStats.GetValueOrDefault(key)
            + passiveStats.GetValueOrDefault(AttributeModifiers.BaseAllAttributes);
        int str = Attr(Stat.BaseStrength, cls.Strength), dex = Attr(Stat.BaseDexterity, cls.Dexterity), intel = Attr(Stat.BaseIntelligence, cls.Intelligence);
        bool Fits(ItemCatalog.BaseType t)
        {
            var req = Program.Items.Requirements(t);
            return t.DropLevel <= level && req.Str <= str && req.Dex <= dex && req.Int <= intel
                && ItemModStats.LevelRequirement(ItemCatalog.Create(t), t.DropLevel, Program.Items.Mods) <= level;
        }
        var inventory = new Dictionary<byte, List<CharacterSaves.SavedPlacement>>();
        uint itemId = 0;
        Item? Wear(string itemClass, byte row)
        {
            var type = Program.Items.All.Where(t => t.Class == itemClass && t.Supported && Fits(t)
                && !t.Id.Contains("Royale", StringComparison.Ordinal) && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                && !t.Components.Contains("Transformed"))
                .OrderByDescending(t => t.DropLevel).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
            if (type is null) return null;
            var item = ItemCatalog.Create(type) with { ItemLevel = (byte)level, Quality = (byte)(level >= 20 ? 20 : 0), Sockets = [], Links = [] };
            inventory[row] = [new(++itemId, 0, 0, CharacterSaves.ToSaved(item, Program.Items.Mods))];
            return item;
        }
        var main = Wear(weapon, 2) ?? (weapon == "Two Hand Mace" ? Wear("One Hand Mace", 2)
            : weapon == "Claw" ? Wear("Dagger", 2) : null)
            ?? throw new InvalidDataException($"No wearable {weapon} for controlled {characterClass}");
        if (Program.Items.ByHash(main.TypeHash)?.Class is "Wand" or "One Hand Sword" or "Claw" or "Dagger" or "One Hand Mace") Wear("Shield", 3);
        Wear("Body Armour", 1); Wear("Helmet", 4); Wear("Gloves", 8); Wear("Boots", 9); Wear("Belt", 10);
        var gems = ControlledGems(characterClass, level, Program.Items.ByHash(main.TypeHash)!);
        if (gems.Count == 0) throw new InvalidDataException($"No usable {skill} for controlled {characterClass}");
        main = main with { Sockets = SocketedGems(gems), Links = [(byte)gems.Count] };
        inventory[2] = [inventory[2][0] with { Item = CharacterSaves.ToSaved(main, Program.Items.Mods) }];
        return new(inventory, Experience: CharacterStats.ExperienceFor(level), Passives: nodes,
            SkillBar: new() { [0] = "Action/Move", [1] = Program.Skills.EffectId(Program.Items.GemEffects(gems[0]).First())! },
            ModuleData: new() { ["PlayerBots.BuildVersion"] = "1" });
    }

    /// <summary>The gems a controlled character carries in its weapon at that level: its one active skill, at the highest
    /// level its attributes allow, and then the supports that resolve for that weapon's class. Shared by the starting build
    /// and by the gear pass, which replaces the weapon and must not take the bot's only skill with it
    /// (<see cref="EquipControlledGear"/>). Empty when the class has no usable gem at that level.</summary>
    internal static List<Item> ControlledGems(CharacterClass characterClass, int level, ItemCatalog.BaseType weapon)
    {
        level = Math.Clamp(level, 1, 100);
        bool spell = characterClass is CharacterClass.Witch or CharacterClass.Templar;
        bool bow = characterClass == CharacterClass.Ranger;
        string skill = characterClass switch
        {
            CharacterClass.Witch or CharacterClass.Templar => "Fireball",
            // The ladder's bow skill (poe.qq.com's s29 ranking, 2026-04-13: lightning arrow leads the ranger's actives).
            // Split Arrow was the beginner gem, with a fraction of its clear.
            // Lightning Arrow's gem drops at level 12; below that a bow user has Split Arrow and nothing else.
            CharacterClass.Ranger => level >= 12 ? "Lightning Arrow" : "Split Arrow",
            CharacterClass.Marauder => "Ground Slam",
            CharacterClass.Shadow => "Frost Blades", _ => "Cleave",
        };
        var (str, dex, intel) = ControlledAttributes(characterClass, level);
        Item? Gem(string name)
        {
            var type = Program.Items.All.FirstOrDefault(t => t.Name == name && Program.Items.Gem(t) is { IsVaalOrAwakened: false });
            if (type is null || Program.Items.Gem(type) is not { } data) return null;
            int effect = data.Variant(0).Effect;
            int gemLevel = Math.Min(data.MaxLevel, Program.Skills.MaxLevelAt(effect, level));
            while (gemLevel > 0)
            {
                int required = Program.Skills.PlayerLevelReq(effect, gemLevel);
                if (required <= level && SkillCatalog.GemAttributeRequirement(data.StrengthPercent, required, data.IsSupport) <= str
                    && SkillCatalog.GemAttributeRequirement(data.DexterityPercent, required, data.IsSupport) <= dex
                    && SkillCatalog.GemAttributeRequirement(data.IntelligencePercent, required, data.IsSupport) <= intel)
                    return ItemCatalog.Create(type) with { GemExperience = data.ExperienceFor(gemLevel) };
                gemLevel--;
            }
            return null;
        }
        if (Gem(skill) is not { } active) return [];
        // The ladder's ordinary links (poe.qq.com's s29 ranking, 2026-04-13), narrowed to the supports this server's
        // damage pass actually applies (GemSupportDamage's more-damage list): physical melee stacks physical multipliers,
        // the bow its element ones, the casters the elemental ones. Each is tried in order and must resolve for the
        // active gem, so a base with fewer sockets simply links fewer.
        string[] candidates = spell
            ? ["Controlled Destruction Support", "Elemental Focus Support", "Concentrated Effect Support",
               "Efficacy Support", "Faster Casting Support", "Added Lightning Damage Support",
               "Increased Critical Strikes Support", "Increased Critical Damage Support"]
            : bow
            ? ["Elemental Damage with Attacks Support", "Added Cold Damage Support", "Added Lightning Damage Support",
               "Cruelty Support", "Increased Critical Strikes Support", "Pierce Support",
               "Faster Attacks Support", "Increased Critical Damage Support"]
            : ["Melee Physical Damage Support", "Brutality Support", "Pulverise Support", "Cruelty Support",
               "Maim Support", "Faster Attacks Support", "Added Fire Damage Support", "Increased Critical Strikes Support"];
        int sockets = SocketsFor(weapon);
        var gems = new List<Item> { active };
        foreach (string supportName in candidates)
        {
            if (gems.Count >= sockets) break;
            if (Gem(supportName) is not { } support) continue;
            int effect = Program.Items.Gem(Program.Items.ByHash(support.TypeHash)!)!.Variant(support.GemVariant).Effect;
            if (Supports.Resolve(Program.Items.GemEffects(active).First(), [new(effect, Program.Items.GemLevel(support), 0, gems.Count)], Program.Skills,
                weapon.ClassRow).Count > 0) gems.Add(support);
        }
        return gems;
    }

    /// <summary>How many sockets a weapon's base class takes, so the link is as long as the item allows: six for the
    /// two-handers, bows and staves, three for the one-handers (the client's own socket limits).</summary>
    internal static int SocketsFor(ItemCatalog.BaseType weapon) => weapon.Class switch
    {
        "Two Hand Mace" or "Two Hand Axe" or "Two Hand Sword" or "Bow" or "Staff" => 6,
        _ => 3,
    };

    /// <summary>Those gems as sockets in the order they are linked.</summary>
    internal static ItemSocket[] SocketedGems(IReadOnlyList<Item> gems) =>
        [.. gems.Select(g => new ItemSocket((SocketColour)(Program.Items.Gem(Program.Items.ByHash(g.TypeHash)!)!.Colour), g))];

    /// <summary>The passives a controlled character of that class and level allocates: a conservative, ordinary allocation
    /// that prefers attributes, life and its own damage type. Shared by its starting gear and by the gear it is given later
    /// (<see cref="ControlledAttributes"/>).</summary>
    internal static List<ushort> ControlledNodes(CharacterClass characterClass, int level)
    {
        level = Math.Clamp(level, 1, 100);
        var cls = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)characterClass));
        bool spell = characterClass is CharacterClass.Witch or CharacterClass.Templar;
        bool bow = characterClass == CharacterClass.Ranger;
        var nodes = new List<ushort>();
        double Score(ushort id)
        {
            var n = Program.Passives.Find(id);
            if (n is null || n.IsKeystone || n.IsJewelSocket) return -1000;
            double score = .1;
            foreach (var (key, value) in n.Stats)
            {
                uint name = key;
                if (name is Stat.BaseStrength or Stat.BaseDexterity or Stat.BaseIntelligence || name == AttributeModifiers.BaseAllAttributes) score += value * .65;
                if (name is Stat.MaximumLifePlusPct or Stat.BaseMaximumLife) score += value * 2;
                if (name is Stat.BaseResistAllElementsPct) score += value;
                if (name == Stat.DamagePlusPct || spell && name is Stat.SpellDamagePlusPct or Stat.FireDamagePlusPct
                    || !spell && name is Stat.AttackDamagePlusPct or Stat.PhysicalDamagePlusPct
                    || bow && name == Stat.ProjectileDamagePlusPct) score += value;
                if (name == (spell ? Stat.BaseCastSpeedPlusPct : Stat.AttackSpeedPlusPct)) score += value * 2;
            }
            return score;
        }
        for (int point = 0; point < level - 1; point++)
        {
            var frontier = OrdinaryPassives.Frontier((byte)cls.IntegerId, AscendancyFamily.Normal, nodes)
                .Where(id => Score(id) >= 0).ToArray();
            if (frontier.Length == 0) break;
            var pick = frontier.OrderByDescending(id => Score(id) + .5 * OrdinaryPassives.Adjacent(id, AscendancyFamily.Normal)
                .Where(next => !nodes.Contains(next)).Select(Score).DefaultIfEmpty(0).Max()).ThenBy(id => id).First();
            nodes.Add(pick);
        }
        OrdinaryPassives.Validate((byte)cls.IntegerId, AscendancyFamily.Normal, nodes);
        return nodes;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(CharacterClass Class, int Level), (int Str, int Dex, int Int)> AttributeCache = new();

    /// <summary>The strength, dexterity and intelligence that build reaches at that level (base attributes plus the
    /// allocation's attribute nodes): the highest requirements a base item it wears may ask for. Cached, since every bot's
    /// gear is picked through it.</summary>
    internal static (int Str, int Dex, int Int) ControlledAttributes(CharacterClass characterClass, int level)
    {
        level = Math.Clamp(level, 1, 100);
        return AttributeCache.GetOrAdd((characterClass, level), key =>
        {
            var cls = Program.Skills.ClassFor(Program.Skills.RowOfClass((int)key.Class));
            var passiveStats = Program.Passives.Stats(ControlledNodes(key.Class, key.Level));
            int Attr(uint stat, int basis) => basis + passiveStats.GetValueOrDefault(stat)
                + passiveStats.GetValueOrDefault(AttributeModifiers.BaseAllAttributes);
            return (Attr(Stat.BaseStrength, cls.Strength), Attr(Stat.BaseDexterity, cls.Dexterity), Attr(Stat.BaseIntelligence, cls.Intelligence));
        });
    }

    /// <summary>Whether a base item's own attribute requirements are within what that class reaches at that level: the check
    /// behind the gear bots are given, so none is dressed in something it could not wear.</summary>
    internal static bool GearFitsClass(CharacterClass characterClass, int level, ItemCatalog.BaseType type)
    {
        var (str, dex, intel) = ControlledAttributes(characterClass, level);
        var req = Program.Items.Requirements(type);
        return req.Str <= str && req.Dex <= dex && req.Int <= intel;
    }
}
