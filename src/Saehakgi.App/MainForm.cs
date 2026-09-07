using Saehakgi.Core.Manifest;
using Saehakgi.Core.Migration;

namespace Saehakgi.App;

/// <summary>
/// Slice-2 GUI: export selected settings to an encrypted USB bundle, import them
/// on the new PC, and reset (undo) the last import. Built in code (no designer)
/// to keep the scaffold self-contained; visual polish comes later.
/// </summary>
public sealed class MainForm : Form
{
    private readonly MigrationEngine _engine = new();
    private readonly TextBox _log = new();

    // Export tab
    private readonly CheckBox _chkFolders = new() { Text = "폴더 (아래 목록, 하위 전체)", AutoSize = true };
    private readonly CheckBox _chkBookmarks = new() { Text = "즐겨찾기 (크롬·엣지)", AutoSize = true };
    private readonly CheckBox _chkMouse = new() { Text = "마우스 설정", AutoSize = true };
    private readonly CheckBox _chkCerts = new() { Text = "공동인증서 (GPKI/NPKI)", AutoSize = true };
    private readonly CheckBox _chkEnv = new() { Text = "환경변수 (사용자)", AutoSize = true };
    private readonly CheckBox _chkPersonalization = new() { Text = "개인화 (탐색기·테마·작업표시줄)", AutoSize = true };
    private readonly CheckBox _chkPrograms = new() { Text = "설치 프로그램 목록 (winget)", AutoSize = true };
    private readonly CheckBox _chkStartup = new() { Text = "시작프로그램", AutoSize = true };
    private readonly ListBox _lstFolders = new();
    private readonly TextBox _txtPassExport = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtPassExport2 = new() { UseSystemPasswordChar = true };

    // Import tab
    private readonly TextBox _txtBundlePath = new() { ReadOnly = true };
    private readonly TextBox _txtPassImport = new() { UseSystemPasswordChar = true };
    private readonly CheckedListBox _clbItems = new() { CheckOnClick = true };

    // Reset tab
    private readonly Label _lblResetInfo = new() { AutoSize = true, Text = "" };

    public MainForm()
    {
        Text = "saehakgi — 새 학기 세팅 이전";
        Width = 780;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildExportTab());
        tabs.TabPages.Add(BuildImportTab());
        tabs.TabPages.Add(BuildResetTab());

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Color.FromArgb(30, 30, 30);
        _log.ForeColor = Color.Gainsboro;

        var logPanel = new GroupBox { Text = "진행 로그", Dock = DockStyle.Bottom, Height = 160 };
        var logInner = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        logInner.Controls.Add(_log);
        logPanel.Controls.Add(logInner);

        Controls.Add(tabs);
        Controls.Add(logPanel);

