using System.Diagnostics;
using Saehakgi.Core.Manifest;
using Saehakgi.Core.Migration;
using Saehakgi.Core.Migration.Modules;
using Saehakgi.Core.Native;
using Saehakgi.Core.Util;

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
    private readonly CheckBox _chkWifi = new() { Text = "Wi-Fi 프로필", AutoSize = true };
    private readonly CheckBox _chkPower = new() { Text = "전원 계획", AutoSize = true };
    private readonly CheckBox _chkFonts = new() { Text = "설치 폰트 (사용자)", AutoSize = true };
    private readonly ListBox _lstFolders = new();
    private readonly TextBox _txtPassExport = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtPassExport2 = new() { UseSystemPasswordChar = true };
    private readonly Label _lblProgramSel = new() { AutoSize = true, Text = "설치 프로그램: 전체" };
    private List<string>? _selectedWingetIds;

    // Import tab
    private readonly TextBox _txtBundlePath = new() { ReadOnly = true };
    private readonly TextBox _txtPassImport = new() { UseSystemPasswordChar = true };
    private readonly CheckedListBox _clbItems = new() { CheckOnClick = true };

    // Reset tab
    private readonly Label _lblResetInfo = new() { AutoSize = true, Text = "" };

    // Browser (cookies) tab — extension id is pinned, so prefill it.
    private readonly TextBox _txtExtId = new() { Text = NativeHostRegistration.DefaultExtensionId };

    public MainForm()
    {
        Text = "saehakgi — 새 학기 세팅 이전";
        Width = 780;
        Height = 720;
        MinimumSize = new Size(700, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildExportTab());
        tabs.TabPages.Add(BuildImportTab());
        tabs.TabPages.Add(BuildResetTab());
        tabs.TabPages.Add(BuildBrowserTab());

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
        var checks = new[]
        {
            _chkFolders, _chkBookmarks, _chkMouse, _chkCerts, _chkEnv, _chkPersonalization,
            _chkPrograms, _chkStartup, _chkWifi, _chkPower, _chkFonts,
        };
        int checkTop = y;
        for (int i = 0; i < checks.Length; i++)
        {
            checks[i].Checked = true;
            checks[i].Location = new Point(20 + (i % 2) * 320, checkTop + (i / 2) * 26);
            page.Controls.Add(checks[i]);
        }
        y = checkTop + ((checks.Length + 1) / 2) * 26 + 8;

        var btnPickPrograms = new Button { Text = "설치 프로그램 선택…", Left = 20, Top = y, Width = 160 };
        btnPickPrograms.Click += (_, _) => PickPrograms();
        page.Controls.Add(btnPickPrograms);
        _lblProgramSel.Location = new Point(190, y + 6);
        page.Controls.Add(_lblProgramSel);
        y += 38;

        _lstFolders.SetBounds(20, y, 480, 110);
        page.Controls.Add(_lstFolders);

        var btnAdd = new Button { Text = "폴더 추가…", Left = 512, Top = y, Width = 130 };
        var btnRemove = new Button { Text = "제거", Left = 512, Top = y + 34, Width = 130 };
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

    private void PickPrograms()
    {
        Log("winget 설치 목록을 불러옵니다… (잠시 걸릴 수 있음)");
        Enabled = false;
        Task.Run(InstalledProgramsModule.ListWingetIds).ContinueWith(t =>
        {
            BeginInvoke(() =>
            {
                Enabled = true;
                if (t.Exception is not null) { Log("목록 불러오기 실패: " + t.Exception.GetBaseException().Message); return; }
                var ids = t.Result;
                if (ids.Count == 0) { Warn("winget에서 가져올 수 있는 프로그램이 없습니다 (winget 미설치일 수 있음)."); return; }

                using var picker = new ProgramPickerForm(ids, _selectedWingetIds);
                if (picker.ShowDialog(this) == DialogResult.OK)
                {
                    _selectedWingetIds = picker.Selected;
                    _lblProgramSel.Text = $"설치 프로그램: {_selectedWingetIds.Count}개 선택";
                    Log($"설치 프로그램 {_selectedWingetIds.Count}개 선택됨");
                }
            });
        });
    }

    private void DoExport()
    {
        var types = SelectedExportTypes();
        if (types.Count == 0) { Warn("옮길 항목을 하나 이상 선택하세요."); return; }

        var pass = _txtPassExport.Text;
        if (pass.Length < 4) { Warn("암호는 4자 이상 입력하세요."); return; }
        if (pass != _txtPassExport2.Text) { Warn("암호 확인이 일치하지 않습니다."); return; }

        var request = new MigrationRequest { SelectedWingetIds = _selectedWingetIds };
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
        if (_chkWifi.Checked) list.Add(MigrationItemType.WifiProfiles);
        if (_chkPower.Checked) list.Add(MigrationItemType.PowerPlan);
        if (_chkFonts.Checked) list.Add(MigrationItemType.Fonts);
        return list;
    }

    // ---------------- Import ----------------

    private TabPage BuildImportTab()
    {
        var page = new TabPage("② 가져오기 (새 PC)") { Padding = new Padding(12) };
        int y = 12;

        page.Controls.Add(Header("번들 열기", ref y, page.Width));
        _txtBundlePath.SetBounds(20, y, 480, 24);
        page.Controls.Add(_txtBundlePath);
        var btnBrowse = new Button { Text = "찾아보기…", Left = 512, Top = y - 2, Width = 130 };
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
        page.Controls.Add(_clbItems);
        y += 162;

        var btnImport = new Button { Text = "선택 항목 가져오기", Left = 20, Top = y, Width = 200, Height = 34 };
        btnImport.Click += (_, _) => DoImport();
        page.Controls.Add(btnImport);

        var btnInstall = new Button { Text = "설치 프로그램 지금 설치…", Left = 232, Top = y, Width = 200, Height = 34 };
        btnInstall.Click += (_, _) => InstallPrograms();
        page.Controls.Add(btnInstall);

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

    private void InstallPrograms()
    {
        var jsonPath = InstalledProgramsModule.DesktopReinstallJsonPath;
        if (!File.Exists(jsonPath))
        {
            Warn("먼저 '선택 항목 가져오기'로 설치 프로그램 목록을 적용하세요.\n(바탕화면에 saehakgi-재설치 폴더가 생성됩니다.)");
            return;
        }
        var ids = InstalledProgramsModule.ReadPackageIds(jsonPath);
        if (ids.Count == 0) { Warn("winget으로 설치할 대상이 없습니다."); return; }

        if (MessageBox.Show(this,
                $"winget으로 {ids.Count}개 프로그램을 설치 시도합니다.\n" +
                "네트워크와 시간이 필요하며, 자동 설치되지 않은 항목만 '설치-체크리스트.html'에 남습니다. 계속할까요?",
                "설치 확인", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        Log($"── 프로그램 설치 시작 (winget, {ids.Count}개) ──");
        Enabled = false;
        Task.Run(() =>
        {
            var failed = new List<string>();
            foreach (var id in ids)
            {
                Log($"설치 중: {id}");
                ProcessRunner.RunStreaming("winget",
                    $"install --id {id} --exact --silent --accept-source-agreements --accept-package-agreements",
                    Log, 900_000);
                // Verify presence — covers "installed now", "already installed", and failures uniformly.
                bool present = ProcessRunner.Run("winget", $"list --id {id} --exact", 60_000).ExitCode == 0;
                if (present) Log($"  ✓ {id}");
                else { failed.Add(id); Log($"  ⚠️ 설치 확인 실패: {id}"); }
            }
            InstalledProgramsModule.RegenerateDesktopChecklist(failed);
            return failed;
        }).ContinueWith(t =>
        {
            BeginInvoke(() =>
            {
                Enabled = true;
                if (t.Exception is not null) { Log("❌ 설치 실행 실패: " + t.Exception.GetBaseException().Message); return; }
                var failed = t.Result;
                Log(failed.Count == 0
                    ? "✅ winget 프로그램 전부 설치됨. 체크리스트에는 직접 설치할 항목만 남았습니다."
                    : $"설치 시도 완료 — {failed.Count}개는 자동 설치 실패로 체크리스트에 추가됨.");
                Log("체크리스트: " + InstalledProgramsModule.DesktopChecklistPath);
                try { Process.Start(new ProcessStartInfo(InstalledProgramsModule.DesktopChecklistPath) { UseShellExecute = true }); }
                catch { /* user can open it manually */ }
            });
        });
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

    // ---------------- Browser (cookies) ----------------

    private TabPage BuildBrowserTab()
    {
        var page = new TabPage("④ 브라우저 쿠키") { Padding = new Padding(12) };
        int y = 12;
        page.Controls.Add(Header("브라우저 쿠키 이전", ref y, page.Width));

        var info = new Label
        {
            Left = 12, Top = y, Width = 712, Height = 292, AutoSize = false,
            Text =
                "구조: 이 앱은 '다리'만 놓습니다. 실제 쿠키 내보내기/가져오기는 브라우저 확장 팝업에서 합니다.\r\n" +
                "(쿠키는 브라우저만 만질 수 있기 때문입니다.)\r\n" +
                "\r\n" +
                "■ 준비 — 기존 PC·새 PC 각각 1회\r\n" +
                "  1) 이 앱을 USB의 dist 폴더에서 실행하세요 (saehakgi.exe).\r\n" +
                "  2) 크롬/엣지 → 확장 관리 → '개발자 모드' 켜기 → '압축해제된 확장 프로그램 로드'\r\n" +
                "       → USB의 dist\\extension 폴더 선택.\r\n" +
                "  3) 아래 '① 호스트 등록'을 누르세요 (확장 ID는 이미 입력됨).\r\n" +
                "  4) 브라우저를 완전히 종료 후 다시 실행.\r\n" +
                "\r\n" +
                "■ 이전 — 확장 아이콘(퍼즐) → 'saehakgi 쿠키 이전' 팝업\r\n" +
                "  · 기존 PC: USB 파일 경로(예: E:\\saehakgi-cookies.dat)+암호 입력 → '내보내기'\r\n" +
                "  · 새   PC: 같은 경로+같은 암호 입력 → '가져오기'\r\n" +
                "\r\n" +
                "⚠ 비밀번호는 옮길 수 없습니다(브라우저 제한). 일부 사이트는 재로그인이 필요합니다.\r\n" +
                "   자세한 단계는 오른쪽 '도움말 열기'를 누르세요.",
        };
        page.Controls.Add(info);
        y += 300;

        page.Controls.Add(new Label { Text = "확장 ID:", Left = 20, Top = y + 4, AutoSize = true });
        _txtExtId.SetBounds(90, y, 360, 24);
        page.Controls.Add(_txtExtId);
        y += 36;

        var btnReg = new Button { Text = "① 호스트 등록", Left = 20, Top = y, Width = 130, Height = 30 };
        btnReg.Click += (_, _) => RegisterHost();
        var btnUnreg = new Button { Text = "등록 해제", Left = 158, Top = y, Width = 100, Height = 30 };
        btnUnreg.Click += (_, _) =>
        {
            try { NativeHostRegistration.Unregister(); Log("네이티브 호스트 등록 해제됨"); }
            catch (Exception ex) { Log("해제 실패: " + ex.Message); }
        };
        var btnHelp = new Button { Text = "도움말 열기", Left = 266, Top = y, Width = 120, Height = 30 };
        btnHelp.Click += (_, _) => OpenCookieHelp();
        page.Controls.Add(btnReg);
        page.Controls.Add(btnUnreg);
        page.Controls.Add(btnHelp);
        return page;
    }

    private void OpenCookieHelp()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "쿠키-사용안내.html");
            File.WriteAllText(path, CookieHelpHtml(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log("도움말 열기: " + path);
        }
        catch (Exception ex)
        {
            Warn("도움말을 열 수 없습니다: " + ex.Message);
        }
    }

    private static string CookieHelpHtml() => """
<!doctype html><html lang="ko"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>saehakgi 브라우저 쿠키 이전 안내</title>
<style>
:root{--bg:#f7f7f8;--fg:#1a1a1a;--card:#fff;--muted:#666;--line:#e3e3e6;--accent:#2f6df6}
@media(prefers-color-scheme:dark){:root{--bg:#16171a;--fg:#e8e8ea;--card:#212226;--muted:#9a9aa0;--line:#33343a;--accent:#6c9bff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.6 'Segoe UI',system-ui,sans-serif}
main{max-width:760px;margin:0 auto;padding:24px 18px 60px}
h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-left:4px solid var(--accent);padding-left:10px}
.lead{color:var(--muted)}
ol{padding-left:22px}li{margin:6px 0}
code{background:rgba(127,127,127,.15);padding:1px 6px;border-radius:5px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 18px;margin-top:12px}
.warn{border-left:4px solid #e0a400}
.pill{display:inline-block;background:var(--accent);color:#fff;border-radius:999px;padding:1px 10px;font-size:13px}
</style></head><body><main>
<h1>브라우저 쿠키 이전 안내</h1>
<p class="lead">이 앱은 <b>다리(호스트)</b>만 놓습니다. 실제 쿠키 내보내기·가져오기는 <b>브라우저 확장 팝업</b>에서 합니다. 쿠키는 브라우저만 만질 수 있기 때문입니다.</p>

<h2><span class="pill">준비</span> 기존 PC·새 PC 각각 한 번</h2>
<ol>
<li>이 앱을 <b>USB의 <code>dist</code> 폴더</b>에서 실행합니다(<code>saehakgi.exe</code>). 등록이 옆의 <code>Saehakgi.Host.exe</code> 경로를 기록하므로 꼭 dist 폴더에서 실행하세요.</li>
<li>크롬은 <code>chrome://extensions</code>, 엣지는 <code>edge://extensions</code> 로 이동 → 오른쪽 <b>개발자 모드</b>를 켜고 → <b>“압축해제된 확장 프로그램을 로드합니다”</b> → USB의 <code>dist\extension</code> 폴더를 선택합니다.</li>
<li>앱의 <b>④ 브라우저 쿠키</b> 탭에서 <b>“① 호스트 등록”</b>을 누릅니다. (확장 ID <code>%EXTID%</code> 는 이미 입력되어 있습니다.)</li>
<li><b>브라우저를 완전히 종료</b>했다가 다시 엽니다. (등록은 브라우저 시작 시 반영됩니다.)</li>
</ol>

<h2><span class="pill">내보내기</span> 기존 PC</h2>
<ol>
<li>브라우저 오른쪽 위 <b>퍼즐(확장) 아이콘 → “saehakgi 쿠키 이전”</b> 클릭.</li>
<li>팝업에 <b>USB 파일 경로</b>(예: <code>E:\saehakgi-cookies.dat</code>)와 <b>암호</b>를 입력.</li>
<li><b>내보내기</b> 클릭 → “내보내기 완료: 쿠키 N개 저장” 확인.</li>
</ol>

<h2><span class="pill">가져오기</span> 새 PC</h2>
<ol>
<li>새 PC에서도 위 <b>준비 1~4</b>를 먼저 합니다.</li>
<li>확장 팝업에 <b>같은 USB 파일 경로 + 같은 암호</b> 입력 → <b>가져오기</b> 클릭.</li>
<li>로그인 상태를 쓸 사이트를 새로고침합니다.</li>
</ol>

<h2>자주 막히는 점</h2>
<div class="card"><b>드라이브 문자</b> — USB가 PC마다 <code>E:</code>/<code>F:</code> 로 다를 수 있습니다. 팝업 경로를 그 PC의 실제 문자에 맞추세요.</div>
<div class="card"><b>브라우저 재시작</b> — 호스트 등록 후 브라우저를 껐다 켜지 않으면 “호스트 없음” 오류가 납니다.</div>
<div class="card warn"><b>비밀번호는 옮길 수 없습니다</b> — 브라우저가 저장된 비밀번호를 확장에 노출하지 않습니다. 크롬/엣지 설정의 <b>비밀번호 내보내기/가져오기(CSV)</b>를 사용하세요.</div>
<div class="card warn"><b>재로그인이 필요할 수 있음</b> — 구글 등 기기 바인딩 세션이나 서버에서 만료·회전된 세션은 쿠키를 옮겨도 새 PC에서 다시 로그인해야 합니다.</div>
</main></body></html>
""".Replace("%EXTID%", NativeHostRegistration.DefaultExtensionId);

    private void RegisterHost()
    {
        var extId = _txtExtId.Text.Trim();
        if (extId.Length == 0) { Warn("확장 ID를 입력하세요."); return; }

        var hostExe = Path.Combine(AppContext.BaseDirectory, "Saehakgi.Host.exe");
        if (!File.Exists(hostExe))
        {
            Warn($"호스트 실행 파일을 찾을 수 없습니다:\n{hostExe}\n\n배포 시 saehakgi.exe 와 Saehakgi.Host.exe 를 같은 폴더에 두세요.");
            return;
        }
        try
        {
            var manifest = NativeHostRegistration.Register(hostExe, extId);
            Log("네이티브 호스트 등록됨: " + manifest);
            Warn("등록되었습니다. 브라우저를 완전히 종료 후 다시 열어 확장 팝업에서 이전하세요.");
        }
        catch (Exception ex)
        {
            Log("등록 실패: " + ex.Message);
            Warn(ex.Message);
        }
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
