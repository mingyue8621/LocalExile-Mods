using System.Diagnostics;
using System.Text;
using GameServer.Modding;

namespace LocalExile.Mods.LootFilter;

/// <summary>
/// 把「物品过滤器」装到客户端的过滤器目录（我的文档\My Games\Path of Exile），并在客户端配置
/// production_Config.ini 里选中指定的那一档。自带两套：剑心（七档）与一乐（八档，小结巴音效），
/// 设置里选一套；两套的 .filter 都已换算成 3.29 客户端数据（tools/adapt-lootfilter.py）。
/// <para>过滤器是客户端资源，服务端只能替**同一台机器**上的客户端装好；要让其他玩家也拿到，
/// 同样的文件得随客户端补丁或启动器分发（它们才在玩家的机器上跑）。</para>
/// <para>游戏开着的时候不写配置：客户端退出时会把 ini 写回，改了就白改。每套的档位文件每次都补齐，
/// 玩家进了游戏可以在「选项 → 游戏 → 物品过滤器」里自己换。</para>
/// </summary>
public sealed class LootFilterMod : IMod
{
    /// <summary>settings.json 的键，按名字对上（大小写无关）。</summary>
    public sealed record Options(bool Enabled, string FilterSet, string Profile, bool SelectInGame, bool CopySounds,
        string? SoundSource, string? TargetFolder, string? FiltersSource);

    /// <summary>一套里的一档：设置里存 <paramref name="Key"/>，装到客户端时按文件名里的关键词认
    /// （关键词取自文件名的自定义部分，所以作者换个版本号、改个前后缀都不影响）。</summary>
    sealed record Tier(string Key, string[] Keywords);

    /// <summary>一套过滤器：模组里的子目录名、装进客户端时音效文件夹叫什么、有哪几档。
    /// 音效文件夹名由过滤器本身决定（.filter 里 CustomAlertSound 写的就是这个相对路径）。</summary>
    sealed record FilterSet(string Id, string Label, string Folder, string SoundFolder, Tier[] Tiers);

    static readonly FilterSet Jianxin = new("jianxin", "剑心", "jianxin", "剑心音效",
    [
        new("campaign", ["剧情"]),
        new("mapping", ["异界"]),
        new("advanced", ["进阶"]),
        new("speed", ["速刷"]),
        new("magic", ["打宝"]),
        new("ultimate", ["终极"]),
        new("clean", ["纯净"]),
    ]);

    static readonly FilterSet Yile = new("yile", "一乐", "yile", "音效",
    [
        new("campaign", ["近战开荒"]),
        new("campaign-bow", ["弓系开荒"]),
        new("campaign-caster", ["法系开荒"]),
        new("mapping", ["lv68-90"]),
        new("mapping-late", ["异界后期"]),
        // 「速刷」与「速刷后期」两个文件名都含「速刷」，所以带上级别段把它认准。
        new("speed", ["lv90-96]速刷"]),
        new("speed-late", ["速刷后期"]),
        new("magic", ["打宝"]),
    ]);

    static readonly FilterSet[] Sets = [Jianxin, Yile];

    IModContext mod = null!;
    Options options = null!;
    FilterSet set = Jianxin;

    public void Load(IModContext context)
    {
        mod = context;
        options = mod.Settings<Options>();
        set = Sets.FirstOrDefault(s => string.Equals(s.Id, options.FilterSet, StringComparison.OrdinalIgnoreCase)) ?? Jianxin;
        GameEvents.Command(mod.Manifest.Id, "lootfilter", "/lootfilter [status|install] — 物品过滤器的安装状态与重装", Command);
        string summary = Install("启动");
        mod.Log($"物品过滤器（{set.Label}）：" + summary);
    }

