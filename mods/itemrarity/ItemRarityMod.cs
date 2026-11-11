using GameServer.Modding;

namespace LocalExile.Mods.ItemRarity;

/// <summary>Better magic, rare and unique equipment odds, each tier on a multiplier of its own: 10 times the chance of
/// every one of them by default, and each can be set apart. Off by default: /mod_enable itemrarity.</summary>
public sealed class ItemRarityMod : IMod
{
    // The defaults are the mod's own multiplier, so a settings file that predates the three tiers still rolls as before
    // instead of dropping nothing.
    sealed record Choices(double Magic = 10, double Rare = 10, double Unique = 10);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        // Multiplied, not set: another mod's rarity stays in, tier by tier.
        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            drops.MagicRarity *= choices.Magic;
            drops.RareRarity *= choices.Rare;
            drops.UniqueRarity *= choices.Unique;
        });
    }
}
