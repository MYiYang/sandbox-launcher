using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SandboxLauncher.Core;
using SandboxLauncher.Core.Files;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Runtime;

namespace SandboxLauncher.App;

/// <summary>
/// 沙盒文件浏览器：沙盒运行时直接浏览会话目录（实时），没运行时浏览 .sbx 快照；
/// 可以把文件导出到宿主机、打开、拖到资源管理器，也可以把宿主的文件放进沙盒；
/// 还能打开程序写在沙盒外（例如微信的 D:\WeChatFiles\xwechat_files）的真实目录。
/// </summary>
public sealed class SandboxFilesDialog : Form
{
    private const string LazyMarker = "__lazy__";

    private readonly SandboxRecord _record;
    private readonly CatalogStore _store;
    private readonly SandboxService _service;
    private readonly SandboxFileSystem _sandboxScope;
    private readonly IReadOnlyList<ExternalFolder> _detected;

    private readonly ToolStrip _toolbar = new();
    private readonly Panel _infoPanel = new();
    private readonly Label _modeLabel = new();
    private readonly Label _externalLabel = new();
    private readonly Panel _pathPanel = new();
    private readonly TextBox _pathBox = new();
    private readonly TextBox _searchBox = new();
    private readonly SplitContainer _split = new();
    private readonly TreeView _tree = new();
    private readonly ListView _list = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _countLabel = new();
    private readonly ToolStripProgressBar _progress = new();

    private IFileScope _scope;
    private string _currentPath = "";
    private List<SandboxFileEntry> _entries = new();
    private string? _lastExportDirectory;
    private bool _suppressTreeEvents;
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    private sealed class BrowserNode
    {
        public BrowserNode(IFileScope scope, string relativePath, bool hasChildren = true)
        {
            Scope = scope;
            RelativePath = relativePath;
            HasChildren = hasChildren;
        }

        public IFileScope Scope { get; }
        public string RelativePath { get; }
        public bool HasChildren { get; set; }
    }

    public SandboxFilesDialog(SandboxRecord record, CatalogStore store, SandboxService service)
    {
        _record = record;
        _store = store;
        _service = service;

        var activity = service.GetActivity(record);
        _sandboxScope = SandboxFileSystem.Open(record, activity.HasSession ? activity.SessionDir : null);
        _scope = _sandboxScope;

        try
        {
            _detected = ExternalFolderDetector.Detect(_sandboxScope);
        }
        catch (Exception)
        {
            _detected = Array.Empty<ExternalFolder>();
        }

        Text = $"沙盒文件 · {record.Name}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 760);
        MinimumSize = new Size(880, 560);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        BuildToolbar();
        BuildInfoPanel();
        BuildPathPanel();
        BuildCenter();
        BuildStatusBar();

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_pathPanel);
        Controls.Add(_infoPanel);
        Controls.Add(_toolbar);

