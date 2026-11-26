using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PlayerBots;

/// <summary>What a bot wears: real base items of the game data (BaseItemTypes) a character of its class and level would
/// use, drawn on its model as the server draws a player's equipment (Equipment.Looks: the inventory row's model slot,
/// the base's ItemVisualIdentity, stance and class; a bow as BowSetup records it).</summary>
static class BotGear
{
    // Model slots (PlayerInventories.VisibleSlots): main hand 0, off hand 1, body 2, helmet 4, gloves 5, boots 6, belt 10.
    const byte MainHand = 0, OffHand = 1, Body = 2, Helmet = 4, Gloves = 5, Boots = 6, Belt = 10, Back = 7;
    static readonly byte[] Slots = [0, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11];
    const ushort DefaultArrow = 0xb2f6, DefaultQuiver = 0x328b;
    const byte NoWeaponType = 0x68, NoStance = 0xff;

    // The weapons a class is played with: (main hand classes, off hand class or null).
    static readonly Dictionary<CharacterClass, (string[] Main, string? Off)[]> Weapons = new()
    {
        [CharacterClass.Marauder] = [(["Two Hand Mace", "Two Hand Axe"], null), (["One Hand Mace", "One Hand Axe"], "Shield")],
        [CharacterClass.Ranger] = [(["Bow"], null)],
        // The caster's six-socket weapon: a staff, with the wand-and-shield entry as the quarter-time alternative.
        [CharacterClass.Witch] = [(["Staff"], null), (["Wand"], "Shield")],
        [CharacterClass.Duelist] = [(["Two Hand Sword"], null), (["One Hand Sword", "Thrusting One Hand Sword"], "Shield")],
        [CharacterClass.Templar] = [(["Staff"], null), (["Sceptre"], "Shield")],
        [CharacterClass.Shadow] = [(["Dagger"], null), (["Claw"], null)],
        [CharacterClass.Scion] = [(["One Hand Sword"], "Shield"), (["Two Hand Sword"], null)],
    };

    // The armour tag of a class's attributes (BaseItemTypes tags).
    static readonly Dictionary<CharacterClass, string> ArmourTag = new()
    {
        [CharacterClass.Marauder] = "str_armour", [CharacterClass.Ranger] = "dex_armour", [CharacterClass.Witch] = "int_armour",
        [CharacterClass.Duelist] = "str_dex_armour", [CharacterClass.Templar] = "str_int_armour", [CharacterClass.Shadow] = "dex_int_armour",
        [CharacterClass.Scion] = "str_dex_int_armour",
    };

    /// <summary>New gear for the bot at its level: base item Ids (BaseItemTypes), main hand first. A dual wielder holds the
    /// same weapon class in both hands.</summary>
    public static List<string> Pick(CharacterClass cls, int level, Random random)
    {
        var gear = new List<string>();
        // The class's damage build comes first — the two-hander, the bow, the staff — because its six sockets are six
        // gems (ControlledBuilds.SocketsFor). The one-handed entry is a quarter of the picks, for a bot with a shield.
        var options = Weapons[cls];
        var (main, off) = options[random.Next(4) == 0 && options.Length > 1 ? 1 : 0];
        if (Best(main, cls, level, random) is { } weapon) gear.Add(weapon.Id);
        if (off is not null && Best([off], cls, level, random, ArmourTag[cls]) is { } shield) gear.Add(shield.Id);
        else if (cls == CharacterClass.Shadow && Best(main, cls, level, random) is { } second) gear.Add(second.Id);
        foreach (string piece in new[] { "Helmet", "Body Armour", "Gloves", "Boots" })
            if (Best([piece], cls, level, random, ArmourTag[cls]) is { } armour) gear.Add(armour.Id);
        if (Best(["Belt"], cls, level, random) is { } belt) gear.Add(belt.Id);
        return gear;
    }

