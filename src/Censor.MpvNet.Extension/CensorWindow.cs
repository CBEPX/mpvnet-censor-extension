using System.ComponentModel;
using System.Globalization;
using Censor.Core;

namespace Censor.MpvNet.Extension;

internal sealed record AuthoringSnapshot(
    ScheduleDocument Document,
    string? SourcePath,
    string? SourceHash,
    string? MediaPath,
    long? MediaSessionId);

internal enum AuthoringSaveMode
{
    Save,
    SaveAs,
    ExportSrt,
    ExportWebVtt,
}

internal sealed class CensorWindow : Form
{
    private const string UnsavedDraftActionMessage =
        "Сначала сохраните или удалите несохранённые изменения.";
    private const string TransferableDraftActionMessage =
        "Сначала сохраните изменения, используйте их для открытого фильма или удалите.";

    // Draft reconciliation uses reference identity to recognize unchanged empty state.
    private static readonly IReadOnlyList<CensorInterval> EmptyIntervals =
        Array.Empty<CensorInterval>();

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
    private readonly Label _filterState = new() { AutoSize = true };
    private readonly Label _draftState = new() { AutoSize = true };
    private readonly Label _authoringNotice = new()
    {
        AccessibleName = "Сообщение редактора",
        AutoSize = true,
        ForeColor = Color.DarkGoldenrod,
        Visible = false,
    };
    private readonly Label _mediaHeading = new()
    {
        AutoEllipsis = true,
        AutoSize = true,
        Font = new("Segoe UI", 11, FontStyle.Bold),
        Text = "Фильм не открыт",
    };
    private readonly Label _intervalCount = new() { AutoSize = true };
    private readonly Label _detachedMessage = new()
    {
        AutoEllipsis = true,
        AutoSize = true,
        ForeColor = Color.DarkGoldenrod,
    };
    private readonly Label _emptyState = new()
    {
        Dock = DockStyle.Fill,
        Text = "Откройте фильм, чтобы добавить интервалы.",
        TextAlign = ContentAlignment.MiddleCenter,
    };
    private readonly ListBox _warnings = new()
    {
        Dock = DockStyle.Top,
        Height = 64,
        IntegralHeight = false,
        Visible = false,
    };
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
        Text = "Автоматически загружать файл интервалов рядом с фильмом",
    };
    private readonly CheckBox _watchdog = new()
    {
        AutoSize = true,
        Text = "Автоматически восстанавливать размытие и компрессию звука",
    };
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
        Text = "Добавить файл интервалов в диагностический ZIP-архив",
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
    private IReadOnlyList<CensorInterval>? _runtimeIntervals;
    private IReadOnlyList<ParseDiagnostic> _draftDiagnostics = [];
    private IReadOnlyList<ParseDiagnostic> _runtimeDiagnostics = [];
    private Button _addButton = null!;
    private Button _advancedToggleButton = null!;
    private Button _applyButton = null!;
    private Button _deleteButton = null!;
    private Button _markEndButton = null!;
    private Button _markStartButton = null!;
    private Button _saveButton = null!;
    private Button _saveDetachedButton = null!;
    private Button _useForCurrentButton = null!;
    private FlowLayoutPanel _advancedActions = null!;
    private FlowLayoutPanel _detachedActions = null!;
    private FlowLayoutPanel _primaryActions = null!;
    private bool _allowClose;
    private bool _rendering;
    private long? _pendingStartMs;
    private string? _mediaPath;
    private long _mediaSessionId;
    private long? _mediaDurationMs;
    private string? _draftMediaPath;
    private long? _draftMediaSessionId;
    private string? _sourcePath;
    private string? _sourceHash;
    private string? _runtimeSchedulePath;
    private string? _runtimeSourceHash;
    private ScheduleDocument? _runtimeDocument;
    private ScheduleDocument? _runtimeActiveDocument;

    public CensorWindow(ExtensionSettings settings, string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        localDataRoot = Path.GetFullPath(localDataRoot);
        _settings = settings;
        _recoveryPath = Path.Combine(localDataRoot, "Recovery", "draft.json");
        _uiStatePath = Path.Combine(localDataRoot, "UiState.json");
        Text = "CensorPlayer — редактор цензуры";
        AccessibleName = Text;
        MinimumSize = new(UiStateStore.MinimumWidth, UiStateStore.MinimumHeight);
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
        tabs.TabPages.Add(new TabPage("Интервалы") { Controls = { BuildIntervalsTab() } });
        tabs.TabPages.Add(new TabPage("Настройки") { Controls = { BuildSettingsTab() } });
        tabs.TabPages.Add(new TabPage("Диагностика") { Controls = { BuildDiagnosticsTab() } });
        Controls.Add(tabs);

        _intervals.CellEndEdit += OnCellEndEdit;
        _intervals.SelectionChanged += (_, _) =>
        {
            if (!_rendering)
                ClearAuthoringNotification();
            UpdateActionStates();
        };
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
    }

    public event Action<string>? ScheduleSelected;
    public event Action<AuthoringSnapshot>? ApplyRequested;
    public event Action<AuthoringSnapshot, AuthoringSaveMode>? SaveRequested;
    public event Action? ReloadRequested;
    public event Action? DisableRequested;
    public event Action<BlurSettings>? BlurPresetSelected;
    public event Action<string>? AudioCompressionPresetSelected;
    public event Action<SettingsFormValues>? SettingsChanged;
    public event Action<long>? SeekRequested;
    public event Action<bool>? DiagnosticsRequested;
    public event Action? SettingsRepairRequested;
    public event Action<string, Exception>? PersistenceError;
    public event Action<string>? AuthoringNotificationRequested;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<long?>? CurrentTimeRequested { get; set; }
    // Loader-smoke seam: fail on an unexpected warning or confirmation dialog.
    // Keep a property: no assignment here, and CS0649-on-fields is an error.
    // The smoke assigns it by name via reflection.
    private Action<string>? WarningSink { get; set; }

    public void UpdateState(
        string? mediaPath,
        long mediaSessionId,
        string? schedulePath,
        string? sourceHash,
        string status,
        long? mediaDurationMs,
        ScheduleDocument? document,
        ScheduleDocument? activeDocument,
        IReadOnlyList<ParseDiagnostic> diagnostics,
        bool hasActiveSchedule)
    {
        var intervals = document?.Intervals ?? EmptyIntervals;
        var mediaChanged = _mediaSessionId != mediaSessionId ||
            !MediaPathsEqual(_mediaPath, mediaPath);
        var runtimeIntervalsUnchanged = ReferenceEquals(_runtimeIntervals, intervals);
        if (mediaChanged || !runtimeIntervalsUnchanged)
            CommitCurrentCellEdit();
        var keepDetachedDraft = mediaChanged && _draft?.IsDirty == true;
        if (mediaChanged)
        {
            _mediaPath = mediaPath;
            _pendingStartMs = null;
        }
        _mediaSessionId = mediaSessionId;
        _mediaDurationMs = mediaDurationMs;
        _runtimeSchedulePath = schedulePath;
        _runtimeSourceHash = sourceHash;
        _runtimeDocument = document;
        _runtimeActiveDocument = activeDocument;
        _runtimeIntervals = intervals;
        _media.Text = string.IsNullOrEmpty(mediaPath) ? "Нет открытого фильма" : mediaPath;
        _mediaHeading.Text = string.IsNullOrEmpty(mediaPath)
            ? "Фильм не открыт"
            : GetMediaTitle(mediaPath) ?? mediaPath;
        _schedule.Text = string.IsNullOrEmpty(schedulePath) ? "Не выбран" : schedulePath;
        var localizedStatus = LocalizeStatus(status);
        _status.Text = localizedStatus;
        _duration.Text = mediaDurationMs.HasValue
            ? FormatTimestamp(mediaDurationMs.Value)
            : "Неизвестна";
        _filterState.Text = status == "WARNING"
            ? "Размытие: требуется внимание"
            : hasActiveSchedule
                ? "Размытие: включено"
                : "Размытие: выключено";
        _diagnostics.Text =
            $"Статус: {localizedStatus}{Environment.NewLine}" +
            $"Фильм: {_media.Text}{Environment.NewLine}" +
            $"Длительность: {_duration.Text}{Environment.NewLine}" +
            $"Файл интервалов: {_schedule.Text}{Environment.NewLine}" +
            $"Фильтры: {_filterState.Text}{Environment.NewLine}" +
            $"Интервалов: {intervals.Count}" +
            (diagnostics.Count == 0
                ? ""
                : Environment.NewLine + Environment.NewLine +
                  string.Join(
                      Environment.NewLine,
                      diagnostics.Select(item =>
                          $"{item.Severity}: {item.Line}:{item.Column} {item.Message}")));
        _runtimeDiagnostics = diagnostics;

        if (mediaChanged)
        {
            if (keepDetachedDraft)
                RenderDraft();
            else
                ReplaceDraftFromRuntime();
            return;
        }
        if (HasDetachedDraft())
        {
            RenderDraft();
            return;
        }

        var reconciliation = DraftReconciliation.Decide(
            runtimeIntervalsUnchanged,
            _draft?.IsDirty == true,
            document is not null && _draft?.Matches(document) == true);
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
            _sourcePath = schedulePath;
            _sourceHash = sourceHash;
            _draftMediaPath = mediaPath;
            _draftMediaSessionId = mediaSessionId;
            RenderDraft();
            return;
        }
        if (reconciliation == DraftReconciliationAction.KeepDirtyDraft)
        {
            RenderDraft();
            return;
        }

        ReplaceDraftFromRuntime();
    }

    public bool ConfirmDurationMismatch() =>
        ShowConfirmation(
            "Длительность в файле интервалов отличается от фильма. Применить всё равно?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public DialogResult ConfirmExternalChange() =>
        ShowConfirmation(
            "Файл изменён другой программой.\n\nДа — перезаписать, Нет — сохранить как новый, Отмена — ничего не делать.",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button3);

    public bool ConfirmSubtitleExport() =>
        ShowConfirmation(
            "SRT и WebVTT сохраняют только интервалы и текст. Название, смещение, запас до и после интервала и служебные строки в экспорт не попадут.\n\nЭкспортировать копию? Несохранённые изменения останутся без изменений.",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public bool MarkSaved(
        string path,
        string sourceHash,
        ScheduleDocument savedDocument,
        string? lastScheduleDirectory)
    {
        // Extension already persisted this directory; keep the picker synchronized
        // even when a newer edit prevents this saved snapshot from marking it clean.
        _settings = _settings with { LastScheduleDirectory = lastScheduleDirectory };
        var wasDetached = HasDetachedDraft();
        if (_draft?.MarkSaved(savedDocument) != true)
        {
            SaveRecovery();
            return false;
        }

        DraftRecoveryStore.Delete(_recoveryPath);
        if (wasDetached)
        {
            ReplaceDraftFromRuntime();
            return true;
        }

        _sourcePath = path;
        _sourceHash = sourceHash;
        _schedule.Text = path;
        RenderDraft();
        return true;
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
        SetBlurPreset(settings.Blur);
        SetAudioCompressionPreset(settings.AudioCompressionPreset);
        PopulateSettings(_settings);
    }

    public void HandleAuthoringCommand(string command, long? capturedTimeMs = null)
    {
        CommitCurrentCellEdit();
        if (command is "mark-start" or "mark-end" or "set-start" or "set-end")
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        switch (command)
        {
            case "mark-start":
                if (!CanEditCurrentMedia())
                {
                    NotifyCannotEdit();
                    break;
                }
                _pendingStartMs = capturedTimeMs ?? CurrentTimeRequested?.Invoke();
                if (_pendingStartMs.HasValue)
                {
                    ClearAuthoringNotification();
                    _draftState.Text = $"Начало отмечено: {FormatTimestamp(_pendingStartMs.Value)}";
                }
                else
                    NotifyAuthoring("Текущая позиция воспроизведения недоступна.");
                UpdateActionStates();
                break;
            case "mark-end":
                if (!CanEditCurrentMedia())
                {
                    NotifyCannotEdit();
                    break;
                }
                if (_pendingStartMs is not { } start)
                {
                    NotifyAuthoring("Сначала отметьте начало интервала.");
                    break;
                }
                if ((capturedTimeMs ?? CurrentTimeRequested?.Invoke()) is not { } end)
                {
                    NotifyAuthoring(
                        "Текущая позиция воспроизведения недоступна. Повторите после завершения операции.");
                    break;
                }
                if (end <= start)
                {
                    NotifyAuthoring("Конец интервала должен быть позже отмеченного начала.");
                    break;
                }
                if (!EnsureDraft())
                    break;
                _draft!.Add(start, end);
                _pendingStartMs = null;
                Changed(selectedIndex: _draft.Document.Intervals.Count - 1);
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
                SaveDraft(AuthoringSaveMode.Save);
                break;
            case "apply":
                ApplyDraft();
                break;
        }
    }

    public string? ChooseSavePath(string? currentPath, string? suggestedPath)
    {
        var initialPath = string.IsNullOrWhiteSpace(currentPath) ? suggestedPath : currentPath;
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "censor.txt",
            FileName = string.IsNullOrWhiteSpace(initialPath)
                ? "schedule" + ScheduleFileKinds.CanonicalSuffix
                : Path.GetFileName(initialPath),
            Filter = $"Файл интервалов CensorPlayer|*{ScheduleFileKinds.CanonicalSuffix}",
            InitialDirectory = ResolveInitialDirectory(initialPath),
            OverwritePrompt = true,
            Title = "Сохранить файл интервалов",
        };
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (ScheduleFileKinds.IsCanonicalPath(dialog.FileName))
                return dialog.FileName;

            Warn("Имя файла должно оканчиваться на .censor.txt.");
        }
        return null;
    }

    public string? ChooseExportPath(string? mediaPath, SubtitleFormat format)
    {
        var extension = format == SubtitleFormat.Srt
            ? ScheduleFileKinds.SrtSuffix
            : ScheduleFileKinds.WebVttSuffix;
        var suggested = SidecarLocator.SuggestCanonicalPath(mediaPath);
        var fileName = suggested is null
            ? "schedule" + extension
            : Path.GetFileName(suggested)[..^ScheduleFileKinds.CanonicalSuffix.Length] + extension;
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = extension[1..],
            FileName = fileName,
            Filter = format == SubtitleFormat.Srt ? "SubRip|*.srt" : "WebVTT|*.vtt",
            InitialDirectory = ResolveInitialDirectory(suggested),
            OverwritePrompt = true,
            Title = "Экспортировать копию",
        };
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (dialog.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return dialog.FileName;
            Warn($"Имя файла должно оканчиваться на {extension}.");
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
            Text = "Выберите файл интервалов",
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
        CommitCurrentCellEdit();
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

    private TableLayoutPanel BuildIntervalsTab()
    {
        _addButton = Button("Добавить вручную", AddInterval);
        _markStartButton = Button("Отметить начало (F7)", () =>
            HandleAuthoringCommand("mark-start"));
        _markEndButton = Button("Отметить конец (F8)", () =>
            HandleAuthoringCommand("mark-end"));
        _deleteButton = Button("Удалить", DeleteSelected);
        _applyButton = Button("Применить интервалы", ApplyDraft);
        _saveButton = Button("Сохранить файл", () => SaveDraft(AuthoringSaveMode.Save));
        _primaryActions = Flow(
            _addButton,
            _deleteButton,
            Button("Отменить", Undo),
            Button("Повторить", Redo),
            Button("Перейти к началу", GoToSelectedStart),
            _applyButton,
            _saveButton);

        _advancedActions = Flow(
            _markStartButton,
            _markEndButton,
            Button("Импортировать файл…", SelectSchedule),
            Button("Перезагрузить файл", () => ReloadRequested?.Invoke()),
            Button("Дублировать", DuplicateSelected),
            Button("Объединить", MergeSelected),
            Button("Разделить", SplitSelected),
            Button("Начало = текущая позиция", () => CaptureBoundary(start: true)),
            Button("Конец = текущая позиция", () => CaptureBoundary(start: false)),
            Button("Начало −100", () => ShiftSelected(start: true, -100)),
            Button("Начало +100", () => ShiftSelected(start: true, 100)),
            Button("Конец −100", () => ShiftSelected(start: false, -100)),
            Button("Конец +100", () => ShiftSelected(start: false, 100)),
            Button("Предыдущий", () => NavigateInterval(-1)),
            Button("Следующий", () => NavigateInterval(1)),
            Button("Выключить размытие", () => DisableRequested?.Invoke()),
            new Label { AutoSize = true, Margin = new(3, 8, 3, 0), Text = "Смещение, мс:" },
            _offset,
            new Label { AutoSize = true, Margin = new(12, 8, 3, 0), Text = "Сдвиг всех интервалов, мс:" },
            _shiftAll,
            Button("Сдвинуть", ShiftAll),
            Button("Сохранить как…", () => SaveDraft(AuthoringSaveMode.SaveAs)),
            Button("Экспортировать SRT…", () => SaveDraft(AuthoringSaveMode.ExportSrt)),
            Button("Экспортировать WebVTT…", () => SaveDraft(AuthoringSaveMode.ExportWebVtt)),
            Button("Удалить несохранённые изменения", DiscardDraft));
        _advancedActions.Visible = false;
        _advancedToggleButton = Button("Дополнительно ▾", () =>
        {
            _advancedActions.Visible = !_advancedActions.Visible;
            _advancedToggleButton.Text = _advancedActions.Visible
                ? "Дополнительно ▴"
                : "Дополнительно ▾";
        });
        _primaryActions.Controls.Add(_advancedToggleButton);

        _saveDetachedButton = Button(
            "Сохранить изменения",
            () => SaveDraft(AuthoringSaveMode.Save));
        _useForCurrentButton = Button(
            "Использовать для текущего фильма",
            UseDraftForCurrentMedia);
        _detachedActions = Flow(
            _detachedMessage,
            _saveDetachedButton,
            _useForCurrentButton,
            Button("Удалить", DiscardDraft));
        _detachedActions.BackColor = Color.LemonChiffon;
        _detachedActions.Padding = new(6);
        _detachedActions.Visible = false;

        var header = Flow(
            _mediaHeading,
            _intervalCount,
            _filterState,
            _draftState,
            _authoringNotice);
        header.Padding = new(3, 3, 3, 0);

        var gridHost = new Panel { Dock = DockStyle.Fill };
        gridHost.Controls.Add(_intervals);
        gridHost.Controls.Add(_emptyState);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new(8),
            RowCount = 6,
        };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_detachedActions, 0, 1);
        layout.Controls.Add(_warnings, 0, 2);
        layout.Controls.Add(_primaryActions, 0, 3);
        layout.Controls.Add(_advancedActions, 0, 4);
        layout.Controls.Add(gridHost, 0, 5);
        return layout;
    }

    private TableLayoutPanel BuildSettingsTab()
    {
        var advanced = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
        };
        AddRow(advanced, 0, "Запас перед началом, мс:", _leadIn);
        AddRow(advanced, 1, "Запас после конца, мс:", _leadOut);
        AddRow(advanced, 2, "Порог объединения, мс:", _mergeGap);
        AddRow(advanced, 3, "Допуск длительности, мс:", _durationTolerance);
        AddRow(advanced, 4, "Защитный запас до интервала, мс:", _earlyGuard);
        AddRow(advanced, 5, "Период проверки размытия, мс:", _watchdogInterval);
        advanced.Visible = false;
        Button advancedToggle = null!;
        advancedToggle = Button("Расширенные параметры ▾", () =>
        {
            advanced.Visible = !advanced.Visible;
            advancedToggle.Text = advanced.Visible
                ? "Расширенные параметры ▴"
                : "Расширенные параметры ▾";
        });

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new(12),
            AutoSize = true,
        };
        AddRow(layout, 0, "Размытие:", _blurPreset);
        AddRow(layout, 1, "Компрессия звука:", _audioCompressionPreset);
        layout.Controls.Add(_autoSidecar, 1, 2);
        layout.Controls.Add(_watchdog, 1, 3);
        layout.Controls.Add(advancedToggle, 1, 4);
        layout.Controls.Add(advanced, 1, 5);
        layout.Controls.Add(Button("Сохранить настройки", SaveSettings), 1, 6);
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
        if (!CanEditCurrentMedia())
        {
            NotifyCannotEdit();
            return;
        }
        CommitCurrentCellEdit();
        if (!EnsureDraft())
            return;
        var start = CurrentTimeRequested?.Invoke() ?? 0;
        _draft!.Add(start, checked(start + 1_000));
        Changed(selectedIndex: _draft.Document.Intervals.Count - 1);
        _intervals.Focus();
        _intervals.BeginEdit(selectAll: true);
    }

    private void DeleteSelected()
    {
        if (!TryGetEditableDraft(out var draft))
            return;
        var selected = SelectedIndices();
        if (selected.Length == 0)
        {
            NotifyAuthoring("Сначала выберите интервал.");
            return;
        }
        draft.Delete(selected);
        var next = draft.Document.Intervals.Count == 0
            ? (int?)null
            : Math.Min(selected[0], draft.Document.Intervals.Count - 1);
        Changed(selectedIndex: next);
    }

    private void DuplicateSelected()
    {
        if (!TryGetEditableInterval(out var index))
            return;
        _draft!.Duplicate(index);
        Changed(selectedIndex: index + 1);
    }

    private void MergeSelected()
    {
        if (!TryGetEditableDraft(out var draft))
            return;
        var selected = SelectedIndices();
        if (selected.Length == 0)
        {
            NotifyAuthoring("Сначала выберите интервалы.");
            return;
        }
        try
        {
            draft.Merge(selected);
            Changed(selectedIndex: selected[0]);
        }
        catch (ArgumentException exception)
        {
            NotifyAuthoring(exception.Message);
        }
    }

    private void SplitSelected()
    {
        if (!TryGetEditableInterval(out var index))
            return;
        if (CurrentTimeRequested?.Invoke() is not { } position)
        {
            NotifyAuthoring(
                "Текущая позиция воспроизведения недоступна. Повторите после завершения операции.");
            return;
        }
        try
        {
            _draft!.Split(index, position);
            Changed(selectedIndex: index);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            NotifyAuthoring(exception.Message);
        }
    }

    private void CaptureBoundary(bool start, long? capturedTimeMs = null)
    {
        if (!TryGetEditableInterval(out var index))
            return;
        if ((capturedTimeMs ?? CurrentTimeRequested?.Invoke()) is not { } position)
        {
            NotifyAuthoring(
                "Текущая позиция воспроизведения недоступна. Повторите после завершения операции.");
            return;
        }
        var interval = _draft!.Document.Intervals[index];
        _draft.Update(
            index,
            start ? position : interval.StartMs,
            start ? interval.EndMs : position,
            interval.Note);
        Changed(selectedIndex: index, renderSelectedOnly: true);
    }

    private void ShiftSelected(bool start, long delta)
    {
        if (!TryGetEditableInterval(out var index))
            return;
        try
        {
            _draft!.ShiftBoundary(index, start, delta);
            Changed(selectedIndex: index, renderSelectedOnly: true);
        }
        catch (OverflowException)
        {
            NotifyAuthoring("Граница вышла за допустимый диапазон.");
        }
    }

    private void ShiftAll()
    {
        if (!TryGetEditableDraft(out var draft))
            return;
        try
        {
            draft.ShiftAll((long)_shiftAll.Value);
            Changed();
        }
        catch (OverflowException)
        {
            NotifyAuthoring("Сдвиг вышел за допустимый диапазон.");
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
        if (!TryGetValidDraft(out var snapshot))
            return;
        ApplyRequested?.Invoke(snapshot);
    }

    private void SaveDraft(AuthoringSaveMode mode)
    {
        if (!TryGetValidDraft(out var snapshot))
            return;
        SaveRequested?.Invoke(snapshot, mode);
    }

    private bool TryGetValidDraft(out AuthoringSnapshot snapshot)
    {
        CommitCurrentCellEdit();
        snapshot = new(new(new(), [], []), null, null, null, null);
        if (_draft is null)
        {
            NotifyAuthoring("Сначала откройте фильм и создайте интервалы.");
            return false;
        }
        var diagnostics = _draft.Validate(
            _settings.Limits.MaxIntervals,
            _settings.Limits.MaxTextFileBytes);
        RenderDraftSummary(diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            NotifyAuthoring("Исправьте ошибки в интервалах перед применением или сохранением.");
            return false;
        }
        snapshot = new(
            _draft.Document,
            _sourcePath,
            _sourceHash,
            _draftMediaPath,
            _draftMediaSessionId);
        ClearAuthoringNotification();
        return true;
    }

    private void NavigateInterval(int direction)
    {
        if (!TryGetEditableDraft(out var draft))
            return;
        var current = SelectedIndex() ?? (direction > 0 ? -1 : 0);
        var next = Math.Clamp(current + direction, 0, draft.Document.Intervals.Count - 1);
        SelectRow(next);
        SeekRequested?.Invoke(draft.Document.Intervals[next].StartMs);
    }

    private void GoToSelectedStart()
    {
        if (!TryGetEditableInterval(out var index))
            return;
        SeekRequested?.Invoke(_draft!.Document.Intervals[index].StartMs);
    }

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_rendering || _draft is null || e.RowIndex < 0)
            return;
        if (e.RowIndex >= _draft.Document.Intervals.Count ||
            e.RowIndex >= _intervals.Rows.Count ||
            e.ColumnIndex < 0 ||
            e.ColumnIndex >= _intervals.Columns.Count ||
            _intervals.Rows.Count != _draft.Document.Intervals.Count)
        {
            RenderDraft();
            return;
        }
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
                "Введите время как ЧЧ:ММ:СС или ЧЧ:ММ:СС.ммм. Минус ставится перед часами.",
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
        var draft = _draft;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_draft, draft))
                    return;
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
        CommitCurrentCellEdit();
        // Visible is false with a hidden parent form; text records the pending notice.
        if (HasDetachedDraft() && _authoringNotice.Text.Length > 0)
            _authoringNotice.Text = GetDetachedDraftActionMessage();
        else
            ClearAuthoringNotification();
        _emptyState.Text = HasDetachedDraft()
            ? GetDetachedDraftActionMessage()
            : !HasCurrentMedia()
                ? "Откройте фильм, чтобы добавить интервалы."
                : "Интервалов пока нет. Нажмите «Добавить вручную» и укажите время сцены.";
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
                _draftState.Text = "Нет интервалов";
                _intervalCount.Text = "Интервалов: 0";
                _emptyState.Visible = true;
                _intervals.Visible = false;
                RenderWarnings([]);
                UpdateDetachedState();
                UpdateActionStates();
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
            // ponytail: Full rebuild targets normal 10–20-scene drafts; use
            // VirtualMode only if measured projects grow beyond that workload.
            for (var index = 0; index < intervals.Count; index++)
            {
                var rowIndex = _intervals.Rows.Add();
                PopulateIntervalRow(
                    _intervals.Rows[rowIndex],
                    index,
                    intervals[index],
                    errorRows.Contains(index));
            }
            _intervalCount.Text = $"Интервалов: {intervals.Count}";
            _emptyState.Visible = intervals.Count == 0;
            _intervals.Visible = intervals.Count > 0;
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
        {
            UpdateActionStates();
            return;
        }
        var validIndices = restoreIndices
            .Where(index => index >= 0 && index < _intervals.Rows.Count)
            .ToArray();
        if (validIndices.Length == 0)
        {
            UpdateActionStates();
            return;
        }
        var current = restoreCurrent.HasValue &&
            validIndices.Contains(restoreCurrent.Value)
                ? restoreCurrent.Value
                : validIndices[0];
        _rendering = true;
        try
        {
            _intervals.ClearSelection();
            foreach (var index in validIndices)
                _intervals.Rows[index].Selected = true;
            _intervals.CurrentCell = _intervals.Rows[current].Cells["start"];
        }
        finally
        {
            _rendering = false;
        }
        UpdateActionStates();
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
        var applied = _runtimeActiveDocument is not null &&
            _draft.Matches(_runtimeActiveDocument);
        var saved = !_draft.IsDirty && !string.IsNullOrWhiteSpace(_sourcePath);
        _draftState.Text = $"{(applied ? "Применено" : "Не применено")} · " +
            $"{(saved ? "Сохранено" : "Не сохранено")}";
        UpdateDetachedState();
        UpdateActionStates();
    }

    private bool HasDetachedDraft() =>
        _draft is not null &&
        !DraftReconciliation.BelongsToCurrentSession(
            _draftMediaPath,
            _draftMediaSessionId,
            _mediaPath,
            _mediaSessionId);

    private void RenderWarnings(IReadOnlyList<ParseDiagnostic> diagnostics)
    {
        _warnings.Items.Clear();
        foreach (var item in diagnostics
            .Select(diagnostic => (Diagnostic: diagnostic, Location: "интервал"))
            .Concat(_runtimeDiagnostics.Select(
                diagnostic => (Diagnostic: diagnostic, Location: "строка файла")))
            .Distinct()
            .OrderBy(item => item.Diagnostic.Line <= 0
                ? int.MaxValue
                : item.Diagnostic.Line))
        {
            var diagnostic = item.Diagnostic;
            var severity = diagnostic.Severity == DiagnosticSeverity.Error
                ? "Ошибка"
                : "Предупреждение";
            _warnings.Items.Add(diagnostic.Line > 0
                ? $"{severity}, {item.Location} {diagnostic.Line}: {diagnostic.Message}"
                : $"{severity}: {diagnostic.Message}");
        }
        _warnings.Visible = _warnings.Items.Count > 0;
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

        if (ShowConfirmation(
                "Файл settings.json создан более новой версией CensorPlayer. Перезаписать его настройками, которые сейчас показаны в окне? Неизвестные параметры будут удалены, а исходный файл останется в settings.json.pre-repair.",
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
            const string Message = "Не удалось сохранить файл восстановления.";
            _draftState.Text = Message;
            PersistenceError?.Invoke(Message, exception);
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
            const string Message = "Не удалось сохранить положение окна.";
            _draftState.Text = Message;
            PersistenceError?.Invoke(Message, exception);
        }
    }

    public void OfferRecoveryIfAvailable()
    {
        var recovered = DraftRecoveryStore.Load(_recoveryPath);
        if (recovered.Status == DraftRecoveryStatus.NotFound)
            return;
        if (recovered.Status == DraftRecoveryStatus.Unreadable)
        {
            Warn(recovered.Warning ?? "Не удалось прочитать несохранённые изменения.");
            return;
        }
        var restore = ShowConfirmation(
            "Найдены несохранённые изменения. Восстановить их?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;
        if (restore)
        {
            CommitCurrentCellEdit();
            _draft = new(recovered.Document!);
            _draft.MarkDirty();
            _sourcePath = null;
            _sourceHash = null;
            _draftMediaPath = null;
            _draftMediaSessionId = null;
            _schedule.Text = "Восстановленные изменения не связаны с файлом";
            RenderDraft();
        }
        else
        {
            DraftRecoveryStore.Delete(_recoveryPath);
        }
    }

    private bool EnsureDraft()
    {
        if (!HasCurrentMedia() || HasDetachedDraft())
            return false;
        if (_draft is null)
        {
            _draft = new(CreateNewDocument(_mediaPath!, _mediaDurationMs));
            _draftMediaPath = _mediaPath;
            _draftMediaSessionId = _mediaSessionId;
        }
        return true;
    }

    private void SelectSchedule()
    {
        CommitCurrentCellEdit();
        if (_draft?.IsDirty == true)
        {
            Warn(UnsavedDraftActionMessage);
            return;
        }
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter =
                $"Файлы интервалов|*{ScheduleFileKinds.CanonicalSuffix};" +
                $"*{ScheduleFileKinds.SrtSuffix};*{ScheduleFileKinds.WebVttSuffix}|" +
                $"Censor TXT|*{ScheduleFileKinds.CanonicalSuffix}|" +
                $"SubRip|*{ScheduleFileKinds.SrtSuffix}|" +
                $"WebVTT|*{ScheduleFileKinds.WebVttSuffix}",
            InitialDirectory = _settings.RememberLastScheduleDirectory
                ? _settings.LastScheduleDirectory
                : null,
            Multiselect = false,
            Title = "Импортировать файл интервалов",
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
        CommitCurrentCellEdit();
        if (_draft?.IsDirty == true)
        {
            Warn(UnsavedDraftActionMessage);
            return;
        }
        if (TryGetDroppedSchedule(e.Data, out var path) && ConfirmSubtitleImport(path))
            ScheduleSelected?.Invoke(path);
    }

    private void DiscardDraft()
    {
        CommitCurrentCellEdit();
        if (_draft?.IsDirty == true &&
            ShowConfirmation(
                "Удалить несохранённые изменения?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }
        var wasDetached = HasDetachedDraft();
        DraftRecoveryStore.Delete(_recoveryPath);
        if (wasDetached)
        {
            ReplaceDraftFromRuntime();
            return;
        }

        _draft = null;
        _sourcePath = null;
        _sourceHash = null;
        _draftMediaPath = null;
        _draftMediaSessionId = null;
        if (_runtimeSchedulePath is null)
            ReplaceDraftFromRuntime();
        else
        {
            RenderDraft();
            ReloadRequested?.Invoke();
        }
    }

    private void UseDraftForCurrentMedia()
    {
        if (_draft is null || !HasCurrentMedia() || !HasDetachedDraft())
            return;

        _draft.RetargetMedia(GetMediaTitle(_mediaPath!), _mediaDurationMs);
        _draftMediaPath = _mediaPath;
        _draftMediaSessionId = _mediaSessionId;
        _sourcePath = null;
        _sourceHash = null;
        Changed();
    }

    private void ReplaceDraftFromRuntime()
    {
        CommitCurrentCellEdit();
        _pendingStartMs = null;
        _sourcePath = _runtimeSchedulePath;
        _sourceHash = _runtimeSourceHash;
        _draftMediaPath = _mediaPath;
        _draftMediaSessionId = HasCurrentMedia() ? _mediaSessionId : null;
        _draft = !HasCurrentMedia()
            ? null
            : new(_runtimeDocument ?? CreateNewDocument(_mediaPath!, _mediaDurationMs));
        RenderDraft();
    }

    private void UpdateDetachedState()
    {
        var detached = HasDetachedDraft();
        _detachedActions.Visible = detached;
        if (!detached)
            return;

        var owner = GetMediaTitle(_draftMediaPath);
        var current = GetMediaTitle(_mediaPath);
        _detachedMessage.Text = owner is null
            ? "Восстановленные изменения не связаны с открытым фильмом."
            : $"Есть несохранённые изменения для «{owner}». " +
              (current is null ? "Сейчас фильм не открыт." : $"Сейчас открыт «{current}».");
        _saveDetachedButton.Text = owner is null
            ? "Сохранить файл"
            : $"Сохранить для «{owner}»";
        _useForCurrentButton.Text = current is null
            ? "Использовать для текущего фильма"
            : $"Использовать для «{current}»";
        _useForCurrentButton.Enabled = HasCurrentMedia();
    }

    private void UpdateActionStates()
    {
        if (_primaryActions is null)
            return;

        var canEditCurrent = CanEditCurrentMedia();
        if (!canEditCurrent)
        {
            _advancedActions.Visible = false;
            _advancedToggleButton.Text = "Дополнительно ▾";
        }
        _primaryActions.Enabled = canEditCurrent;
        _advancedActions.Enabled = canEditCurrent;
        _markStartButton.Enabled = canEditCurrent;
        _markEndButton.Enabled = canEditCurrent && _pendingStartMs.HasValue;
        _addButton.Enabled = canEditCurrent;
        _deleteButton.Enabled = canEditCurrent && SelectedIndex().HasValue;
        _applyButton.Enabled = canEditCurrent && _draft is not null;
        _saveButton.Enabled = canEditCurrent && _draft is not null;
        _intervals.ReadOnly = !canEditCurrent;
    }

    private bool HasCurrentMedia() => !string.IsNullOrWhiteSpace(_mediaPath);

    private string GetDetachedDraftActionMessage() =>
        HasCurrentMedia() ? TransferableDraftActionMessage : UnsavedDraftActionMessage;

    private bool CanEditCurrentMedia() => HasCurrentMedia() && !HasDetachedDraft();

    private bool TryGetEditableDraft(out ScheduleDraft draft)
    {
        draft = null!;
        if (!CanEditCurrentMedia())
        {
            NotifyCannotEdit();
            return false;
        }
        if (_draft is null || _draft.Document.Intervals.Count == 0)
        {
            NotifyAuthoring("Сначала добавьте интервал.");
            return false;
        }
        draft = _draft;
        ClearAuthoringNotification();
        return true;
    }

    private bool TryGetEditableInterval(out int index)
    {
        index = -1;
        if (!TryGetEditableDraft(out _))
            return false;
        if (SelectedIndex() is not { } selected)
        {
            NotifyAuthoring("Сначала выберите интервал.");
            return false;
        }
        index = selected;
        return true;
    }

    private static ScheduleDocument CreateNewDocument(
        string mediaPath,
        long? mediaDurationMs) =>
        new(
            new(Title: GetMediaTitle(mediaPath), MediaDurationMs: mediaDurationMs),
            [],
            []);

    private static string? GetMediaTitle(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            return null;
        try
        {
            if (Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                var segment = uri.Segments.LastOrDefault()?.Trim('/');
                var title = string.IsNullOrWhiteSpace(segment)
                    ? uri.Host
                    : Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(segment));
                return title.Trim();
            }
            return Path.GetFileNameWithoutExtension(mediaPath).Trim();
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or UriFormatException)
        {
            return mediaPath.Trim();
        }
    }

    private string? ResolveInitialDirectory(string? path)
    {
        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory)
            ? directory
            : _settings.RememberLastScheduleDirectory
                ? _settings.LastScheduleDirectory
                : null;
    }

    private static bool MediaPathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetDroppedSchedule(IDataObject? data, out string path)
    {
        path = "";
        if (data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return false;
        path = files[0];
        return ScheduleFileKinds.IsSupportedPath(path);
    }

    private void CommitCurrentCellEdit()
    {
        if (_intervals.IsCurrentCellInEditMode)
            _intervals.EndEdit();
    }

    private bool ConfirmSubtitleImport(string path)
    {
        if (!ScheduleFileKinds.TryGetSubtitleFormat(path, out _))
            return true;

        return ShowConfirmation(
            "Каждый фрагмент субтитров станет отдельным интервалом размытия. Импортировать файл?",
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
            AudioCompressionPresets.Off;
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
            "DISABLED" => "Размытие выключено",
            "DURATION MISMATCH" => "Длительность не совпадает",
            "EMPTY SCHEDULE" => "Интервалов нет",
            "ERROR" => "Ошибка",
            "IDLE" => "Ожидание",
            "INVALID DRAFT" => "В интервалах есть ошибки",
            "INVALID SCHEDULE PATH" => "Некорректный путь к файлу интервалов",
            "LOADING" => "Загрузка",
            "LOOKING FOR SIDECAR" => "Поиск файла интервалов рядом с фильмом",
            "NO CURRENT MEDIA" => "Нет открытого фильма",
            "NO LOCAL MEDIA" => "Открыт не локальный файл",
            "NO SCHEDULE" => "Файл интервалов не выбран",
            "NO SCHEDULE TO APPLY" => "Нет интервалов для применения",
            "NO SCHEDULE TO RELOAD" => "Нет файла интервалов для перезагрузки",
            "OPERATION UNAVAILABLE" => "Операция недоступна",
            "READY TO APPLY" => "Готово к применению",
            "RELOADED" => "Перезагружено — нажмите «Применить интервалы»",
            "SAVED" => "Сохранено",
            "SELECT SIDECAR" => "Выберите файл интервалов",
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

    private void NotifyCannotEdit() =>
        NotifyAuthoring(
            HasDetachedDraft() ? GetDetachedDraftActionMessage() : "Сначала откройте фильм.");

    private void NotifyAuthoring(string message)
    {
        _authoringNotice.Text = message;
        _authoringNotice.Visible = true;
        AuthoringNotificationRequested?.Invoke(message);
    }

    private void ClearAuthoringNotification()
    {
        _authoringNotice.Text = "";
        _authoringNotice.Visible = false;
    }

    private void Warn(string message)
    {
        if (WarningSink is { } warningSink)
        {
            warningSink(message);
            return;
        }
        MessageBox.Show(
            this,
            message,
            "CensorPlayer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private DialogResult ShowConfirmation(
        string message,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        if (WarningSink is { } warningSink)
        {
            warningSink(message);
            return DialogResult.Cancel;
        }
        return MessageBox.Show(this, message, "CensorPlayer", buttons, icon, defaultButton);
    }

    private sealed record SidecarChoice(string Name, string Path);
}
