using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.CurrencyLoot;

/// <summary>What a kill drops in currency, item by item: each entry is how many of it every kill drops, and a fraction
/// is the chance of one more (0.05 is a 5% chance, 2.5 is two and a half). Either beside the server's own drops or
/// instead of them. The listed currency waits for its act, as the game's own tiers do: Chaos Orbs from the act set
/// below and the rest of the list from a later one, so a new character is not showered with Exalted Orbs.
/// Off by default: /mod_enable currencyloot.</summary>
public sealed class CurrencyLootMod : IMod
{
    sealed record Choices(string Mode, string[]? Rarities, Dictionary<string, double>? Items, List<string>? Monsters,
        int ChaosAct, int HighAct);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        var rarities = (choices.Rarities ?? []).Select(r => Enum.Parse<MonsterRarity>(r, ignoreCase: true)).ToHashSet();
        // "only": nothing of the server's own drops (equipment, gems, flasks, its own currency); the items below are all.
        bool only = string.Equals(choices.Mode, "only", StringComparison.OrdinalIgnoreCase);
        var monsters = (choices.Monsters ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();
        var items = (choices.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Key) && i.Value > 0).ToArray();
        int chaosAct = Math.Clamp(choices.ChaosAct, 1, 10), highAct = Math.Clamp(choices.HighAct, 1, 10);
        mod.Log($"{(only ? "only" : "besides")} the server's drops: {items.Length} item(s), rarities "
            + (rarities.Count == 0 ? "all" : string.Join("/", rarities.Select(r => r.ToString().ToLowerInvariant())))
            + $", Chaos Orbs from act {chaosAct}, the rest from act {highAct}"
            + (monsters.Length == 0 ? ", every monster" : $", monsters matching {string.Join(", ", monsters)}"));
        GameEvents.Drops.Add(mod.Manifest.Id, drops =>
        {
            // No rarities chosen means every monster: an empty set would otherwise drop nothing at all.
            if (rarities.Count > 0 && !rarities.Contains(drops.Monster.Rarity)) return;
            if (monsters.Length > 0 && !monsters.Any(m =>
                    drops.Monster.VarietyId.Contains(m, StringComparison.OrdinalIgnoreCase)
                    || drops.Monster.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) return;
            if (only) drops.Cancel = true;
            int act = ActOf(drops.Conn.Layout.Id);
            foreach (var (item, count) in items)
            {
                // Each currency waits for its act, as the game's own drop tables do: a Chaos Orb later than the basics,
                // everything above it later still. An Atlas map always counts as past the last act.
                if (act < (IsChaosOrb(item) ? chaosAct : highAct)) continue;
                // The whole part always drops; the fraction is the chance of one more, so 0.05 is a 5% chance and 2.5 is
                // two plus a half.
                int whole = (int)Math.Floor(count);
                for (int i = 0; i < whole; i++) drops.Items.Add(item);
                if (Random.Shared.NextDouble() < count - whole) drops.Items.Add(item);
            }
        });
    }

    /// <summary>The Chaos Orb's own name or BaseItemTypes id (CurrencyRare; CurrencyUpgradeToRare is the Orb of Alchemy).</summary>
    static bool IsChaosOrb(string item) =>
        item.Trim().Equals("Chaos Orb", StringComparison.OrdinalIgnoreCase)
        || item.EndsWith("/CurrencyRare", StringComparison.OrdinalIgnoreCase);

    /// <summary>The act an area belongs to (1 … 10), or 10 for an Atlas map. The campaign's area ids are "1_&lt;act&gt;_…"
    /// for the first part and "2_&lt;act&gt;_…" for the second (Act 6 is "2_6_…"); anything else — a hideout, the mine —
    /// counts as 0, so the listed currency does not drop there.</summary>
    static int ActOf(string area)
    {
        if (area.StartsWith("MapWorlds", StringComparison.Ordinal)) return 10;
        if (!area.StartsWith("1_", StringComparison.Ordinal) && !area.StartsWith("2_", StringComparison.Ordinal)) return 0;
        int end = area.IndexOf('_', 2);
        return int.TryParse(end < 0 ? area[2..] : area[2..end], out int act) ? Math.Clamp(act, 1, 10) : 0;
    }
}
