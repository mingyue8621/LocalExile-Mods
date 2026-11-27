using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Modding;

namespace LocalExile.Mods.PlayerTitle;

/// <summary>
/// 头顶称号。称号画不进客户端里：客户端没有这类文字资产，ggpk 也写不进去。所以这个模组只做「托管」——
/// 把随它一起发的本地透明覆盖层（<c>overlay/TitleOverlay.exe</c>）拉起来，由覆盖层把称号序列帧画在角色头顶。
/// 不写客户端文件、不注入进程，只有开这个服务端的那台电脑自己看得见。
/// <para>
/// 显示时机交给服务端判断，不靠客户端画面：<see cref="GameEvents.ClientReady"/>（角色进入游戏、加载完成）才拉起覆盖层，
/// <see cref="GameEvents.PlayerLeaving"/> 的 <c>disconnected</c>（退出到角色选择界面、关游戏、掉线）收掉。
/// 换区域传的是 <c>left the area</c>，不动覆盖层。所以角色选择界面不会看到称号。
/// </para>
/// </summary>
public sealed class PlayerTitleMod : IMod, IDisposable
{
    /// <summary>settings.json 的声明，按名字读回来。</summary>
    sealed record Choices(bool Enabled, bool AutoByAscendancy, string? Title, string? CustomTitle, double Scale,
        int OffsetX, int OffsetY, int Opacity, int FrameMs, bool FollowWindow);

    /// <summary>素材目录名与中文名，和覆盖层内置的一份一致。</summary>
    static readonly (string Id, string Name)[] Titles =
    [
        ("sfx_19901", "初入江湖"), ("sfx_19902", "小有名气"), ("sfx_19903", "武林新秀"),
        ("sfx_19904", "门派翘楚"), ("sfx_19905", "登堂入室"), ("sfx_19906", "宗门魁首"),
        ("sfx_19907", "武林高手"), ("sfx_19908", "人中龙凤"), ("sfx_19909", "名震一方"),
        ("sfx_19910", "英雄豪杰"), ("sfx_19911", "九州大侠"), ("sfx_19912", "一代宗师"),
        ("sfx_19913", "武学泰斗"), ("sfx_19914", "叱咤风云"), ("sfx_19915", "威震海内"),
        ("sfx_19916", "傲视群雄"), ("sfx_19917", "武林盟主"), ("sfx_19918", "傲骨天成"),
        ("sfx_19919", "独步天下"), ("sfx_19920", "震古烁今"), ("sfx_19921", "一击定乾坤"),
        ("sfx_19922", "除魔卫道"), ("sfx_19923", "剑光寒十九州"), ("sfx_19924", "每日特惠"),
    ];

