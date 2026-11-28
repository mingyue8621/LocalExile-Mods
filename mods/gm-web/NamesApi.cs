using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalExile.Mods.GmWeb;

/// <summary>
/// GET /names: the page's name tables, built once from the server's own catalogs — every area /goto resolves
/// (AreaTransitionCatalog's atlas), the monsters /spawn_mob can create (MonsterCatalog, one row per name), and
/// every base type and unique /item can give (ItemCatalog and UniqueCatalog). Nothing is read from the client.
/// Each row also carries the Chinese name from <c>zh.json.gz</c> (shipped beside this assembly), so the page lists
/// 区域 / 怪物 / 物品 in Chinese while the command it sends still uses the English name the server resolves.
/// </summary>
static class NamesApi
{
    static byte[]? _cache;

    /// <summary>The mod's own folder (from <see cref="GmWebMod.Load"/>): the assembly is loaded from a private copy
    /// elsewhere, so <c>Assembly.Location</c> never points at the files the mod shipped.</summary>
    internal static string? Folder;

    /// <summary>Points the name tables at the mod's folder and drops the built response, so a reloaded mod picks up
    /// a replaced zh.json.gz.</summary>
    internal static void Configure(string folder)
    {
        Folder = folder;
        _cache = null;
    }

    internal static async Task Handle(NetworkStream stream, string method)
    {
        if (method != "GET") { await GmWebMod.Write(stream, 404, "text/plain", "not found"u8.ToArray()); return; }
        try
        {
            _cache ??= Build();
            await GmWebMod.Write(stream, 200, "application/json; charset=utf-8", _cache);
        }
        catch (Exception e)
        {
            await GmWebMod.Write(stream, 500, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(new { error = e.Message }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    static byte[] Build()
    {
        var result = new JsonObject { ["area"] = AreaTable(), ["mob"] = MobTable(), ["item"] = ItemTable() };
        return JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    static string Str(JsonNode? n) => n!.GetValue<string>();

    /// <summary>The page's rows are [cn, hans, en, id]: cn/hans carry the Chinese name when the shipped dictionary
    /// has one (falling back to English), en always the English name the command is sent with.</summary>
    static JsonArray Row(string? cn, string en, string id)
    {
        string hant = string.IsNullOrWhiteSpace(cn) ? en : cn;
        return new(hant, hant, en, id);
    }

    /// <summary>The Chinese names shipped with this mod, loaded on the first /names request (a missing or broken
    /// file only leaves the page in English).</summary>
    sealed class ChineseNames
    {
        public Dictionary<string, string> Areas = new(StringComparer.Ordinal);
        public Dictionary<string, string> Mobs = new(StringComparer.Ordinal);
        public Dictionary<string, string> Items = new(StringComparer.OrdinalIgnoreCase);

        public string? Area(string id) => Areas.GetValueOrDefault(id);
        public string? Mob(string id) => Mobs.GetValueOrDefault(id);
        public string? Item(string name) => Items.GetValueOrDefault(name);
    }

    static readonly Lazy<ChineseNames> Zh = new(LoadChineseNames);

    static ChineseNames LoadChineseNames()
    {
        var names = new ChineseNames();
        try
        {
            string folder = Folder ?? Path.GetDirectoryName(typeof(NamesApi).Assembly.Location) ?? "";
            string path = Path.Combine(folder, "zh.json.gz");
            if (!File.Exists(path)) return names;
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var doc = JsonDocument.Parse(gzip);
            Fill(doc.RootElement, "areas", names.Areas);
            Fill(doc.RootElement, "mobs", names.Mobs);
            Fill(doc.RootElement, "names", names.Items);
            Console.WriteLine($"[gm-web] 中文名：区域 {names.Areas.Count}，怪物 {names.Mobs.Count}，物品 {names.Items.Count}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gm-web] zh.json.gz 读取失败，页面按英文显示：{e.Message}");
        }
        return names;
    }

    static void Fill(JsonElement root, string key, Dictionary<string, string> into)
    {
        if (!root.TryGetProperty(key, out var table) || table.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in table.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.String) into[entry.Name] = entry.Value.GetString()!;
    }

    /// <summary>Areas in campaign id order: exactly the ones /goto resolves, from the atlas the server loads.</summary>
    static JsonArray AreaTable()
    {
        var zh = Zh.Value;
        var list = new List<(string Id, JsonArray Row)>();
        foreach (var (id, name) in AreaTransitionCatalog.Areas)
        {
            string en = string.IsNullOrWhiteSpace(name) ? id : name!;
            list.Add((id, Row(zh.Area(id), en, id)));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return [.. list.Select(e => (JsonNode)e.Row)];
    }

    /// <summary>One row per English name, keeping the shortest metadata id (the base variety), only the varieties
    /// the server can actually spawn — sorted by name.</summary>
    static JsonArray MobTable()
    {
        var zh = Zh.Value;
        var seen = new Dictionary<string, JsonArray>();
        foreach (var v in Monsters.All)
        {
            if (!v.Supported || string.IsNullOrWhiteSpace(v.Name)) continue;
            if (!seen.TryGetValue(v.Name, out var cur) || v.Id.Length < Str(cur[3]).Length)
                seen[v.Name] = Row(zh.Mob(v.Id), v.Name, v.Id);
        }
        return [.. seen.Values.OrderBy(r => Str(r[2]), StringComparer.OrdinalIgnoreCase).Cast<JsonNode>()];
    }

    /// <summary>Base types and uniques together, sorted by name: /item resolves either by name, and the page's
    /// give button carries that name as the row's id.</summary>
    static JsonArray ItemTable()
    {
        var zh = Zh.Value;
        // BaseItemTypes also holds unnamed rows (internal, hideout); a button with no name is no use.
        var seen = new Dictionary<string, JsonArray>();
        foreach (var t in Items.All)
            if (!string.IsNullOrWhiteSpace(t.Name))
                seen[t.Name] = Row(zh.Item(t.Name), t.Name, t.Name);
        foreach (var u in Uniques.All)
            if (!string.IsNullOrWhiteSpace(u.Name))
                seen[u.Name] = Row(zh.Item(u.Name), u.Name, u.Name);
        return [.. seen.Values.OrderBy(r => Str(r[2]), StringComparer.OrdinalIgnoreCase).Cast<JsonNode>()];
    }
}
