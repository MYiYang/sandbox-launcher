using System.Drawing;
using System.Windows.Forms;
using SandboxLauncher.Core.Model;
using SandboxLauncher.Core.Runtime;
using SandboxLauncher.Core.Snapshot;

namespace SandboxLauncher.App;

/// <summary>新建沙盒：填名称 + 选快照文件放哪（任意位置）。</summary>
public sealed class NewSandboxDialog : Form
{
    private readonly TextBox _name = new() { Width = 360 };
    private readonly TextBox _path = new() { Width = 280 };
    private readonly Label _hint = new() { AutoSize = false, Width = 460, Height = 40, ForeColor = Color.DimGray };

    public string SandboxName => _name.Text.Trim();
    public string SnapshotPath => _path.Text.Trim();

    public NewSandboxDialog(string defaultDirectory)
    {
        Text = "新建沙盒";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 220);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var lblName = new Label { Text = "沙盒名称", Location = new Point(20, 24), Size = new Size(70, 22) };
        _name.Location = new Point(100, 21);

        var lblPath = new Label { Text = "快照文件", Location = new Point(20, 62), Size = new Size(70, 22) };
        _path.Location = new Point(100, 59);
        _path.Text = Path.Combine(defaultDirectory,
            "沙盒-" + DateTime.Now.ToString("MMdd-HHmm") + SnapshotFile.Extension);
        var browse = new Button { Text = "浏览…", Location = new Point(388, 58), Size = new Size(80, 26) };
        browse.Click += (_, _) => Browse();

        _hint.Location = new Point(100, 92);
        _hint.Text = "快照是单个文件，可以放在任意磁盘/目录，也可以随时复制给别的电脑用。";

        var ok = new Button { Text = "创建", DialogResult = DialogResult.OK, Location = new Point(292, 168), Size = new Size(90, 30) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(388, 168), Size = new Size(80, 30) };
        ok.Click += (_, _) => Validate();