        Log("준비됨. USB에서 실행 후 항목을 선택하세요.");
    }

    // ---------------- Export ----------------

    private TabPage BuildExportTab()
    {
        var page = new TabPage("① 내보내기 (기존 PC)") { Padding = new Padding(12) };
        int y = 12;

        page.Controls.Add(Header("옮길 항목 선택", ref y, page.Width));
        foreach (var chk in new[] { _chkFolders, _chkBookmarks, _chkMouse, _chkCerts, _chkEnv, _chkPersonalization, _chkPrograms, _chkStartup })
        {
            chk.Checked = true;
            chk.Location = new Point(20, y);
            page.Controls.Add(chk);
            y += 26;
        }

        _lstFolders.SetBounds(20, y, 480, 110);
        _lstFolders.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_lstFolders);

        var btnAdd = new Button { Text = "폴더 추가…", Left = 512, Top = y, Width = 110 };
        var btnRemove = new Button { Text = "제거", Left = 512, Top = y + 34, Width = 110 };
        btnAdd.Anchor = btnRemove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnAdd.Click += (_, _) => AddFolder();
        btnRemove.Click += (_, _) => { if (_lstFolders.SelectedItem is not null) _lstFolders.Items.Remove(_lstFolders.SelectedItem); };
        page.Controls.Add(btnAdd);
        page.Controls.Add(btnRemove);
        y += 120;

        page.Controls.Add(new Label { Text = "USB 번들 암호(패스프레이즈):", Left = 20, Top = y + 4, AutoSize = true });
        _txtPassExport.SetBounds(230, y, 200, 24);
        page.Controls.Add(_txtPassExport);
        page.Controls.Add(new Label { Text = "확인:", Left = 450, Top = y + 4, AutoSize = true });
        _txtPassExport2.SetBounds(500, y, 160, 24);
        page.Controls.Add(_txtPassExport2);
        y += 40;

        var btnExport = new Button { Text = "USB로 내보내기…", Left = 20, Top = y, Width = 200, Height = 34 };
        btnExport.Click += (_, _) => DoExport();
        page.Controls.Add(btnExport);

        return page;
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "이전할 폴더 선택" };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            if (!_lstFolders.Items.Contains(dlg.SelectedPath))
                _lstFolders.Items.Add(dlg.SelectedPath);
        }
    }

    private void DoExport()
    {
        var types = SelectedExportTypes();
        if (types.Count == 0) { Warn("옮길 항목을 하나 이상 선택하세요."); return; }

        var pass = _txtPassExport.Text;
        if (pass.Length < 4) { Warn("암호는 4자 이상 입력하세요."); return; }
        if (pass != _txtPassExport2.Text) { Warn("암호 확인이 일치하지 않습니다."); return; }

        var request = new MigrationRequest();
        foreach (var item in _lstFolders.Items) request.FolderPaths.Add(item.ToString()!);
        if (types.Contains(MigrationItemType.Folder) && request.FolderPaths.Count == 0)
        {
            Warn("폴더를 선택했지만 목록이 비어 있습니다. 폴더를 추가하세요."); return;
        }

        using var save = new SaveFileDialog
        {
            Title = "번들 저장 위치 (USB 권장)",
            Filter = "saehakgi 번들 (*.saehakgi)|*.saehakgi",
            FileName = $"saehakgi-{DateTime.Now:yyyyMMdd}.saehakgi",
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        RunAsync("내보내기", () => _engine.Export(types, request, save.FileName, pass, Log));
    }

    private List<MigrationItemType> SelectedExportTypes()
    {
        var list = new List<MigrationItemType>();
        if (_chkFolders.Checked) list.Add(MigrationItemType.Folder);
        if (_chkBookmarks.Checked) list.Add(MigrationItemType.Bookmarks);
        if (_chkMouse.Checked) list.Add(MigrationItemType.MouseSettings);
        if (_chkCerts.Checked) list.Add(MigrationItemType.Certificate);
        if (_chkEnv.Checked) list.Add(MigrationItemType.EnvironmentVariables);
        if (_chkPersonalization.Checked) list.Add(MigrationItemType.Personalization);
        if (_chkPrograms.Checked) list.Add(MigrationItemType.InstalledPrograms);
        if (_chkStartup.Checked) list.Add(MigrationItemType.StartupPrograms);
        return list;
    }

    // ---------------- Import ----------------

    private TabPage BuildImportTab()
    {
        var page = new TabPage("② 가져오기 (새 PC)") { Padding = new Padding(12) };
        int y = 12;

        page.Controls.Add(Header("번들 열기", ref y, page.Width));
        _txtBundlePath.SetBounds(20, y, 480, 24);
        _txtBundlePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_txtBundlePath);
        var btnBrowse = new Button { Text = "찾아보기…", Left = 512, Top = y - 2, Width = 110, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnBrowse.Click += (_, _) => BrowseBundle();
        page.Controls.Add(btnBrowse);
        y += 36;

        page.Controls.Add(new Label { Text = "암호:", Left = 20, Top = y + 4, AutoSize = true });
        _txtPassImport.SetBounds(70, y, 200, 24);
        page.Controls.Add(_txtPassImport);
        var btnLoad = new Button { Text = "미리보기", Left = 290, Top = y - 2, Width = 100 };
        btnLoad.Click += (_, _) => LoadBundle();
        page.Controls.Add(btnLoad);
        y += 40;

        page.Controls.Add(new Label { Text = "가져올 항목 (체크):", Left = 20, Top = y, AutoSize = true });
        y += 22;
        _clbItems.SetBounds(20, y, 602, 150);
        _clbItems.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        page.Controls.Add(_clbItems);
        y += 162;

        var btnImport = new Button { Text = "선택 항목 가져오기", Left = 20, Top = y, Width = 200, Height = 34 };
        btnImport.Click += (_, _) => DoImport();
        page.Controls.Add(btnImport);

        return page;
    }

    private void BrowseBundle()
    {
        using var open = new OpenFileDialog { Filter = "saehakgi 번들 (*.saehakgi)|*.saehakgi|모든 파일 (*.*)|*.*" };
        if (open.ShowDialog(this) == DialogResult.OK) _txtBundlePath.Text = open.FileName;
    }

    private void LoadBundle()
    {
        if (!File.Exists(_txtBundlePath.Text)) { Warn("번들 파일을 선택하세요."); return; }
        if (_txtPassImport.Text.Length == 0) { Warn("암호를 입력하세요."); return; }

        RunAsync("미리보기", () =>
        {
            var manifest = _engine.Inspect(_txtBundlePath.Text, _txtPassImport.Text);
            BeginInvoke(() =>
            {
                _clbItems.Items.Clear();
                foreach (var item in manifest.Items)
                    _clbItems.Items.Add(new ItemRow(item), isChecked: true);
            });
            Log($"번들 확인: {manifest.SourceMachine}({manifest.SourceUser}), {manifest.Items.Count}개 항목, {manifest.CreatedUtc.ToLocalTime()}");
        });
    }

    private void DoImport()
    {
        if (!File.Exists(_txtBundlePath.Text)) { Warn("번들 파일을 선택하세요."); return; }
        if (_clbItems.CheckedItems.Count == 0) { Warn("가져올 항목을 하나 이상 체크하세요."); return; }

        var types = _clbItems.CheckedItems.Cast<ItemRow>().Select(r => r.Item.Type).Distinct().ToList();
        if (MessageBox.Show(this,
                "선택한 항목을 이 PC에 적용합니다. 기존 값이 있으면 백업 후 덮어씁니다.\n계속할까요?",
                "가져오기 확인", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        var pass = _txtPassImport.Text;
        RunAsync("가져오기", () => _engine.Import(_txtBundlePath.Text, pass, types, new MigrationRequest(), Log));
    }

    private sealed class ItemRow(ManifestItem item)
    {
        public ManifestItem Item { get; } = item;
        public override string ToString() => $"[{Item.Type}] {Item.DisplayName}";
    }

    // ---------------- Reset ----------------

    private TabPage BuildResetTab()
    {
        var page = new TabPage("③ 초기화 (되돌리기)") { Padding = new Padding(12) };
        int y = 12;

        page.Controls.Add(Header("마지막 가져오기를 되돌립니다 (옮긴 항목만)", ref y, page.Width));
        _lblResetInfo.Location = new Point(20, y);
        page.Controls.Add(_lblResetInfo);
        y += 60;

        var btnRefresh = new Button { Text = "상태 새로고침", Left = 20, Top = y, Width = 130 };
        btnRefresh.Click += (_, _) => RefreshResetInfo();
        page.Controls.Add(btnRefresh);

        var btnReset = new Button { Text = "마지막 가져오기 초기화", Left = 160, Top = y, Width = 200, Height = 30 };
        btnReset.Click += (_, _) => DoReset();
        page.Controls.Add(btnReset);

        page.Enter += (_, _) => RefreshResetInfo();
        return page;
    }

    private void RefreshResetInfo()
    {
        var applied = MigrationEngine.LoadLatestApplied();
        _lblResetInfo.Text = applied is null
            ? "되돌릴 가져오기 기록이 없습니다."
            : $"마지막 가져오기: {applied.AppliedUtc.ToLocalTime()}\n변경 기록: {applied.Actions.Count}개\n원본 번들: {applied.BundlePath}";
    }

    private void DoReset()
    {
        var applied = MigrationEngine.LoadLatestApplied();
        if (applied is null) { Warn("되돌릴 기록이 없습니다."); return; }
        if (MessageBox.Show(this,
                $"{applied.Actions.Count}개의 변경을 되돌립니다. 계속할까요?",
                "초기화 확인", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        RunAsync("초기화", () => _engine.Reset(applied, Log));
    }

    // ---------------- Shared ----------------

    private static Label Header(string text, ref int y, int width)
    {
        var lbl = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Location = new Point(12, y),
            AutoSize = true,
        };
        y += 30;
        return lbl;
    }

    private void RunAsync(string label, Action work)
    {
        Log($"── {label} 시작 ──");
        Enabled = false;
        Task.Run(work).ContinueWith(t =>
        {
            BeginInvoke(() =>
            {
                Enabled = true;
                if (t.Exception is not null)
                {
                    var msg = t.Exception.GetBaseException().Message;
                    Log($"❌ {label} 실패: {msg}");
                    MessageBox.Show(this, msg, $"{label} 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log($"✅ {label} 완료");
                }
            });
        });
    }

    private void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(message)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
