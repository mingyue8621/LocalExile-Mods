using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.MapDrop;

/// <summary>What a kill drops in maps, the way the game does it: maps start dropping in Act 8 (the lowest tier only
/// during the campaign), and inside a map a normal monster drops the area's tier, magic and rare monsters one above it
/// and a map boss two above (poewiki, Maps). A map area of this server spawns far fewer monsters than the game's, so the
/// server's own per-kill roll alone leaves a character without maps to run.
/// Off by default: /mod_enable mapdrop.</summary>
public sealed class MapDropMod : IMod
{
    sealed record Choices(double Normal, double Magic, double Rare, double Unique, bool FollowMap, int Tier,
        int MaxTier, bool Campaign, List<string>? Monsters);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        var monsters = (choices.Monsters ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
        int fixedTier = Math.Clamp(choices.Tier, 1, 16);
        int maxTier = Math.Clamp(choices.MaxTier, 1, 16);
        double Chance(MonsterRarity rarity) => Math.Clamp(rarity switch
        {
            MonsterRarity.Magic => choices.Magic,
            MonsterRarity.Rare => choices.Rare,
            MonsterRarity.Unique => choices.Unique,
            _ => choices.Normal,
        }, 0, 1);
        mod.Log($"maps: normal {choices.Normal:P2}, magic {choices.Magic:P2}, rare {choices.Rare:P2}, unique {choices.Unique:P2} per kill; "
            + (choices.FollowMap ? "a map's own tier (magic/rare +1, bosses +2)" : $"tier {fixedTier}")
            + $", at most {maxTier}"
            + (choices.Campaign ? ", in every act" : ", from Act 8 on and in maps")
            + (monsters.Length == 0 ? ", every monster" : $", monsters matching {string.Join(", ", monsters)}"));
        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            // The game's own rules: the first maps appear in Act 8, and the campaign only ever gives the lowest tier.
            bool inMap = InMap(drops);
            if (!choices.Campaign && !MapsDropHere(drops)) return;
            if (monsters.Length > 0 && !monsters.Any(m =>
                    drops.Monster.VarietyId.Contains(m, StringComparison.OrdinalIgnoreCase)
                    || drops.Monster.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return;
            double chance = Chance(drops.Monster.Rarity);
            if (chance <= 0 || Random.Shared.NextDouble() >= chance) return;
            int? open = OpenTier(drops);
            int tier = choices.FollowMap && open is { } at ? at : fixedTier;
            // Magic and rare monsters drop one tier above the map they are in, a map's boss two (poewiki, Maps). The
            // campaign has no such bonus: it only drops the lowest tier.
            if (inMap)
                tier += drops.Monster.Rarity switch
                {
                    MonsterRarity.Magic or MonsterRarity.Rare => 1,
                    MonsterRarity.Unique => 2,
                    _ => 0,
                };
            // The map keys are generic bases named by their tier: a BaseItemTypes name the drop pipeline resolves.
            drops.Items.Add($"Map (Tier {Math.Clamp(tier, 1, maxTier)})");
        });
    }

    /// <summary>Whether maps drop in this area at all. The game starts them in Act 8 (the second half of the campaign,
    /// WorldAreas ids "2_8_…" through "2_10_…") and the Atlas maps; Acts 1 to 7, the hubs and the mine give none.</summary>
    static bool MapsDropHere(DropRoll drops)
    {
        string area = drops.Conn.Layout.Id;
        if (InMap(drops)) return true;
        if (!area.StartsWith("2_", StringComparison.Ordinal)) return false;
        int end = area.IndexOf('_', 2);
        return int.TryParse(end < 0 ? area[2..] : area[2..end], out int act) && act >= 8;
    }

    /// <summary>An Atlas map area: the id still says so when the open-map record is gone (a restart).</summary>
    static bool InMap(DropRoll drops) => drops.Conn.Layout.Id.StartsWith("MapWorlds", StringComparison.Ordinal);

    /// <summary>The tier of the map the kill's character has open, when they are in it (the tier the server's own map
    /// roll would use); null anywhere else, so the fixed tier applies.</summary>
    static int? OpenTier(DropRoll drops)
    {
        if (drops.Conn.Character is not { Length: > 0 } character) return null;
        return MapDevice.OpenOf(character) is { } open && open.Area.Hash16 == drops.Conn.Layout.Hash
            ? Math.Clamp(open.Area.Tier, 1, 16) : null;
    }
}
