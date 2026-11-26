using GameServer.Core.Protocol.Instance;

using static Game;

namespace GameServer.Modules.PlayerBots;

public static partial class BotPlay
{
    // ---- what a party bot is wearing ----
    // A controlled player used to carry no items at all: it fought unarmed (WeaponFor falls back to the class's unarmed
    // skill, so its hits were fists) and every other client drew it bare. The PlayerBots module picks gear for the bot's
    // class and level (BotGear); this puts those base items on the character as real equipment, so the model shows them
    // and the weapon's damage counts.

    /// <summary>Wears the gear <see cref="BotHooks.Gear"/> names for this character, at the given level. Replaces
    /// whatever is in those slots, so a bot that came back at another level is dressed for that level.</summary>
    internal static void EquipControlledGear(Player conn, int level)
    {
        if (BotHooks.Gear?.Invoke(Program.CharacterName(conn)) is not { Count: > 0 } ids) return;
        var items = ItemsOf(conn);
        var catalog = ItemsFor(conn);
        var used = new HashSet<byte>();
        // One draw per character and level: the same bot wears the same gear after a relog, and rolls again only when it
        // levels (a bot re-rolled on every join would change its weapon's affixes under the party's feet).
        var rng = new Random(StableSeed(Program.CharacterName(conn), level));
        int worn = 0, carried = 0;
        foreach (string id in ids)
        {
            if (catalog.All.FirstOrDefault(t => t.Id == id) is not { } type || RowFor(type, used) is not { } row) continue;
            used.Add(row);
            // The gems socketed in what this replaces go on its replacement: a bot's only skill is the gem in its weapon, and
            // replacing the weapon without it left the follower with no skill at all, so it stood in every fight doing
            // nothing (live 2026-11-09: "the party bots do not attack"). A bot whose weapon lost its gems that way gets its
            // class's set back here, instead of staying mute for good.
            var gems = items.Items(row).SelectMany(old => old.Item.Sockets).Where(s => s.Gem is not null).ToList();
            if (gems.Count == 0 && row == PlayerInventories.Weapon1)
                gems = [.. SocketedGems(ControlledGems(
                    (GameServer.Core.Accounts.CharacterClass)SkillsFor(conn).ClassFor(conn.Character.ClassRow).IntegerId, level, type))];
            foreach (var old in items.Items(row).ToList()) items.Remove(row, old.Id);
            var fresh = ItemCatalog.Create(type) with
            {
                ItemLevel = (byte)Math.Clamp(level, 1, 100),
                // A bot's gear used to be white — base items with no affixes at all — so its weapon had only the base's
                // own damage and its armour nothing at all. The ladder's gear is rare, and a rare weapon carries a damage
                // prefix: that is most of an ordinary build's damage (2026-11-10).
                Quality = (byte)(level >= 20 ? 20 : 0),
            };
            fresh = Program.Rolls.Roll(fresh, ItemRarity.Rare, rng,
                guaranteed: DamageAffix(type, level, spell: type.Class is "Wand" or "Sceptre" or "Staff"), maxModLevel: level);
            if (gems.Count > 0) fresh = fresh with { Sockets = [.. gems], Links = [(byte)Math.Min(gems.Count, 6)] };
            items.Add(row, 0, 0, fresh);
            worn++;
            carried += gems.Count;
        }
        if (worn > 0) Console.WriteLine($"[bots] {Program.CharacterName(conn)} wears {worn} piece(s) at level {level}, {carried} gem(s) in the weapon");
        EquipControlledFlasks(level, items, catalog);
    }

    /// <summary>The damage prefix a weapon of this base is guaranteed to carry: local physical damage for a weapon that
    /// hits, spell damage for a caster's. The highest the item level allows. Null for armour and for a base no such
    /// modifier fits (the roll then stays ordinary).</summary>
    internal static ModCatalog.Mod? DamageAffix(ItemCatalog.BaseType type, int level, bool spell)
    {
        uint stat = spell ? Stat.SpellDamagePlusPct : Stat.LocalPhysicalDamagePlusPct;
        return Program.Items.Mods.All
            .Where(m => m.Domain == 1 && m.GenerationType == ItemRolls.Prefix && m.Level <= level && !m.EssenceOnly
                && m.WeightFor(type.Tags) > 0 && m.Stats.Any(s => s.Id == stat))
            .OrderByDescending(m => m.Level)
            .FirstOrDefault();
    }

    /// <summary>A draw stable for one character at one level (FNV-1a over its name, mixed with the level).</summary>
    static int StableSeed(string name, int level)
    {
        uint hash = 2166136261;
        foreach (char c in name) { hash ^= c; hash *= 16777619; }
        return (int)(hash ^ (uint)level * 2654435761u);
    }

    /// <summary>The flasks a bot of this level carries: the best life and mana flask its level allows. A bot carried the
    /// level-1 starter flasks (or none), so a fight longer than its mana lasted left it unable to swing a 12-mana Cleave:
    /// five characters stood in front of a boss doing nothing (live 2026-11-10).</summary>
    static void EquipControlledFlasks(int level, PlayerInventories items, ItemCatalog catalog)
    {
        var best = catalog.All
            .Where(t => t.Class is "LifeFlask" or "ManaFlask" && t.DropLevel > 0 && t.DropLevel <= level)
            .GroupBy(t => t.Class)
            .Select(g => g.OrderByDescending(t => t.DropLevel).First())
            .OrderBy(t => t.Class == "LifeFlask" ? 0 : 1)
            .ToList();
        if (best.Count == 0) return;
        foreach (var old in items.Items(PlayerInventories.Flask).ToList()) items.Remove(PlayerInventories.Flask, old.Id);
        byte slot = 0;
        foreach (var type in best) items.Add(PlayerInventories.Flask, slot++, 0, ItemCatalog.Create(type));
        Console.WriteLine($"[bots] flasks for level {level}: {string.Join(", ", best.Select(t => t.Name))}");
    }

    // The slot a base item is worn in: the hand that takes that weapon class, the shield or quiver hand, or the armour row
    // its class names (PlayerInventories.Accepts knows the class lists). A row already given a piece in this pass is skipped,
    // so a dual wielder's second one-hander goes to the off hand instead of replacing the first.
    static byte? RowFor(ItemCatalog.BaseType type, HashSet<byte> used) =>
        new[]
        {
            PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.BodyArmour, PlayerInventories.Helmet,
            PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Belt,
        }.Cast<byte?>().FirstOrDefault(row => !used.Contains(row!.Value) && PlayerInventories.Accepts(row.Value, type.Class));
}
