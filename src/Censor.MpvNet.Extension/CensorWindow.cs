using Censor.Core;

namespace Censor.MpvNet.Extension;

internal sealed class CensorWindow : Form
{
    private static readonly (string Label, BlurSettings Settings)[] BlurPresets =
    [
        ("Strong (30 / 2)", BlurSettings.Strong),
        ("Balanced (40 / 2)", BlurSettings.Balanced),
        ("Maximum (50 / 3)", BlurSettings.Maximum),
    ];

    private readonly Label _media = new() { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Label _schedule = new() { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly ComboBox _blurPreset = new()
    {
        AccessibleName = "Blur preset",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 170,
    };
    private readonly DataGridView _intervals = new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Dock = DockStyle.Fill,
        ReadOnly = true,
        RowHeadersVisible = false,
    };

    private bool _allowClose;
    private IReadOnlyList<CensorInterval>? _renderedIntervals;

    public CensorWindow()
    {
        Text = "Censor Extension";
        MinimumSize = new(640, 360);
        Size = new(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        _intervals.Columns.Add("start", "Start");
        _intervals.Columns.Add("end", "End");
        _intervals.Columns.Add("note", "Note");

        var load = new Button { AutoSize = true, Text = "Load schedule…" };
        var reload = new Button { AutoSize = true, Text = "Reload" };
        var disable = new Button { AutoSize = true, Text = "Disable" };
        load.Click += (_, _) => SelectSchedule();
        reload.Click += (_, _) => ReloadRequested?.Invoke();
        disable.Click += (_, _) => DisableRequested?.Invoke();
        _blurPreset.Items.AddRange(BlurPresets.Select(preset => preset.Label).ToArray());
        _blurPreset.SelectedIndex = 1;
        _blurPreset.SelectedIndexChanged += (_, _) =>
            BlurPresetSelected?.Invoke(BlurPresets[_blurPreset.SelectedIndex].Settings);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttons.Controls.AddRange(
        [
            load,
            reload,
            disable,
            new Label { AutoSize = true, Margin = new(12, 8, 3, 0), Text = "Blur:" },
            _blurPreset,
        ]);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new(12),
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Media:" }, 0, 0);
        layout.Controls.Add(_media, 1, 0);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Schedule:" }, 0, 1);
        layout.Controls.Add(_schedule, 1, 1);
        layout.Controls.Add(new Label { AutoSize = true, Text = "Status:" }, 0, 2);
        layout.Controls.Add(_status, 1, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(_intervals, 0, 4);
        layout.SetColumnSpan(_intervals, 2);
        Controls.Add(layout);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
    }

    public event Action<string>? ScheduleSelected;

    public event Action? ReloadRequested;

    public event Action? DisableRequested;

    public event Action<BlurSettings>? BlurPresetSelected;

    public void UpdateState(
        string? mediaPath,
        string? schedulePath,
        string status,
        IReadOnlyList<CensorInterval> intervals)
    {
        _media.Text = string.IsNullOrEmpty(mediaPath) ? "No media" : mediaPath;
        _schedule.Text = string.IsNullOrEmpty(schedulePath) ? "None" : schedulePath;
        _status.Text = status;
        if (ReferenceEquals(_renderedIntervals, intervals))
            return;

        _renderedIntervals = intervals;
        _intervals.Rows.Clear();
        foreach (var interval in intervals)
        {
            _intervals.Rows.Add(
                FormatTimestamp(interval.StartMs),
                FormatTimestamp(interval.EndMs),
                interval.Note ?? "");
        }
    }

    public bool ConfirmDurationMismatch() =>
        MessageBox.Show(
            this,
            "The schedule duration differs from the current media. Apply it anyway?",
            "Censor Extension",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private void SelectSchedule()
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Censor schedules|*.censor.txt;*.srt;*.vtt|Censor TXT|*.censor.txt|SubRip|*.srt|WebVTT|*.vtt",
            Multiselect = false,
            Title = "Load censor schedule",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            ScheduleSelected?.Invoke(dialog.FileName);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDroppedSchedule(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (TryGetDroppedSchedule(e.Data, out var path))
            ScheduleSelected?.Invoke(path);
    }

    private static bool TryGetDroppedSchedule(IDataObject? data, out string path)
    {
        path = "";
        if (data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return false;

        path = files[0];
        return path.EndsWith(".censor.txt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTimestamp(long milliseconds)
    {
        var hours = milliseconds / 3_600_000;
        var minutes = (milliseconds / 60_000) % 60;
        var seconds = (milliseconds / 1_000) % 60;
        var millis = milliseconds % 1_000;
        return FormattableString.Invariant($"{hours:D2}:{minutes:D2}:{seconds:D2}.{millis:D3}");
    }
}