    // One of the two highest bases of the classes a character of this level can wear (DropLevel), with the tag if given, and
    // only one whose own attribute requirements the character's allocation meets (BotHooks.GearFitsClass): worn
    // anyway it would read "requirements not met" and give nothing. When nothing of that class fits at all (a low-level
    // caster asked for strength armour) the unfiltered pick is kept: better than an empty slot.
    static ItemCatalog.BaseType? Best(string[] classes, CharacterClass cls, int level, Random random, string? tag = null)
    {
        IEnumerable<ItemCatalog.BaseType> Candidates(bool byAttributes) => Program.Items.All
            .Where(t => classes.Contains(t.Class) && t.VisualIdentity != 0 && t.DropLevel <= Math.Max(1, level)
                && t.Id.StartsWith("Metadata/Items/", StringComparison.Ordinal) && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                && !t.Id.Contains("Royale", StringComparison.Ordinal) && !t.Id.Contains("Talisman", StringComparison.Ordinal)
                && (tag is null || t.Tags.Contains(tag) || !t.Tags.Any(g => g.EndsWith("_armour", StringComparison.Ordinal) || g.EndsWith("_shield", StringComparison.Ordinal)))
                && (!byAttributes || BotPlay.GearFitsClass(cls, Math.Max(1, level), t)));
        var fitting = Candidates(true).OrderByDescending(t => t.DropLevel).Take(2).ToList();
        if (fitting.Count == 0) fitting = Candidates(false).OrderByDescending(t => t.DropLevel).Take(2).ToList();
        return fitting.Count == 0 ? null : fitting[random.Next(fitting.Count)];
    }

    /// <summary>The model's entries for the gear, every visible slot (an empty one where nothing is worn). <paramref
    /// name="seed"/> is what a cosmetic pick is stable by (the bot's name): the game module answers a skin for each base
    /// item, only one whose ItemClasses name that item's class.</summary>
    public static IReadOnlyList<VisibleItem> Looks(IReadOnlyList<string> gear, string seed)
    {
        var looks = Slots.ToDictionary(s => s, VisibleItem.Empty);
        var types = gear.Select(id => Program.Items.All.FirstOrDefault(t => t.Id == id)).Where(t => t is not null).Select(t => t!).ToList();
        bool firstHand = true;
        foreach (var t in types)
        {
            var look = new VisibleItem(0, t.VisualIdentity, t.Stance, t.ClassRow is >= 0 and < 255 ? (byte)(t.ClassRow + 1) : (byte)1);
            byte? slot = t.Class switch
            {
                "Helmet" => Helmet, "Body Armour" => Body, "Gloves" => Gloves, "Boots" => Boots, "Belt" => Belt,
                "Shield" => OffHand,
                _ when firstHand => MainHand,
                _ => OffHand,
            };
            if (slot is MainHand) firstHand = false;
            var skin = BotPlay.BotSkin(t.Id, seed);
            if (t.Class == "Bow")
            {
                // Held in the left hand, an arrow in the right, the quiver on the back (BowSetup). Its skin dresses the bow's
                // own entry (the off hand's), as a player's bow skin does (Equipment.Looks moves slot 0 to 1 for a bow).
                looks[MainHand] = new VisibleItem(MainHand, DefaultArrow) { Type = look.Type, Stance = t.Stance };
                looks[OffHand] = new VisibleItem(OffHand, t.VisualIdentity) { Type = NoWeaponType, Stance = NoStance };
                looks[Back] = new VisibleItem(Back, DefaultQuiver) { Type = NoWeaponType, Stance = NoStance };
                if (skin is { } bowSkin) looks[OffHand] = looks[OffHand] with { SkinVisual = bowSkin.Visual, SkinHash = bowSkin.Hash };
                continue;
            }
            looks[slot.Value] = look with { Slot = slot.Value };
            if (skin is { } worn) looks[slot.Value] = looks[slot.Value] with { SkinVisual = worn.Visual, SkinHash = worn.Hash };
        }
        return [.. looks.OrderBy(e => e.Key).Select(e => e.Value)];
    }

    /// <summary>The gear's names for the log.</summary>
    public static string Describe(IReadOnlyList<string> gear) => string.Join(", ",
        gear.Select(id => Program.Items.All.FirstOrDefault(t => t.Id == id)?.Name).Where(n => n is not null));
}