    /// <summary>
    /// 升华 → 称号。键是客户端 Ascendancy 表的 <c>Id</c> 列（不是显示名：游侠那行 Id 是 Raider，3.25 起显示成 Warden，
    /// 两个都收）。<c>normal</c> 是还没升华，<c>max</c> 是升华满 4 次（四座迷宫都打过，8 点）。
    /// 这张表第一次进游戏会写成 <c>data/mods/playertitle/ascendancy-titles.json</c>，想换配对直接改那个文件，不用重编模组。
    /// </summary>
    static readonly Dictionary<string, string> DefaultAscendancyTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_说明"] = "左边是升华 Id（游戏数据里的英文名），右边是称号图号。normal = 还没升华，max = 升华满 4 次。改完存盘，下次进游戏生效。",
        ["normal"] = "sfx_19901",
        ["max"] = "sfx_19923",
        ["Saboteur"] = "sfx_19902",
        ["Trickster"] = "sfx_19903",
        ["Reliquarian"] = "sfx_19904",
        ["Raider"] = "sfx_19905",     // 显示名 Warden 守望者
        ["Warden"] = "sfx_19905",     // 万一以后 Id 跟着改名
        ["Chieftain"] = "sfx_19906",
        ["Pathfinder"] = "sfx_19907",
        ["Ascendant"] = "sfx_19908",
        ["Deadeye"] = "sfx_19909",
        ["Gladiator"] = "sfx_19910",
        ["Guardian"] = "sfx_19911",
        ["Hierophant"] = "sfx_19912",
        ["Luminary"] = "sfx_19913",
        ["Elementalist"] = "sfx_19914",
        ["Assassin"] = "sfx_19915",
        ["Champion"] = "sfx_19916",
        ["Juggernaut"] = "sfx_19917",
        ["Berserker"] = "sfx_19918",
        ["Occultist"] = "sfx_19919",
        ["Necromancer"] = "sfx_19920",
        ["Slayer"] = "sfx_19921",
        ["Inquisitor"] = "sfx_19922",
    };

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly object _gate = new();

    IModContext? _mod;
    Choices? _choices;
    string _title = "";
    string _lastCharacter = "";
    Process? _overlay;
    IDisposable? _clientReady;
    IDisposable? _playerLeaving;
    bool _disposed;
    bool _pendingStart;

    public void Load(IModContext mod)
    {
        string id = mod.Manifest.Id;
        var choices = mod.Settings<Choices>();
        // 按升华自动时不看设置里手选的称号：先按上次算出来的恢复（模组重载拿不到玩家对象，
        // 没法再按升华算一遍），没有记录才用初入江湖占位，角色进游戏再重算。
        string title = UsesAscendancy(choices) ? LoadLastTitle(mod) : ResolveTitle(choices);
        if (UsesAscendancy(choices) && title.Length == 0)
        {
            title = "sfx_19901";
        }

        GameEvents.Command(id, "title", "/title [称号]  不带参数列出可选称号，/title off 关闭", (conn, args) =>
        {
            string wanted = string.Join(' ', args).Trim();

            if (wanted.Length == 0)
            {
                string current = _title;
                conn.Commands!.Reply($"/title 当前 {(current.Length == 0 ? "无" : Names(current))}。可选："
                    + string.Join("、", Titles.Select(t => t.Name))
                    + "；/title off 关闭，/title on 打开。");
                return Task.CompletedTask;
            }

            if (wanted is "off" or "关" or "关闭")
            {
                conn.Commands!.Reply("头顶称号已关闭（/title on 再打开）");
                mod.SaveSettings(choices with { Enabled = false });
                return Task.CompletedTask;
            }

            if (wanted is "on" or "开" or "打开")
            {
                conn.Commands!.Reply("头顶称号已打开");
                mod.SaveSettings(choices with { Enabled = true });
                return Task.CompletedTask;
            }

            var match = Titles.FirstOrDefault(t =>
                string.Equals(t.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, wanted, StringComparison.Ordinal));
            if (match.Id is null)
            {
                conn.Commands!.Reply($"/title 没有「{wanted}」。可用：{string.Join("、", Titles.Select(t => t.Name))}");
                return Task.CompletedTask;
            }

            conn.Commands!.Reply($"头顶称号改为「{match.Name}」");
            mod.SaveSettings(choices with { Enabled = true, Title = match.Id, CustomTitle = "" });
            return Task.CompletedTask;
        });

        if (!choices.Enabled)
        {
            WriteLog(mod, "已关闭（enabled = false），进入游戏也不显示称号");
            return;
        }

        if (title.Length == 0)
        {
            WriteLog(mod, "没有选称号，进入游戏也不显示称号");
            return;
        }

        string overlayDir = Path.Combine(mod.Folder, "overlay");
        string exe = Path.Combine(overlayDir, "TitleOverlay.exe");
        if (!File.Exists(exe))
        {
            WriteLog(mod, $"缺少覆盖层程序：{exe}");
            return;
        }

        // 覆盖层是窗口，要有交互式桌面。Windows 服务（会话 0）里画不出来，说清楚比默默失败好。
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
        {
            WriteLog(mod, "服务端不在交互式桌面（会话 0），覆盖层不会显示：请在玩游戏的这台电脑上启动服务端");
            return;
        }

        _mod = mod;
        _choices = choices;
        _title = title;

        // 角色进入游戏（客户端加载完成）才拉起覆盖层：角色选择界面、登录界面都不会有称号。
        // 换区域也会再触发一次，所以这里不写日志（StartOverlay 里那句只在真正拉起时写）。
        _clientReady = GameEvents.ClientReady.Add(id, player =>
        {
            _lastCharacter = player.Character.Name ?? "?";

            // 按升华换称号：换个角色进来，称号跟着这个角色的升华走。换区域也会触发，但算出来一样就不动。
            if (UsesAscendancy(choices))
            {
                var (picked, why) = AscendancyTitle(mod, player.Character);
                bool changed;
                lock (_gate)
                {
                    changed = !string.Equals(_title, picked, StringComparison.OrdinalIgnoreCase);
                    if (changed)
                    {
                        _title = picked;
                    }
                }

                if (changed)
                {
                    WriteLog(mod, $"角色「{_lastCharacter}」{why}，称号「{Names(picked)}」");
                }
            }

            MarkInGame(mod, true);
            lock (_gate)
            {
                // 记下这一局用的称号：改设置、/mod_reload 会重载模组，重载时靠它恢复，不会退回初入江湖。
                SaveLastTitle(mod, _title);
            }

            ScheduleStart();
            return Task.CompletedTask;
        });

        // 只认「真的退出」；换区域传的是 "left the area"，覆盖层不动。
        _playerLeaving = GameEvents.PlayerLeaving.Add(id, (player, why) =>
        {
            if (!string.Equals(why, "disconnected", StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            // 坑：这个回调挂在连接的 finally 里，换地图/进图/回城时连接也会关，报的同样是 disconnected。
            // 那时新连接已经 ClientReady 过了，照旧逻辑收掉覆盖层就等于每次切场景都把称号关掉。
            // 用玩家自己的区域切换标记区分：在换图（或带着传送请求）就别动。
            if (player.LeavingArea || player.PendingTransfer is not null)
            {
                return Task.CompletedTask;
            }

            MarkInGame(mod, false);
            StopOverlay();
            return Task.CompletedTask;
        });

        // 改设置、/mod_reload 会让模组重载：玩家这时可能正在游戏里，事件不会再触发一次，按状态文件恢复。
        if (IsInGame(mod))
        {
            StartOverlay();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _clientReady?.Dispose();
        _clientReady = null;
        _playerLeaving?.Dispose();
        _playerLeaving = null;

        StopOverlay();
        _mod = null;
        _choices = null;
    }

    /// <summary>
    /// 稍等一下再起：<c>ClientReady</c> 之后客户端还要过一小段加载画面，立刻拉起来会浮在加载界面上。
    /// </summary>
    void ScheduleStart()
    {
        if (_pendingStart)
        {
            return;
        }

        _pendingStart = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                if (_disposed || _mod is null)
                {
                    return;
                }

                // 等这一秒半的工夫玩家可能又退出了，再确认一次。
                if (!IsInGame(_mod))
                {
                    return;
                }

                StartOverlay();
            }
            catch
            {
                // 启动失败已经写进日志了，这里不再抛。
            }
            finally
            {
                _pendingStart = false;
            }
        });
    }

    void StartOverlay()
    {
        var mod = _mod;
        var choices = _choices;
        if (mod is null || choices is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_overlay is { HasExited: false })
            {
                return;
            }

            _overlay?.Dispose();
            _overlay = null;

            string overlayDir = Path.Combine(mod.Folder, "overlay");
            string exe = Path.Combine(overlayDir, "TitleOverlay.exe");
            if (!File.Exists(exe))
            {
                WriteLog(mod, $"缺少覆盖层程序：{exe}");
                return;
            }

            string configPath = ConfigPath(mod);
            WriteConfig(configPath, Fingerprint(choices, _title), choices, _title);

            // 日志绝不能写进模组目录：宿主递归监视整个模组目录，任何写入都会重载一次模组，
            // 重载又启覆盖层、又写日志（2026-11-27 每 0.6 秒循环 150 次）。所以指到 data/mods/playertitle/logs/ 下。
            string logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? Path.GetTempPath(), "logs", "overlay.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            }
            catch
            {
                logPath = "";
            }

            // 同名进程只留一个：上次崩溃留下的、或手动双击开的，都换成由模组托管的这一个。
            StopStray();

            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = overlayDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(configPath);
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--silent");
            if (logPath.Length > 0)
            {
                start.ArgumentList.Add("--log");
                start.ArgumentList.Add(logPath);
            }

            try
            {
                _overlay = Process.Start(start);
                // 控制台/游戏里只留这一条：其余过程信息一律写文件（data/mods/playertitle/logs/mod.log）。
                mod.Log("称号加载成功");
                string who = _lastCharacter.Length > 0 ? $"角色「{_lastCharacter}」" : "角色";
                WriteLog(mod, $"{who}头顶称号「{Names(_title)}」已显示（覆盖层 pid {_overlay?.Id.ToString(CultureInfo.InvariantCulture) ?? "?"}）");
            }
            catch (Exception e)
            {
                WriteLog(mod, $"覆盖层启动失败：{e.Message}");
            }
        }
    }

    void StopOverlay()
    {
        lock (_gate)
        {
            Process? process = _overlay;
            _overlay = null;
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
                // 已经退出或没有权限。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>这次是不是按升华自动选：开关开着，又没填自定义素材目录名（填了就以那份素材为准）。</summary>
    static bool UsesAscendancy(Choices choices) =>
        choices.AutoByAscendancy && string.IsNullOrWhiteSpace(choices.CustomTitle);

    static string ResolveTitle(Choices choices)
    {
        if (!string.IsNullOrWhiteSpace(choices.CustomTitle))
        {
            return choices.CustomTitle.Trim();
        }

        return string.IsNullOrWhiteSpace(choices.Title) ? "" : choices.Title.Trim();
    }

    /// <summary>四座迷宫的奖励：普通、残酷、无情、永恒。全拿是 8 点，也就是「升华满」。</summary>
    const int AscendancyAwardCount = 4;

    /// <summary>按角色的升华分支与升华次数选称号，附带一句写日志用的说明。</summary>
    static (string Id, string Why) AscendancyTitle(IModContext mod, Character character)
    {
        var table = AscendancyTable(mod);
        string normal = table.TryGetValue("normal", out var n) && n.Length > 0 ? n : "sfx_19901";
        var progress = character.MainAscendancy;

        if (progress.Selection is not { } selection)
        {
            return (normal, "还没升华");
        }

        int times = progress.EarnedAwards.Count;
        if (times >= AscendancyAwardCount)
        {
            string top = table.TryGetValue("max", out var max) && max.Length > 0 ? max : normal;
            return (top, $"{selection.Id} 升华满 {times} 次");
        }

        return table.TryGetValue(selection.Id, out var chosen)
            ? (chosen, $"{selection.Id} 升华 {times} 次")
            : (normal, $"{selection.Id} 升华 {times} 次（对照表里没有这一项，先用初入江湖）");
    }

    /// <summary>
    /// 升华 → 称号。文件存在就用文件里的（玩家能自己改），缺项用内置表补；不存在就写一份内置表出去。
    /// </summary>
    static Dictionary<string, string> AscendancyTable(IModContext mod)
    {
        var table = new Dictionary<string, string>(DefaultAscendancyTitles, StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(Path.GetDirectoryName(ConfigPath(mod)) ?? Path.GetTempPath(), "ascendancy-titles.json");
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonSerializer.Serialize(DefaultAscendancyTitles, JsonOptions));
            }
            else
            {
                var custom = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (custom is not null)
                {
                    foreach (var (key, value) in custom)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        {
                            table[key.Trim()] = value.Trim();
                        }
                    }
                }
            }
        }
        catch
        {
            // 对照表坏了就用内置的那份，不影响显示。
        }

        return table;
    }

    static string Names(string id) => Titles.FirstOrDefault(t => t.Id == id).Name ?? id;

    /// <summary>模组托管时覆盖层的配置，放在 <c>data/mods/&lt;id&gt;/overlay.json</c>：模组自己的文件夹会被更新替换。</summary>
    static string ConfigPath(IModContext mod)
    {
        string[] roots =
        [
            Path.GetFullPath(Path.Combine(mod.Folder, "..", "..", "..")),
            Environment.CurrentDirectory,
        ];

        foreach (string root in roots)
        {
            string data = Path.Combine(root, "data");
            if (!Directory.Exists(data))
            {
                continue;
            }

            string dir = Path.Combine(data, "mods", mod.Manifest.Id);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "overlay.json");
        }

        string fallback = Path.Combine(Environment.CurrentDirectory, "data", "mods", mod.Manifest.Id);
        Directory.CreateDirectory(fallback);
        return Path.Combine(fallback, "overlay.json");
    }

    /// <summary>「玩家是否在游戏里」的状态文件，与覆盖层配置同目录（<c>data/mods/&lt;id&gt;/ingame.txt</c>）。</summary>
    static string StatePath(IModContext mod) =>
        Path.Combine(Path.GetDirectoryName(ConfigPath(mod)) ?? Path.GetTempPath(), "ingame.txt");

    /// <summary>
    /// 出问题时留个痕迹，但**不刷服务端控制台**：游戏里只该看到一条「称号加载成功」。
    /// 文件放 <c>data/mods/&lt;id&gt;/logs/mod.log</c>：模组自己的文件夹会被宿主递归监视，写进去会触发重载。
    /// </summary>
    static void WriteLog(IModContext mod, string message)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(ConfigPath(mod)) ?? Path.GetTempPath(), "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "mod.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 64 * 1024)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch
        {
            // 写不了日志不影响显示。
        }
    }

    /// <summary>这一局用的称号（<c>data/mods/&lt;id&gt;/last-title.txt</c>）：模组重载时没法再按升华算，靠它恢复。</summary>
    static string LastTitlePath(IModContext mod) =>
        Path.Combine(Path.GetDirectoryName(ConfigPath(mod)) ?? Path.GetTempPath(), "last-title.txt");

    static void SaveLastTitle(IModContext mod, string title)
    {
        if (title.Length == 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(LastTitlePath(mod), title);
        }
        catch
        {
            // 写不了就下次再算，不影响显示。
        }
    }

    static string LoadLastTitle(IModContext mod)
    {
        try
        {
            string path = LastTitlePath(mod);
            if (!File.Exists(path))
            {
                return "";
            }

            string text = File.ReadAllText(path).Trim();
            return Titles.Any(t => string.Equals(t.Id, text, StringComparison.OrdinalIgnoreCase)) ? text : "";
        }
        catch
        {
            return "";
        }
    }

    static void MarkInGame(IModContext mod, bool inGame)
    {
        try
        {
            File.WriteAllText(StatePath(mod), inGame ? "in" : "out");
        }
        catch
        {
            // 写不了不影响显示：拿不到状态就当不在游戏里。
        }
    }

    /// <summary>
    /// 上次记下的状态还能不能用？上一次服务端运行留下的不算——那时连着的客户端早已断开，
    /// 只看本次服务端进程启动之后写的那份。
    /// </summary>
    static bool IsInGame(IModContext mod)
    {
        try
        {
            string path = StatePath(mod);
            if (!File.Exists(path))
            {
                return false;
            }

            if (File.GetLastWriteTimeUtc(path) < Process.GetCurrentProcess().StartTime.ToUniversalTime())
            {
                return false;
            }

            return string.Equals(File.ReadAllText(path).Trim(), "in", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 写覆盖层的配置。指纹（决定显示效果的那几个值）没变就不动文件：这样覆盖层里用热键微调的位置、缩放会留下来，
    /// 而启动器里一改设置，指纹变了，下一次加载就照新的写。
    /// </summary>
    static void WriteConfig(string path, string fingerprint, Choices choices, string title)
    {
        try
        {
            if (File.Exists(path))
            {
                using var current = JsonDocument.Parse(File.ReadAllText(path));
                if (current.RootElement.TryGetProperty("Source", out var source) &&
                    string.Equals(source.GetString(), fingerprint, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
        catch
        {
            // 配置坏了就重写一份。
        }

        var config = new Dictionary<string, object?>
        {
            ["Source"] = fingerprint,
            ["TitlesRoot"] = "titles",
            ["CurrentTitle"] = title,
            ["Scale"] = Math.Clamp(choices.Scale, 0.2, 4.0),
            ["OffsetX"] = Math.Clamp(choices.OffsetX, -4000, 4000),
            ["OffsetY"] = Math.Clamp(choices.OffsetY, -4000, 4000),
            ["AnchorX"] = 0.5,
            ["AnchorY"] = 0.5,
            ["BottomAligned"] = true,
            ["Opacity"] = Math.Clamp(choices.Opacity, 5, 100),
            ["FrameMs"] = Math.Clamp(choices.FrameMs, 10, 500),
            ["FollowGameWindow"] = choices.FollowWindow,
            ["Visible"] = true,
            ["GameProcesses"] = new[]
            {
                "Game", "Game.patched", "PathOfExile", "PathOfExile_x64", "PathOfExileSteam", "PathOfExile_x64Steam",
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }

    static string Fingerprint(Choices choices, string title)
    {
        string key = string.Join('|',
            title,
            choices.Scale.ToString("0.###", CultureInfo.InvariantCulture),
            choices.OffsetX.ToString(CultureInfo.InvariantCulture),
            choices.OffsetY.ToString(CultureInfo.InvariantCulture),
            choices.Opacity.ToString(CultureInfo.InvariantCulture),
            choices.FrameMs.ToString(CultureInfo.InvariantCulture),
            choices.FollowWindow ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    static void StopStray()
    {
        foreach (var process in Process.GetProcessesByName("TitleOverlay"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch
            {
                // 已经退出或没有权限。
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