    async Task Command(ClientState conn, string[] args)
    {
        string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        if (action is "install" or "reload")
        {
            string summary = Install("手动");
            await conn.Commands!.Reply($"物品过滤器（{set.Label}）：{summary}");
            return;
        }
        if (action != "status" && action != "list")
        {
            await conn.Commands!.Reply("用法：/lootfilter [status|install]");
            return;
        }
        await conn.Commands!.Reply($"物品过滤器（{set.Label}）：{Describe()}\n用 /lootfilter install 重新安装一次（游戏开着时不会改选中项）。");
    }

    /// <summary>装一遍，返回一句给人看的结果。</summary>
    string Install(string why)
    {
        if (!options.Enabled) return "已在模组设置里关闭，没有动客户端";
        string target = TargetFolder();
        try
        {
            Directory.CreateDirectory(target);
            int filters = CopyFilters(target);
            int sounds = options.CopySounds ? CopySounds(target) : 0;
            string? selected = options.SelectInGame ? Select(target) : null;
            return $"{filters} 个过滤文件、{sounds} 个音效已放到 {target}"
                + (selected is null ? "；没有改动游戏里的选中项" : "；已选中 " + selected)
                + $"（{why}）";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return $"安装失败：{e.GetType().Name}: {e.Message}";
        }
    }

    string Describe()
    {
        if (!options.Enabled) return "已关闭（设置里重新打开后 /lootfilter install）";
        string target = TargetFolder();
        int present = CountFilters(target);
        string file = FilterFileFor(options.Profile) is { } name && File.Exists(Path.Combine(target, name))
            ? name
            : "（目录里没找到这一档）";
        string sounds = Directory.Exists(Path.Combine(target, set.SoundFolder)) ? "有音效" : "没有音效";
        return $"目录 {target}；{present} 个过滤文件，{sounds}；默认档位 {file}";
    }

    static int CountFilters(string target)
    {
        try { return Directory.Exists(target) ? Directory.GetFiles(target, "*.filter").Length : 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>客户端的过滤器目录：设置里写了就用它，没写就找「我的文档\My Games\Path of Exile」。</summary>
    string TargetFolder()
    {
        if (options.TargetFolder is { Length: > 0 } given) return given.Trim();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] documents =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Documents"),
            Path.Combine(home, "OneDrive", "Documents"),
        ];
        foreach (string folder in documents)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            string candidate = Path.Combine(folder, "My Games", "Path of Exile");
            if (Directory.Exists(candidate)) return candidate;
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile");
        return fallback.Length > 0 ? fallback : Path.Combine(home, "Documents", "My Games", "Path of Exile");
    }

