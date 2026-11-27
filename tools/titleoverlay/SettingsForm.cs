namespace TitleOverlay;

public partial class SettingsForm : Form
{
    private bool _syncing;

    public SettingsForm()
    {
        InitializeComponent();
        Text = "流放之路 头顶称号 · 设置";
        lblHotkey.Text =
            "全局热键（Ctrl+Alt）：\r\n" +
            "  方向键  微调位置（按住 Shift 一次 20 像素）\r\n" +
            "  + / -   缩放称号     [ / ]  切换上一个 / 下一个称号\r\n" +
            "  H       显示 / 隐藏   S      打开本窗口   0  位置复位";
        WireEvents();
        RefreshFromConfig(syncControls: true);
        timerStatus.Start();
    }

    private void WireEvents()
    {
        lstTitles.SelectedIndexChanged += OnTitleSelected;
        numScale.ValueChanged += OnNumericChanged;
        numAnchorX.ValueChanged += OnNumericChanged;
        numAnchorY.ValueChanged += OnNumericChanged;
        numOffsetX.ValueChanged += OnNumericChanged;
        numOffsetY.ValueChanged += OnNumericChanged;
        numFrameMs.ValueChanged += OnNumericChanged;
        tbOpacity.ValueChanged += OnOpacityChanged;
        chkFollow.CheckedChanged += OnCheckChanged;
        chkBottomAligned.CheckedChanged += OnCheckChanged;
        btnApply.Click += OnApply;
        btnToggle.Click += OnToggleVisible;
        btnPickRoot.Click += OnPickRoot;
        btnExit.Click += OnExit;
        timerStatus.Tick += OnStatusTick;
        FormClosing += OnFormClosing;
    }

    // ---------- 控件 <-> 配置 ----------

    public void RefreshFromConfig(bool syncControls)
    {
        var cfg = App.Config;

        if (syncControls)
        {
            _syncing = true;
            try
            {
                RebuildTitleList(cfg.CurrentTitle);
                numScale.Value = ClampDecimal((decimal)cfg.Scale, numScale.Minimum, numScale.Maximum);
                numAnchorX.Value = ClampDecimal((decimal)Math.Round(cfg.AnchorX * 100), numAnchorX.Minimum, numAnchorX.Maximum);
                numAnchorY.Value = ClampDecimal((decimal)Math.Round(cfg.AnchorY * 100), numAnchorY.Minimum, numAnchorY.Maximum);
                numOffsetX.Value = ClampDecimal(cfg.OffsetX, numOffsetX.Minimum, numOffsetX.Maximum);
                numOffsetY.Value = ClampDecimal(cfg.OffsetY, numOffsetY.Minimum, numOffsetY.Maximum);
                numFrameMs.Value = ClampDecimal(cfg.FrameMs, numFrameMs.Minimum, numFrameMs.Maximum);
                tbOpacity.Value = Math.Clamp(cfg.Opacity, tbOpacity.Minimum, tbOpacity.Maximum);
                lblOpacityValue.Text = tbOpacity.Value + "%";
                chkFollow.Checked = cfg.FollowGameWindow;
                chkBottomAligned.Checked = cfg.BottomAligned;
            }
            finally
            {
                _syncing = false;
            }
        }

        btnToggle.Text = cfg.Visible ? "隐藏称号" : "显示称号";
    }

    private void RebuildTitleList(string currentId)
    {
        if (lstTitles.Items.Count != App.Library.Titles.Count)
        {
            lstTitles.BeginUpdate();
            lstTitles.Items.Clear();
            foreach (var entry in App.Library.Titles)
            {
                lstTitles.Items.Add(entry);
            }
            lstTitles.EndUpdate();
        }

        var index = App.Library.Titles.FindIndex(t => string.Equals(t.Id, currentId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && lstTitles.SelectedIndex != index)
        {
            lstTitles.SelectedIndex = index;
        }
    }

    private static decimal ClampDecimal(decimal value, decimal min, decimal max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    // ---------- 事件 ----------

    private void OnTitleSelected(object? sender, EventArgs e)
    {
        if (_syncing || lstTitles.SelectedItem is not TitleEntry entry)
        {
            return;
        }

        App.Config.CurrentTitle = entry.Id;
        App.SaveAndApply();
    }

    private void OnNumericChanged(object? sender, EventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var cfg = App.Config;
        cfg.Scale = (double)numScale.Value;
        cfg.AnchorX = (double)numAnchorX.Value / 100.0;
        cfg.AnchorY = (double)numAnchorY.Value / 100.0;
        cfg.OffsetX = (int)numOffsetX.Value;
        cfg.OffsetY = (int)numOffsetY.Value;
        cfg.FrameMs = (int)numFrameMs.Value;
        App.SaveAndApply();
    }

    private void OnOpacityChanged(object? sender, EventArgs e)
    {
        lblOpacityValue.Text = tbOpacity.Value + "%";
        if (_syncing)
        {
            return;
        }

        App.Config.Opacity = tbOpacity.Value;
        App.SaveAndApply();
    }

    private void OnCheckChanged(object? sender, EventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        App.Config.FollowGameWindow = chkFollow.Checked;
        App.Config.BottomAligned = chkBottomAligned.Checked;
        App.SaveAndApply();
    }

    private void OnApply(object? sender, EventArgs e)
    {
        App.SaveAndApply();
        RefreshFromConfig(syncControls: true);
    }

    private void OnToggleVisible(object? sender, EventArgs e)
    {
        App.Config.Visible = !App.Config.Visible;
        App.SaveAndApply();
    }

    private void OnPickRoot(object? sender, EventArgs e)
    {
        var current = App.Config.ResolveTitlesRoot();
        if (Directory.Exists(current))
        {
            folderDialog.SelectedPath = current;
        }

        if (folderDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        App.Config.TitlesRoot = folderDialog.SelectedPath;
        App.Config.CurrentTitle = "";
        App.RescanTitles(pickDefaultIfMissing: true);
        App.SaveAndApply();
        RefreshFromConfig(syncControls: true);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        App.Config.Save();
        Application.Exit();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        var status = App.Overlay?.Status ?? "覆盖层未运行";
        var root = App.Config.ResolveTitlesRoot();
        var folder = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        lblStatus.Text =
            $"状态：{status}\r\n" +
            $"素材：{folder}（{App.Library.Titles.Count} 个称号）";
        btnToggle.Text = App.Config.Visible ? "隐藏称号" : "显示称号";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // 关窗只是收起面板，称号继续显示；Ctrl+Alt+S 再打开。
            e.Cancel = true;
            Hide();
        }
    }
}
