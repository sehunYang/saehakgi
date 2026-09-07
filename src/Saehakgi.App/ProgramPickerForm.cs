namespace Saehakgi.App;

/// <summary>
/// Lets the user pick which installed programs (winget package ids) to carry in
/// the bundle. Supports a live filter and select-all/none while preserving each
/// item's checked state across filtering.
/// </summary>
public sealed class ProgramPickerForm : Form
{
    private readonly List<string> _allIds;
    private readonly Dictionary<string, bool> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckedListBox _list = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _filter = new() { Dock = DockStyle.Fill };
    private readonly Label _count = new() { AutoSize = true, Padding = new Padding(6, 8, 0, 0) };

    public List<string> Selected { get; private set; } = new();

    public ProgramPickerForm(IReadOnlyList<string> ids, IReadOnlyCollection<string>? preselected)
    {
        _allIds = ids.ToList();
        var pre = preselected is null ? null : new HashSet<string>(preselected, StringComparer.OrdinalIgnoreCase);
        foreach (var id in _allIds) _checked[id] = pre is null || pre.Contains(id);

        Text = "설치 프로그램 선택 (winget)";
        Width = 540;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9F);

        var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 6, 8, 4) };
        _filter.PlaceholderText = "검색…";
        _filter.TextChanged += (_, _) => Repopulate();
        top.Controls.Add(_filter);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 90 };
        var all = new Button { Text = "전체 선택", Width = 90 };
        var none = new Button { Text = "전체 해제", Width = 90 };
        ok.Click += (_, _) => Selected = _allIds.Where(id => _checked[id]).ToList();
        all.Click += (_, _) => SetAllVisible(true);
        none.Click += (_, _) => SetAllVisible(false);
        buttons.Controls.AddRange(new Control[] { ok, cancel, none, all });
        AcceptButton = ok;
        CancelButton = cancel;

        _list.ItemCheck += (s, e) =>
        {
            if (_list.Items[e.Index] is string id)
                BeginInvoke(() => _checked[id] = e.NewValue == CheckState.Checked);
        };

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 26, Controls = { _count } });
        Controls.Add(buttons);

        Repopulate();
    }

    private void Repopulate()
    {
        var term = _filter.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var id in _allIds)
        {
            if (term.Length > 0 && !id.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            _list.Items.Add(id, _checked[id]);
        }
        _list.EndUpdate();
        UpdateCount();
    }

    private void SetAllVisible(bool value)
    {
        for (int i = 0; i < _list.Items.Count; i++)
        {
            _list.SetItemChecked(i, value);
            if (_list.Items[i] is string id) _checked[id] = value;
        }
        UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = $"선택됨 {_allIds.Count(id => _checked[id])} / 전체 {_allIds.Count}";
}
