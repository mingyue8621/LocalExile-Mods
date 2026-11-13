using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.CurrencyLoot;

/// <summary>What a kill drops in currency, item by item: each entry is how many of it every kill drops, and a fraction
/// is the chance of one more (0.05 is a 5% chance, 2.5 is two and a half). Either beside the server's own drops or
/// instead of them. Off by default: /mod_enable currencyloot.</summary>
public sealed class CurrencyLootMod : IMod
{
    sealed record Choices(string Mode, string[]? Rarities, Dictionary<string, double>? Items, List<string>? Monsters);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        var rarities = (choices.Rarities ?? []).Select(r => Enum.Parse<MonsterRarity>(r, ignoreCase: true)).ToHashSet();
        // "only": nothing of the server's own drops (equipment, gems, flasks, its own currency); the items below are all.
        bool only = string.Equals(choices.Mode, "only", StringComparison.OrdinalIgnoreCase);
        var monsters = (choices.Monsters ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
        var items = (choices.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Key) && i.Value > 0).ToArray();
        mod.Log($"{(only ? "only" : "besides")} the server's drops: {items.Length} item(s), rarities "
            + (rarities.Count == 0 ? "all" : string.Join("/", rarities.Select(r => r.ToString().ToLowerInvariant())))
            + (monsters.Length == 0 ? ", every monster" : $", monsters matching {string.Join(", ", monsters)}"));
        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            // No rarities chosen means every monster: an empty set would otherwise drop nothing at all.
            if (rarities.Count > 0 && !rarities.Contains(drops.Monster.Rarity)) return;
            if (monsters.Length > 0 && !monsters.Any(m =>
                    drops.Monster.VarietyId.Contains(m, StringComparison.OrdinalIgnoreCase)
                    || drops.Monster.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return;
            if (only) drops.Cancel = true;
            foreach (var (item, count) in items)
            {
                // The whole part always drops; the fraction is the chance of one more, so 0.05 is a 5% chance and 2.5 is
                // two plus a half.
                int whole = (int)Math.Floor(count);
                for (int i = 0; i < whole; i++) drops.Items.Add(item);
                if (Random.Shared.NextDouble() < count - whole) drops.Items.Add(item);
            }
        });
    }
}
