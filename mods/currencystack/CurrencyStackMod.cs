using System.Collections;
using System.Reflection;
using System.Text.Json;
using GameServer.Core;
using GameServer.Modding;

namespace LocalExile.Mods.CurrencyStack;

/// <summary>How many of one currency fit in a cell. The client's own CurrencyItems table sets it (Orb of Transmutation
/// 40, Chaos Orb 10, Orb of Alchemy 10 …) and the server's item catalog reads it once at startup, so this mod rewrites
/// those rows and rebuilds the catalog: every stackable currency goes to the ceiling, with per-currency overrides. The
/// three exceptions the game itself relies on stay as they are — a currency with a full-stack conversion (Chaos Shard
/// 20 → Chaos Orb: raising its size would raise the combining threshold with it and the shards would never combine),
/// one that does not stack at all, and one already above the ceiling (gold 50000, rogue markers 25000).</summary>
public sealed class CurrencyStackMod : IMod
{
    const string CurrencyTable = "CurrencyItems";
    const int Floor = 2, Ceiling = 9999;
    const string BaselineFile = "currencystack.baseline.json";

    /// <summary>The declared settings, as they are read.</summary>
    sealed record Choices(int StackSize, Dictionary<string, int>? Overrides);

    /// <summary>A currency's size before this mod touched it, and the size the table holds since: the two differ only when
    /// the table no longer has what the mod wrote (a rebuilt client.db, a hand-run SQL update), and then the current size
    /// is the new baseline.</summary>
    public sealed record Entry(int Original, int Applied);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        int target = Math.Clamp(choices.StackSize, Floor, Ceiling);
        var overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, size) in choices.Overrides ?? new Dictionary<string, int>())
            if (!string.IsNullOrWhiteSpace(name)) overrides[name.Trim()] = Math.Clamp(size, Floor, Ceiling);

        if (!ClientTables.Has(CurrencyTable) || !ClientTables.Has("BaseItemTypes") || !ClientTables.Has("ItemClasses"))
        {
            mod.Log($"client.db 缺少 {CurrencyTable} / BaseItemTypes / ItemClasses 表，什么也没改");
            return;
        }

        // The rows come back ordered by _row and the memory table drops that column, so a row's index is its _row: the
        // key the CurrencyItems rows point at.
        var classes = new Dictionary<int, string>();
        foreach (var row in ClientTables.Table("ItemClasses"))
            classes[row.Index] = row.Text("Id") ?? "";

        var types = new Dictionary<int, (string Id, string Name, string Class)>();
        foreach (var row in ClientTables.Table("BaseItemTypes"))
            types[row.Index] = (row.Text("Id") ?? "", row.Text("Name") ?? "",
                classes.GetValueOrDefault(row.IntOrNull("ItemClassesKey") ?? -1, ""));

        var currency = ClientTables.Table(CurrencyTable);
        if (Column(currency, "StackSize") is not { } column)
        {
            mod.Log($"{CurrencyTable} 表没有 StackSize 列，什么也没改");
            return;
        }
        if (column.GetType().GetMethod("SetInteger", BindingFlags.Public | BindingFlags.Instance) is not { } set)
        {
            mod.Log("服务端的客户端表没有 SetInteger 了，这个模组需要更新");
            return;
        }

        var baseline = LoadBaseline(mod);
        bool saved = false;
        int changed = 0, untouched = 0, combining = 0;
        var examples = new List<string>();

        foreach (var row in currency)
        {
            if (!types.TryGetValue(row.IntOrNull("BaseItemTypesKey") ?? -1, out var type) || type.Id.Length == 0) continue;
            int current = row.Int("StackSize");
            if (!baseline.TryGetValue(type.Id, out var entry) || entry.Applied != current)
            {
                entry = new Entry(current, current);
                baseline[type.Id] = entry;
                saved = true;
            }

            int wanted;
            // An override names a currency either by its metadata id or by the name a player sees ("Chaos Orb").
            bool overridden = overrides.TryGetValue(type.Id, out int asked)
                || (type.Name.Length > 0 && overrides.TryGetValue(type.Name, out asked));
            if (overridden) wanted = asked;
            else
            {
                // Only the class the game stacks, and only what the game lets us raise without breaking it.
                if (!string.Equals(type.Class, "StackableCurrency", StringComparison.OrdinalIgnoreCase)) { untouched++; continue; }
                if ((row.IntOrNull("FullStack_BaseItemTypesKey") ?? -1) >= 0) { combining++; continue; }
                if (entry.Original <= 1 || entry.Original > Ceiling) { untouched++; continue; }
                wanted = target;
            }

            if (wanted == current) { untouched++; continue; }
            set.Invoke(column, [row.Index, (long)wanted]);
            baseline[type.Id] = entry with { Applied = wanted };
            saved = true;
            changed++;
            if (examples.Count < 5) examples.Add($"{Label(type)} {current}→{wanted}");
        }

        if (saved) SaveBaseline(mod, baseline);
        mod.Log($"上限 {target}：改动 {changed} 种通货，{untouched} 种保持原样，满堆自动合成的 {combining} 种不动"
            + (examples.Count > 0 ? "；例如 " + string.Join("、", examples) : ""));
        if (changed > 0) Rebuild(mod);
    }

    /// <summary>A currency as a log line names it: the player's name when it has one, else the id's last part.</summary>
    static string Label((string Id, string Name, string Class) type) =>
        type.Name.Length > 0 ? type.Name : type.Id[(type.Id.LastIndexOf('/') + 1)..];

    /// <summary>The item catalog read the sizes once, at startup: building it again makes the new sizes the game's.</summary>
    static void Rebuild(IModContext mod)
    {
        var items = Program.Items;
        var rolls = Program.Rolls;
        var bench = Program.Bench;
        try
        {
            Program.Items = new ItemCatalog();
            Program.Rolls = new ItemRolls(Program.Items);
            Program.Bench = new BenchCrafting(Program.Items, Program.Rolls);
            mod.Log("物品目录已按新上限重建");
        }
        catch (Exception e)
        {
            Program.Items = items;
            Program.Rolls = rolls;
            Program.Bench = bench;
            mod.Log($"重建物品目录失败，仍按旧上限运行：{e.Message}");
        }
    }

    /// <summary>The StackSize column of a table, which is internal to the server: read by reflection, so a server that
    /// renames it fails the load with a log line instead of the game.</summary>
    static object? Column(ClientTable table, string name)
    {
        var field = typeof(ClientTable).GetField("byName", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(table) is IDictionary columns ? columns[name] : null;
    }

    static string BaselinePath => Path.Combine("data", "mods", BaselineFile);

    static Dictionary<string, Entry> LoadBaseline(IModContext mod)
    {
        try
        {
            if (File.Exists(BaselinePath)
                && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(BaselinePath)) is { } read)
                return new Dictionary<string, Entry>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) { mod.Log($"读取 {BaselinePath} 失败，按首次运行处理：{e.Message}"); }
        return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveBaseline(IModContext mod, Dictionary<string, Entry> baseline)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { mod.Log($"写入 {BaselinePath} 失败：{e.Message}"); }
    }
}
