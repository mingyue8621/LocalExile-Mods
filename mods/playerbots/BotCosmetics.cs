using GameServer.Core.Protocol.Instance;

using static Game;

namespace GameServer.Modules.PlayerBots;

public static partial class BotPlay
{
    // ---- cosmetics for bots ----
    // A bot's look follows its class and weapon: an armour skin on the row that item is worn in, a weapon skin on the hand
    // that holds it — and only where the cosmetic's own ItemClasses name that item's class (CosmeticEffects.FitsItem), so a
    // bow skin can never dress a sceptre. 3201 of the 4545 ItemisedVisualEffect rows carry a visual with such a list (live
    // data 3.29.3c), so there is always something to pick; the 13 with art but no list are left out, since nothing says
    // what they belong on. The pick is stable: the same character and item always give the same skin.

    /// <summary>The Inventories.dat model slot an equipment row is drawn in (the numbers PlayerInventories.VisibleSlots
    /// maps its rows to).</summary>
    static byte ModelSlotOf(byte row) => row switch
    {
        PlayerInventories.Weapon1 or PlayerInventories.Weapon2 => 0,
        PlayerInventories.Offhand1 or PlayerInventories.Offhand2 => 1,
        PlayerInventories.BodyArmour => 2,
        PlayerInventories.Helmet => 4,
        PlayerInventories.Gloves => 5,
        PlayerInventories.Boots => 6,
        PlayerInventories.Belt => 10,
        _ => 16,
    };

    /// <summary>The Cosmetics tab slot that dresses a model slot (its MicrotransactionSlot __col7), the one whose name is
    /// the item's kind when several share the slot.</summary>
    static byte? CosmeticSlotFor(byte model, string? name)
    {
        var slots = MtxEffects.Slots.Where(s => s.Value.ModelSlot == model).ToList();
        if (slots.Count == 0) return null;
        var named = slots.Where(s => name is not null && s.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count > 0 ? named[0].Key : slots[0].Key;
    }

    // The cosmetics that fit a class, built once (a bot's body is published many times a second, so neither the scan over
    // every owned microtransaction nor the pick may run per publish), and the pick itself per (item, seed).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, List<InstanceMicrotransactions.Record>> SkinsByClass = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Item, string Seed), (uint, ushort)?> SkinPicks = new();

    /// <summary>A skin for that base item, stable by seed and item: an owned cosmetic whose effect has art and whose
    /// ItemClasses list this item's class. Null when nothing fits it.</summary>
    static InstanceMicrotransactions.Record? SkinFor(ItemCatalog.BaseType type, string seed)
    {
        var fits = SkinsByClass.GetOrAdd(type.ClassRow, classRow =>
        {
            List<InstanceMicrotransactions.Record> found = [];
            foreach (var record in Mtx.Owned)
                if (MtxEffects.Of(record.MtxTypeHash) is { Visual: not 0 } fx && fx.ItemClasses.Count > 0
                    && fx.ItemClasses.Contains(classRow))
                    found.Add(record);
            return found;
        });
        if (fits.Count == 0) return null;
        uint hash = 2166136261;
        foreach (char c in seed + "|" + type.Id) hash = unchecked((hash ^ c) * 16777619);
        return fits[(int)(hash % (uint)fits.Count)];
    }

    /// <summary>The skin a base item takes, as (MtxTypeGameSpecific hash, ItemVisualIdentity art), for the body of a bot in
    /// town — the PlayerBots module draws it in its own model entries. Null when nothing fits. Cached per item and seed: a
    /// town body is republished every tick.</summary>
    internal static (uint Hash, ushort Visual)? BotSkin(string baseItemId, string seed)
    {
        if (SkinPicks.TryGetValue((baseItemId, seed), out var cached)) return cached;
        (uint, ushort)? answer = null;
        if (Program.Items.All.FirstOrDefault(t => t.Id == baseItemId) is { } type
            && SkinFor(type, seed) is { } record && MtxEffects.Of(record.MtxTypeHash) is { Visual: not 0 } fx)
            answer = (record.MtxTypeHash, fx.Visual);
        SkinPicks[(baseItemId, seed)] = answer;
        return answer;
    }

    /// <summary>Dresses a controlled player (a party bot) in the skins its equipment takes, into the worn table the client's
    /// model is built from (Equipment.Looks: SkinVisual + the hash, as a player's own picks arrive).</summary>
    internal static void WearBotCosmetics(Player conn)
    {
        string name = Program.CharacterName(conn);
        var items = ItemsOf(conn);
        List<InstanceMicrotransactions.Equipped> worn = [];
        foreach (byte row in new[]
                 {
                     PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.BodyArmour, PlayerInventories.Helmet,
                     PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Belt,
                 })
        {
            if (items.Items(row).FirstOrDefault() is not { } held) continue;
            if (ItemsFor(conn).ByHash(held.Item.TypeHash) is not { } type) continue;
            if (CosmeticSlotFor(ModelSlotOf(row), type.Class) is not { } slot) continue;
            if (SkinFor(type, name) is not { } record) continue;
            worn.Add(new InstanceMicrotransactions.Equipped(slot, record.Id, record.MtxTypeHash, record.Qword, 0));
        }
        if (worn.Count == 0) return;
        conn.Character.WornCosmetics = worn;
        Console.WriteLine($"[bots] {name} wears {worn.Count} cosmetic(s)");
    }
}
