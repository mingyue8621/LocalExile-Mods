using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.MapDrop;

/// <summary>What a kill drops in maps, as Currency Loot does for currency: a chance by monster rarity, of the tier the
/// player's open map is played at (or a fixed tier), bosses always. A map area of this server spawns far fewer monsters
/// than the game's, so the server's own per-kill roll alone leaves a character without maps to run.
/// Off by default: /mod_enable mapdrop.</summary>
public sealed class MapDropMod : IMod
{
    sealed record Choices(double Normal, double Magic, double Rare, double Unique, bool FollowMap, int Tier, List<string>? Monsters);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        var monsters = (choices.Monsters ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
        int fixedTier = Math.Clamp(choices.Tier, 1, 16);
        double Chance(MonsterRarity rarity) => Math.Clamp(rarity switch
        {
            MonsterRarity.Magic => choices.Magic,
            MonsterRarity.Rare => choices.Rare,
            MonsterRarity.Unique => choices.Unique,
            _ => choices.Normal,
        }, 0, 1);
        mod.Log($"maps: normal {choices.Normal:P2}, magic {choices.Magic:P2}, rare {choices.Rare:P2}, unique {choices.Unique:P2} per kill; "
            + (choices.FollowMap ? "the open map's tier" : $"tier {fixedTier}")
            + (monsters.Length == 0 ? ", every monster" : $", monsters matching {string.Join(", ", monsters)}"));
        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            if (monsters.Length > 0 && !monsters.Any(m =>
                    drops.Monster.VarietyId.Contains(m, StringComparison.OrdinalIgnoreCase)
                    || drops.Monster.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return;
            double chance = Chance(drops.Monster.Rarity);
            if (chance <= 0 || Random.Shared.NextDouble() >= chance) return;
            int tier = choices.FollowMap && OpenTier(drops) is int open ? open : fixedTier;
            // The map keys are generic bases named by their tier: a BaseItemTypes name the drop pipeline resolves.
            drops.Items.Add($"Map (Tier {tier})");
        });
    }

    /// <summary>The tier of the map the kill's character has open, when they are in it (the tier the server's own map
    /// roll would use); null anywhere else, so the fixed tier applies.</summary>
    static int? OpenTier(DropRoll drops)
    {
        if (drops.Conn.Character is not { Length: > 0 } character) return null;
        return MapDevice.OpenOf(character) is { } open && open.Area.Hash16 == drops.Conn.Layout.Hash
            ? Math.Clamp(open.Area.Tier, 1, 16) : null;
    }
}
