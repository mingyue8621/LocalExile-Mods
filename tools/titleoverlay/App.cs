namespace TitleOverlay;

internal static class App
{
    public static AppConfig Config { get; private set; } = new AppConfig();

    public static TitleLibrary Library { get; } = new TitleLibrary();

    public static OverlayForm? Overlay { get; private set; }

    private static SettingsForm? _settings;

    public static void Initialize()
    {
        Config = AppConfig.Load();
        RescanTitles(pickDefaultIfMissing: true);

        if (!File.Exists(AppConfig.ConfigPath))
        {
            Config.Save();
        }
    }

    public static void RescanTitles(bool pickDefaultIfMissing)
    {
        Library.Scan(Config.ResolveTitlesRoot());

        if (Library.Titles.Count == 0)
        {
            return;
        }

        if (pickDefaultIfMissing && Library.Find(Config.CurrentTitle) is null)
        {
            Config.CurrentTitle = Library.Titles[0].Id;
        }
    }

    public static void Attach(OverlayForm overlay)
    {
        Overlay = overlay;
    }

    /// <summary>把当前配置落到覆盖层与磁盘。</summary>
    public static void SaveAndApply()
    {
        Config.Normalize();
        Config.Save();
        Overlay?.ApplyConfig(Config);
        _settings?.RefreshFromConfig(syncControls: true);
    }

    public static void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm();
            _settings.FormClosed += (_, _) => _settings = null;
            _settings.Show();
            Program.Log($"设置窗口 bounds={_settings.Bounds} visible={_settings.Visible}");
        }
        else
        {
            if (_settings.WindowState == FormWindowState.Minimized)
            {
                _settings.WindowState = FormWindowState.Normal;
            }

            if (!_settings.Visible)
            {
                _settings.Show();
            }

            _settings.Activate();
        }
    }

    public static void NotifyConfigChangedByOverlay()
    {
        _settings?.RefreshFromConfig(syncControls: true);
    }
}