    /// <summary>这一套的所有档位文件都放过去，客户端才能在游戏里自由切换；内容没变的不重写。
    /// 源目录优先用设置里指定的（换一版过滤器只改设置），否则用模组自带的 filters/&lt;这套&gt;。
    /// 换套不过滤的另一套：留在客户端的旧文件还能在游戏里选，不会顶掉这次的选中项。</summary>
    int CopyFilters(string target)
    {
        string source = FiltersFrom();
        if (source.Length == 0 || !Directory.Exists(source)) return 0;
        int copied = 0;
        foreach (string file in Directory.GetFiles(source, "*.filter"))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (Same(file, destination)) continue;
            File.Copy(file, destination, overwrite: true);
            copied++;
        }
        return copied;
    }

    /// <summary>过滤器文件从哪来：设置里的「过滤器来源目录」，没设就用模组自带的这一套。</summary>
    string FiltersFrom()
    {
        string configured = (options.FiltersSource ?? "").Trim();
        if (configured.Length > 0 && Directory.Exists(configured)) return configured;
        return Path.Combine(mod.Folder, "filters", set.Folder);
    }

    /// <summary>音效按需补齐（12~13 MB、200 多个），只在缺失或大小不同时复制。源优先用设置里指定的
    /// （换了风格包只改设置），否则用模组自带的 sounds/&lt;这套&gt;。</summary>
    int CopySounds(string target)
    {
        string source = SoundsFrom();
        if (source.Length == 0 || !Directory.Exists(source)) return 0;
        string destination = Path.Combine(target, set.SoundFolder);
        Directory.CreateDirectory(destination);
        int copied = 0;
        foreach (string file in Directory.GetFiles(source, "*.mp3"))
        {
            string to = Path.Combine(destination, Path.GetFileName(file));
            if (Same(file, to)) continue;
            File.Copy(file, to, overwrite: true);
            copied++;
        }
        return copied;
    }

    /// <summary>音效从哪来：设置里的「音效来源目录」，没设就用模组自带的这一套。</summary>
    string SoundsFrom()
    {
        string configured = (options.SoundSource ?? "").Trim();
        if (configured.Length > 0 && Directory.Exists(configured)) return configured;
        return Path.Combine(mod.Folder, "sounds", set.Folder);
    }

    static bool Same(string from, string to)
    {
        try
        {
            if (!File.Exists(to)) return false;
            return new FileInfo(from).Length == new FileInfo(to).Length;
        }
        catch (IOException) { return false; }
    }

    /// <summary>这一档对应的过滤文件名（按文件名关键词认，兼容不同版本的文件名）。找不到就用这套的
    /// 「异界」档兜底：设置里留着上一套的档位（比如从剑心换到一乐），那档这一套可能没有。</summary>
    string? FilterFileFor(string profile)
    {
        string source = FiltersFrom();
        if (source.Length == 0 || !Directory.Exists(source)) return null;
        Tier tier = TierFor(profile);
        foreach (string file in Directory.GetFiles(source, "*.filter").OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            foreach (string keyword in tier.Keywords)
                if (name.Contains(keyword, StringComparison.Ordinal)) return name;
        }
        return null;
    }

    Tier TierFor(string profile)
    {
        foreach (Tier tier in set.Tiers)
            if (string.Equals(tier.Key, profile, StringComparison.OrdinalIgnoreCase)) return tier;
        // 这套（或这一版）没有这一档：退回「异界」，别因为一个设置值就整套都不选中。
        foreach (Tier tier in set.Tiers)
            if (tier.Key == "mapping") return tier;
        return set.Tiers[0];
    }

    /// <summary>把 ini 里的 item_filter 改成这一档；游戏在跑、或者客户端还没生成配置时不写。</summary>
    string? Select(string target)
    {
        string? file = FilterFileFor(options.Profile);
        if (file is null || !File.Exists(Path.Combine(target, file)))
        {
            mod.Log($"物品过滤器（{set.Label}）：{target} 里没有匹配「{options.Profile}」这一档的过滤文件，选中项没动");
            return null;
        }
        if (GameRunning())
        {
            mod.Log("物品过滤器：游戏正在运行，改写会被客户端退出时覆盖，这次不改选中项");
            return null;
        }
        string ini = Path.Combine(target, "production_Config.ini");
        if (!File.Exists(ini))
        {
            mod.Log("物品过滤器：还没找到 production_Config.ini，先启动一次客户端，再 /lootfilter install");
            return null;
        }
        string text = File.ReadAllText(ini, Encoding.UTF8).Replace("\r\n", "\n");
        string[] lines = text.Split('\n');
        string wanted = "item_filter=" + file;
        bool changed = false, already = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("item_filter=", StringComparison.Ordinal)) continue;
            if (lines[i] == wanted) already = true;
            else { lines[i] = wanted; changed = true; }
        }
        if (already && !changed) return file;
        if (!changed) return null;   // 没有这个键：客户端版本不按 ini 记过滤器，不去瞎加
        string backup = ini + ".before-lootfilter";
        try { if (!File.Exists(backup)) File.Copy(ini, backup, overwrite: false); }
        catch (IOException) { }
        File.WriteAllText(ini, string.Join("\n", lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return file;
    }

    /// <summary>客户端在跑的时候它会把自己的配置写回，这时候改 ini 没用。</summary>
    static bool GameRunning()
    {
        foreach (string name in new[] { "PathOfExile_x64", "PathOfExile", "Game.patched", "Game" })
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return false;
    }
}
