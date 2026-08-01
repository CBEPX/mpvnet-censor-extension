using System.ComponentModel;
using System.Globalization;
using Censor.Core;

namespace Censor.MpvNet.Extension;

internal sealed class CensorWindow : Form
{
    private readonly List<(string Label, BlurSettings Settings)> _blurPresets =
    [
        ("Умеренное (30 / 2)", BlurSettings.Moderate),
        ("Сбалансированное (40 / 2)", BlurSettings.Balanced),
        ("Максимальное (50 / 3)", BlurSettings.Maximum),
    ];

    private readonly Label _media = ValueLabel();
    private readonly Label _schedule = ValueLabel();
    private readonly Label _status = ValueLabel();
    private readonly Label _duration = ValueLabel();
    private readonly Label _filterState = ValueLabel();
    private readonly Label _draftState = ValueLabel();
    private readonly ListBox _warnings = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _intervals = CreateGrid();
    private readonly ComboBox _blurPreset = new()
    {
        AccessibleName = "Степень размытия",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 230,
    };
    private readonly ComboBox _audioCompressionPreset = new()
    {
        AccessibleName = "Компрессия звука",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 230,
    };
    private readonly CheckBox _autoSidecar = new()
    {
        AutoSize = true,
        Text = "Автозагрузка расписания рядом с фильмом",
    };
    private readonly CheckBox _watchdog = new() { AutoSize = true, Text = "Автоматически восстанавливать фильтры цензуры" };
    private readonly NumericUpDown _watchdogInterval = Milliseconds(250, 60_000);
    private readonly NumericUpDown _leadIn = Milliseconds(0, 86_400_000);
    private readonly NumericUpDown _leadOut = Milliseconds(0, 86_400_000);
    private readonly NumericUpDown _mergeGap = Milliseconds(0, 86_400_000);
    private readonly NumericUpDown _durationTolerance = Milliseconds(0, 86_400_000);
    private readonly NumericUpDown _earlyGuard = Milliseconds(0, 86_400_000);
    private readonly NumericUpDown _offset = Milliseconds(-86_400_000, 86_400_000);
    private readonly NumericUpDown _shiftAll = Milliseconds(-86_400_000, 86_400_000);
    private readonly CheckBox _includeSchedule = new()
    {
        AutoSize = true,
        Text = "Добавить расписание в диагностический ZIP-архив",
    };
    private readonly TextBox _diagnostics = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };
    private readonly System.Windows.Forms.Timer _recoveryTimer = new() { Interval = 500 };
    private readonly string _recoveryPath;
    private readonly string _uiStatePath;
    private ExtensionSettings _settings;
    private ScheduleDraft? _draft;
    private IReadOnlyList<CensorInterval>? _sourceIntervals;
    private IReadOnlyList<CensorInterval>? _runtimeIntervals;
    private IReadOnlyList<ParseDiagnostic> _draftDiagnostics = [];
    private IReadOnlyList<ParseDiagnostic> _runtimeDiagnostics = [];
    private bool _allowClose;
    private bool _rendering;
    private long? _pendingStartMs;
    private string? _mediaPath;
    private string? _sourcePath;

    public CensorWindow(ExtensionSettings settings, string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        localDataRoot = Path.GetFullPath(localDataRoot);
        _settings = settings;
        _recoveryPath = Path.Combine(localDataRoot, "Recovery", "draft.json");
        _uiStatePath = Path.Combine(localDataRoot, "UiState.json");
        Text = "CensorPlayer — редактор цензуры";
        AccessibleName = Text;
        MinimumSize = new(760, 520);
        Size = new(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        if (UiStateStore.Load(_uiStatePath) is { } uiState)
        {
            var restoredBounds = new Rectangle(
                uiState.Left,
                uiState.Top,
                uiState.Width,
                uiState.Height);
            if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(restoredBounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = restoredBounds;
            }
        }

        var blurPresetIndex = EnsureBlurPreset(settings.Blur);
        _blurPreset.Items.AddRange(_blurPresets.Select(item => item.Label).ToArray());
        _blurPreset.SelectedIndex = blurPresetIndex;
        _blurPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_rendering)
                return;
            var blur = _blurPresets[_blurPreset.SelectedIndex].Settings;
            _settings = _settings with { Blur = blur };
            BlurPresetSelected?.Invoke(blur);
        };
        _audioCompressionPreset.Items.AddRange(
            AudioCompressionPresets.All.Select(item => item.DisplayName).ToArray());
        var initialAudioPreset = ResolveAudioPreset(settings.AudioCompressionPreset);
        _settings = _settings with { AudioCompressionPreset = initialAudioPreset.Preset.Id };
        _audioCompressionPreset.SelectedIndex = initialAudioPreset.Index;
        _audioCompressionPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_rendering)
                return;
            var preset = AudioCompressionPresets.All[_audioCompressionPreset.SelectedIndex];
            _settings = _settings with { AudioCompressionPreset = preset.Id };
            AudioCompressionPresetSelected?.Invoke(preset.Id);
        };
        PopulateSettings(settings);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new TabPage("Текущий фильм") { Controls = { BuildCurrentTab() } });
        tabs.TabPages.Add(new TabPage("Интервалы") { Controls = { BuildIntervalsTab() } });
        tabs.TabPages.Add(new TabPage("Настройки") { Controls = { BuildSettingsTab() } });
        tabs.TabPages.Add(new TabPage("Диагностика") { Controls = { BuildDiagnosticsTab() } });
        Controls.Add(tabs);

        _intervals.CellEndEdit += OnCellEndEdit;
        _offset.ValueChanged += (_, _) =>
        {
            if (_rendering || _draft is null)
                return;
            _draft.SetOffset((long)_offset.Value);
            Changed();
        };
        _recoveryTimer.Tick += (_, _) =>
        {
            _recoveryTimer.Stop();
            SaveRecovery();
        };
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        Shown += (_, _) => OfferRecovery();
    }

    public event Action<string>? ScheduleSelected;
    public event Action<ScheduleDocument>? ApplyRequested;
    public event Action<ScheduleDocument, string?, bool>? SaveRequested;
    public event Action? ReloadRequested;
    public event Action? DisableRequested;
    public event Action<BlurSettings>? BlurPresetSelected;
    public event Action<string>? AudioCompressionPresetSelected;
    public event Action<SettingsFormValues>? SettingsChanged;
    public event Action<long>? SeekRequested;
    public event Action<long>? PreviewRequested;
    public event Action<bool>? DiagnosticsRequested;
    public event Action? SettingsRepairRequested;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<long?>? CurrentTimeRequested { get; set; }

    public void UpdateState(
        string? mediaPath,
        string? schedulePath,
        string status,
        long? mediaDurationMs,
        ScheduleDocument? document,
        IReadOnlyList<ParseDiagnostic> diagnostics,
        bool hasActiveSchedule)
    {
        var intervals = document?.Intervals ?? [];
        if (!string.Equals(_mediaPath, mediaPath, StringComparison.OrdinalIgnoreCase))
        {
            _mediaPath = mediaPath;
            _pendingStartMs = null;
        }
        _media.Text = string.IsNullOrEmpty(mediaPath) ? "Нет открытого фильма" : mediaPath;
        _schedule.Text = string.IsNullOrEmpty(schedulePath) ? "Не выбрано" : schedulePath;
        var localizedStatus = LocalizeStatus(status);
        _status.Text = localizedStatus;
        _duration.Text = mediaDurationMs.HasValue
            ? FormatTimestamp(mediaDurationMs.Value)
            : "Неизвестна";
        _filterState.Text = status == "WARNING"
            ? localizedStatus
            : hasActiveSchedule
                ? "Активна"
                : "Нет активной цепочки фильтров";
        _diagnostics.Text =
            $"Статус: {localizedStatus}{Environment.NewLine}" +
            $"Фильм: {_media.Text}{Environment.NewLine}" +
            $"Расписание: {_schedule.Text}{Environment.NewLine}" +
            $"Интервалов: {intervals.Count}";
        _runtimeDiagnostics = diagnostics;

        var reconciliation = DraftReconciliation.Decide(
            ReferenceEquals(_runtimeIntervals, intervals),
            ReferenceEquals(_sourceIntervals, intervals),
            _draft?.IsDirty == true,
            document is not null && _draft?.Matches(document) == true);
        _runtimeIntervals = intervals;
        if (reconciliation == DraftReconciliationAction.RefreshWarnings)
        {
            if (_draft is null)
                RenderWarnings([]);
            else
                RenderDraftSummary(_draftDiagnostics);
            return;
        }
        if (reconciliation == DraftReconciliationAction.LinkMatchingDraft)
        {
            _sourceIntervals = intervals;
            _sourcePath = schedulePath;
            RenderDraft();
            return;
        }
        if (reconciliation == DraftReconciliationAction.KeepDirtyDraft)
        {
            RenderDraft();
            _draftState.Text = "Несохранённый черновик не связан с текущим расписанием";
            return;
        }

        _sourceIntervals = intervals;
        _sourcePath = schedulePath;
        _draft = new(document ?? new(new(), [], []));
        RenderDraft();
    }

    public bool ConfirmDurationMismatch() =>
        MessageBox.Show(
            this,
            "Длительность расписания отличается от фильма. Применить всё равно?",
            "CensorPlayer",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public DialogResult ConfirmExternalChange() =>
        MessageBox.Show(
            this,
            "Файл изменён другой программой.\n\nДа — перезаписать, Нет — сохранить как новый, Отмена — ничего не делать.",
            "CensorPlayer",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button3);

    public bool ConfirmSubtitleExport() =>
        MessageBox.Show(
            this,
            "SRT и WebVTT сохраняют только интервалы и текст. Название, смещение, запас до и после интервала и служебные строки в экспорт не попадут.\n\nЭкспортировать копию? Черновик не будет помечен как сохранённый.",
            "CensorPlayer",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public void MarkSaved(
        string path,
        ScheduleDocument savedDocument,
        string? lastScheduleDirectory)
    {
        _sourcePath = path;
        _schedule.Text = path;
        _settings = _settings with { LastScheduleDirectory = lastScheduleDirectory };
        if (_draft?.MarkSaved(savedDocument) == true)
            DraftRecoveryStore.Delete(_recoveryPath);
        else
            SaveRecovery();
        RenderDraft();
    }

    public void ShowDiagnosticsResult(string message)
    {
        _diagnostics.Text = message;
    }

    public void OpenSchedulePicker() => SelectSchedule();

    public void SetAudioCompressionPreset(string presetId)
    {
        var resolved = ResolveAudioPreset(presetId);
        _rendering = true;
        try
        {
            _settings = _settings with { AudioCompressionPreset = resolved.Preset.Id };
            _audioCompressionPreset.SelectedIndex = resolved.Index;
        }
        finally
        {
            _rendering = false;
        }
    }

    public void SetBlurPreset(BlurSettings settings)
    {
        _rendering = true;
        try
        {
            var index = EnsureBlurPreset(settings);
            while (_blurPreset.Items.Count < _blurPresets.Count)
                _blurPreset.Items.Add(_blurPresets[_blurPreset.Items.Count].Label);
            _settings = _settings with { Blur = settings };
            _blurPreset.SelectedIndex = index;
        }
        finally
        {
            _rendering = false;
        }
    }

    public void SetSettings(ExtensionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        PopulateSettings(settings);
    }

    public void HandleAuthoringCommand(string command, long? capturedTimeMs = null)
    {
        switch (command)
        {
            case "mark-start":
                _pendingStartMs = capturedTimeMs ?? CurrentTimeRequested?.Invoke();
                _draftState.Text = _pendingStartMs.HasValue
                    ? $"Начало отмечено: {FormatTimestamp(_pendingStartMs.Value)}"
                    : "Текущая позиция воспроизведения недоступна";
                break;
            case "mark-end":
                if (_pendingStartMs is { } start &&
                    (capturedTimeMs ?? CurrentTimeRequested?.Invoke()) is { } end &&
                    end > start)
                {
                    EnsureDraft();
                    _draft!.Add(start, end);
                    _pendingStartMs = null;
                    Changed();
                }
                else
                {
                    Warn("Сначала отметьте начало интервала. Конец должен быть позже начала.");
                }
                break;
            case "set-start":
                CaptureBoundary(start: true, capturedTimeMs);
                break;
            case "set-end":
                CaptureBoundary(start: false, capturedTimeMs);
                break;
            case "previous":
                NavigateInterval(-1);
                break;
            case "next":
                NavigateInterval(1);
                break;
            case "save":
                SaveDraft(saveAs: false);
                break;
        }
    }

    public string? ChooseSavePath(string? currentPath)
    {
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "censor.txt",
            FileName = string.IsNullOrWhiteSpace(currentPath)
                ? "schedule" + ScheduleFileKinds.CanonicalSuffix
                : Path.GetFileName(currentPath),
            Filter =
                $"Censor TXT|*{ScheduleFileKinds.CanonicalSuffix}|" +
                $"SubRip|*{ScheduleFileKinds.SrtSuffix}|" +
                $"WebVTT|*{ScheduleFileKinds.WebVttSuffix}",
            InitialDirectory = _settings.RememberLastScheduleDirectory
                ? _settings.LastScheduleDirectory
                : null,
            OverwritePrompt = true,
            Title = "Сохранить расписание",
        };
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (ScheduleFileKinds.IsSupportedPath(dialog.FileName))
                return dialog.FileName;

            Warn("Допустимы только файлы .censor.txt, .srt и .vtt. Проверьте имя файла.");
        }
        return null;
    }

    public string? ChooseDiagnosticsPath(string directory)
    {
        Directory.CreateDirectory(directory);
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "zip",
            FileName = $"CensorPlayer-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "ZIP-архив|*.zip",
            InitialDirectory = directory,
            OverwritePrompt = true,
            Title = "Экспортировать диагностику",
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    public string? ChooseSidecar(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return null;

        using var dialog = new Form
        {
            Text = "Выберите файл расписания",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Size = new(640, 320),
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            DisplayMember = nameof(SidecarChoice.Name),
        };
        foreach (var path in paths)
            list.Items.Add(new SidecarChoice(Path.GetFileName(path), path));
        list.SelectedIndex = 0;
        var ok = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "Выбрать",
        };
        var cancel = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "Отмена",
        };
        var buttons = Flow(ok, cancel);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(list, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        _ = dialog.Handle;
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                dialog.BeginInvoke(new Action(dialog.Close));
            }
            catch (InvalidOperationException)
            {
            }
        });
        if (cancellationToken.IsCancellationRequested)
            return null;
        return dialog.ShowDialog(this) == DialogResult.OK &&
            !cancellationToken.IsCancellationRequested &&
            list.SelectedItem is SidecarChoice choice
                ? choice.Path
                : null;
    }

    public void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            SaveRecovery();
            SaveUiState();
            e.Cancel = true;
            Hide();
            return;
        }

        SaveRecovery();
        SaveUiState();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _recoveryTimer.Dispose();
        base.Dispose(disposing);
    }

    private TableLayoutPanel BuildCurrentTab()
    {
        var load = Button("Загрузить файл…", SelectSchedule);
        var apply = Button("Применить", ApplyDraft);
        var reload = Button("Перезагрузить", () => ReloadRequested?.Invoke());
        var disable = Button("Отключить", () => DisableRequested?.Invoke());
        var next = Button("Следующий интервал", () => NavigateInterval(1));
        var buttons = Flow(load, apply, reload, disable, next);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new(12),
            RowCount = 8,
        };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        AddRow(layout, 0, "Фильм:", _media);
        AddRow(layout, 1, "Длительность:", _duration);
        AddRow(layout, 2, "Расписание:", _schedule);
        AddRow(layout, 3, "Состояние:", _status);
        AddRow(layout, 4, "Цепочка фильтров:", _filterState);
        AddRow(layout, 5, "Черновик:", _draftState);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(_warnings, 0, 7);
        layout.SetColumnSpan(_warnings, 2);
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        return layout;
    }

    private TableLayoutPanel BuildIntervalsTab()
    {
        var toolbar = Flow(
            Button("Добавить", AddInterval),
            Button("Удалить", DeleteSelected),
            Button("Дублировать", DuplicateSelected),
            Button("Объединить", MergeSelected),
            Button("Разрезать", SplitSelected),
            Button("Отменить", Undo),
            Button("Повторить", Redo));
        var capture = Flow(
            Button("Начало = текущая позиция", () => CaptureBoundary(start: true)),
            Button("Конец = текущая позиция", () => CaptureBoundary(start: false)),
            Button("Начало −100", () => ShiftSelected(start: true, -100)),
            Button("Начало +100", () => ShiftSelected(start: true, 100)),
            Button("Конец −100", () => ShiftSelected(start: false, -100)),
            Button("Конец +100", () => ShiftSelected(start: false, 100)));
        var global = Flow(
            new Label { AutoSize = true, Margin = new(3, 8, 3, 0), Text = "Смещение, мс:" },
            _offset,
            new Label { AutoSize = true, Margin = new(12, 8, 3, 0), Text = "Сдвиг всех интервалов, мс:" },
            _shiftAll,
            Button("Сдвинуть", ShiftAll),
            Button("Предыдущий", () => NavigateInterval(-1)),
            Button("Следующий", () => NavigateInterval(1)),
            Button("Предпросмотр", PreviewSelected),
            Button("Сохранить", () => SaveDraft(saveAs: false)),
            Button("Сохранить как…", () => SaveDraft(saveAs: true)),
            Button("Отбросить черновик", DiscardDraft));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(capture, 0, 1);
        layout.Controls.Add(global, 0, 2);
        layout.Controls.Add(_intervals, 0, 3);
        return layout;
    }

    private TableLayoutPanel BuildSettingsTab()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new(12),
            AutoSize = true,
        };
        AddRow(layout, 0, "Размытие:", _blurPreset);
        AddRow(layout, 1, "Компрессия звука:", _audioCompressionPreset);
        AddRow(layout, 2, "Запас перед началом, мс:", _leadIn);
        AddRow(layout, 3, "Запас после конца, мс:", _leadOut);
        AddRow(layout, 4, "Порог объединения, мс:", _mergeGap);
        AddRow(layout, 5, "Допуск длительности, мс:", _durationTolerance);
        AddRow(layout, 6, "Защитный запас до интервала, мс:", _earlyGuard);
        AddRow(layout, 7, "Период проверки фильтра, мс:", _watchdogInterval);
        layout.Controls.Add(_autoSidecar, 1, 8);
        layout.Controls.Add(_watchdog, 1, 9);
        layout.Controls.Add(Button("Сохранить настройки", SaveSettings), 1, 10);
        return layout;
    }

    private TableLayoutPanel BuildDiagnosticsTab()
    {
        var controls = Flow(
            _includeSchedule,
            Button("Экспортировать диагностический ZIP-архив…", () =>
                DiagnosticsRequested?.Invoke(_includeSchedule.Checked)),
            Button("Перезаписать файл настроек…", RepairSettings));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(controls, 0, 0);
        layout.Controls.Add(_diagnostics, 0, 1);
        return layout;
    }

    private void AddInterval()
    {
        EnsureDraft();
        var start = CurrentTimeRequested?.Invoke() ?? 0;
        _draft!.Add(start, checked(start + 1_000));
        Changed(selectedIndex: _draft.Document.Intervals.Count - 1);
    }

    private void DeleteSelected()
    {
        if (_draft is null)
            return;
        var selected = SelectedIndices();
        if (selected.Length == 0)
            return;
        _draft.Delete(selected);
        var next = _draft.Document.Intervals.Count == 0
            ? (int?)null
            : Math.Min(selected[0], _draft.Document.Intervals.Count - 1);
        Changed(selectedIndex: next);
    }

    private void DuplicateSelected()
    {
        if (_draft is null || SelectedIndex() is not { } index)
            return;
        _draft.Duplicate(index);
        Changed(selectedIndex: index + 1);
    }

    private void MergeSelected()
    {
        if (_draft is null)
            return;
        try
        {
            var selected = SelectedIndices();
            _draft.Merge(selected);
            Changed(selectedIndex: selected[0]);
        }
        catch (ArgumentException exception)
        {
            Warn(exception.Message);
        }
    }

    private void SplitSelected()
    {
        if (_draft is null || SelectedIndex() is not { } index ||
            CurrentTimeRequested?.Invoke() is not { } position)
        {
            return;
        }
        try
        {
            _draft.Split(index, position);
            Changed(selectedIndex: index);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Warn(exception.Message);
        }
    }

    private void CaptureBoundary(bool start, long? capturedTimeMs = null)
    {
        if (_draft is null || SelectedIndex() is not { } index ||
            (capturedTimeMs ?? CurrentTimeRequested?.Invoke()) is not { } position)
        {
            return;
        }
        var interval = _draft.Document.Intervals[index];
        _draft.Update(
            index,
            start ? position : interval.StartMs,
            start ? interval.EndMs : position,
            interval.Note);
        Changed(selectedIndex: index, renderSelectedOnly: true);
    }

    private void ShiftSelected(bool start, long delta)
    {
        if (_draft is null || SelectedIndex() is not { } index)
            return;
        try
        {
            _draft.ShiftBoundary(index, start, delta);
            Changed(selectedIndex: index, renderSelectedOnly: true);
        }
        catch (OverflowException)
        {
            Warn("Граница вышла за допустимый диапазон.");
        }
    }

    private void ShiftAll()
    {
        if (_draft is null)
            return;
        try
        {
            _draft.ShiftAll((long)_shiftAll.Value);
            Changed();
        }
        catch (OverflowException)
        {
            Warn("Сдвиг вышел за допустимый диапазон.");
        }
    }

    private void Undo()
    {
        if (_draft?.Undo() == true)
            Changed();
    }

    private void Redo()
    {
        if (_draft?.Redo() == true)
            Changed();
    }

    private void ApplyDraft()
    {
        if (!TryGetValidDraft(out var document))
            return;
        ApplyRequested?.Invoke(document);
    }

    private void SaveDraft(bool saveAs)
    {
        if (!TryGetValidDraft(out var document))
            return;
        SaveRequested?.Invoke(
            document,
            _sourcePath,
            saveAs || string.IsNullOrWhiteSpace(_sourcePath));
    }

    private bool TryGetValidDraft(out ScheduleDocument document)
    {
        document = new(new(), [], []);
        if (_draft is null)
        {
            Warn("Сначала загрузите или создайте расписание.");
            return false;
        }
        var diagnostics = _draft.Validate(
            _settings.Limits.MaxIntervals,
            _settings.Limits.MaxTextFileBytes);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            Warn("Исправьте ошибки черновика перед применением или сохранением.");
            return false;
        }
        document = _draft.Document;
        return true;
    }

    private void NavigateInterval(int direction)
    {
        if (_draft is null || _draft.Document.Intervals.Count == 0)
            return;
        var current = SelectedIndex() ?? (direction > 0 ? -1 : 0);
        var next = Math.Clamp(current + direction, 0, _draft.Document.Intervals.Count - 1);
        SelectRow(next);
        SeekRequested?.Invoke(_draft.Document.Intervals[next].StartMs);
    }

    private void PreviewSelected()
    {
        if (_draft is null || SelectedIndex() is not { } index)
            return;
        PreviewRequested?.Invoke(_draft.Document.Intervals[index].StartMs);
    }

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_rendering || _draft is null || e.RowIndex < 0)
            return;
        var interval = _draft.Document.Intervals[e.RowIndex];
        var start = interval.StartMs;
        var end = interval.EndMs;
        var note = Convert.ToString(
            _intervals.Rows[e.RowIndex].Cells["note"].Value,
            CultureInfo.InvariantCulture);
        var column = _intervals.Columns[e.ColumnIndex].Name;
        var text = Convert.ToString(
            _intervals.Rows[e.RowIndex].Cells[e.ColumnIndex].Value,
            CultureInfo.InvariantCulture) ?? "";
        var timestampValid = column switch
        {
            "start" => ScheduleText.TryParseDraftTimestamp(text, out start),
            "end" => ScheduleText.TryParseDraftTimestamp(text, out end),
            _ => true,
        };
        if (!timestampValid)
        {
            DeferRender(
                e.RowIndex,
                "Формат времени: ЧЧ:ММ:СС.мс; минус ставится перед часами.",
                renderSelectedOnly: true);
            return;
        }
        _draft.Update(e.RowIndex, start, end, note);
        Changed(
            deferRender: true,
            selectedIndex: e.RowIndex,
            renderSelectedOnly: true);
    }

    private void Changed(
        bool deferRender = false,
        int? selectedIndex = null,
        bool renderSelectedOnly = false)
    {
        _recoveryTimer.Stop();
        _recoveryTimer.Start();
        if (deferRender)
            DeferRender(selectedIndex, renderSelectedOnly: renderSelectedOnly);
        else if (renderSelectedOnly && selectedIndex is { } index)
            RenderDraftRow(index);
        else
            RenderDraft(selectedIndex);
    }

    private void DeferRender(
        int? selectedIndex,
        string? status = null,
        bool renderSelectedOnly = false)
    {
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (renderSelectedOnly && selectedIndex is { } index)
                    RenderDraftRow(index);
                else
                    RenderDraft(selectedIndex);
                if (status is not null)
                    _draftState.Text = status;
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RenderDraft(int? selectedIndex = null)
    {
        var restoreIndices = selectedIndex.HasValue
            ? [selectedIndex.Value]
            : SelectedIndices();
        var restoreCurrent = selectedIndex ?? SelectedIndex();
        _intervals.SuspendLayout();
        _rendering = true;
        try
        {
            _intervals.Rows.Clear();
            _warnings.Items.Clear();
            if (_draft is null)
            {
                _draftDiagnostics = [];
                _draftState.Text = "Нет черновика";
                return;
            }

            var draftDiagnostics = _draft.Validate(
                _settings.Limits.MaxIntervals,
                _settings.Limits.MaxTextFileBytes,
                checkSerializedSize: false);
            var errorRows = draftDiagnostics
                .Where(item => item.Line > 0)
                .Select(item => item.Line - 1)
                .ToHashSet();
            var intervals = _draft.Document.Intervals;
            for (var index = 0; index < intervals.Count; index++)
            {
                var rowIndex = _intervals.Rows.Add();
                PopulateIntervalRow(
                    _intervals.Rows[rowIndex],
                    index,
                    intervals[index],
                    errorRows.Contains(index));
            }
            RenderDraftSummary(draftDiagnostics);
        }
        finally
        {
            try
            {
                _intervals.ResumeLayout();
            }
            finally
            {
                _rendering = false;
            }
        }
        if (_intervals.Rows.Count == 0)
            return;
        var validIndices = restoreIndices
            .Where(index => index >= 0 && index < _intervals.Rows.Count)
            .ToArray();
        if (validIndices.Length == 0)
            return;
        _intervals.ClearSelection();
        foreach (var index in validIndices)
            _intervals.Rows[index].Selected = true;
        var current = restoreCurrent.HasValue &&
            validIndices.Contains(restoreCurrent.Value)
                ? restoreCurrent.Value
                : validIndices[0];
        _intervals.CurrentCell = _intervals.Rows[current].Cells["start"];
    }

    private void RenderDraftRow(int index)
    {
        if (_draft is null ||
            index < 0 ||
            index >= _draft.Document.Intervals.Count ||
            _intervals.Rows.Count != _draft.Document.Intervals.Count)
        {
            RenderDraft(index);
            return;
        }

        _rendering = true;
        try
        {
            var diagnostics = _draft.Validate(
                _settings.Limits.MaxIntervals,
                _settings.Limits.MaxTextFileBytes,
                checkSerializedSize: false);
            PopulateIntervalRow(
                _intervals.Rows[index],
                index,
                _draft.Document.Intervals[index],
                diagnostics.Any(item => item.Line == index + 1));
            RenderDraftSummary(diagnostics);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderDraftSummary(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        _draftDiagnostics = diagnostics;
        RenderWarnings(diagnostics);
        var wasRendering = _rendering;
        _rendering = true;
        try
        {
            _offset.Value = Math.Clamp(
                _draft!.Document.Metadata.OffsetMs ?? 0,
                (long)_offset.Minimum,
                (long)_offset.Maximum);
        }
        finally
        {
            _rendering = wasRendering;
        }
        _draftState.Text = _draft.IsDirty
            ? "Изменения не применены и не сохранены"
            : "Сохранено";
    }

    private void RenderWarnings(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        _warnings.Items.Clear();
        foreach (var diagnostic in diagnostics.Where(item => item.Line > 0))
            _warnings.Items.Add($"#{diagnostic.Line}: {diagnostic.Message}");
        foreach (var diagnostic in diagnostics.Where(item => item.Line <= 0))
        {
            var severity = diagnostic.Severity == DiagnosticSeverity.Error
                ? "Ошибка"
                : "Предупреждение";
            _warnings.Items.Add($"{severity}: {diagnostic.Message}");
        }
        RenderDiagnostics(_runtimeDiagnostics);
    }

    private static void PopulateIntervalRow(
        DataGridViewRow row,
        int index,
        CensorInterval interval,
        bool hasError)
    {
        row.Cells["number"].Value = index + 1;
        row.Cells["start"].Value = FormatTimestamp(interval.StartMs);
        row.Cells["end"].Value = FormatTimestamp(interval.EndMs);
        row.Cells["duration"].Value = interval.EndMs >= interval.StartMs
            ? FormatDuration(interval.EndMs - interval.StartMs)
            : "—";
        row.Cells["note"].Value = interval.Note ?? "";
        row.Cells["status"].Value = hasError ? "Ошибка" : "Корректно";
    }

    private void RenderDiagnostics(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var severity = diagnostic.Severity == DiagnosticSeverity.Error
                ? "Ошибка"
                : "Предупреждение";
            _warnings.Items.Add(
                $"{severity}: {diagnostic.Line}:{diagnostic.Column} {diagnostic.Message}");
        }
    }

    private void SaveSettings()
    {
        SettingsChanged?.Invoke(new(
            _autoSidecar.Checked,
            _watchdog.Checked,
            (int)_watchdogInterval.Value,
            (long)_leadIn.Value,
            (long)_leadOut.Value,
            (long)_mergeGap.Value,
            (long)_durationTolerance.Value,
            (long)_earlyGuard.Value));
    }

    private void RepairSettings()
    {
        if (_settings.Schema == 1)
        {
            Warn("Файл настроек уже совместим с этой версией CensorPlayer.");
            return;
        }

        if (MessageBox.Show(
                this,
                "Файл settings.json создан более новой версией CensorPlayer. Перезаписать его настройками, которые сейчас показаны в окне? Неизвестные параметры будут удалены, а исходный файл останется в settings.json.bak.",
                "CensorPlayer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            SettingsRepairRequested?.Invoke();
        }
    }

    private void PopulateSettings(ExtensionSettings settings)
    {
        _rendering = true;
        try
        {
            _autoSidecar.Checked = settings.AutoLoadSidecar;
            _watchdog.Checked = settings.WatchdogEnabled;
            _watchdogInterval.Value = settings.WatchdogIntervalMs;
            _leadIn.Value = settings.LeadInMs;
            _leadOut.Value = settings.LeadOutMs;
            _mergeGap.Value = settings.MergeGapMs;
            _durationTolerance.Value = settings.DurationToleranceMs;
            _earlyGuard.Value = settings.EarlyIntervalGuardMs;
        }
        finally
        {
            _rendering = false;
        }
    }

    private void SaveRecovery()
    {
        try
        {
            if (_draft is not null)
            {
                DraftRecoveryStore.SaveOrDelete(
                    _recoveryPath,
                    _draft.Document,
                    _draft.IsDirty);
            }
        }
        catch (Exception exception)
        {
            _draftState.Text =
                $"Не удалось сохранить файл восстановления: {exception.Message}";
        }
    }

    private void SaveUiState()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            UiStateStore.Save(
                _uiStatePath,
                new(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
        }
        catch (Exception exception)
        {
            _draftState.Text =
                $"Не удалось сохранить положение окна: {exception.Message}";
        }
    }

    private void OfferRecovery()
    {
        var recovered = DraftRecoveryStore.Load(_recoveryPath);
        if (recovered.Status == DraftRecoveryStatus.NotFound)
            return;
        if (recovered.Status == DraftRecoveryStatus.Unreadable)
        {
            Warn(recovered.Warning ?? "Не удалось прочитать несохранённый черновик.");
            return;
        }
        var restore = MessageBox.Show(
            this,
            "Найден несохранённый черновик. Восстановить его?",
            "CensorPlayer",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;
        if (restore)
        {
            _draft = new(recovered.Document!);
            _draft.MarkDirty();
            _draftState.Text = "Восстановлен несохранённый черновик";
            RenderDraft();
        }
        else
        {
            DraftRecoveryStore.Delete(_recoveryPath);
        }
    }

    private void EnsureDraft()
    {
        _draft ??= new(new(new(), [], []));
    }

    private void SelectSchedule()
    {
        if (_draft?.IsDirty == true)
        {
            Warn("Сначала сохраните или отбросьте текущий черновик.");
            return;
        }
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter =
                $"Расписания цензуры|*{ScheduleFileKinds.CanonicalSuffix};" +
                $"*{ScheduleFileKinds.SrtSuffix};*{ScheduleFileKinds.WebVttSuffix}|" +
                $"Censor TXT|*{ScheduleFileKinds.CanonicalSuffix}|" +
                $"SubRip|*{ScheduleFileKinds.SrtSuffix}|" +
                $"WebVTT|*{ScheduleFileKinds.WebVttSuffix}",
            InitialDirectory = _settings.RememberLastScheduleDirectory
                ? _settings.LastScheduleDirectory
                : null,
            Multiselect = false,
            Title = "Загрузить расписание цензуры",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (!ConfirmSubtitleImport(dialog.FileName))
            return;
        if (_settings.RememberLastScheduleDirectory)
        {
            _settings = _settings with
            {
                LastScheduleDirectory = Path.GetDirectoryName(dialog.FileName),
            };
        }
        ScheduleSelected?.Invoke(dialog.FileName);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = _draft?.IsDirty != true &&
            TryGetDroppedSchedule(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_draft?.IsDirty == true)
        {
            Warn("Сначала сохраните или отбросьте текущий черновик.");
            return;
        }
        if (TryGetDroppedSchedule(e.Data, out var path) && ConfirmSubtitleImport(path))
            ScheduleSelected?.Invoke(path);
    }

    private void DiscardDraft()
    {
        if (_draft?.IsDirty == true &&
            MessageBox.Show(
                this,
                "Отбросить несохранённые изменения?",
                "CensorPlayer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        _draft = null;
        _sourceIntervals = null;
        DraftRecoveryStore.Delete(_recoveryPath);
        RenderDraft();
        ReloadRequested?.Invoke();
    }

    private static bool TryGetDroppedSchedule(IDataObject? data, out string path)
    {
        path = "";
        if (data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return false;
        path = files[0];
        return ScheduleFileKinds.IsSupportedPath(path);
    }

    private bool ConfirmSubtitleImport(string path)
    {
        if (!ScheduleFileKinds.TryGetSubtitleFormat(path, out _))
            return true;

        return MessageBox.Show(
            this,
            "Этот файл субтитров будет использован как расписание размытия: каждый фрагмент станет отдельным интервалом. Продолжить?",
            "CensorPlayer",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private int[] SelectedIndices() =>
        _intervals.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Index)
            .Distinct()
            .Order()
            .ToArray();

    private int? SelectedIndex() =>
        _intervals.CurrentCell is { RowIndex: >= 0 } cell ? cell.RowIndex : null;

    private static (AudioCompressionPresetDefinition Preset, int Index) ResolveAudioPreset(
        string? presetId)
    {
        var preset = AudioCompressionPresets.Find(presetId) ??
            AudioCompressionPresets.Find(AudioCompressionPresets.OffId)!;
        for (var index = 0; index < AudioCompressionPresets.All.Count; index++)
        {
            if (AudioCompressionPresets.All[index].Id == preset.Id)
                return (preset, index);
        }
        return (AudioCompressionPresets.All[0], 0);
    }

    private void SelectRow(int index)
    {
        if (index < 0 || index >= _intervals.Rows.Count)
            return;
        _intervals.ClearSelection();
        _intervals.Rows[index].Selected = true;
        _intervals.CurrentCell = _intervals.Rows[index].Cells["start"];
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Dock = DockStyle.Fill,
            MultiSelect = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        grid.Columns.Add("number", "№");
        grid.Columns.Add("start", "Начало");
        grid.Columns.Add("end", "Конец");
        grid.Columns.Add("duration", "Длительность");
        grid.Columns.Add("note", "Примечание");
        grid.Columns.Add("status", "Состояние");
        grid.Columns["number"]!.ReadOnly = true;
        grid.Columns["duration"]!.ReadOnly = true;
        grid.Columns["status"]!.ReadOnly = true;
        return grid;
    }

    private static Label ValueLabel() =>
        new() { AutoEllipsis = true, Dock = DockStyle.Fill };

    private static NumericUpDown Milliseconds(long minimum, long maximum) =>
        new()
        {
            Minimum = minimum,
            Maximum = maximum,
            ThousandsSeparator = true,
            Width = 130,
        };

    private static Button Button(string text, Action action)
    {
        var button = new Button { AutoSize = true, Text = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static void AddRow(
        TableLayoutPanel layout,
        int row,
        string label,
        Control value)
    {
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new(3, 7, 8, 3),
            Text = label,
        }, 0, row);
        layout.Controls.Add(value, 1, row);
    }

    private int EnsureBlurPreset(BlurSettings settings)
    {
        var index = _blurPresets.FindIndex(item => item.Settings == settings);
        if (index >= 0)
            return index;

        _blurPresets.Add((
            FormattableString.Invariant(
                $"Пользовательское ({settings.Sigma:0.##} / {settings.Steps})"),
            settings));
        return _blurPresets.Count - 1;
    }

    private static string LocalizeStatus(string status) =>
        status switch
        {
            "ACTIVE" => "Активно",
            "APPLYING" => "Применение",
            "DISABLED" => "Отключено",
            "DURATION MISMATCH" => "Длительность не совпадает",
            "EMPTY SCHEDULE" => "Расписание пусто",
            "ERROR" => "Ошибка",
            "IDLE" => "Ожидание",
            "INVALID DRAFT" => "В черновике есть ошибки",
            "INVALID SCHEDULE PATH" => "Некорректный путь к расписанию",
            "LOADING" => "Загрузка",
            "LOOKING FOR SIDECAR" => "Поиск расписания рядом с фильмом",
            "NO CURRENT MEDIA" => "Нет открытого фильма",
            "NO LOCAL MEDIA" => "Открыт не локальный файл",
            "NO SCHEDULE" => "Расписание не выбрано",
            "NO SCHEDULE TO APPLY" => "Нет расписания для применения",
            "NO SCHEDULE TO RELOAD" => "Нет расписания для перезагрузки",
            "OPERATION UNAVAILABLE" => "Операция недоступна",
            "READY TO APPLY" => "Готово к применению",
            "SAVED" => "Сохранено",
            "SELECT SIDECAR" => "Выберите файл расписания",
            "WARNING" => "Требуется внимание",
            _ => status,
        };

    private static string FormatTimestamp(long milliseconds)
    {
        var negative = milliseconds < 0;
        var magnitude = Math.Abs((decimal)milliseconds);
        var hours = decimal.ToInt64(decimal.Truncate(magnitude / 3_600_000));
        var minutes = decimal.ToInt32(decimal.Truncate(magnitude / 60_000) % 60);
        var seconds = decimal.ToInt32(decimal.Truncate(magnitude / 1_000) % 60);
        var millis = decimal.ToInt32(magnitude % 1_000);
        return FormattableString.Invariant(
            $"{(negative ? "-" : "")}{hours:D2}:{minutes:D2}:{seconds:D2}.{millis:D3}");
    }

    private static string FormatDuration(long milliseconds) =>
        FormattableString.Invariant($"{milliseconds / 1_000}.{milliseconds % 1_000:D3} с");

    private void Warn(string message) =>
        MessageBox.Show(this, message, "CensorPlayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private sealed record SidecarChoice(string Name, string Path);
}
