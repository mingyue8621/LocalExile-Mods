using System.Diagnostics;
using System.Text;
using GameServer.Modding;

namespace LocalExile.Mods.LootFilter;

/// <summary>
/// 把「剑心物品过滤器」装到客户端的过滤器目录（我的文档\My Games\Path of Exile），并在客户端配置
/// production_Config.ini 里选中指定的那一档。
/// <para>过滤器是客户端资源，服务端只能替**同一台机器**上的客户端装好；要让其他玩家也拿到，
/// 同样的文件得随客户端补丁或启动器分发（它们才在玩家的机器上跑）。</para>
/// <para>游戏开着的时候不写配置：客户端退出时会把 ini 写回，改了就白改。七套文件每次都补齐，
/// 玩家进了游戏可以在「选项 → 游戏 → 物品过滤器」里自己换。</para>
/// </summary>
public sealed class LootFilterMod : IMod
{
    /// <summary>settings.json 的键，按名字对上（大小写无关）。</summary>
    public sealed record Options(bool Enabled, string Profile, bool SelectInGame, bool CopySounds,
        string? SoundSource, string? TargetFolder, string? FiltersSource);

    /// <summary>七档过滤器按文件名里的关键词认（剑心「异界」，别的作者「中期/71-90」也认）。
    /// 键存进设置；装到客户端后按关键词找实际文件名，所以换一版包不用改代码。</summary>
    static readonly (string Key, string[] Keywords)[] Tiers =
    [
        ("campaign", ["剧情", "开荒", "1-70"]),
        ("mapping", ["异界", "中期", "71-90"]),
        ("advanced", ["进阶"]),
        ("speed", ["速刷", "92+"]),
        ("magic", ["打宝", "96+"]),
        ("ultimate", ["终极"]),
        ("clean", ["纯净"]),
    ];

    /// <summary>音效目录：filter 里的 CustomAlertSound 写的就是这个相对路径，所以要放在过滤器旁边。</summary>
    const string SoundFolder = "剑心音效";

    IModContext mod = null!;
    Options options = null!;

    public void Load(IModContext context)
    {
        mod = context;
        options = mod.Settings<Options>();
        GameEvents.Command(mod.Manifest.Id, "lootfilter", "/lootfilter [status|install] — 物品过滤器的安装状态与重装", Command);
        string summary = Install("启动");
        mod.Log("物品过滤器：" + summary);
    }

    async Task Command(ClientState conn, string[] args)
    {
        string action = args.Length == 0 ? "status" : args[0].ToLowerInvariant();
        if (action is "install" or "reload")
        {
            string summary = Install("手动");
            await conn.Commands!.Reply($"物品过滤器：{summary}");
            return;
        }
        if (action != "status" && action != "list")
        {
            await conn.Commands!.Reply("用法：/lootfilter [status|install]");
            return;
        }
        await conn.Commands!.Reply($"物品过滤器：{Describe()}\n用 /lootfilter install 重新安装一次（游戏开着时不会改选中项）。");
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
        string file = Installed(target, options.Profile) ?? "（目录里没找到这一档）";
        string sounds = Directory.Exists(Path.Combine(target, SoundFolder)) ? "有音效" : "没有音效";
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

    /// <summary>七套都放过去，客户端才能在游戏里自由切换；内容没变的不重写。
    /// 源目录优先用设置里指定的（换一版过滤器只改设置），否则用模组自带的 filters。</summary>
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

    /// <summary>过滤器文件从哪来：设置里的「过滤器来源目录」，没设就用模组自带的 filters。</summary>
    string FiltersFrom()
    {
        string configured = (options.FiltersSource ?? "").Trim();
        if (configured.Length > 0 && Directory.Exists(configured)) return configured;
        return Path.Combine(mod.Folder, "filters");
    }

    /// <summary>音效按需补齐：16 MB、230 个，只在缺失或大小不同时复制。源优先用设置里指定的
    /// （换了风格包只改设置），否则用模组自带的 sounds。</summary>
    int CopySounds(string target)
    {
        string source = SoundsFrom();
        if (source.Length == 0 || !Directory.Exists(source)) return 0;
        string destination = Path.Combine(target, SoundFolder);
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

    /// <summary>音效从哪来：设置里的「音效来源目录」，没设就用模组自带的 sounds。</summary>
    string SoundsFrom()
    {
        string configured = (options.SoundSource ?? "").Trim();
        if (configured.Length > 0 && Directory.Exists(configured)) return configured;
        return Path.Combine(mod.Folder, "sounds");
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

    /// <summary>客户端过滤器目录里，属于这一档的那个文件（按文件名关键词认，兼容不同作者的命名）。</summary>
    string? Installed(string target, string profile)
    {
        if (!Directory.Exists(target)) return null;
        (string Key, string[] Keywords) tier = Tiers[1];
        foreach (var candidate in Tiers)
            if (string.Equals(candidate.Key, profile, StringComparison.OrdinalIgnoreCase)) tier = candidate;
        foreach (string file in Directory.GetFiles(target, "*.filter"))
        {
            string name = Path.GetFileName(file);
            foreach (string keyword in tier.Keywords)
                if (name.Contains(keyword, StringComparison.Ordinal)) return name;
        }
        return null;
    }

    /// <summary>把 ini 里的 item_filter 改成这一档；游戏在跑、或者客户端还没生成配置时不写。</summary>
    string? Select(string target)
    {
        string? file = Installed(target, options.Profile);
        if (file is null)
        {
            mod.Log($"物品过滤器：{target} 里没有匹配「{options.Profile}」这一档的过滤文件，选中项没动");
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
