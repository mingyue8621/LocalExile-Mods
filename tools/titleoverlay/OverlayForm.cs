using System.Diagnostics;
using System.Drawing.Imaging;

namespace TitleOverlay;

/// <summary>
/// 透明置顶、点击穿透的覆盖层窗口：把称号序列帧画在角色头顶。
/// 纯客户端本地绘制，不读内存、不注入、不改游戏文件，只有自己看得见。
/// </summary>
public partial class OverlayForm : Form
{
    private const int HotkeyMoveUp = 0xA001;
    private const int HotkeyMoveDown = 0xA002;
    private const int HotkeyMoveLeft = 0xA003;
    private const int HotkeyMoveRight = 0xA004;
    private const int HotkeyScaleUp = 0xA005;
    private const int HotkeyScaleDown = 0xA006;
    private const int HotkeyPrevTitle = 0xA007;
    private const int HotkeyNextTitle = 0xA008;
    private const int HotkeyToggleVisible = 0xA009;
    private const int HotkeyOpenSettings = 0xA00A;
    private const int HotkeyResetAnchor = 0xA00B;

    private const string Title = "流放之路 头顶称号";

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private List<Bitmap> _frames = [];
    private List<Bitmap> _scaled = [];
    private string _loadedTitle = "";
    private double _loadedScale = -1;

    private int _lastFrame = -1;
    private Point _lastPosition = new(int.MinValue, int.MinValue);
    private int _lastOpacity = -1;
    private bool _nativeVisible;
    private bool _hotkeysRegistered;

    public event EventHandler? ConfigChanged;

    public OverlayForm()
    {
        InitializeComponent();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public AppConfig Config { get; set; } = new();

    /// <summary>诊断信息，供设置窗口显示。</summary>
    public string Status { get; private set; } = "等待游戏窗口";

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                          | NativeMethods.WS_EX_TRANSPARENT   // 鼠标点击穿透
                          | NativeMethods.WS_EX_TOOLWINDOW    // 不出现在 Alt+Tab
                          | NativeMethods.WS_EX_NOACTIVATE;   // 不抢焦点
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
        ReloadAssets(force: true);
        SetNativeVisible(false);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>只创建窗口句柄、不显示窗口；覆盖层靠 UpdateLayeredWindow 自行控制可见性。</summary>
    public void EnsureHandle()
    {
        _ = Handle;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideLayered();
            return;
        }

        UnregisterHotkeys();
        _timer.Stop();
        ReleaseAssets();
        base.OnFormClosing(e);
    }

    // ---------- 素材 ----------

    public void ReloadAssets(bool force = false)
    {
        var id = Config.CurrentTitle;
        if (string.IsNullOrWhiteSpace(id))
        {
            var first = App.Library.Titles.FirstOrDefault();
            id = first?.Id ?? "";
            Config.CurrentTitle = id;
        }

        bool titleChanged = force || !string.Equals(_loadedTitle, id, StringComparison.OrdinalIgnoreCase);
        bool scaleChanged = Math.Abs(_loadedScale - Config.Scale) > 0.0005;

        if (!titleChanged && !scaleChanged)
        {
            return;
        }

        var entry = App.Library.Find(id);
        if (entry is null)
        {
            ReleaseAssets();
            Status = App.Library.Titles.Count == 0
                ? $"素材目录里没有称号：{Config.ResolveTitlesRoot()}"
                : $"找不到称号 {id}";
            return;
        }

        if (titleChanged)
        {
            var loaded = TitleLibrary.LoadFrames(entry);
            _frames = loaded;
            _loadedTitle = id;
            _loadedScale = -1;
            _clock.Restart();
        }

        var scaled = TitleLibrary.ScaleFrames(_frames, Config.Scale);
        var old = _scaled;
        _scaled = scaled;
        foreach (var bmp in old)
        {
            bmp.Dispose();
        }

        _loadedScale = Config.Scale;
        _lastFrame = -1;
        Status = $"{entry.Name} · {_frames.Count} 帧 · {_scaled[0].Width}×{_scaled[0].Height}";
    }

    private void ReleaseAssets()
    {
        foreach (var bmp in _scaled) bmp.Dispose();
        foreach (var bmp in _frames) bmp.Dispose();
        _scaled = [];
        _frames = [];
        _loadedTitle = "";
        _loadedScale = -1;
    }