        Controls.AddRange(new Control[] { lblName, _name, lblPath, _path, browse, _hint, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Browse()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "快照文件保存到…",
            Filter = "沙盒快照 (*.sbx)|*.sbx",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.FileName;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(SandboxName))
        {
            MessageBox.Show(this, "请输入沙盒名称。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            return;
        }

        if (string.IsNullOrWhiteSpace(SnapshotPath))
        {
            MessageBox.Show(this, "请选择快照文件位置。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            return;
        }

        var path = SnapshotPath.EndsWith(SnapshotFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? SnapshotPath
            : SnapshotPath + SnapshotFile.Extension;
        if (File.Exists(path))
        {
            MessageBox.Show(this, "这个快照文件已经存在，请换一个名字。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
        }
    }
}

/// <summary>导入客户端：选 exe/目录；大程序可以让用户自己挑「创建 VHDX 虚拟磁盘」的位置和盘符。</summary>
public sealed class ImportClientDialog : Form
{
    private readonly RadioButton _radioFile = new() { Text = "选择程序文件（.exe）", Checked = true };
    private readonly RadioButton _radioFolder = new() { Text = "选择程序目录（连同依赖一起）" };
    private readonly TextBox _path = new() { Width = 380, ReadOnly = true };
    private readonly TextBox _name = new() { Width = 380 };
    private readonly TextBox _args = new() { Width = 380 };

    private readonly CheckBox _chkReference = new()
    {
        Text = "引用安装目录（不复制文件）",
        AutoSize = false,
        Width = 480,
        Height = 22
    };

    private readonly CheckBox _chkVhd = new()
    {
        Text = "创建虚拟磁盘（VHDX）：程序搬进虚拟盘，之后可原样多开",
        AutoSize = false,
        Width = 460,
        Height = 22
    };

    private readonly ComboBox _cboLocation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly ComboBox _cboLetter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly NumericUpDown _numSize = new() { Minimum = 1, Maximum = 4096, Value = 8, Width = 64 };
    private readonly Label _vhdHint = new() { AutoSize = false, Width = 460, Height = 40, ForeColor = Color.DimGray };

    private readonly Label _hint = new()
    {
        AutoSize = false,
        Width = 480,
        Height = 52,
        ForeColor = Color.DimGray
    };

    public string SourcePath => _path.Text.Trim();
    public string ClientName => _name.Text.Trim();
    public string Arguments => _args.Text.Trim();
    public bool CreateVhd => _chkVhd.Checked;
    public string VhdLocationRoot => (_cboLocation.SelectedItem as DiskCandidate)?.Root ?? "";
    public long VhdSizeBytes => (long)_numSize.Value * 1024L * 1024 * 1024;
    public char? VhdDriveLetter =>
        _cboLetter.SelectedItem is string text && text.Length == 1 && char.IsLetter(text[0])
            ? char.ToUpperInvariant(text[0])
            : null;

    public AppSourceMode Mode => _chkReference.Checked || _chkVhd.Checked ? AppSourceMode.Reference : AppSourceMode.Copy;

    private string _probeText = "还没选择程序";
    private long _probeBytes;
    private int _probeToken;

    public ImportClientDialog(string? initialPath = null)
    {
        Text = "导入客户端到沙盒";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 496);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _radioFile.SetBounds(20, 20, 200, 22);
        _radioFolder.SetBounds(20, 44, 360, 22);

        var browse = new Button { Text = "浏览…", Location = new Point(406, 84), Size = new Size(90, 28) };
        browse.Click += (_, _) => Browse();

        _path.SetBounds(20, 86, 380, 24);

        var lblName = new Label { Text = "显示名称", Location = new Point(20, 126), Size = new Size(70, 22) };
        _name.SetBounds(96, 123, 400, 24);

        var lblArgs = new Label { Text = "启动参数", Location = new Point(20, 164), Size = new Size(70, 22) };
        _args.SetBounds(96, 161, 400, 24);

        _chkReference.SetBounds(20, 194, 480, 22);
        _hint.SetBounds(20, 220, 480, 52);

        var group = new GroupBox { Text = "虚拟磁盘（大程序推荐）", Location = new Point(20, 278), Size = new Size(480, 156) };
        _chkVhd.SetBounds(10, 2, 460, 22);
        _chkVhd.CheckedChanged += (_, _) => UpdateVhdState();

        var lblWhere = new Label { Text = "放哪", Location = new Point(10, 32), Size = new Size(38, 22) };
        _cboLocation.SetBounds(50, 29, 412, 24);
        var lblLetter = new Label { Text = "盘符", Location = new Point(10, 62), Size = new Size(38, 22) };
        _cboLetter.SetBounds(50, 59, 80, 24);
        var lblSize = new Label { Text = "容量GB", Location = new Point(150, 62), Size = new Size(60, 22) };
        _numSize.SetBounds(214, 59, 60, 24);
        _vhdHint.SetBounds(10, 90, 456, 56);

        group.Controls.AddRange(new Control[]
        {
            _chkVhd, lblWhere, _cboLocation, lblLetter, _cboLetter, lblSize, _numSize, _vhdHint
        });

        var ok = new Button { Text = "导入", DialogResult = DialogResult.OK, Location = new Point(312, 444), Size = new Size(90, 30) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(408, 444), Size = new Size(90, 30) };
        ok.Click += (_, _) => ValidateInput();

        Controls.AddRange(new Control[]
        {
            _radioFile, _radioFolder, _path, browse, lblName, _name, lblArgs, _args, _chkReference, _hint, group, ok, cancel
        });
        AcceptButton = ok;
        CancelButton = cancel;

        _chkReference.CheckedChanged += (_, _) => UpdateHint();
        UpdateHint();
        UpdateVhdState();

        LoadDisksAsync();
        FillLetters();

        if (!string.IsNullOrWhiteSpace(initialPath)) SetSource(initialPath);
    }

    private void LoadDisksAsync()
    {
        Task.Run(() => VhdService.ListDisks()).ContinueWith(task =>
        {
            if (IsDisposed) return;
            _cboLocation.Items.Clear();
            foreach (var disk in task.Result) _cboLocation.Items.Add(disk);
            if (_cboLocation.Items.Count > 0) _cboLocation.SelectedIndex = 0;
            UpdateVhdState();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FillLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        _cboLetter.Items.Add("自动");
        for (var c = 'V'; c <= 'Z'; c++)
        {
            if (!used.Contains(c)) _cboLetter.Items.Add(c.ToString());
        }

        _cboLetter.SelectedIndex = 0;
    }

    private void UpdateVhdState()
    {
        var on = _chkVhd.Checked;
        _cboLocation.Enabled = on;
        _cboLetter.Enabled = on;
        _numSize.Enabled = on;

        if (on)
        {
            _chkReference.Checked = true;
            _chkReference.Enabled = false;
            var disk = _cboLocation.SelectedItem as DiskCandidate;
            _vhdHint.Text = disk != null
                ? $"虚拟盘会放在 {disk.Display}；程序目录被「移」进去（不额外占盘），之后每个分身只需一小块差分盘。"
                : "正在读取磁盘信息…";

            if (disk != null && _numSize.Value * 1024L * 1024 * 1024 > disk.FreeBytes && disk.FreeBytes > 0)
                _numSize.Value = Math.Max(1, disk.FreeBytes / 1024 / 1024 / 1024);
        }
        else
        {
            _chkReference.Enabled = true;
            _vhdHint.Text = "不创建虚拟盘：按上面的「复制进快照 / 引用安装目录」方式导入。";
        }

        UpdateHint();
    }

    private void Browse()
    {
        if (_radioFolder.Checked)
        {
            using var dialog = new FolderBrowserDialog { Description = "选择客户端程序的安装目录" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            SetSource(dialog.SelectedPath);
        }
        else
        {
            using var dialog = new OpenFileDialog { Title = "选择客户端程序", Filter = "程序 (*.exe)|*.exe" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            SetSource(dialog.FileName);
        }
    }

    private void SetSource(string path)
    {
        _path.Text = path;
        var isDir = Directory.Exists(path);
        _radioFolder.Checked = isDir;
        _radioFile.Checked = !isDir;
        if (string.IsNullOrWhiteSpace(_name.Text))
            _name.Text = isDir ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path);
        ProbeAsync(path);
    }

    /// <summary>后台估算体积（几十 GB 的目录也能很快给出「≥ …」的结论），并据此建议导入方式。</summary>
    private async void ProbeAsync(string path)
    {
        var token = ++_probeToken;
        var isFile = File.Exists(path);
        _hint.Text = "正在估算体积…";

        var (bytes, files, truncated) = await Task.Run(() => AppImporter.Probe(path, 128L * 1024 * 1024 * 1024));

        // 目录：再完整量一次体积，用来给虚拟盘一个靠谱的默认容量（几十 GB 也就几秒）
        if (Directory.Exists(path))
        {
            var measured = await Task.Run(() => AppImporter.Measure(path));
            if (measured.Bytes > 0)
            {
                bytes = measured.Bytes;
                files = measured.Count;
                truncated = false;
            }
        }

        if (token != _probeToken || IsDisposed) return;

        _probeBytes = bytes;
        _probeText = bytes > 0
            ? $"约 {(truncated ? "≥" : "")}{FormatSize(bytes)}、{files} 个文件"
            : "（没能统计体积）";

        if (Directory.Exists(path))
            _numSize.Value = Math.Max(1, Math.Min(4096, bytes * 12 / 10 / 1024 / 1024 / 1024));

        // 只给了一个 exe：复制单文件必然缺依赖 → 引用；大目录 → 建议虚拟磁盘
        if (isFile || bytes >= AppImporter.LargeAppThreshold || truncated)
            _chkReference.Checked = true;
        if (bytes >= AppImporter.LargeAppThreshold || truncated)
            _chkVhd.Checked = true;

        UpdateHint();
        UpdateVhdState();
    }

    private void UpdateHint()
    {
        if (_chkVhd.Checked)
        {
            _hint.Text = $"虚拟磁盘（{_probeText}）：程序目录被搬进 VHDX（不复制、不额外占盘），" +
                         "之后每个分身挂一块差分盘，磁盘只涨各自写入的部分；启动时启动器会自动挂载。";
            return;
        }

        _hint.Text = _chkReference.Checked
            ? $"引用安装目录（{_probeText}）：不复制文件、快照几乎不增长，启动时直接跑原目录里的程序，" +
              "只把 AppData / Temp / 文档 放进沙盒（程序写安装目录的改动不隔离）。"
            : $"复制进快照（{_probeText}）：整个目录压进 .sbx，快照约等于程序体积；导入后不再依赖原安装目录。";
    }

    private static string FormatSize(long bytes)
    {
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

    private void ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || (!File.Exists(SourcePath) && !Directory.Exists(SourcePath)))
        {
            MessageBox.Show(this, "请选择要导入的程序文件或目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            return;
        }

        if (CreateVhd && !Directory.Exists(SourcePath))
        {
            MessageBox.Show(this, "创建虚拟磁盘需要选择整个程序目录（不能只选一个 exe）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            return;
        }

        if (CreateVhd && _cboLocation.SelectedItem == null)
        {
            MessageBox.Show(this, "还没读取到磁盘列表，稍等一下再试。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
            return;
        }

        // 复制模式只给一个 exe：依赖不会跟着走，绝大多数程序起不来 —— 先劝一次。
        if (Mode == AppSourceMode.Copy && File.Exists(SourcePath))
        {
            var answer = MessageBox.Show(this,
                "只复制这一个 exe，它旁边的 DLL / 资源 / 子目录不会进沙盒，多数程序会立刻退出。" +
                "建议勾选「引用安装目录」——不复制文件，直接在原目录启动。仍然只复制这个 exe 吗？",
                "这个方式很可能起不来", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) DialogResult = DialogResult.None;
        }
    }
}

/// <summary>全局设置：会话临时目录、默认快照目录。</summary>
public sealed class SettingsDialog : Form
{
    private readonly TextBox _sessionRoot = new() { Width = 330 };
    private readonly TextBox _snapshotDir = new() { Width = 330 };

    public string SessionRoot => _sessionRoot.Text.Trim();
    public string DefaultSnapshotDirectory => _snapshotDir.Text.Trim();

    public SettingsDialog(string sessionRoot, string snapshotDirectory)
    {
        Text = "全局设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 220);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var lbl1 = new Label { Text = "会话临时目录", Location = new Point(20, 28), Size = new Size(90, 22) };
        _sessionRoot.Location = new Point(116, 25);
        _sessionRoot.Text = sessionRoot;
        var b1 = new Button { Text = "…", Location = new Point(452, 24), Size = new Size(30, 26) };
        b1.Click += (_, _) => PickFolder(_sessionRoot);

        var lbl2 = new Label { Text = "默认快照目录", Location = new Point(20, 70), Size = new Size(90, 22) };
        _snapshotDir.Location = new Point(116, 67);
        _snapshotDir.Text = snapshotDirectory;
        var b2 = new Button { Text = "…", Location = new Point(452, 66), Size = new Size(30, 26) };
        b2.Click += (_, _) => PickFolder(_snapshotDir);

        var hint = new Label
        {
            Location = new Point(20, 106),
            Size = new Size(460, 56),
            ForeColor = Color.DimGray,
            Text = "会话临时目录：沙盒运行时解包到这里，退出后回写并清理。\n默认快照目录：新建沙盒时快照文件的默认保存位置（每个沙盒都能单独改成任意位置）。"
        };

        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Location = new Point(292, 172), Size = new Size(90, 30) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(388, 172), Size = new Size(90, 30) };

        Controls.AddRange(new Control[] { lbl1, _sessionRoot, b1, lbl2, _snapshotDir, b2, hint, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void PickFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }
}