        WireEvents();
        BuildTree();
        Navigate(_sandboxScope, "");
        UpdateModeLabel();
        AppendStatus($"已打开「{record.Name}」：{_sandboxScope.KindText}");
    }

    public SandboxFilesDialog(SandboxRecord record, CatalogStore store, SandboxService service,
        string? initialPath, string? initialHostPath = null)
        : this(record, store, service)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(initialHostPath) && Directory.Exists(initialHostPath))
            {
                Navigate(new HostFolderScope(initialHostPath), "");
                return;
            }

            if (!string.IsNullOrWhiteSpace(initialPath))
                Navigate(_sandboxScope, _sandboxScope.Normalize(initialPath));
        }
        catch (Exception)
        {
            // 初始路径无效就留在根目录
        }
    }

    // ---------------- 界面搭建 ----------------

    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(6, 4, 6, 4);
        _toolbar.RenderMode = ToolStripRenderMode.System;

        _toolbar.Items.Add(Button("上级", NavigateUp));
        _toolbar.Items.Add(Button("刷新", RefreshCurrent));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("导出到宿主机…", ExportSelected));
        _toolbar.Items.Add(Button("打开", OpenSelected));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("导入文件…", ImportFiles));
        _toolbar.Items.Add(Button("导入文件夹…", ImportFolder));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("打开所在目录", OpenCurrentFolder));
        _toolbar.Items.Add(Button("添加宿主目录…", AddHostFolder));
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(Button("删除", DeleteSelected));
    }

    private static ToolStripButton Button(string text, Action onClick)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = false,
            Width = TextRenderer.MeasureText(text, new Font("Microsoft YaHei UI", 9F), Size.Empty,
                TextFormatFlags.NoPadding).Width + 34,
            Margin = new Padding(2, 0, 2, 0)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void BuildInfoPanel()
    {
        _infoPanel.Dock = DockStyle.Top;
        _infoPanel.Height = 54;
        _infoPanel.Padding = new Padding(10, 4, 10, 2);
        _infoPanel.BackColor = Color.FromArgb(247, 249, 252);

        _modeLabel.Dock = DockStyle.Top;
        _modeLabel.Height = 22;
        _modeLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _modeLabel.AutoEllipsis = true;

        _externalLabel.Dock = DockStyle.Top;
        _externalLabel.Height = 22;
        _externalLabel.ForeColor = Color.FromArgb(180, 95, 6);
        _externalLabel.AutoEllipsis = true;

        _infoPanel.Controls.Add(_externalLabel);
        _infoPanel.Controls.Add(_modeLabel);
    }

    private void BuildPathPanel()
    {
        _pathPanel.Dock = DockStyle.Top;
        _pathPanel.Height = 36;
        _pathPanel.Padding = new Padding(10, 6, 10, 4);

        var label = new Label { Text = "位置", Width = 44, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        var searchLabel = new Label
        {
            Text = "筛选", Width = 44, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleLeft
        };

        _searchBox.Dock = DockStyle.Right;
        _searchBox.Width = 170;
        _searchBox.PlaceholderText = "按名称筛选当前目录";
        _pathBox.PlaceholderText = "相对沙盒根的路径（空 = 沙盒根目录）";

        _pathBox.Dock = DockStyle.Fill;
        _pathBox.Font = new Font("Consolas", 9.5F);

        _pathPanel.Controls.Add(_pathBox);
        _pathPanel.Controls.Add(label);
        _pathPanel.Controls.Add(_searchBox);
        _pathPanel.Controls.Add(searchLabel);
    }

    private void BuildCenter()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = 6;
        _split.Panel1MinSize = 220;

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.ShowNodeToolTips = true;
        _tree.ShowLines = false;
        _tree.ItemHeight = 22;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = true;
        _list.AllowDrop = false;
        _list.Columns.Add("名称", 360);
        _list.Columns.Add("大小", 100, HorizontalAlignment.Right);
        _list.Columns.Add("修改时间", 140);
        _list.Columns.Add("类型", 80);

        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 4, 6) };
        leftPanel.Controls.Add(_tree);
        _split.Panel1.Controls.Add(leftPanel);

        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 6, 8, 6) };
        rightPanel.Controls.Add(_list);
        _split.Panel2.Controls.Add(rightPanel);
    }

    private void BuildStatusBar()
    {
        _statusLabel.Text = "就绪";
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _countLabel.Text = "";
        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Width = 120;

        _status.Items.Add(_statusLabel);
        _status.Items.Add(_countLabel);
        _status.Items.Add(_progress);
    }

    // ---------------- 事件 ----------------

    private void WireEvents()
    {
        _tree.BeforeExpand += (_, e) =>
        {
            if (e.Node != null) PopulateNode(e.Node);
        };
        _tree.AfterSelect += (_, e) =>
        {
            if (_suppressTreeEvents) return;
            if (e.Node?.Tag is BrowserNode node) Navigate(node.Scope, node.RelativePath);
        };

        _list.DoubleClick += (_, _) => ActivateSelection();
        _list.SelectedIndexChanged += (_, _) => UpdateSelectionStatus();
        _list.ColumnClick += (_, e) => SortByColumn(e.Column);
        _list.ItemDrag += (_, e) => DragOut(e.Item);
        _list.MouseUp += (_, e) => ShowListMenu(e);
        _list.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    ActivateSelection();
                    e.Handled = true;
                    break;
                case Keys.Back:
                    NavigateUp();
                    e.Handled = true;
                    break;
                case Keys.F5:
                    RefreshCurrent();
                    e.Handled = true;
                    break;
            }
        };

        _searchBox.TextChanged += (_, _) => RefreshList();
        _pathBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            try
            {
                Navigate(_scope, _scope.Normalize(_pathBox.Text));
            }
            catch (Exception ex)
            {
                ShowError("路径无效", ex);
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) RefreshCurrent();
            if (e.KeyCode == Keys.Escape) Close();
        };

        FormClosed += (_, _) => _sandboxScope.Dispose();
    }

    // ---------------- 目录树 ----------------

    private void BuildTree()
    {
        _suppressTreeEvents = true;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();

            var quick = new TreeNode("常用位置");
            foreach (var (label, relative) in new[]
                     {
                         ("沙盒根目录（fs）", ""),
                         ("用户目录 profile", "profile"),
                         ("我的文档", "profile/Documents"),
                         ("下载", "profile/Downloads"),
                         ("桌面", "profile/Desktop"),
                         ("图片", "profile/Pictures"),
                         ("临时目录", "profile/Temp"),
                         ("AppData", "profile/AppData"),
                         ("客户端程序", "apps")
                     })
            {
                if (relative.Length > 0 && !_sandboxScope.DirectoryExists(relative)) continue;
                quick.Nodes.Add(new TreeNode(label)
                {
                    Tag = new BrowserNode(_sandboxScope, relative, hasChildren: false),
                    ToolTipText = _sandboxScope.KindText + " · " + (relative.Length == 0 ? "fs" : relative)
                });
            }

            _tree.Nodes.Add(quick);

            var sandboxNode = new TreeNode($"沙盒内文件 · {_record.Name}")
            {
                Tag = new BrowserNode(_sandboxScope, ""),
                ToolTipText = _sandboxScope.DisplayRoot
            };
            AddLazyChild(sandboxNode);
            _tree.Nodes.Add(sandboxNode);

            var hostNode = new TreeNode("宿主目录（沙盒外）");
            AddHostFolders(hostNode);
            _tree.Nodes.Add(hostNode);

            if (_detected.Count > 0) hostNode.Expand();
            else _tree.Nodes.Add(new TreeNode("（可用「添加宿主目录…」把沙盒外的文件夹加进来）")
            {
                ForeColor = Color.DimGray,
                Tag = null
            });

            quick.Expand();
            sandboxNode.Expand();
            _tree.SelectedNode = sandboxNode;
        }
        finally
        {
            _tree.EndUpdate();
            _suppressTreeEvents = false;
        }
    }

    private void AddHostFolders(TreeNode hostNode)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _record.Settings.HostFolders)
        {
            if (!Directory.Exists(folder)) continue;
            known.Add(folder);
            AddHostFolderNode(hostNode, folder, "已添加的宿主目录");
        }

        foreach (var detected in _detected)
        {
            if (!known.Add(detected.Path)) continue;
            AddHostFolderNode(hostNode, detected.Path, "自动检测：" + detected.Reason);
        }
    }

    private void AddHostFolderNode(TreeNode parent, string path, string reason)
    {
        IFileScope scope;
        try
        {
            scope = new HostFolderScope(path);
        }
        catch (Exception)
        {
            return;
        }

        var node = new TreeNode(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)))
        {
            Tag = new BrowserNode(scope, "", hasChildren: false),
            ToolTipText = reason + "\n" + path,
            ForeColor = Color.FromArgb(140, 70, 0)
        };
        parent.Nodes.Add(node);

        // 微信收到的文件：直接挂出「收到的文件」快捷节点
        var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (folderName.Equals("xwechat_files", StringComparison.OrdinalIgnoreCase) ||
            folderName.Equals("WeChat Files", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<SandboxFileEntry> accounts;
            try { accounts = scope.List(""); }
            catch (Exception) { return; }

            foreach (var account in accounts.Where(entry => entry.IsDirectory).Take(12))
            {
                var candidates = folderName.Equals("WeChat Files", StringComparison.OrdinalIgnoreCase)
                    ? new[] { account.Name + "/FileStorage/File", account.Name + "/FileStorage" }
                    : new[] { account.Name + "/msg/file", account.Name + "/msg/attach", account.Name + "/msg" };
                foreach (var candidate in candidates)
                {
                    if (!scope.DirectoryExists(candidate)) continue;
                    var shortName = account.Name.Length > 15 ? account.Name[..15] + "…" : account.Name;
                    node.Nodes.Add(new TreeNode($"收到的文件 · {shortName}")
                    {
                        Tag = new BrowserNode(scope, candidate, hasChildren: false),
                        ToolTipText = account.Name + Environment.NewLine +
                                      Path.Combine(path, candidate.Replace('/', Path.DirectorySeparatorChar))
                    });
                    break;
                }
            }

            if (node.Nodes.Count > 0) node.Expand();
        }
    }

    private static void AddLazyChild(TreeNode node)
    {
        node.Nodes.Add(new TreeNode("…") { Tag = LazyMarker });
    }

    private void PopulateNode(TreeNode node)
    {
        if (node.Tag is not BrowserNode tag) return;
        if (!tag.HasChildren) return;
        tag.HasChildren = false;

        node.Nodes.Clear();
        IReadOnlyList<SandboxFileEntry> entries;
        try
        {
            entries = tag.Scope.List(tag.RelativePath);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            var child = new TreeNode(entry.Name)
            {
                Tag = new BrowserNode(tag.Scope, entry.RelativePath)
            };
            AddLazyChild(child);
            node.Nodes.Add(child);
        }

        if (node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("（空）") { ForeColor = Color.DimGray });
    }

    // ---------------- 列表 ----------------

    private void Navigate(IFileScope scope, string relative)
    {
        _scope = scope;
        _currentPath = scope.Normalize(relative);
        _searchBox.Text = "";
        RefreshList();
        UpdatePathBox();
        UpdateStatusBar();
        SyncTreeSelection(scope, _currentPath);
    }

    /// <summary>让目录树高亮到当前浏览的位置；找不到就把树的选中项清掉，避免焦点回到树上时跳回别处。</summary>
    private void SyncTreeSelection(IFileScope scope, string relativePath)
    {
        TreeNode? match = null;

        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (match != null) return;
                if (node.Tag is BrowserNode browserNode &&
                    browserNode.Scope.Kind == scope.Kind &&
                    string.Equals(browserNode.Scope.DisplayRoot, scope.DisplayRoot, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(browserNode.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    match = node;
                    return;
                }

                Walk(node.Nodes);
            }
        }

        Walk(_tree.Nodes);
        _suppressTreeEvents = true;
        try
        {
            _tree.SelectedNode = match;
            match?.EnsureVisible();
        }
        finally
        {
            _suppressTreeEvents = false;
        }
    }

    private void RefreshList()
    {
        IReadOnlyList<SandboxFileEntry> entries;
        try
        {
            entries = _scope.List(_currentPath);
        }
        catch (Exception ex)
        {
            ShowError("读取目录失败", ex);
            entries = Array.Empty<SandboxFileEntry>();
        }

        _entries = entries.ToList();
        var filter = _searchBox.Text.Trim();
        IEnumerable<SandboxFileEntry> visible = _entries;
        if (filter.Length > 0)
            visible = visible.Where(entry => entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        visible = SortEntries(visible);
        var list = visible.ToList();

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var entry in list)
            {
                var item = new ListViewItem(entry.Name) { Tag = entry };
                item.SubItems.Add(entry.IsDirectory ? "" : entry.SizeText);
                item.SubItems.Add(entry.ModifiedText);
                item.SubItems.Add(entry.KindText);
                if (entry.IsDirectory) item.ForeColor = Color.FromArgb(30, 80, 160);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _countLabel.Text = $"共 {_entries.Count(entry => entry.IsDirectory)} 个文件夹 / {_entries.Count(entry => !entry.IsDirectory)} 个文件";
        UpdateSelectionStatus();
    }

    private IEnumerable<SandboxFileEntry> SortEntries(IEnumerable<SandboxFileEntry> entries)
    {
        var list = entries.ToList();
        if (_sortColumn < 0)
            return list.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);

        Func<SandboxFileEntry, object> key = _sortColumn switch
        {
            1 => entry => entry.SizeBytes,
            2 => entry => entry.Modified,
            3 => entry => entry.KindText,
            _ => entry => entry.Name
        };

        return _sortAscending
            ? list.OrderByDescending(entry => entry.IsDirectory).ThenBy(key)
            : list.OrderByDescending(entry => entry.IsDirectory).ThenByDescending(key);
    }

    private void SortByColumn(int column)
    {
        if (_sortColumn == column) _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        RefreshList();
    }

    private void UpdatePathBox() =>
        _pathBox.Text = _scope is HostFolderScope
            ? Path.Combine(_scope.DisplayRoot, _currentPath.Replace('/', Path.DirectorySeparatorChar))
            : _currentPath;

    private void UpdateSelectionStatus()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) return;

        var files = selected.Where(entry => !entry.IsDirectory).ToList();
        var size = files.Sum(entry => entry.SizeBytes);
        var text = $"已选 {selected.Count} 项" + (files.Count > 0 ? $"（{FileCopyResult.FormatSize(size)}）" : "");
        _countLabel.Text = text;
    }

    private void UpdateStatusBar()
    {
        var mode = _scope.KindText;
        _statusLabel.Text = $"{mode} · {(_currentPath.Length == 0 ? "根目录" : _currentPath)}";
    }

    private void UpdateModeLabel()
    {
        var activity = _service.GetActivity(_record);
        _modeLabel.Text = $"数据来源：{_sandboxScope.KindText}　|　沙盒状态：{activity.StatusText}";
        _modeLabel.ForeColor = activity.Running ? Color.SeaGreen : Color.FromArgb(60, 60, 60);

        if (_detected.Count > 0)
        {
            var first = _detected[0];
            _externalLabel.Text = _detected.Count == 1
                ? $"⚠ 发现沙盒外目录：{first.Path}（{first.Reason}）——左侧「宿主目录」可直接打开/拷贝"
                : $"⚠ 发现 {_detected.Count} 个沙盒外目录（例如 {first.Path}）——左侧「宿主目录」可直接打开/拷贝";
        }
        else
        {
            _externalLabel.Text = "提示：快照里的文件可以导出到宿主机；程序写到沙盒外的目录（如微信默认存储）请用「添加宿主目录…」加进来。";
            _externalLabel.ForeColor = Color.DimGray;
        }
    }

    private void AppendStatus(string message) => _statusLabel.Text = message;

    // ---------------- 操作 ----------------

    private List<SandboxFileEntry> SelectedEntries() =>
        _list.SelectedItems.Cast<ListViewItem>()
            .Select(item => (SandboxFileEntry)item.Tag!)
            .ToList();

    private void NavigateUp()
    {
        if (_currentPath.Length == 0) return;
        Navigate(_scope, ScopePath.Parent(_currentPath));
    }

    private void ActivateSelection()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0)
        {
            OpenCurrentFolder();
            return;
        }

        var first = selected[0];
        if (first.IsDirectory)
        {
            Navigate(_scope, first.RelativePath);
            return;
        }

        OpenEntry(first);
    }

    private void OpenSelected()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0)
        {
            OpenCurrentFolder();
            return;
        }

        foreach (var entry in selected.Take(10)) OpenEntry(entry);
    }

    private void OpenEntry(SandboxFileEntry entry)
    {
        try
        {
            if (entry.IsDirectory)
            {
                Navigate(_scope, entry.RelativePath);
                return;
            }

            UseWaitCursor = true;
            var tempRoot = Path.Combine(Path.GetTempPath(), "SandboxLauncher", "open", _record.Id);
            var path = _scope.Materialize(entry.RelativePath, tempRoot);
            UseWaitCursor = false;
            AppendStatus($"已准备打开：{path}");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UseWaitCursor = false;
            ShowError("打开失败", ex);
        }
    }

    private void ExportSelected()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先在右边选中要导出的文件或文件夹（可以多选）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var totalBytes = selected.Where(entry => !entry.IsDirectory).Sum(entry => entry.SizeBytes);
        using var dialog = new FolderBrowserDialog
        {
            Description = "导出到宿主机上的哪个文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        var suggested = _lastExportDirectory ?? DefaultExportDirectory();
        try { Directory.CreateDirectory(suggested); } catch { /* 用默认目录 */ }
        if (Directory.Exists(suggested)) dialog.SelectedPath = suggested;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _lastExportDirectory = dialog.SelectedPath;
        var target = dialog.SelectedPath;
        var paths = selected.Select(entry => entry.RelativePath).ToList();
        var overwrite = true;

        RunBackground($"正在导出 {selected.Count} 项（{FileCopyResult.FormatSize(totalBytes)}）…",
            () => _scope.CopyOut(paths, target, overwrite),
            result =>
            {
                var answer = MessageBox.Show(this,
                    $"已导出 {result.Summary} 到：\n{result.TargetPath}\n\n要打开目标文件夹吗？",
                    "导出完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes && !string.IsNullOrWhiteSpace(result.TargetPath))
                    OpenInExplorer(result.TargetPath);
            });
    }

    private string DefaultExportDirectory()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var baseDirectory = Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(baseDirectory, "SandboxLauncher", _record.Name);
    }

    private void ImportFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要放进沙盒的文件（可多选）",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var targetRelative = _currentPath;
        var files = dialog.FileNames.ToList();
        RunBackground($"正在把 {files.Count} 个文件放进沙盒…",
            () =>
            {
                var total = new FileCopyResult();
                foreach (var file in files)
                {
                    var part = _scope.CopyIn(file, targetRelative, true);
                    total.FileCount += part.FileCount;
                    total.Bytes += part.Bytes;
                    total.Paths.AddRange(part.Paths);
                    total.Skipped.AddRange(part.Skipped);
                }

                return total;
            },
            result =>
            {
                RefreshAfterWrite();
                MessageBox.Show(this, $"已导入 {result.Summary}。", "导入完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
    }

    private void ImportFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要放进沙盒的文件夹（会连同里面的内容一起复制）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var source = dialog.SelectedPath;
        var targetRelative = _currentPath;
        RunBackground("正在把文件夹放进沙盒…",
            () => _scope.CopyIn(source, targetRelative, true),
            result =>
            {
                RefreshAfterWrite();
                MessageBox.Show(this, $"已导入 {result.Summary}。", "导入完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
    }

    private void OpenCurrentFolder()
    {
        try
        {
            UseWaitCursor = true;
            if (_scope is HostFolderScope)
            {
                OpenInExplorer(_scope.GetHostPath(_currentPath)!);
                return;
            }

            if (_sandboxScope.IsLive)
            {
                OpenInExplorer(_sandboxScope.GetHostPath(_currentPath)!);
                return;
            }

            // 快照：先把当前目录解到临时目录再打开（体积大时先问一句）
            var (count, bytes) = _sandboxScope.Measure(_currentPath);
            if (bytes > 300L * 1024 * 1024 || count > 5000)
            {
                var answer = MessageBox.Show(this,
                    $"这个目录在快照里有 {count} 个文件（{FileCopyResult.FormatSize(bytes)}），" +
                    "要先解到临时目录才能在资源管理器里打开。\n\n继续吗？（只想拿几个文件的话，用「导出到宿主机」更划算）",
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return;
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "SandboxLauncher", "preview", _record.Id);
            var path = _sandboxScope.Materialize(_currentPath, tempRoot);
            UseWaitCursor = false;
            AppendStatus($"快照内容已解到：{path}");
            OpenInExplorer(path);
        }
        catch (Exception ex)
        {
            ShowError("打开目录失败", ex);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void OpenInExplorer(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private void AddHostFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择想加进来的宿主目录（沙盒外的真实文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var folder = dialog.SelectedPath;
        if (!_record.Settings.HostFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            _record.Settings.HostFolders.Add(folder);
        _store.Save();
        BuildTree();
        AppendStatus($"已添加宿主目录：{folder}");
    }

    private void DeleteSelected()
    {
        var selected = SelectedEntries();
        if (selected.Count == 0) return;
        var names = string.Join("、", selected.Take(5).Select(entry => entry.Name)) +
                    (selected.Count > 5 ? $" 等 {selected.Count} 项" : "");
        if (MessageBox.Show(this, $"确定从{(_sandboxScope.IsLive ? "运行中的沙盒" : "沙盒快照")}里删除 {names} 吗？\n这个操作不能撤销。",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        try
        {
            var deleted = _scope.Delete(selected.Select(entry => entry.RelativePath));
            RefreshAfterWrite();
            AppendStatus($"已删除 {deleted} 个文件");
        }
        catch (Exception ex)
        {
            ShowError("删除失败", ex);
        }
    }

    private void ShowListMenu(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = _list.GetItemAt(e.X, e.Y);
        if (item != null)
        {
            if (!item.Selected) item.Selected = true;
        }

        var selected = SelectedEntries();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => OpenSelected());
        menu.Items.Add("导出到宿主机…", null, (_, _) => ExportSelected());
        menu.Items.Add("打开所在目录", null, (_, _) => OpenCurrentFolder());
        menu.Items.Add("复制路径", null, (_, _) => CopySelectedPaths(selected));
        if (_scope is HostFolderScope) menu.Items.Add("在资源管理器中显示", null, (_, _) => RevealSelected(selected));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("导入文件到当前目录…", null, (_, _) => ImportFiles());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除", null, (_, _) => DeleteSelected());
        menu.Show(_list, e.Location);
    }

    private void CopySelectedPaths(List<SandboxFileEntry> selected)
    {
        if (selected.Count == 0) return;
        var text = string.Join(Environment.NewLine, selected.Select(entry => _scope is HostFolderScope
            ? _scope.GetHostPath(entry.RelativePath)
            : entry.RelativePath));
        try
        {
            Clipboard.SetText(text);
            AppendStatus("路径已复制到剪贴板");
        }
        catch (Exception ex)
        {
            ShowError("复制失败", ex);
        }
    }

    private void RevealSelected(List<SandboxFileEntry> selected)
    {
        if (selected.Count == 0) return;
        var path = _scope.GetHostPath(selected[0].RelativePath);
        if (path != null) OpenInExplorer(path);
    }

    // ---------------- 拖出到资源管理器 ----------------

    private void DragOut(object? item)
    {
        if (item is not ListViewItem listItem || listItem.Tag is not SandboxFileEntry entry) return;
        try
        {
            var paths = PrepareDragPaths(new[] { entry });
            if (paths.Count == 0) return;
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            _list.DoDragDrop(data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            ShowError("拖出失败", ex);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private List<string> PrepareDragPaths(IReadOnlyList<SandboxFileEntry> entries)
    {
        var paths = new List<string>();
        foreach (var entry in entries)
        {
            var hostPath = _scope.GetHostPath(entry.RelativePath);
            if (hostPath != null)
            {
                paths.Add(hostPath);
                continue;
            }

            // 快照里的内容：先解到临时目录，再作为真实文件拖出去
            if (entry.IsDirectory)
            {
                var (count, bytes) = _scope.Measure(entry.RelativePath);
                if (bytes > 300L * 1024 * 1024 || count > 3000)
                {
                    var answer = MessageBox.Show(this,
                        $"「{entry.Name}」有 {count} 个文件（{FileCopyResult.FormatSize(bytes)}），" +
                        "拖出去前要先解到临时目录。\n\n继续吗？（建议改用「导出到宿主机」）",
                        "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer != DialogResult.Yes) return new List<string>();
                }
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "SandboxLauncher", "drag", _record.Id);
            _statusLabel.Text = $"正在准备拖出：{entry.Name}…";
            UseWaitCursor = true;
            paths.Add(_sandboxScope.Materialize(entry.RelativePath, tempRoot));
            UseWaitCursor = false;
            UpdateStatusBar();
        }

        return paths;
    }

    // ---------------- 杂项 ----------------

    private void RefreshAfterWrite()
    {
        try { _scope.Refresh(); } catch { /* ignore */ }
        RefreshList();
        UpdateStatusBar();
        if (_scope is SandboxFileSystem sandboxScope)
        {
            var current = _currentPath;
            BuildTree();
            Navigate(sandboxScope, current);
        }
    }

    private void RefreshCurrent()
    {
        try { _scope.Refresh(); } catch { /* ignore */ }
        RefreshList();
        UpdatePathBox();
        UpdateStatusBar();
        AppendStatus("已刷新");
    }

    private void RunBackground(string status, Func<FileCopyResult> work, Action<FileCopyResult> onSuccess)
    {
        _progress.Visible = true;
        _statusLabel.Text = status;
        Enabled = false;
        UseWaitCursor = true;

        Task.Run(() =>
        {
            try
            {
                var result = work();
                OnUi(() =>
                {
                    FinishBackground();
                    onSuccess(result);
                });
            }
            catch (Exception ex)
            {
                OnUi(() =>
                {
                    FinishBackground();
                    ShowError("操作失败", ex);
                });
            }
        });
    }

    private void FinishBackground()
    {
        _progress.Visible = false;
        Enabled = true;
        UseWaitCursor = false;
        UpdateStatusBar();
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

    private void ShowError(string title, Exception ex)
    {
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppendStatus("❌ " + title + "：" + ex.Message);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _split.Panel1MinSize = 220;
            _split.SplitterDistance = 300;
        }
        catch (InvalidOperationException)
        {
            // 窗口太窄时忽略
        }
    }
}
