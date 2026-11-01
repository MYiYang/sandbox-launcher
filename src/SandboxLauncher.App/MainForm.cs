using System.Drawing;
using System.Windows.Forms;
using SandboxLauncher.Core;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Runtime;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.App;

public sealed class MainForm : Form
{
    private readonly CatalogStore _store;
    private readonly SandboxService _service;

    private readonly ToolStrip _toolbar = new();
    private readonly SplitContainer _split = new();
    private readonly ListView _list = new();
    private readonly ImageList _icons = new();
    private readonly RichTextBox _log = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripProgressBar _progress = new();

    // 详情区
    private readonly Label _lblName = new();
    private readonly TextBox _txtSnapshot = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblSize = new();
    private readonly Label _lblTimes = new();
    private readonly ListView _apps = new();
    private readonly CheckBox _chkRedirect = new();
    private readonly TextBox _txtRegKeys = new();
    private readonly Label _lblHint = new();

    private readonly System.Windows.Forms.Timer _timer = new();
    private SandboxRecord? _selected;

    public MainForm(CatalogStore store, SandboxService service)
    {
        _store = store;
        _service = service;

        Text = $"沙盒启动器 SandboxLauncher {AppPaths.Version}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1300, 820);
        MinimumSize = new Size(1060, 680);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildToolbar();
        BuildMainArea();
        BuildStatusBar();
        WireEvents();
        RefreshList();
        AppendLog("沙盒启动器已就绪。每个沙盒就是一个 .sbx 快照文件，可以放任意位置、随时导入导出。");
        AppendLog($"沙盒目录：{_store.Root}");

        // 拖拽导入：把 exe / 程序目录拖进窗口，选好方式导入后立刻启动
        AllowDrop = true;
        DragEnter += OnDragEnterFiles;
        DragDrop += OnDropFiles;
        AppendLog("提示：把 exe 或程序目录直接拖进窗口就能导入并启动；几十 GB 的大软件在对话框里勾「引用安装目录」，不复制文件。");
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            _split.Panel1MinSize = 300;
            _split.Panel2MinSize = 470;
            _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Width - 520);
        }
        catch (InvalidOperationException)
        {
            // 窗口太窄时保持默认分割位置
        }

    }

    // ---------------- 界面搭建 ----------------

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(6, 4, 6, 4);
        _toolbar.RenderMode = ToolStripRenderMode.System;

        _toolbar.Items.Add(Button("新建沙盒", NewSandbox));
        _toolbar.Items.Add(Button("导入快照", ImportSnapshot));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("导入客户端", ImportClient));
        _toolbar.Items.Add(Button("启动沙盒", StartSelected));
        _toolbar.Items.Add(Button("停止", StopSelected));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("移动快照", MoveSnapshot));
        _toolbar.Items.Add(Button("导出副本", ExportSnapshot));
        _toolbar.Items.Add(Button("打开位置", OpenSnapshotFolder));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("沙盒文件", OpenSandboxFiles));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("设置", OpenSettings));

        Controls.Add(_toolbar);
    }

    private static ToolStripButton Button(string text, Action onClick)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = false,
            Width = TextRenderer.MeasureText(text, new Font("Microsoft YaHei UI", 9F)).Width + 22,
            Margin = new Padding(2, 0, 2, 0)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void BuildMainArea()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = 6;
        var split = _split;

        // --- 左：沙盒列表 ---
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.SmallImageList = _icons;
        _icons.ColorDepth = ColorDepth.Depth32Bit;
        _icons.ImageSize = new Size(20, 20);
        _list.Columns.Add("名称", 140);
        _list.Columns.Add("状态", 62);
        _list.Columns.Add("客户端", 136);
        _list.Columns.Add("快照文件", 226);
        _list.Columns.Add("大小", 72, HorizontalAlignment.Right);
        _list.Columns.Add("最近启动", 112);

        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        leftPanel.Controls.Add(_list);
        split.Panel1.Controls.Add(leftPanel);

        // --- 右：详情 ---
        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6), AutoScroll = true };

        var groupInfo = new GroupBox { Text = "沙盒信息", Dock = DockStyle.Top, Height = 148 };
        _lblName.SetBounds(12, 26, 490, 22);
        _lblName.AutoEllipsis = true;
        _lblStatus.AutoEllipsis = true;
        _lblSize.AutoEllipsis = true;
        _lblTimes.AutoEllipsis = true;
        _lblName.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        _lblStatus.SetBounds(12, 52, 490, 20);
        _lblSize.SetBounds(12, 74, 490, 20);
        _lblTimes.SetBounds(12, 96, 490, 20);

        _txtSnapshot.SetBounds(12, 118, 420, 23);
        _txtSnapshot.ReadOnly = true;
        var btnBrowse = new Button { Text = "…", Location = new Point(438, 117), Size = new Size(28, 24) };
        btnBrowse.Click += (_, _) => OpenSnapshotFolder();
        groupInfo.Controls.AddRange(new Control[] { _lblName, _lblStatus, _lblSize, _lblTimes, _txtSnapshot, btnBrowse });

        var groupApps = new GroupBox { Text = "沙盒内客户端", Dock = DockStyle.Top, Height = 190 };
        _apps.SetBounds(12, 24, 470, 130);
        _apps.View = View.Details;
        _apps.FullRowSelect = true;
        _apps.MultiSelect = false;
        _apps.ShowItemToolTips = true;
        _apps.Columns.Add("客户端", 108);
        _apps.Columns.Add("方式", 72);
        _apps.Columns.Add("可执行文件", 210);
        _apps.Columns.Add("大小", 74, HorizontalAlignment.Right);
        var btnAddApp = new Button { Text = "导入客户端…", Location = new Point(12, 158), Size = new Size(110, 26) };
        btnAddApp.Click += (_, _) => ImportClient();
        var btnRemoveApp = new Button { Text = "移除所选", Location = new Point(130, 158), Size = new Size(92, 26) };
        btnRemoveApp.Click += (_, _) => RemoveSelectedApp();
        groupApps.Controls.AddRange(new Control[] { _apps, btnAddApp, btnRemoveApp });

        var groupSettings = new GroupBox { Text = "隔离设置", Dock = DockStyle.Top, Height = 210 };
        _chkRedirect.SetBounds(12, 24, 470, 22);
        _chkRedirect.Text = "把用户目录 / 临时目录指向沙盒内部（推荐）";

        var lblReg = new Label { Text = "注册表隔离键（每行一个，HKCU 下）：", Location = new Point(12, 52), Size = new Size(450, 20) };
        _txtRegKeys.SetBounds(12, 74, 470, 70);
        _txtRegKeys.Multiline = true;
        _txtRegKeys.ScrollBars = ScrollBars.Vertical;
        _txtRegKeys.Font = new Font("Consolas", 9F);

        var btnSave = new Button { Text = "保存设置", Location = new Point(12, 152), Size = new Size(96, 26) };
        btnSave.Click += (_, _) => SaveSettingsFromUi();
        _lblHint.SetBounds(116, 148, 366, 56);
        _lblHint.ForeColor = Color.DimGray;
        _lblHint.Text = "同一时刻只允许一个沙盒隔离注册表；同一程序要同时开多个，请只做文件隔离（不勾注册表）。";
        groupSettings.Controls.AddRange(new Control[] { _chkRedirect, lblReg, _txtRegKeys, btnSave, _lblHint });

        rightPanel.Controls.Add(groupSettings);
        rightPanel.Controls.Add(groupApps);
        rightPanel.Controls.Add(groupInfo);
        split.Panel2.Controls.Add(rightPanel);

        // --- 底部日志 ---
        var logBox = new GroupBox { Text = "运行日志", Dock = DockStyle.Bottom, Height = 132 };
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BackColor = Color.FromArgb(250, 250, 250);
        _log.Font = new Font("Consolas", 9F);
        _log.WordWrap = false;
        _log.ScrollBars = RichTextBoxScrollBars.Both;
        logBox.Controls.Add(_log);

        Controls.Add(logBox);
        Controls.Add(split);
        split.BringToFront();
    }

    private void BuildStatusBar()
    {
        _statusStrip.Items.Add(_statusLabel);
        _statusLabel.Text = "就绪";
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Width = 120;
        _statusStrip.Items.Add(_progress);
        Controls.Add(_statusStrip);
        _statusStrip.BringToFront();
    }

    // ---------------- 事件 ----------------

    private void WireEvents()
    {
        _list.SelectedIndexChanged += (_, _) => RefreshDetails();
        _list.DoubleClick += (_, _) => StartSelected();
        _list.MouseUp += (_, e) => ShowContextMenu(e);

        _service.SessionStarted += session => OnUi(() =>
        {
            AppendLog($"▶ 已启动「{session.Record.Name}」→ {session.AppName}（PID {session.ProcessId}）");
            if (session.StartWarning != null) AppendLog("注意：" + session.StartWarning);
            RefreshList();
        });
        _service.SessionStopped += (session, exitCode, _) => OnUi(() =>
        {
            AppendLog($"■ 「{session.Record.Name}」已退出（退出码 {exitCode}），改动已写回快照。");
            RefreshList();
            RefreshDetails();

            // 秒退：把原因直接摆到用户面前（最常见是「同一个程序已经在别处跑着」的单实例锁）
            if (session.ExitHint != null && (session.StartWarning != null || exitCode != 0))
                MessageBox.Show(this, session.ExitHint, $"「{session.Record.Name}」很快就退出了",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        _service.Log += line => OnUi(() => AppendLog(line));

        _timer.Interval = 1500;
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();

        FormClosing += (_, e) =>
        {
            if (_service.Running.Count == 0) return;
            var result = MessageBox.Show(this,
                $"还有 {_service.Running.Count} 个沙盒在运行。关闭启动器会先停止它们并把改动写回快照。继续吗？",
                "确认关闭", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }

            UseWaitCursor = true;
            _service.StopAll();
            UseWaitCursor = false;
        };
    }

    private void ShowContextMenu(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = _list.GetItemAt(e.X, e.Y);
        if (item != null) item.Selected = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("启动", null, (_, _) => StartSelected());
        menu.Items.Add("停止", null, (_, _) => StopSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("浏览沙盒文件…", null, (_, _) => OpenSandboxFiles());
        menu.Items.Add("导入客户端…", null, (_, _) => ImportClient());
        menu.Items.Add("移动快照…", null, (_, _) => MoveSnapshot());
        menu.Items.Add("导出副本…", null, (_, _) => ExportSnapshot());
        menu.Items.Add("打开快照位置", null, (_, _) => OpenSnapshotFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("在目录中移除（保留快照）", null, (_, _) => DeleteSandbox(false));
        menu.Items.Add("删除沙盒并删除快照", null, (_, _) => DeleteSandbox(true));
        menu.Show(_list, e.Location);
    }

    // ---------------- 列表 / 详情 ----------------

    private void RefreshList()
    {
        var selectedId = _selected?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();

        foreach (var record in _store.Catalog.Sandboxes.OrderByDescending(s => s.LastStartedAt ?? s.CreatedAt))
        {
            _icons.Images.Add(LoadIconImage(record));
            var imageIndex = _icons.Images.Count - 1;

            var running = _service.GetRunning(record.Id) != null;
            var item = new ListViewItem(record.Name) { Tag = record, ImageIndex = imageIndex };
            item.SubItems.Add(running ? "运行中" : "空闲");
            item.SubItems.Add(record.AppsSummary);
            item.SubItems.Add(Path.GetFileName(record.SnapshotPath));
            item.SubItems.Add(FormatSize(SnapshotSize(record)));
            item.SubItems.Add(record.LastStartedAt?.ToString("MM-dd HH:mm") ?? "—");
            if (running) item.ForeColor = Color.SeaGreen;
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        UpdateStatus();

        if (selectedId != null)
        {
            var toSelect = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((SandboxRecord)i.Tag!).Id == selectedId);
            if (toSelect != null)
            {
                toSelect.Selected = true;
                _selected = (SandboxRecord)toSelect.Tag!;
            }
        }

        if (_selected == null && _list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
            _selected = (SandboxRecord)_list.Items[0].Tag!;
        }

        RefreshDetails();
        UpdateStatus();
    }

    private void RefreshDetails()
    {
        if (_list.SelectedItems.Count > 0) _selected = (SandboxRecord)_list.SelectedItems[0].Tag!;
        if (_selected == null)
        {
            _lblName.Text = "（没有选中沙盒）";
            _lblStatus.Text = "";
            _lblSize.Text = "";
            _lblTimes.Text = "";
            _txtSnapshot.Text = "";
            _apps.Items.Clear();
            return;
        }

        var record = _selected;
        var running = _service.GetRunning(record.Id);
        _lblName.Text = record.Name;
        _lblStatus.Text = running != null ? $"状态：运行中（{running.AppName}，PID {running.ProcessId}）" : "状态：空闲";
        _lblStatus.ForeColor = running != null ? Color.SeaGreen : SystemColors.ControlText;
        if (record.Apps.Any(a => a.IsReference && !a.SourceAvailable))
        {
            _lblStatus.Text += "　⚠ 有引用客户端的源程序不在了（请重新导入或改用复制）";
            _lblStatus.ForeColor = Color.Firebrick;
        }
        _lblSize.Text = $"快照：{(File.Exists(record.SnapshotPath) ? FormatSize(SnapshotSize(record)) : "文件不存在")}" +
                        (record.Apps.Any(a => a.IsReference)
                            ? $"　引用客户端：{record.Apps.Count(a => a.IsReference)} 个（程序文件不占快照）"
                            : "");
        _lblTimes.Text = $"最近启动：{FormatTime(record.LastStartedAt)}　最近退出：{FormatTime(record.LastStoppedAt)}";

        // 虚拟磁盘（基础盘 / 分身）关系与占用
        var vhdApps = record.Apps.Where(a => !string.IsNullOrWhiteSpace(a.VhdPath)).ToList();
        if (vhdApps.Count > 0)
        {
            var parts = vhdApps.Select(a =>
            {
                var exists = File.Exists(a.VhdPath);
                var size = exists ? FormatSize(new FileInfo(a.VhdPath!).Length) : "文件不存在";
                var stem = Path.GetFileNameWithoutExtension(a.VhdPath);
                var kind = stem.EndsWith("-child", StringComparison.OrdinalIgnoreCase) ? "差分盘" : stem.EndsWith("-base", StringComparison.OrdinalIgnoreCase) ? "基础盘" : "虚拟磁盘";
                return $"{Path.GetFileName(a.VhdPath)}（{kind}，{size}）→ {a.VhdDriveLetter}:";
            });
            _lblSize.Text += "　VHD：" + string.Join("；", parts);
        }
        _txtSnapshot.Text = record.SnapshotPath;

        _apps.Items.Clear();
        foreach (var app in record.Apps)
        {
            var item = new ListViewItem(app.Name) { Tag = app };
            item.SubItems.Add(app.ModeShortText);
            item.SubItems.Add(app.ExecutablePath);
            item.SubItems.Add(app.IsReference ? "不占快照" : FormatSize(app.SizeBytes));
            if (app.IsReference && !app.SourceAvailable)
            {
                item.ForeColor = Color.Firebrick;
                item.ToolTipText = "源程序不在了（安装目录被移动、重命名或卸载？）请重新导入，或改用复制进快照";
            }
            else if (app.IsReference)
            {
                item.ToolTipText = $"引用安装目录：程序文件不复制，启动时直接运行 {app.ExecutablePath}";
            }
            else
            {
                item.ToolTipText = $"已复制进快照：{app.ExecutablePath}（{app.FileCount} 个文件）";
            }

            _apps.Items.Add(item);
        }

        _chkRedirect.Checked = record.Settings.RedirectUserProfile;
        _txtRegKeys.Text = string.Join(Environment.NewLine, record.Settings.IsolateRegistryKeys);
    }

    private long SnapshotSize(SandboxRecord record)
    {
        try
        {
            return File.Exists(record.SnapshotPath) ? new FileInfo(record.SnapshotPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Image LoadIconImage(SandboxRecord record)
    {
        var base64 = record.Apps.FirstOrDefault(a => a.IconBase64 != null)?.IconBase64;
        if (!string.IsNullOrEmpty(base64))
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(base64));
                using var bitmap = new Bitmap(stream);
                return new Bitmap(bitmap, new Size(20, 20));
            }
            catch
            {
                // 图标坏了不影响使用
            }
        }

        return SystemIcons.Application.ToBitmap();
    }

    // ---------------- 工具栏动作 ----------------

    private void NewSandbox()
    {
        using var dialog = new NewSandboxDialog(_store.DefaultSnapshotDirectory);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var record = _store.Create(dialog.SandboxName, dialog.SnapshotPath);
            SnapshotFile.Create(record.SnapshotPath, new SnapshotManifest
            {
                SandboxId = record.Id,
                Name = record.Name,
                Settings = record.Settings
            });
            _selected = record;
            RefreshList();
            AppendLog($"已新建沙盒「{record.Name}」，快照：{record.SnapshotPath}");
        }
        catch (Exception ex)
        {
            ShowError("新建沙盒失败", ex);
        }
    }

    private void ImportSnapshot()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择一个沙盒快照（.sbx）",
            Filter = "沙盒快照 (*.sbx)|*.sbx|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_store.Settings.LastOpenSnapshotDirectory)
                ? _store.Settings.LastOpenSnapshotDirectory
                : _store.DefaultSnapshotDirectory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _store.Settings.LastOpenSnapshotDirectory = Path.GetDirectoryName(dialog.FileName);
            var record = _service.RegisterSnapshot(dialog.FileName);
            _store.Save();
            _selected = record;
            RefreshList();
            AppendLog($"已导入快照并登记为沙盒「{record.Name}」（{record.Apps.Count} 个客户端）");
        }
        catch (Exception ex)
        {
            ShowError("导入快照失败", ex);
        }
    }

    private void ImportClient() => ImportClientFrom(null, startAfterImport: false);

    /// <summary>
    /// 导入客户端（可预填路径）。拖拽导入时 startAfterImport = true —— 拖进来就跑。
    /// </summary>
    private void ImportClientFrom(string? initialPath, bool startAfterImport)
    {
        if (_selected == null)
        {
            MessageBox.Show(this, "请先在左边选中一个沙盒。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_service.IsRunning(_selected.Id))
        {
            MessageBox.Show(this, "请先停止这个沙盒，再导入客户端。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ImportClientDialog(initialPath);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var record = _selected;
        var mode = dialog.Mode;
        var extraArgs = dialog.Arguments;
        var createVhd = dialog.CreateVhd;
        var vhdLocation = dialog.VhdLocationRoot;
        var vhdSize = dialog.VhdSizeBytes;
        var vhdLetter = dialog.VhdDriveLetter;
        _progress.Visible = true;
        _statusLabel.Text = createVhd ? "正在创建虚拟磁盘并搬入程序（大程序要等一会儿）…"
            : mode == AppSourceMode.Reference ? "正在引用程序（不复制文件）…" : "正在导入客户端…";
        Enabled = false;

        Task.Run(() =>
        {
            try
            {
                var app = createVhd
                    ? _service.ImportAppToVhd(record, dialog.SourcePath, dialog.ClientName, vhdLocation, vhdSize, vhdLetter,
                        message => OnUi(() => AppendLog(message))).App
                    : _service.ImportApp(record, dialog.SourcePath, dialog.ClientName,
                        message => OnUi(() => AppendLog(message)), mode);
                if (!string.IsNullOrWhiteSpace(extraArgs))
                {
                    app.Arguments = extraArgs;
                    _service.UpdateApp(record, app);   // 同时写进快照清单，下次启动才不会被旧值覆盖
                }

                OnUi(() =>
                {
                    AppendLog($"✅ 客户端「{app.Name}」已导入沙盒「{record.Name}」（{app.ModeText}）");
                    RefreshList();
                });

                if (startAfterImport) OnUi(StartSelected);
            }
            catch (Exception ex)
            {
                OnUi(() => ShowError("导入客户端失败", ex));
            }
            finally
            {
                OnUi(() =>
                {
                    Enabled = true;
                    _progress.Visible = false;
                    _statusLabel.Text = "就绪";
                });
            }
        });
    }

    // ---------- 拖拽导入：把 exe / 程序目录直接拖进窗口 ----------

    private void OnDragEnterFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDropFiles(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var path = paths[0];
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        if (_selected == null)
        {
            MessageBox.Show(this, "请先在左边选中（或新建）一个沙盒，再把程序拖进来。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ImportClientFrom(path, startAfterImport: true);
    }

    private void RemoveSelectedApp()
    {
        if (_selected == null || _apps.SelectedItems.Count == 0) return;
        var app = (SandboxAppRecord)_apps.SelectedItems[0].Tag!;
        if (MessageBox.Show(this, $"确定从沙盒里移除客户端「{app.Name}」吗？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            _service.RemoveApp(_selected, app.Id);
            RefreshList();
            AppendLog($"已移除客户端「{app.Name}」");
        }
        catch (Exception ex)
        {
            ShowError("移除失败", ex);
        }
    }

    private void StartSelected()
    {
        if (_selected == null)
        {
            MessageBox.Show(this, "请先在左边选中一个沙盒。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_selected.Apps.Count == 0)
        {
            MessageBox.Show(this, "这个沙盒还没导入客户端，请先点「导入客户端」。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            _service.Start(_selected);
            RefreshList();
        }
        catch (Exception ex)
        {
            ShowError("启动失败", ex);
        }
    }

    private void StopSelected()
    {
        if (_selected == null) return;
        if (!_service.IsRunning(_selected.Id))
        {
            AppendLog($"「{_selected.Name}」当前没有运行。");
            return;
        }

        _progress.Visible = true;
        Task.Run(() =>
        {
            _service.Stop(_selected.Id);
            OnUi(() =>
            {
                _progress.Visible = false;
                RefreshList();
            });
        });
    }

    private void MoveSnapshot()
    {
        if (_selected == null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "把快照文件移动到…",
            Filter = "沙盒快照 (*.sbx)|*.sbx",
            FileName = Path.GetFileName(_selected.SnapshotPath),
            InitialDirectory = Path.GetDirectoryName(_selected.SnapshotPath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _service.RelocateSnapshot(_selected, dialog.FileName);
            _store.Save();
            RefreshList();
            AppendLog($"快照已移动到：{_selected.SnapshotPath}");
        }
        catch (Exception ex)
        {
            ShowError("移动快照失败", ex);
        }
    }

    private void ExportSnapshot()
    {
        if (_selected == null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "导出快照副本",
            Filter = "沙盒快照 (*.sbx)|*.sbx",
            FileName = $"{_selected.Name}-副本.sbx",
            InitialDirectory = Path.GetDirectoryName(_selected.SnapshotPath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var target = _service.ExportSnapshot(_selected, dialog.FileName);
            AppendLog($"已导出快照副本：{target}");
        }
        catch (Exception ex)
        {
            ShowError("导出失败", ex);
        }
    }

    private void OpenSandboxFiles()
    {
        if (_selected == null)
        {
            MessageBox.Show(this, "请先在左边选中一个沙盒。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            using var dialog = new SandboxFilesDialog(_selected, _store, _service);
            dialog.ShowDialog(this);
            RefreshDetails();
        }
        catch (Exception ex)
        {
            ShowError("打开沙盒文件失败", ex);
        }
    }

    private void OpenSnapshotFolder()
    {
        if (_selected == null) return;
        try
        {
            var folder = Path.GetDirectoryName(_selected.SnapshotPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(_selected.SnapshotPath)
                    ? $"/select,\"{_selected.SnapshotPath}\""
                    : $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("打开目录失败", ex);
        }
    }

    private void DeleteSandbox(bool deleteSnapshot)
    {
        if (_selected == null) return;
        var message = deleteSnapshot
            ? $"确定删除沙盒「{_selected.Name}」并删除快照文件吗？\n{_selected.SnapshotPath}"
            : $"确定从列表移除沙盒「{_selected.Name}」吗？（快照文件保留）";
        if (MessageBox.Show(this, message, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            _service.Stop(_selected.Id);
            _store.Remove(_selected, deleteSnapshot);
            _selected = null;
            RefreshList();
            AppendLog(deleteSnapshot ? "沙盒与快照已删除" : "沙盒已从列表移除（快照保留）");
        }
        catch (Exception ex)
        {
            ShowError("删除失败", ex);
        }
    }

    private void SaveSettingsFromUi()
    {
        if (_selected == null) return;
        _selected.Settings.RedirectUserProfile = _chkRedirect.Checked;
        _selected.Settings.IsolateRegistryKeys = _txtRegKeys.Lines
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
        _store.Save();
        AppendLog($"已保存「{_selected.Name}」的隔离设置：用户目录重定向={_selected.Settings.RedirectUserProfile}，" +
                  $"注册表键={( _selected.Settings.IsolateRegistryKeys.Count == 0 ? "无" : string.Join("、", _selected.Settings.IsolateRegistryKeys))}");
        RefreshDetails();
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsDialog(_store.SessionRoot, _store.DefaultSnapshotDirectory);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _store.Settings.SessionRoot = dialog.SessionRoot;
        _store.Settings.DefaultSnapshotDirectory = dialog.DefaultSnapshotDirectory;
        _store.Save();
        RefreshList();
        AppendLog("全局设置已保存");
    }

    // ---------------- 杂项 ----------------

    private void UpdateStatus()
    {
        var running = _service.Running.Count;
        _statusLabel.Text = running == 0
            ? $"就绪 · 共 {_store.Catalog.Sandboxes.Count} 个沙盒"
            : $"运行中 {running} 个 · 共 {_store.Catalog.Sandboxes.Count} 个沙盒";
    }

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(action); } catch (ObjectDisposedException) { }
        }
        else
        {
            action();
        }
    }

    private void AppendLog(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ShowError(string title, Exception ex)
    {
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppendLog($"❌ {title}：{ex.Message}");
    }

    private static string FormatTime(DateTimeOffset? time) => time?.ToString("yyyy-MM-dd HH:mm") ?? "—";

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