    public void ApplyConfig(AppConfig config)
    {
        Config = config;
        ReloadAssets();
        _lastFrame = -1;
        _lastPosition = new Point(int.MinValue, int.MinValue);
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 每帧绘制 ----------

    /// <summary>自检模式下由外部驱动绘制，定时器不参与。</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool ManualMode { get; set; }

    private void OnTick(object? sender, EventArgs e)
    {
        if (ManualMode)
        {
            return;
        }

        if (_scaled.Count == 0)
        {
            HideLayered();
            return;
        }

        if (!Config.Visible)
        {
            HideLayered();
            Status = "已隐藏（Ctrl+Alt+H 显示）";
            return;
        }

        Rectangle anchorRect;
        if (Config.FollowGameWindow)
        {
            var hwnd = GameWindow.FindGameWindow(Config.GameProcesses);
            if (hwnd == IntPtr.Zero)
            {
                HideLayered();
                Status = "未找到游戏窗口";
                LogProbe("探测：未找到游戏窗口（找 " + string.Join('/', Config.GameProcesses) + "）");
                return;
            }

            var foreground = GameWindow.GetForegroundProcessName();
            // 前台判断改成「前台窗口是否就是游戏窗口（或同一进程）」，不再依赖 exe 名字：
            // 客户端的实际进程是 Game.patched.exe，名字对不上会被误判成后台。
            if (!GameWindow.IsWindowForeground(hwnd))
            {
                HideLayered();
                Status = "游戏窗口不在前台";
                LogProbe($"探测：游戏窗口在后台，前台进程={foreground} hwnd=0x{hwnd:X}");
                return;
            }

            anchorRect = GameWindow.GetClientScreenRect(hwnd);
            if (anchorRect.Width < 64 || anchorRect.Height < 64)
            {
                HideLayered();
                Status = "游戏窗口尺寸异常";
                LogProbe($"探测：客户区异常 {anchorRect.Width}×{anchorRect.Height}");
                return;
            }

            LogProbe($"探测：hwnd=0x{hwnd:X} 前台={foreground} 客户区={anchorRect.Width}×{anchorRect.Height}@({anchorRect.X},{anchorRect.Y})");
        }
        else
        {
            anchorRect = Screen.FromPoint(new Point(Cursor.Position.X, Cursor.Position.Y)).Bounds;
        }

        var bitmap = _scaled[0];
        int w = bitmap.Width;
        int h = bitmap.Height;

        int centerX = anchorRect.Left + (int)Math.Round(anchorRect.Width * Config.AnchorX);
        int anchorY = anchorRect.Top + (int)Math.Round(anchorRect.Height * Config.AnchorY);

        var pos = new Point(
            centerX + Config.OffsetX - w / 2,
            Config.BottomAligned ? anchorY + Config.OffsetY - h : anchorY + Config.OffsetY - h / 2);

        long elapsed = _clock.ElapsedMilliseconds;
        int frame = (int)(elapsed / Config.FrameMs % _scaled.Count);
        var current = _scaled[frame];

        if (frame == _lastFrame && pos == _lastPosition && Config.Opacity == _lastOpacity && _nativeVisible)
        {
            return;
        }

        if (DrawLayered(current, pos, Config.Opacity))
        {
            _lastFrame = frame;
            _lastPosition = pos;
            _lastOpacity = Config.Opacity;
            Status = $"{Config.CurrentTitle} · 帧 {frame + 1}/{_scaled.Count} · ({pos.X},{pos.Y}) · {Math.Round(Config.Scale * 100)}%";
            LogProbe($"绘制：({pos.X},{pos.Y}) 尺寸={w}×{h} 锚点={Config.AnchorX:0.##}/{Config.AnchorY:0.##} 偏移=({Config.OffsetX},{Config.OffsetY})");
        }
        else
        {
            LogProbe($"绘制失败：({pos.X},{pos.Y}) 尺寸={w}×{h}（UpdateLayeredWindow 返回 false）");
        }
    }

    /// <summary>按类别（冒号前那段）去重：同一类的诊断只在内容变化时写一次，避免每帧刷屏。</summary>
    private readonly Dictionary<string, string> _probeCache = new();

    private void LogProbe(string text)
    {
        int split = text.IndexOf('：');
        string kind = split > 0 ? text[..split] : text;

        if (_probeCache.TryGetValue(kind, out var last) && string.Equals(last, text, StringComparison.Ordinal))
        {
            return;
        }

        _probeCache[kind] = text;
        Program.Log(text);
    }

    private Bitmap? _blankSurface;

    private bool DrawLayered(Bitmap bitmap, Point position, int opacityPercent)
    {
        if (!_nativeVisible)
        {
            // 先用 1×1 全透明表面占位，再 Show()，避免弹出黑块；
            // 而且窗口必须在「已显示」状态下 UpdateLayeredWindow 才会真正参与合成。
            _blankSurface ??= LayeredSurface.CreateSolid(1, 1, Color.Transparent);
            LayeredSurface.Present(Handle, _blankSurface, position, 100);
            Show();
            _nativeVisible = true;
        }

        return LayeredSurface.Present(Handle, bitmap, position, opacityPercent);
    }

    private void HideLayered()
    {
        SetNativeVisible(false);
        _lastFrame = -1;
        _lastPosition = new Point(int.MinValue, int.MinValue);
    }

    private void SetNativeVisible(bool visible)
    {
        if (_nativeVisible == visible)
        {
            return;
        }

        // 必须走 WinForms 自己的 Show/Hide：直接 ShowWindow/SetWindowPos 绕过控件状态，
        // 窗口不会真正参与合成（实测屏幕上看不到任何东西）。
        if (visible)
        {
            Show();
        }
        else
        {
            Hide();
        }

        _nativeVisible = visible;
    }

    /// <summary>自检用：直接把指定帧画到屏幕坐标。</summary>
    internal bool RenderFrame(int index, Point position, int opacityPercent)
    {
        if (_scaled.Count == 0)
        {
            return false;
        }

        index = Math.Clamp(index, 0, _scaled.Count - 1);
        return DrawLayered(_scaled[index], position, opacityPercent);
    }

    internal void HideOverlay() => HideLayered();

    internal int FrameCount => _scaled.Count;

    internal Size FrameSize => _scaled.Count == 0 ? Size.Empty : _scaled[0].Size;

    // ---------- 热键 ----------

    private void RegisterHotkeys()
    {
        if (_hotkeysRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        uint mod = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        _hotkeysRegistered = true;

        TryRegister(HotkeyMoveUp, mod, (uint)Keys.Up);
        TryRegister(HotkeyMoveDown, mod, (uint)Keys.Down);
        TryRegister(HotkeyMoveLeft, mod, (uint)Keys.Left);
        TryRegister(HotkeyMoveRight, mod, (uint)Keys.Right);
        TryRegister(HotkeyScaleUp, mod, (uint)Keys.Oemplus);
        TryRegister(HotkeyScaleDown, mod, (uint)Keys.OemMinus);
        TryRegister(HotkeyPrevTitle, mod, (uint)Keys.OemOpenBrackets);
        TryRegister(HotkeyNextTitle, mod, (uint)Keys.OemCloseBrackets);
        TryRegister(HotkeyToggleVisible, mod, (uint)Keys.H);
        TryRegister(HotkeyOpenSettings, mod, (uint)Keys.S);
        TryRegister(HotkeyResetAnchor, mod, (uint)Keys.D0);
    }

    private void TryRegister(int id, uint mod, uint vk)
    {
        if (!NativeMethods.RegisterHotKey(Handle, id, mod, vk))
        {
            Program.Log($"热键注册失败：id={id} key={(Keys)vk}");
        }
    }

    private void UnregisterHotkeys()
    {
        if (!_hotkeysRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        for (int id = HotkeyMoveUp; id <= HotkeyResetAnchor; id++)
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }

        _hotkeysRegistered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            switch ((int)m.WParam)
            {
                case HotkeyMoveUp: Nudge(0, -5); break;
                case HotkeyMoveDown: Nudge(0, 5); break;
                case HotkeyMoveLeft: Nudge(-5, 0); break;
                case HotkeyMoveRight: Nudge(5, 0); break;
                case HotkeyScaleUp: ScaleBy(0.05); break;
                case HotkeyScaleDown: ScaleBy(-0.05); break;
                case HotkeyPrevTitle: CycleTitle(-1); break;
                case HotkeyNextTitle: CycleTitle(1); break;
                case HotkeyToggleVisible:
                    Config.Visible = !Config.Visible;
                    App.SaveAndApply();
                    break;
                case HotkeyOpenSettings: App.ShowSettings(); break;
                case HotkeyResetAnchor:
                    Config.AnchorX = 0.5;
                    Config.AnchorY = 0.5;
                    Config.OffsetX = 0;
                    Config.OffsetY = -200;
                    App.SaveAndApply();
                    break;
            }
            return;
        }

        base.WndProc(ref m);
    }

    private void Nudge(int dx, int dy)
    {
        // dx/dy 的单位是 5 像素；按住 Shift 时一次 20 像素。
        bool big = (ModifierKeys & Keys.Shift) == Keys.Shift;
        int factor = big ? 4 : 1;
        Config.OffsetX += dx * factor;
        Config.OffsetY += dy * factor;
        App.SaveAndApply();
    }

    private void ScaleBy(double delta)
    {
        Config.Scale = Math.Clamp(Math.Round(Config.Scale + delta, 2), 0.2, 4.0);
        App.SaveAndApply();
    }

    private void CycleTitle(int direction)
    {
        var titles = App.Library.Titles;
        if (titles.Count == 0)
        {
            return;
        }

        int index = titles.FindIndex(t => string.Equals(t.Id, Config.CurrentTitle, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = 0;
        }
        else
        {
            index = (index + direction + titles.Count) % titles.Count;
        }

        Config.CurrentTitle = titles[index].Id;
        App.SaveAndApply();
    }
}
