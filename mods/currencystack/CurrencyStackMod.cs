using System.Collections;
using System.Reflection;
using GameServer.Core;
using GameServer.Modding;

namespace LocalExile.Mods.CurrencyStack;

/// <summary>Every stackable currency has its own setting, declared in settings.json under the name a player sees ("Chaos
/// Orb", "Exalted Orb", …). This mod writes those sizes into the client table and rebuilds the item catalog, which reads
/// them once at startup. Most default to 9999; a full-stack conversion (Chaos Shard 20 → Chaos Orb) and a size already
/// above 9999 keep their own number, so changing those is a deliberate choice with a warning in the setting.</summary>
public sealed class CurrencyStackMod : IMod
{
    const string CurrencyTable = "CurrencyItems";

    public void Load(IModContext mod)
    {
        // The merged settings: one integer for each currency, keyed by the game's own display name.
        var sizes = mod.Settings<Dictionary<string, int>>();
        if (!ClientTables.Has(CurrencyTable) || !ClientTables.Has("BaseItemTypes"))
        {
            mod.Log($"client.db 缺少 {CurrencyTable} 或 BaseItemTypes 表，什么也没改");
            return;
        }

        // A row's index is its _row: ClientTable.Read keeps the ORDER BY but drops the column, and the CurrencyItems
        // rows point at BaseItemTypes._row.
        var names = new Dictionary<int, string>();
        foreach (var row in ClientTables.Table("BaseItemTypes"))
            names[row.Index] = row.Text("Name") ?? "";

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

        int changed = 0, same = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examples = new List<string>();
        foreach (var row in currency)
        {
            if (!names.TryGetValue(row.IntOrNull("BaseItemTypesKey") ?? -1, out var name) || name.Length == 0) continue;
            if (!sizes.TryGetValue(name, out int wanted)) continue;
            seen.Add(name);
            int current = row.Int("StackSize");
            if (current == wanted) { same++; continue; }
            set.Invoke(column, [row.Index, (long)wanted]);
            changed++;
            if (examples.Count < 5) examples.Add($"{name} {current}→{wanted}");
        }

        mod.Log($"{sizes.Count} 种通货设置：改动 {changed} 种，{same} 种已经是设定值"
            + (examples.Count > 0 ? "；例如 " + string.Join("、", examples) : ""));
        if (sizes.Keys.Where(key => !seen.Contains(key)).ToList() is [_, ..] missing)
            mod.Log($"这些通货在当前客户端数据里找不到：{string.Join("、", missing)}");
        if (changed > 0) Rebuild(mod);
    }

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
}
