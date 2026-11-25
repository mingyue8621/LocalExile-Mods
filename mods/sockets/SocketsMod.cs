using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace LocalExile.Mods.Sockets;

/// <summary>How many sockets and links a dropped piece of equipment gets, chosen by hand: a range to roll in (both ends
/// the same pins one number), or only raising a low roll, or only capping a high one. The colour is white or the base's
/// attribute colours. Only kills' drops pass through here: shop stock, quest rewards, gambling and strongboxes keep the
/// game's own rolls. Off by default: /mod_enable sockets.</summary>
public sealed class SocketsMod : IMod
{
    // The declared settings as they are read: a range is [low, high] in this file's JSON.
    sealed record Choices(string? Mode, int[]? Sockets, string? LinkMode, int[]? Links,
        string[]? Rarities, string? Colours);

    // The choices, checked once when the mod loads.
    sealed record Rule(int Low, int High, int LinkLow, int LinkHigh, string Mode, string LinkMode, bool White,
        HashSet<string> Rarities);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        int low = Math.Clamp(End(choices.Sockets, 0), 1, SocketLayout.MaximumSockets);
        int high = Math.Clamp(End(choices.Sockets, 1), low, SocketLayout.MaximumSockets);
        int linkLow = Math.Clamp(End(choices.Links, 0), 1, SocketLayout.MaximumSockets);
        int linkHigh = Math.Clamp(End(choices.Links, 1), linkLow, SocketLayout.MaximumSockets);
        // No rarity chosen means every rarity: the setting is a filter, and an empty filter lets everything through.
        var rarities = (choices.Rarities ?? []).Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().ToLowerInvariant()).ToHashSet();
        var rule = new Rule(low, high, linkLow, linkHigh,
            One(choices.Mode, "replace", "replace", "raise", "cap", "links"),
            One(choices.LinkMode, "range", "range", "retail", "full", "none"),
            !string.Equals(choices.Colours?.Trim(), "attribute", StringComparison.OrdinalIgnoreCase),
            rarities);
        mod.Log($"孔数 {low}..{high}（{rule.Mode}），连接 {LinkText(rule)}，孔色 {(rule.White ? "全白" : "按属性")}"
            + (rarities.Count == 0 ? "，所有稀有度" : "，稀有度 " + string.Join("/", rarities)));
        GameEvents.Drops.Add(mod.Manifest.Id, drops => drops.Changes.Add((drop, rng) => Change(rule, drop, rng)));
    }

    /// <summary>One drop, with its sockets and links rebuilt to the rule. A drop the rule leaves alone is returned as it
    /// is, unchanged: no sockets component, a fixed-socket base, another rarity, or a gem already in a socket.</summary>
    static IEnumerable<ItemDrops.Drop> Change(Rule rule, ItemDrops.Drop drop, Random rng)
    {
        var item = drop.Item;
        if (rule.Rarities.Count > 0 && !rule.Rarities.Contains(item.Rarity.ToString().ToLowerInvariant()))
            return new[] { drop };
        int capacity = Capacity(drop.Type);
        if (capacity == 0) return new[] { drop };
        // A socket holding a gem is never rebuilt: the gem would be lost with it.
        if (item.Sockets.Any(socket => socket.Gem is not null)) return new[] { drop };

        int before = item.Sockets.Count;
        // Links only: the sockets stay exactly as the game rolled them, colours and all. A base that rolled none stays none.
        if (string.Equals(rule.Mode, "links", StringComparison.Ordinal))
            return before == 0 ? new[] { drop }
                : new[] { drop with { Item = item with { Links = Links(rule, before, rng) } } };
        int count = rule.Mode switch
        {
            "raise" => Math.Clamp(Math.Max(before, rule.Low), 1, capacity),
            "cap" => Math.Clamp(Math.Min(before, rule.High), 1, capacity),
            _ => Math.Clamp(rng.Next(rule.Low, rule.High + 1), 1, capacity),
        };
        // Only raising or capping: a drop already inside the range keeps its sockets and links as the game gave them.
        if (rule.Mode is "raise" or "cap" && count == before) return new[] { drop };
        var requirements = Program.Items.Requirements(drop.Type);
        var sockets = new List<ItemSocket>(count);
        for (int i = 0; i < count; i++)
            sockets.Add(new ItemSocket(rule.White
                ? SocketColour.White
                : SocketColours.Roll(requirements, item.ItemLevel, item.Quality, rng)));
        return new[] { drop with { Item = item with { Sockets = sockets, Links = Links(rule, count, rng) } } };
    }

    /// <summary>The sockets a dropped base can hold: the class's setting, the six-socket ceiling, and the base's shape (the
    /// client lays sockets out in columns of at most three and two columns at most, so a 1x4 staff holds 3, not 6).
    /// Zero for a base the rule must not touch: no sockets component, a resonator, the Stygian Vise, a one-socket base.</summary>
    static int Capacity(ItemCatalog.BaseType type)
    {
        if (!type.Components.Contains("Sockets")) return 0;
        if (ItemCatalog.ResonatorSockets(type) > 0 || type.Id == AbyssJewels.StygianVise || type.HasOneSocket) return 0;
        int byShape = Math.Min(Math.Max((int)type.Width, 1), 2) * 3;
        return Math.Min(Math.Min(SystemSettings.Default.SocketMax(type.Class), SocketLayout.MaximumSockets), byShape);
    }

    static IReadOnlyList<byte> Links(Rule rule, int count, Random rng) => rule.LinkMode switch
    {
        "full" => new byte[] { (byte)count },
        "none" => Unlinked(count),
        "retail" => ItemCatalog.RollSocketLinks(count, rng),
        // A roll above the socket count (6 links on 3 sockets) counts as the socket count's own maximum.
        _ => Layouts(count, rng.Next(rule.LinkLow, rule.LinkHigh + 1), rng),
    };

    static byte[] Unlinked(int count)
    {
        var links = new byte[count];
        Array.Fill(links, (byte)1);
        return links;
    }

    // Every way to split 1..6 sockets into link groups, by socket count and the size of its largest group: "4 links" on
    // six sockets is [4,2] or [4,1,1], never [5,1] and never [6].
    static readonly byte[][][][] LayoutsByMax = BuildLayouts();

    static IReadOnlyList<byte> Layouts(int count, int maxGroup, Random rng)
    {
        var byMax = LayoutsByMax[Math.Clamp(count, 1, SocketLayout.MaximumSockets)];
        var options = byMax[Math.Clamp(maxGroup, 1, Math.Clamp(count, 1, SocketLayout.MaximumSockets))];
        return options[rng.Next(options.Length)];
    }

    static byte[][][][] BuildLayouts()
    {
        var table = new byte[SocketLayout.MaximumSockets + 1][][][];
        for (int count = 1; count <= SocketLayout.MaximumSockets; count++)
        {
            // One bit per gap between two sockets: set means they stay linked, clear starts a new group.
            var all = new List<byte[]>();
            for (int mask = 0; mask < 1 << (count - 1); mask++)
            {
                var groups = new List<byte>(count) { 1 };
                for (int gap = 0; gap < count - 1; gap++)
                {
                    if ((mask & (1 << gap)) != 0) groups[^1]++;
                    else groups.Add(1);
                }
                all.Add([.. groups]);
            }
            var byMax = new byte[count + 1][][];
            for (int max = 1; max <= count; max++)
                byMax[max] = [.. all.Where(groups => groups.Max() == max)];
            table[count] = byMax;
        }
        return table;
    }

    /// <summary>One end of a range setting, or the setting's own fallback when the file gives no pair.</summary>
    static int End(int[]? pair, int index) =>
        pair is { Length: > 0 } ends && index < ends.Length ? ends[index]
            : index == 0 ? 1 : SocketLayout.MaximumSockets;

    static string One(string? value, string fallback, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string chosen = value.Trim().ToLowerInvariant();
        return allowed.Contains(chosen) ? chosen : fallback;
    }

    static string LinkText(Rule rule) => rule.LinkMode switch
    {
        "retail" => "游戏原本的概率",
        "full" => "全连",
        "none" => "全断开",
        _ => $"{rule.LinkLow}..{rule.LinkHigh} 连",
    };
}
