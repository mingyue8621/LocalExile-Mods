using System.Drawing.Imaging;

namespace TitleOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // --config 必须在读配置之前生效：模组把自己的设置写成一份 json，再用 --config 指过来。
        var options = LaunchOptions.Parse(args);
        AppConfig.ConfigPathOverride = options.ConfigPath;
        Program.LogPath = options.LogPath;

        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--selftest":
                    RunSelfTest(args);
                    return;
                case "--diag":
                    RunDiag(args);
                    return;
                case "--hold":
                    RunHold(args);
                    return;
                case "--overlaytest":
                    RunOverlayTest(args);
                    return;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("未处理异常：" + (e.ExceptionObject?.ToString() ?? "未知"));
        Application.ThreadException += (_, e) => Log("界面异常：" + e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ApplicationConfiguration.Initialize();
            App.Initialize();
            Log($"启动，素材 {App.Library.Titles.Count} 个，当前 {App.Config.CurrentTitle}");

            var overlay = new OverlayForm { Config = App.Config };
            overlay.EnsureHandle();
            App.Attach(overlay);

            if (options.ParentPid > 0)
            {
                WatchParent(options.ParentPid);
            }

            // 设置窗口要等消息循环真正跑起来再显示，否则窗口不参与合成（屏幕上看不到）。
            // 静默模式（模组托管）不弹设置窗口，热键 Ctrl+Alt+S 仍可打开。
            var starter = new System.Windows.Forms.Timer { Interval = 250 };
            starter.Tick += (_, _) =>
            {
                starter.Stop();
                starter.Dispose();
                if (options.Silent)
                {
                    Log("静默模式：由模组托管，不弹设置窗口");
                    return;
                }

                App.ShowSettings();
            };
            starter.Start();

            Application.Run(new ApplicationContext());
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex);
        }
    }

    /// <summary>
    /// 日志文件。绝不能落在覆盖层自己所在目录：模组宿主监视整个模组目录，任何写入都会把模组重载一次，
    /// 重载又启动覆盖层、又写日志——2026-11-27 就是这样每 0.6 秒循环了 150 次。
    /// </summary>
    internal static string? LogPath { get; set; }

    internal static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalExile", "playertitle", "logs", "overlay.log");

    internal static void Log(string message)
    {
        string path = LogPath ?? DefaultLogPath;
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // 超过 1 MB 换一份，长期挂着也不会长成几十兆。
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch
        {
            // 忽略日志写入失败。
        }
    }

    /// <summary>命令行开关：--config &lt;json&gt;、--parent &lt;pid&gt;、--silent、--log &lt;file&gt;。</summary>
    private sealed record LaunchOptions(string? ConfigPath, int ParentPid, bool Silent, string? LogPath)
    {
        public static LaunchOptions Parse(string[] args)
        {
            string? config = null;
            string? log = null;
            int parent = 0;
            bool silent = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config" when i + 1 < args.Length:
                        try
                        {
                            config = Path.GetFullPath(args[++i]);
                        }
                        catch
                        {
                            // 路径非法就当没给。
                        }
                        break;
                    case "--log" when i + 1 < args.Length:
                        try
                        {
                            log = Path.GetFullPath(args[++i]);
                        }
                        catch
                        {
                            // 同上。
                        }
                        break;
                    case "--parent" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) && pid > 0:
                        parent = pid;
                        i++;
                        break;
                    case "--silent":
                        silent = true;
                        break;
                }
            }

            return new LaunchOptions(config, parent, silent, log);
        }
    }

    /// <summary>看住托管自己的进程：它退出（崩溃、被结束）时覆盖层跟着退出，不留孤儿窗口。</summary>
    private static void WatchParent(int pid)
    {
        var watch = new System.Windows.Forms.Timer { Interval = 1000 };
        watch.Tick += (_, _) =>
        {
            bool gone;
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(pid);
                gone = parent.HasExited;
            }
            catch (ArgumentException)
            {
                gone = true;
            }
            catch
            {
                gone = false;
            }

            if (!gone)
            {
                return;
            }

            watch.Stop();
            watch.Dispose();
            Log($"宿主进程 {pid} 已退出，覆盖层关闭");
            Application.Exit();
        };
        watch.Start();
    }

    private static void PumpFor(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// 无界面自检：--selftest &lt;称号ID&gt; &lt;缩放&gt; &lt;输出png&gt;
    /// 把缩放后的首帧贴到棋盘背景上导出，用于确认真实 alpha 与尺寸。
    /// </summary>
    private static void RunSelfTest(string[] args)
    {
        string titleId = args.Length > 1 ? args[1] : "sfx_19901";
        double scale = args.Length > 2 && double.TryParse(args[2], out var s) ? s : 1.0;
        string outPath = args.Length > 3
            ? args[3]
            : Path.Combine(Environment.CurrentDirectory, "selftest.png");

        var cfg = AppConfig.Load();
        var library = new TitleLibrary();
        library.Scan(cfg.ResolveTitlesRoot());

        var entry = library.Find(titleId) ?? library.Titles.FirstOrDefault();
        if (entry is null)
        {
            File.WriteAllText(outPath + ".txt", "找不到称号素材目录：" + cfg.ResolveTitlesRoot());
            return;
        }

        var frames = TitleLibrary.LoadFrames(entry);
        var scaled = TitleLibrary.ScaleFrames(frames, scale);
        var first = scaled[0];

        using (var canvas = new Bitmap(first.Width + 40, first.Height + 40, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(255, 30, 30, 34));
                const int cell = 10;
                using var light = new SolidBrush(Color.FromArgb(255, 52, 52, 58));
                for (int y = 0; y < canvas.Height; y += cell)
                {
                    for (int x = 0; x < canvas.Width; x += cell)
                    {
                        if (((x / cell) + (y / cell)) % 2 == 0)
                        {
                            g.FillRectangle(light, x, y, cell, cell);
                        }
                    }
                }

                g.DrawImage(first, new Point(20, 20));
                g.DrawRectangle(Pens.OrangeRed, 20, 20, first.Width - 1, first.Height - 1);
            }

            canvas.Save(outPath, ImageFormat.Png);
        }

        int opaque = 0;
        for (int y = 0; y < first.Height; y += 4)
        {
            for (int x = 0; x < first.Width; x += 4)
            {
                if (first.GetPixel(x, y).A > 8)
                {
                    opaque++;
                }
            }
        }

        int total = ((first.Width + 3) / 4) * ((first.Height + 3) / 4);
        var report = new System.Text.StringBuilder();
        report.AppendLine($"称号 {entry.Id}（{entry.Name}） 帧数 {frames.Count}");
        report.AppendLine($"输出尺寸 {first.Width}×{first.Height}，非透明采样 {opaque}/{total}");
        report.AppendLine("输出：" + outPath);
        File.WriteAllText(outPath + ".txt", report.ToString());

        foreach (var bmp in scaled) bmp.Dispose();
        foreach (var bmp in frames) bmp.Dispose();
    }

    /// <summary>
    /// 状态诊断：--diag &lt;称号ID&gt; &lt;输出txt&gt;
    /// 报告窗口样式、可见性、位置与虚拟屏幕范围。
    /// </summary>
    private static void RunDiag(string[] args)
    {
        string titleId = args.Length > 1 ? args[1] : "sfx_19901";
        string outPath = args.Length > 2
            ? args[2]
            : Path.Combine(Environment.CurrentDirectory, "diag.txt");

        ApplicationConfiguration.Initialize();
        App.Initialize();

        var report = new System.Text.StringBuilder();
        var entry = App.Library.Find(titleId) ?? App.Library.Titles.FirstOrDefault();
        if (entry is null)
        {
            File.WriteAllText(outPath, "找不到素材：" + App.Config.ResolveTitlesRoot());
            return;
        }

        var overlay = new OverlayForm { Config = App.Config, ManualMode = true };
        overlay.Config.CurrentTitle = entry.Id;
        overlay.EnsureHandle();
        bool drawn = overlay.RenderFrame(0, new Point(300, 300), 100);
        report.AppendLine($"称号 {entry.Name} 帧数 {App.Library.Titles.Count}");
        report.AppendLine($"RenderFrame = {drawn}  帧数 = {overlay.FrameCount}  尺寸 = {overlay.FrameSize}");
        report.AppendLine($"IsWindowVisible = {NativeMethods.IsWindowVisible(overlay.Handle)}");
        report.AppendLine($"EXSTYLE = 0x{NativeMethods.GetWindowLong(overlay.Handle, NativeMethods.GWL_EXSTYLE):X8}");
        if (NativeMethods.GetWindowRect(overlay.Handle, out var wr))
        {
            report.AppendLine($"WindowRect = ({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})");
        }
        report.AppendLine($"虚拟屏幕 = {SystemInformation.VirtualScreen}");
        overlay.HideOverlay();

        File.WriteAllText(outPath, report.ToString());
    }

    /// <summary>
    /// 预览：--hold &lt;称号ID&gt; &lt;x&gt; &lt;y&gt; &lt;秒&gt;，循环播放动画，到期自动退出。不开游戏也能看效果。
    /// </summary>
    private static void RunHold(string[] args)
    {
        string titleId = args.Length > 1 ? args[1] : "sfx_19911";
        int x = args.Length > 2 && int.TryParse(args[2], out var px) ? px : 300;
        int y = args.Length > 3 && int.TryParse(args[3], out var py) ? py : 300;
        double seconds = args.Length > 4 && double.TryParse(args[4], out var s) ? s : 6;

        ApplicationConfiguration.Initialize();
        App.Initialize();

        var overlay = new OverlayForm { Config = App.Config, ManualMode = true };
        overlay.Config.CurrentTitle = titleId;
        overlay.EnsureHandle();
        overlay.Config.FollowGameWindow = false;
        overlay.ReloadAssets(force: true);

        var pos = new Point(x, y);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int frames = Math.Max(1, overlay.FrameCount);
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            int index = (int)(clock.ElapsedMilliseconds / overlay.Config.FrameMs % frames);
            overlay.RenderFrame(index, pos, overlay.Config.Opacity);
            Application.DoEvents();
            Thread.Sleep(15);
        }

        overlay.HideOverlay();
        Application.DoEvents();
    }

    /// <summary>
    /// 分层窗口自检：--overlaytest &lt;称号ID&gt; &lt;x&gt; &lt;y&gt; &lt;输出png&gt;
    /// 真走 UpdateLayeredWindow 把首帧画到屏幕 (x,y)，0.6 秒后截取该区域存盘。
    /// </summary>
    private static void RunOverlayTest(string[] args)
    {
        string titleId = args.Length > 1 ? args[1] : "sfx_19911";
        int x = args.Length > 2 && int.TryParse(args[2], out var px) ? px : 300;
        int y = args.Length > 3 && int.TryParse(args[3], out var py) ? py : 300;
        string outPath = args.Length > 4
            ? args[4]
            : Path.Combine(Environment.CurrentDirectory, "overlaytest.png");

        ApplicationConfiguration.Initialize();
        App.Initialize();

        var overlay = new OverlayForm { Config = App.Config, ManualMode = true };
        overlay.Config.CurrentTitle = titleId;
        overlay.EnsureHandle();
        overlay.Config.FollowGameWindow = false;
        overlay.ReloadAssets(force: true);

        var report = new System.Text.StringBuilder();
        report.AppendLine($"称号 {overlay.Config.CurrentTitle} 帧数 {overlay.FrameCount} 尺寸 {overlay.FrameSize}");

        var pos = new Point(x, y);
        bool drawn = overlay.RenderFrame(0, pos, 100);
        report.AppendLine($"UpdateLayeredWindow(100%) = {drawn}");
        PumpFor(600);

        var size = overlay.FrameSize;
        using (var shot = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height)))
        {
            using (var g = Graphics.FromImage(shot))
            {
                g.CopyFromScreen(pos.X, pos.Y, 0, 0, shot.Size);
            }
            shot.Save(outPath, ImageFormat.Png);
        }

        overlay.HideOverlay();
        report.AppendLine("屏幕截图：" + outPath);
        File.WriteAllText(outPath + ".txt", report.ToString());
    }
}
