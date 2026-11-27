#nullable enable

namespace TitleOverlay;

partial class SettingsForm
{
    private System.ComponentModel.IContainer? components = null;

    private Label lblTitle = null!;
    private ListBox lstTitles = null!;
    private Label lblScale = null!;
    private NumericUpDown numScale = null!;
    private Label lblOpacity = null!;
    private TrackBar tbOpacity = null!;
    private Label lblOpacityValue = null!;
    private Label lblAnchorX = null!;
    private NumericUpDown numAnchorX = null!;
    private Label lblAnchorY = null!;
    private NumericUpDown numAnchorY = null!;
    private Label lblOffsetX = null!;
    private NumericUpDown numOffsetX = null!;
    private Label lblOffsetY = null!;
    private NumericUpDown numOffsetY = null!;
    private Label lblFrameMs = null!;
    private NumericUpDown numFrameMs = null!;
    private CheckBox chkFollow = null!;
    private CheckBox chkBottomAligned = null!;
    private Button btnApply = null!;
    private Button btnToggle = null!;
    private Button btnPickRoot = null!;
    private Button btnExit = null!;
    private Label lblHotkey = null!;
    private Label lblStatus = null!;
    private FolderBrowserDialog folderDialog = null!;
    private System.Windows.Forms.Timer timerStatus = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        lblTitle = new Label();
        lstTitles = new ListBox();
        lblScale = new Label();
        numScale = new NumericUpDown();
        lblOpacity = new Label();
        tbOpacity = new TrackBar();
        lblOpacityValue = new Label();
        lblAnchorX = new Label();
        numAnchorX = new NumericUpDown();
        lblAnchorY = new Label();
        numAnchorY = new NumericUpDown();
        lblOffsetX = new Label();
        numOffsetX = new NumericUpDown();
        lblOffsetY = new Label();
        numOffsetY = new NumericUpDown();
        lblFrameMs = new Label();
        numFrameMs = new NumericUpDown();
        chkFollow = new CheckBox();
        chkBottomAligned = new CheckBox();
        btnApply = new Button();
        btnToggle = new Button();
        btnPickRoot = new Button();
        btnExit = new Button();
        lblHotkey = new Label();
        lblStatus = new Label();
        folderDialog = new FolderBrowserDialog();
        timerStatus = new System.Windows.Forms.Timer(components);
        ((System.ComponentModel.ISupportInitialize)numScale).BeginInit();
        ((System.ComponentModel.ISupportInitialize)numAnchorX).BeginInit();
        ((System.ComponentModel.ISupportInitialize)numAnchorY).BeginInit();
        ((System.ComponentModel.ISupportInitialize)numOffsetX).BeginInit();
        ((System.ComponentModel.ISupportInitialize)numOffsetY).BeginInit();
        ((System.ComponentModel.ISupportInitialize)numFrameMs).BeginInit();
        ((System.ComponentModel.ISupportInitialize)tbOpacity).BeginInit();
        SuspendLayout();
        // 
        // lblTitle
        // 
        lblTitle.AutoSize = true;
        lblTitle.Location = new Point(12, 12);
        lblTitle.Name = "lblTitle";
        lblTitle.Size = new Size(60, 17);
        lblTitle.TabIndex = 0;
        lblTitle.Text = "称号列表";
        // 
        // lstTitles
        // 
        lstTitles.FormattingEnabled = true;
        lstTitles.IntegralHeight = false;
        lstTitles.ItemHeight = 17;
        lstTitles.Location = new Point(12, 34);
        lstTitles.Name = "lstTitles";
        lstTitles.Size = new Size(210, 300);
        lstTitles.TabIndex = 1;
        // 
        // lblScale
        // 
        lblScale.AutoSize = true;
        lblScale.Location = new Point(240, 34);
        lblScale.Name = "lblScale";
        lblScale.Size = new Size(44, 17);
        lblScale.TabIndex = 2;
        lblScale.Text = "缩放倍率";
        // 
        // numScale
        // 
        numScale.DecimalPlaces = 2;
        numScale.Increment = new decimal(new int[] { 5, 0, 0, 131072 });
        numScale.Location = new Point(360, 32);
        numScale.Maximum = new decimal(new int[] { 4, 0, 0, 0 });
        numScale.Minimum = new decimal(new int[] { 2, 0, 0, 131072 });
        numScale.Name = "numScale";
        numScale.Size = new Size(96, 23);
        numScale.TabIndex = 3;
        numScale.Value = new decimal(new int[] { 1, 0, 0, 0 });
        // 
        // lblOpacity
        // 
        lblOpacity.AutoSize = true;
        lblOpacity.Location = new Point(240, 68);
        lblOpacity.Name = "lblOpacity";
        lblOpacity.Size = new Size(56, 17);
        lblOpacity.TabIndex = 4;
        lblOpacity.Text = "不透明度";
        // 
        // tbOpacity
        // 
        tbOpacity.Location = new Point(240, 88);
        tbOpacity.Maximum = 100;
        tbOpacity.Minimum = 5;
        tbOpacity.Name = "tbOpacity";
        tbOpacity.Size = new Size(180, 45);
        tbOpacity.TabIndex = 5;
        tbOpacity.TickFrequency = 10;
        tbOpacity.Value = 100;
        // 
        // lblOpacityValue
        // 
        lblOpacity.AutoSize = true;
        lblOpacityValue.AutoSize = true;
        lblOpacityValue.Location = new Point(424, 68);
        lblOpacityValue.Name = "lblOpacityValue";
        lblOpacityValue.Size = new Size(32, 17);
        lblOpacityValue.TabIndex = 6;
        lblOpacityValue.Text = "100%";
        // 
        // lblAnchorX
        // 
        lblAnchorX.AutoSize = true;
        lblAnchorX.Location = new Point(240, 140);
        lblAnchorX.Name = "lblAnchorX";
        lblAnchorX.Size = new Size(80, 17);
        lblAnchorX.TabIndex = 7;
        lblAnchorX.Text = "锚点横向 %";
        // 
        // numAnchorX
        // 
        numAnchorX.Location = new Point(360, 138);
        numAnchorX.Name = "numAnchorX";
        numAnchorX.Size = new Size(96, 23);
        numAnchorX.TabIndex = 8;
        numAnchorX.Value = new decimal(new int[] { 50, 0, 0, 0 });
        // 
        // lblAnchorY
        // 
        lblAnchorY.AutoSize = true;
        lblAnchorY.Location = new Point(240, 172);
        lblAnchorY.Name = "lblAnchorY";
        lblAnchorY.Size = new Size(80, 17);
        lblAnchorY.TabIndex = 9;
        lblAnchorY.Text = "锚点纵向 %";
        // 
        // numAnchorY
        // 
        numAnchorY.Location = new Point(360, 170);
        numAnchorY.Name = "numAnchorY";
        numAnchorY.Size = new Size(96, 23);
        numAnchorY.TabIndex = 10;
        numAnchorY.Value = new decimal(new int[] { 50, 0, 0, 0 });
        // 
        // lblOffsetX
        // 
        lblOffsetX.AutoSize = true;
        lblOffsetX.Location = new Point(240, 204);
        lblOffsetX.Name = "lblOffsetX";
        lblOffsetX.Size = new Size(80, 17);
        lblOffsetX.TabIndex = 11;
        lblOffsetX.Text = "横向微调 px";
        // 
        // numOffsetX
        // 
        numOffsetX.Location = new Point(360, 202);
        numOffsetX.Maximum = new decimal(new int[] { 2000, 0, 0, 0 });
        numOffsetX.Minimum = new decimal(new int[] { 2000, 0, 0, -2147483648 });
        numOffsetX.Name = "numOffsetX";
        numOffsetX.Size = new Size(96, 23);
        numOffsetX.TabIndex = 12;
        // 
        // lblOffsetY
        // 
        lblOffsetY.AutoSize = true;
        lblOffsetY.Location = new Point(240, 236);
        lblOffsetY.Name = "lblOffsetY";
        lblOffsetY.Size = new Size(80, 17);
        lblOffsetY.TabIndex = 13;
        lblOffsetY.Text = "纵向微调 px";
        // 
        // numOffsetY
        // 
        numOffsetY.Location = new Point(360, 234);
        numOffsetY.Maximum = new decimal(new int[] { 2000, 0, 0, 0 });
        numOffsetY.Minimum = new decimal(new int[] { 2000, 0, 0, -2147483648 });
        numOffsetY.Name = "numOffsetY";
        numOffsetY.Size = new Size(96, 23);
        numOffsetY.TabIndex = 14;
        numOffsetY.Value = new decimal(new int[] { 100, 0, 0, -2147483648 });
        // 
        // lblFrameMs
        // 
        lblFrameMs.AutoSize = true;
        lblFrameMs.Location = new Point(240, 268);
        lblFrameMs.Name = "lblFrameMs";
        lblFrameMs.Size = new Size(68, 17);
        lblFrameMs.TabIndex = 15;
        lblFrameMs.Text = "每帧毫秒";
        // 
        // numFrameMs
        // 
        numFrameMs.Location = new Point(360, 266);
        numFrameMs.Maximum = new decimal(new int[] { 500, 0, 0, 0 });
        numFrameMs.Minimum = new decimal(new int[] { 10, 0, 0, 0 });
        numFrameMs.Name = "numFrameMs";
        numFrameMs.Size = new Size(96, 23);
        numFrameMs.TabIndex = 16;
        numFrameMs.Value = new decimal(new int[] { 40, 0, 0, 0 });
        // 
        // chkFollow
        // 
        chkFollow.AutoSize = true;
        chkFollow.Location = new Point(240, 300);
        chkFollow.Name = "chkFollow";
        chkFollow.Size = new Size(150, 21);
        chkFollow.TabIndex = 17;
        chkFollow.Text = "跟随游戏窗口（头顶）";
        chkFollow.UseVisualStyleBackColor = true;
        // 
        // chkBottomAligned
        // 
        chkBottomAligned.AutoSize = true;
        chkBottomAligned.Location = new Point(240, 326);
        chkBottomAligned.Name = "chkBottomAligned";
        chkBottomAligned.Size = new Size(150, 21);
        chkBottomAligned.TabIndex = 18;
        chkBottomAligned.Text = "称号底部贴齐锚点";
        chkBottomAligned.UseVisualStyleBackColor = true;
        // 
        // btnApply
        // 
        btnApply.Location = new Point(12, 348);
        btnApply.Name = "btnApply";
        btnApply.Size = new Size(100, 32);
        btnApply.TabIndex = 19;
        btnApply.Text = "应用";
        btnApply.UseVisualStyleBackColor = true;
        // 
        // btnToggle
        // 
        btnToggle.Location = new Point(122, 348);
        btnToggle.Name = "btnToggle";
        btnToggle.Size = new Size(100, 32);
        btnToggle.TabIndex = 20;
        btnToggle.Text = "显示 / 隐藏";
        btnToggle.UseVisualStyleBackColor = true;
        // 
        // btnPickRoot
        // 
        btnPickRoot.Location = new Point(232, 348);
        btnPickRoot.Name = "btnPickRoot";
        btnPickRoot.Size = new Size(110, 32);
        btnPickRoot.TabIndex = 21;
        btnPickRoot.Text = "素材目录…";
        btnPickRoot.UseVisualStyleBackColor = true;
        // 
        // btnExit
        // 
        btnExit.Location = new Point(352, 348);
        btnExit.Name = "btnExit";
        btnExit.Size = new Size(100, 32);
        btnExit.TabIndex = 22;
        btnExit.Text = "退出";
        btnExit.UseVisualStyleBackColor = true;
        // 
        // lblHotkey
        // 
        lblHotkey.Location = new Point(12, 392);
        lblHotkey.Name = "lblHotkey";
        lblHotkey.Size = new Size(440, 96);
        lblHotkey.TabIndex = 23;
        lblHotkey.Text = "hotkey";
        // 
        // lblStatus
        // 
        lblStatus.BorderStyle = BorderStyle.FixedSingle;
        lblStatus.Location = new Point(12, 494);
        lblStatus.Name = "lblStatus";
        lblStatus.Padding = new Padding(4);
        lblStatus.Size = new Size(440, 54);
        lblStatus.TabIndex = 24;
        lblStatus.Text = "状态";
        // 
        // folderDialog
        // 
        folderDialog.Description = "选择称号素材目录（每个子目录是一个称号，内含 PNG 序列帧）";
        folderDialog.UseDescriptionForTitle = true;
        // 
        // timerStatus
        // 
        timerStatus.Interval = 300;
        // 
        // SettingsForm
        // 
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(466, 562);
        Controls.Add(lblStatus);
        Controls.Add(lblHotkey);
        Controls.Add(btnExit);
        Controls.Add(btnPickRoot);
        Controls.Add(btnToggle);
        Controls.Add(btnApply);
        Controls.Add(chkBottomAligned);
        Controls.Add(chkFollow);
        Controls.Add(numFrameMs);
        Controls.Add(lblFrameMs);
        Controls.Add(numOffsetY);
        Controls.Add(lblOffsetY);
        Controls.Add(numOffsetX);
        Controls.Add(lblOffsetX);
        Controls.Add(numAnchorY);
        Controls.Add(lblAnchorY);
        Controls.Add(numAnchorX);
        Controls.Add(lblAnchorX);
        Controls.Add(lblOpacityValue);
        Controls.Add(tbOpacity);
        Controls.Add(lblOpacity);
        Controls.Add(numScale);
        Controls.Add(lblScale);
        Controls.Add(lstTitles);
        Controls.Add(lblTitle);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Name = "SettingsForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "流放之路 头顶称号 · 设置";
        ((System.ComponentModel.ISupportInitialize)numScale).EndInit();
        ((System.ComponentModel.ISupportInitialize)numAnchorX).EndInit();
        ((System.ComponentModel.ISupportInitialize)numAnchorY).EndInit();
        ((System.ComponentModel.ISupportInitialize)numOffsetX).EndInit();
        ((System.ComponentModel.ISupportInitialize)numOffsetY).EndInit();
        ((System.ComponentModel.ISupportInitialize)numFrameMs).EndInit();
        ((System.ComponentModel.ISupportInitialize)tbOpacity).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }
}
