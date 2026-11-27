using System.Text.Json;
using System.Text.Json.Serialization;

namespace TitleOverlay;

public sealed class AppConfig
{
    /// <summary>称号素材根目录（相对 exe 目录，或绝对路径）。</summary>
    public string TitlesRoot { get; set; } = "titles";

    /// <summary>当前使用的称号目录名。</summary>
    public string CurrentTitle { get; set; } = "";

    /// <summary>缩放倍率（1.0 = PNG 原始像素）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>相对锚点的像素微调。</summary>
    public int OffsetX { get; set; } = 0;

    /// <summary>纵向偏移：角色在屏幕正中，头顶血条约在中心上方 200 像素（1080p 实测）。</summary>
    public int OffsetY { get; set; } = -200;

    /// <summary>锚点：游戏客户区的横向比例（0.5 = 正中）。</summary>
    public double AnchorX { get; set; } = 0.5;

    /// <summary>锚点：游戏客户区的纵向比例（0.5 = 正中，角色脚底所在处）。</summary>
    public double AnchorY { get; set; } = 0.5;

    /// <summary>称号图底部是否贴齐锚点（否则以锚点为中心）。</summary>
    public bool BottomAligned { get; set; } = true;

    /// <summary>整体不透明度 0-100。</summary>
    public int Opacity { get; set; } = 100;

    /// <summary>每帧毫秒。</summary>
    public int FrameMs { get; set; } = 40;

    /// <summary>跟随游戏窗口（关闭时固定在屏幕锚点）。</summary>
    public bool FollowGameWindow { get; set; } = true;

    /// <summary>是否显示。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>识别为游戏的进程名（不含 .exe）。</summary>
    public string[] GameProcesses { get; set; } =
    [
        "Game",
        "PathOfExile",
        "PathOfExile_x64",
        "PathOfExileSteam",
        "PathOfExile_x64Steam"
    ];

    /// <summary>由模组托管时写下的设置指纹：指纹不变就保留热键微调的位置，不覆盖。</summary>
    public string Source { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>--config 指定的配置文件；不指定时放在本机用户目录，不落 exe 所在的模组目录（写那里会触发模组重载）。</summary>
    public static string? ConfigPathOverride { get; set; }

    public static string ConfigPath => ConfigPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalExile", "playertitle", "titleoverlay.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不阻塞启动。
        }

        var fresh = new AppConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            string? folder = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 保存失败不影响运行。
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(TitlesRoot)) TitlesRoot = "titles";
        Scale = Math.Clamp(Scale, 0.2, 4.0);
        AnchorX = Math.Clamp(AnchorX, 0.0, 1.0);
        AnchorY = Math.Clamp(AnchorY, 0.0, 1.0);
        Opacity = Math.Clamp(Opacity, 5, 100);
        FrameMs = Math.Clamp(FrameMs, 10, 500);
        OffsetX = Math.Clamp(OffsetX, -4000, 4000);
        OffsetY = Math.Clamp(OffsetY, -4000, 4000);
        if (GameProcesses is null || GameProcesses.Length == 0)
        {
            GameProcesses = ["Game", "PathOfExile", "PathOfExile_x64"];
        }
    }

    public string ResolveTitlesRoot()
    {
        var root = TitlesRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, root);
        }
        return Path.GetFullPath(root);
    }
}
