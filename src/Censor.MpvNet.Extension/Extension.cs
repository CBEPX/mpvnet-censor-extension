using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Censor.Core;
using MpvNet;
using static MpvNet.Native.LibMpv;

namespace Censor.MpvNet.Extension;

public sealed class Extension : IExtension, IDisposable
{
    private const string LogModule = "CensorExtension";
    private const int MaxRecoveryFailures = 3;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Lock _stateLock = new();
    private readonly Lock _windowLock = new();
    private readonly Queue<Action<CensorWindow>> _pendingWindowActions = new();
    private readonly MediaSessionCoordinator _revisions = new();
    private readonly SemaphoreSlim _filterGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Timer _watchdog;
    private readonly ExtensionLog _log;
    private readonly string _localDataRoot;
    private readonly string _settingsPath;
    private CancellationTokenSource _operationCancellation = new();
    private Task _sessionCleanup = Task.CompletedTask;
    private ActiveSchedule? _activeSchedule;
    private PendingSchedule? _pendingSchedule;
    private BlurSettings _blurSettings = BlurSettings.Balanced;
    private ExtensionSettings _settings;
    private long? _currentDurationMs;
    private string? _currentMediaPath;
    private CensorWindow? _window;
    private Thread? _windowThread;
    private string _windowStatus = "NO SCHEDULE";
    private int _disposed;
    private bool _pauseHeldByExtension;
    private int _recoveryFailures;
    private int _stopping;
    private int _vfReadFailures;

    public Extension()
    {
        _localDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CensorPlayer");
        _settingsPath = Path.Combine(_localDataRoot, "settings.json");
        var loadedSettings = ExtensionSettingsStore.Load(_settingsPath);
        _settings = loadedSettings.Settings;
        _blurSettings = _settings.Blur;
        _log = new(Path.Combine(_localDataRoot, "Logs"), _settings.Logging.RetentionDays);
        foreach (var warning in loadedSettings.Warnings)
            Terminal.WriteError(warning, LogModule);

        Player = Global.Player.CreateNewPlayer("censor");
        _watchdog = new(CheckFilters, null, Timeout.Infinite, Timeout.Infinite);
        Player.StartFile += OnStartFile;
        Player.FileLoaded += OnFileLoaded;
        Player.EndFile += OnEndFile;
        Player.Shutdown += OnShutdown;
        Player.ClientMessage += OnClientMessage;
        Global.Player.Shutdown += OnShutdown;
        Player.ObservePropertyString("vf", OnFiltersChanged);
        if (_settings.WatchdogEnabled)
            _watchdog.Change(_settings.WatchdogIntervalMs, _settings.WatchdogIntervalMs);
    }

    public MpvClient Player { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Player.StartFile -= OnStartFile;
        Player.FileLoaded -= OnFileLoaded;
        Player.EndFile -= OnEndFile;
        Player.Shutdown -= OnShutdown;
        Player.ClientMessage -= OnClientMessage;
        Global.Player.Shutdown -= OnShutdown;

        StopRuntime(removeFilters: true);
        CloseWindow();
        _watchdog.Dispose();
        _operationCancellation.Dispose();
        _revisions.Dispose();
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStartFile() => RunSafely(() => BeginSession("NO SCHEDULE"));

    private void OnFileLoaded() =>
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var extension = (Extension)state!;
            extension.RunSafely(extension.LoadCurrentSidecar);
        }, this);

    private void OnEndFile(mpv_end_file_reason _) => RunSafely(() => BeginSession("IDLE"));

    private void OnShutdown()
    {
        StopRuntime(removeFilters: false);
        CloseWindow();
    }

    private void OnClientMessage(string[] args)
    {
        var copy = args.ToArray();
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (extension, message) = ((Extension, string[]))state!;
            extension.RunSafely(() => extension.HandleClientMessage(message));
        }, (this, copy));
    }

    private void OnFiltersChanged(string _) =>
        ThreadPool.QueueUserWorkItem(static state => ((Extension)state!).CheckFilters(null), this);

    private void HandleClientMessage(string[] args)
    {
        if (args.Length == 0)
            return;

        if (args[0].Equals("censor-open", StringComparison.OrdinalIgnoreCase))
        {
            ShowToolWindow();
        }
        else if (args[0].Equals("censor-pick", StringComparison.OrdinalIgnoreCase))
        {
            QueueWindowAction(window => window.OpenSchedulePicker());
        }
        else if (args[0].Equals("censor-load", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
                LoadManualSchedule(args[1]);
            else
                ShowToolWindow();
        }
        else if (args[0].Equals("censor-reload", StringComparison.OrdinalIgnoreCase))
        {
            ReloadSchedule();
        }
        else if (args[0].Equals("censor-apply", StringComparison.OrdinalIgnoreCase))
        {
            ApplyPendingSchedule();
        }
        else if (args[0].Equals("censor-disable", StringComparison.OrdinalIgnoreCase))
        {
            DisableSchedule();
        }
        else if (args[0].StartsWith("censor-", StringComparison.OrdinalIgnoreCase) &&
                 args[0]["censor-".Length..].ToLowerInvariant() is
                     "mark-start" or "mark-end" or "set-start" or "set-end" or
                     "previous" or "next" or "save")
        {
            var command = args[0]["censor-".Length..].ToLowerInvariant();
            QueueWindowAction(window => window.HandleAuthoringCommand(command));
        }
        else if (args[0].Equals("censor-diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            ShowToolWindow();
        }
    }

    private void BeginSession(string status)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;

        OperationTicket ticket;
        CancellationTokenSource previous;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                return;

            ticket = _revisions.BeginMediaSession();
            _currentMediaPath = null;
            _currentDurationMs = null;
            previous = _operationCancellation;
            _operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _revisions.SessionToken);
            _pendingSchedule = null;
            _sessionCleanup = Task.Run(() => RunSafely(() => ClearSession(ticket)));
        }

        previous.Cancel();
        previous.Dispose();
        UpdateWindow(status, ticket);
    }

    private void ClearSession(OperationTicket ticket)
    {
        _filterGate.Wait();
        var currentSession = false;
        try
        {
            if (!_revisions.IsCurrentMediaSession(ticket) ||
                Volatile.Read(ref _stopping) != 0)
            {
                return;
            }
            currentSession = true;

            ActiveSchedule? active;
            lock (_stateLock)
            {
                active = _activeSchedule;
                _activeSchedule = null;
                _pendingSchedule = null;
            }
            RemoveFilters(active?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
            _recoveryFailures = 0;
            _vfReadFailures = 0;
        }
        finally
        {
            try
            {
                if (currentSession)
                    ReleasePauseIfHeld();
            }
            finally
            {
                _filterGate.Release();
            }
        }
    }

    private void LoadCurrentSidecar()
    {
        OperationTicket ticket;
        CancellationToken token;
        lock (_stateLock)
        {
            ticket = _revisions.Snapshot();
            token = _operationCancellation.Token;
        }

        string mediaPath;
        long? durationMs = null;
        _filterGate.Wait();
        try
        {
            if (!TryGetPropertyString("path", out mediaPath))
                return;
            if (TryGetPropertyDouble("duration", out var durationSeconds) &&
                double.IsFinite(durationSeconds) &&
                durationSeconds > 0)
            {
                durationMs = checked((long)Math.Round(
                    durationSeconds * 1_000,
                    MidpointRounding.AwayFromZero));
            }
        }
        finally
        {
            _filterGate.Release();
        }

        lock (_stateLock)
        {
            if (!IsCurrent(ticket) || token.IsCancellationRequested)
                return;
            _currentMediaPath = mediaPath;
            _currentDurationMs = durationMs;
        }
        if (!_settings.AutoLoadSidecar)
        {
            UpdateWindow("NO SCHEDULE", ticket, token);
            return;
        }

        UpdateWindow("LOOKING FOR SIDECAR", ticket, token);
        _ = LoadCurrentSidecarAsync(ticket, mediaPath, token);
    }

    private async Task LoadCurrentSidecarAsync(
        OperationTicket ticket,
        string mediaPath,
        CancellationToken token)
    {
        try
        {
            if (!File.Exists(mediaPath))
            {
                token.ThrowIfCancellationRequested();
                UpdateWindow("NO LOCAL MEDIA", ticket, token);
                return;
            }

            var sidecars = SidecarLocator.Find(mediaPath);
            token.ThrowIfCancellationRequested();
            if (sidecars.Count == 0)
            {
                UpdateWindow("NO SCHEDULE", ticket, token);
                return;
            }
            if (sidecars.Count > 1)
            {
                UpdateWindow("SELECT SIDECAR", ticket, token);
                var selected = await ChooseSidecarAsync(sidecars, token).ConfigureAwait(false);
                if (selected is null)
                {
                    UpdateWindow("NO SCHEDULE", ticket, token);
                    return;
                }
                await LoadScheduleAsync(ticket, selected, automatic: true, token).ConfigureAwait(false);
                return;
            }

            await LoadScheduleAsync(ticket, sidecars[0], automatic: true, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", ticket, token))
                Show("Не удалось загрузить расписание рядом с фильмом. Подробности — в журнале mpv.net.", ticket, token);
        }
    }

    private async Task<string?> ChooseSidecarAsync(
        IReadOnlyList<string> paths,
        CancellationToken token)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        var dialogToken = timeoutCancellation.Token;
        var result = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!QueueWindowAction(window =>
        {
            try
            {
                if (dialogToken.IsCancellationRequested)
                {
                    result.TrySetCanceled(dialogToken);
                    return;
                }
                result.TrySetResult(window.ChooseSidecar(paths, dialogToken));
            }
            catch (Exception exception)
            {
                result.TrySetException(exception);
            }
        }))
        {
            return null;
        }
        using var registration =
            dialogToken.Register(() => result.TrySetCanceled(dialogToken));
        try
        {
            return await result.Task.WaitAsync(dialogToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task LoadScheduleAsync(
        OperationTicket ticket,
        string schedulePath,
        bool automatic,
        CancellationToken token)
    {
        try
        {
            var totalTimer = Stopwatch.StartNew();
            UpdateWindow("LOADING", ticket, token);
            if (new FileInfo(schedulePath).Length > _settings.Limits.MaxTextFileBytes)
                throw new InvalidDataException(
                    $"Размер расписания превышает {_settings.Limits.MaxTextFileBytes} байт.");
            var bytes = await File.ReadAllBytesAsync(schedulePath, token).ConfigureAwait(false);
            var text = StrictUtf8.GetString(bytes);
            var sourceHash = ComputeHash(bytes);
            token.ThrowIfCancellationRequested();
            var parseTimer = Stopwatch.StartNew();
            var parsed = Parse(schedulePath, text);
            parseTimer.Stop();
            token.ThrowIfCancellationRequested();
            if (!parsed.IsSuccess)
            {
                if (UpdateWindow("ERROR", ticket, token))
                {
                    Show(
                        $"Ошибка расписания: {parsed.Diagnostics.First(item => item.Severity == DiagnosticSeverity.Error).Message}",
                        ticket,
                        token);
                }
                return;
            }

            var document = parsed.Document!;
            var diagnostics = parsed.Diagnostics.ToList();
            long? currentDurationMs;
            lock (_stateLock)
                currentDurationMs = _currentDurationMs;
            if (currentDurationMs.HasValue &&
                document.Intervals.Any(interval => interval.EndMs > currentDurationMs.Value))
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Warning,
                    1,
                    1,
                    "Один или несколько интервалов выходят за длительность фильма."));
            }
            token.ThrowIfCancellationRequested();
            if (!MatchesCurrentDuration(document.Metadata.MediaDurationMs))
            {
                token.ThrowIfCancellationRequested();
                var applyAnyway = !automatic && ConfirmDurationMismatch();
                token.ThrowIfCancellationRequested();
                if (!applyAnyway)
                {
                    if (UpdateWindow("DURATION MISMATCH", ticket, token))
                    {
                        Show(
                            "Расписание не применено: его длительность отличается от длительности фильма.",
                            ticket,
                            token);
                    }
                    return;
                }
            }
            token.ThrowIfCancellationRequested();

            var normalized = ScheduleNormalizer.Normalize(
                document.Intervals,
                ScheduleOptionsResolver.Resolve(document.Metadata, _settings));
            BlurSettings blurSettings;
            lock (_stateLock)
                blurSettings = _blurSettings;
            var compileTimer = Stopwatch.StartNew();
            var plan = FilterCompiler.Compile(normalized, blurSettings);
            compileTimer.Stop();
            _log.Write(
                "schedule-compiled",
                ticket,
                new Dictionary<string, object?>
                {
                    ["schedulePath"] = ExtensionLog.ProtectPath(
                        schedulePath,
                        _settings.Logging.IncludePaths),
                    ["sourceIntervals"] = document.Intervals.Count,
                    ["normalizedIntervals"] = normalized.Count,
                    ["warnings"] = diagnostics.Count,
                    ["parseMs"] = parseTimer.ElapsedMilliseconds,
                    ["compileMs"] = compileTimer.ElapsedMilliseconds,
                    ["totalMs"] = totalTimer.ElapsedMilliseconds,
                });
            token.ThrowIfCancellationRequested();
            if (plan.Chunks.Count == 0)
            {
                if (UpdateWindow("EMPTY SCHEDULE", ticket, token))
                    Show("В расписании нет активных интервалов.", ticket, token);
                return;
            }

            if (!automatic)
            {
                lock (_stateLock)
                {
                    if (!_revisions.IsCurrent(ticket))
                        return;
                    _pendingSchedule = new(
                        ticket,
                        schedulePath,
                        sourceHash,
                        document,
                        plan,
                        normalized,
                        diagnostics);
                }
                UpdateWindow("READY TO APPLY", ticket, token);
                return;
            }

            await ApplyAsync(
                ticket,
                schedulePath,
                sourceHash,
                document,
                plan,
                normalized,
                token).ConfigureAwait(false);
            if (UpdateWindow("ACTIVE", ticket, token))
            {
                Show(
                    $"Применено {plan.IntervalCount} интервалов из файла {Path.GetFileName(schedulePath)}.",
                    ticket,
                    token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            _log.Write(
                "schedule-load-error",
                ticket,
                new Dictionary<string, object?> { ["error"] = ProtectError(exception, schedulePath) });
            if (UpdateWindow("ERROR", ticket, token))
                Show("Не удалось загрузить или применить расписание. Подробности — в журнале mpv.net.", ticket, token);
        }
    }

    private async Task ApplyAsync(
        OperationTicket ticket,
        string? schedulePath,
        string? sourceHash,
        ScheduleDocument document,
        FilterPlan plan,
        IReadOnlyList<NormalizedInterval> intervals,
        CancellationToken token)
    {
        Task sessionCleanup;
        lock (_stateLock)
            sessionCleanup = _sessionCleanup;
        await sessionCleanup.WaitAsync(token).ConfigureAwait(false);
        await _filterGate.WaitAsync(token).ConfigureAwait(false);
        var labels = plan.Chunks.Select(chunk => chunk.Label).ToArray();
        ActiveSchedule? previous = null;
        var filtersReady = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(ticket))
                return;

            lock (_stateLock)
                previous = _activeSchedule;
            HoldPauseIfNeeded(intervals);

            RemoveFilters(previous?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
            foreach (var chunk in plan.Chunks)
            {
                Player.CommandV("vf", "add", chunk.Filter);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(ticket))
                    throw new OperationCanceledException(token);
            }

            if (!TryGetPropertyString("vf", out var filters) ||
                labels.Any(label => !ContainsLabel(filters, label)))
            {
                throw new InvalidOperationException("После применения mpv не сообщил обо всех созданных фильтрах.");
            }
            filtersReady = true;

            lock (_stateLock)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(ticket))
                    throw new OperationCanceledException(token);
                _activeSchedule = new(ticket, schedulePath, sourceHash, document, plan, intervals);
                _pendingSchedule = null;
            }
            _recoveryFailures = 0;
            _vfReadFailures = 0;
            _log.Write(
                "apply-verified",
                ticket,
                new Dictionary<string, object?>
                {
                    ["schedulePath"] = ExtensionLog.ProtectPath(
                        schedulePath,
                        _settings.Logging.IncludePaths),
                    ["intervalCount"] = plan.IntervalCount,
                    ["chunkCount"] = plan.Chunks.Count,
                    ["sigma"] = _blurSettings.Sigma,
                    ["steps"] = _blurSettings.Steps,
                });
        }
        catch (Exception exception)
        {
            filtersReady = false;
            try
            {
                RemoveFilters(labels);
                if (previous is not null &&
                    Volatile.Read(ref _stopping) == 0 &&
                    _revisions.IsCurrentMediaSession(ticket))
                {
                    foreach (var chunk in previous.Plan.Chunks)
                        Player.CommandV("vf", "add", chunk.Filter);
                    var previousLabels = previous.Plan.Chunks
                        .Select(chunk => chunk.Label)
                        .ToArray();
                    if (!TryGetPropertyString("vf", out var restoredFilters) ||
                        previousLabels.Any(label => !ContainsLabel(restoredFilters, label)))
                    {
                        throw new InvalidOperationException(
                            "После отката mpv не сообщил обо всех прежних фильтрах.");
                    }
                    filtersReady = true;
                }
            }
            catch (Exception rollbackException)
            {
                Terminal.WriteError(rollbackException, LogModule);
                _log.Write(
                    "apply-rollback-error",
                    ticket,
                    new Dictionary<string, object?>
                    {
                        ["applyError"] = ProtectError(exception, schedulePath),
                        ["rollbackError"] = ProtectError(rollbackException, schedulePath),
                    });
            }
            throw;
        }
        finally
        {
            try
            {
                if (filtersReady || !_revisions.IsCurrentMediaSession(ticket))
                    ReleasePauseIfHeld();
            }
            finally
            {
                _filterGate.Release();
            }
        }
    }

    private bool MatchesCurrentDuration(long? expectedMs)
    {
        if (expectedMs is null)
            return true;

        _filterGate.Wait();
        try
        {
            if (!TryGetPropertyDouble("duration", out var durationSeconds) ||
                !double.IsFinite(durationSeconds) ||
                durationSeconds <= 0)
            {
                return false;
            }

            var actualMs = checked((long)Math.Round(durationSeconds * 1_000, MidpointRounding.AwayFromZero));
            return Math.Abs(actualMs - expectedMs.Value) <= _settings.DurationToleranceMs;
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private void LoadManualSchedule(string schedulePath)
    {
        if (!File.Exists(schedulePath) ||
            !(schedulePath.EndsWith(".censor.txt", StringComparison.OrdinalIgnoreCase) ||
              schedulePath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
              schedulePath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)))
        {
            UpdateWindow("INVALID SCHEDULE PATH");
            Show("Не удалось открыть расписание: путь некорректен или формат файла не поддерживается.");
            return;
        }

        var operation = StartNewOperation();
        if (operation is null)
        {
            UpdateWindow("NO CURRENT MEDIA");
            Show("Сначала откройте фильм, затем загрузите расписание.");
            return;
        }

        UpdateWindow("LOADING", operation.Value.Ticket, operation.Value.Token);
        _ = LoadScheduleAsync(
            operation.Value.Ticket,
            schedulePath,
            automatic: false,
            operation.Value.Token);
    }

    private void ApplyPendingSchedule()
    {
        PendingSchedule? pending;
        CancellationToken token;
        lock (_stateLock)
        {
            pending = _pendingSchedule;
            token = _operationCancellation.Token;
        }

        if (pending is null || !IsCurrent(pending.Ticket))
        {
            UpdateWindow("NO SCHEDULE TO APPLY");
            return;
        }

        UpdateWindow("APPLYING", pending.Ticket, token);
        _ = ApplyPendingScheduleAsync(pending, token);
    }

    private async Task ApplyPendingScheduleAsync(
        PendingSchedule pending,
        CancellationToken token)
    {
        try
        {
            await ApplyAsync(
                pending.Ticket,
                pending.SchedulePath,
                pending.SourceHash,
                pending.Document,
                pending.Plan,
                pending.Intervals,
                token).ConfigureAwait(false);
            if (UpdateWindow("ACTIVE", pending.Ticket, token))
                Show($"Применено интервалов: {pending.Plan.IntervalCount}.", pending.Ticket, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", pending.Ticket, token))
                Show("Не удалось применить расписание.", pending.Ticket, token);
        }
    }

    private void ApplyDraftDocument(ScheduleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = new ScheduleDraft(document).Validate();
        if (validation.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            UpdateWindow("INVALID DRAFT");
            Show("Исправьте ошибки в интервалах перед применением.");
            return;
        }

        string? schedulePath;
        string? sourceHash;
        lock (_stateLock)
        {
            schedulePath = _pendingSchedule?.SchedulePath ??
                _activeSchedule?.SchedulePath;
            sourceHash = _pendingSchedule?.SourceHash ?? _activeSchedule?.SourceHash;
        }

        var operation = StartNewOperation();
        if (operation is null)
        {
            UpdateWindow("NO CURRENT MEDIA");
            return;
        }

        var normalized = ScheduleNormalizer.Normalize(
            document.Intervals,
            ScheduleOptionsResolver.Resolve(document.Metadata, _settings));
        var plan = FilterCompiler.Compile(normalized, _blurSettings);
        if (plan.Chunks.Count == 0)
        {
            UpdateWindow("EMPTY SCHEDULE", operation.Value.Ticket, operation.Value.Token);
            Show("В расписании нет активных интервалов.", operation.Value.Ticket, operation.Value.Token);
            return;
        }
        var pending = new PendingSchedule(
            operation.Value.Ticket,
            schedulePath,
            sourceHash,
            document,
            plan,
            normalized,
            []);
        lock (_stateLock)
        {
            if (!IsCurrent(pending.Ticket) || operation.Value.Token.IsCancellationRequested)
                return;
            _pendingSchedule = pending;
        }
        UpdateWindow("APPLYING", pending.Ticket, operation.Value.Token);
        _ = ApplyPendingScheduleAsync(pending, operation.Value.Token);
    }

    private void ChangeSettings(ExtensionSettings settings)
    {
        var warnings = ExtensionSettingsStore.Validate(settings);
        if (warnings.Count > 0)
        {
            UpdateWindow("SETTINGS ERROR");
            Show(warnings[0]);
            return;
        }

        bool blurChanged;
        lock (_stateLock)
        {
            blurChanged = settings.Blur != _blurSettings;
            _settings = settings;
        }
        _watchdog.Change(
            settings.WatchdogEnabled ? settings.WatchdogIntervalMs : Timeout.Infinite,
            settings.WatchdogEnabled ? settings.WatchdogIntervalMs : Timeout.Infinite);
        if (blurChanged)
            ChangeBlurPreset(settings.Blur);
        else
        {
            SaveSettings();
            UpdateWindow("SETTINGS SAVED");
        }
    }

    private void SaveDraft(
        ScheduleDocument document,
        string? sourcePath,
        bool choosePath)
    {
        string? currentPath;
        string? expectedHash;
        bool sourceMatchesCurrent;
        OperationTicket saveTicket;
        ExtensionSettings settings;
        BlurSettings blur;
        lock (_stateLock)
        {
            var runtimePath =
                _pendingSchedule?.SchedulePath ?? _activeSchedule?.SchedulePath;
            sourceMatchesCurrent = string.Equals(
                sourcePath,
                runtimePath,
                StringComparison.OrdinalIgnoreCase);
            currentPath = sourcePath;
            expectedHash = sourceMatchesCurrent
                ? _pendingSchedule?.SourceHash ?? _activeSchedule?.SourceHash
                : null;
            saveTicket = _revisions.Snapshot();
            settings = _settings;
            blur = _blurSettings;
        }
        choosePath |= !sourceMatchesCurrent;
        if (currentPath?.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) == true ||
            currentPath?.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) == true)
        {
            currentPath = Path.Combine(
                Path.GetDirectoryName(currentPath) ?? "",
                Path.GetFileNameWithoutExtension(currentPath) + ".censor.txt");
            choosePath = true;
        }

        var path = choosePath || string.IsNullOrWhiteSpace(currentPath)
            ? ChooseSavePath(currentPath)
            : currentPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!choosePath &&
            expectedHash is not null &&
            File.Exists(path) &&
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(ComputeHash(File.ReadAllBytes(path)))))
        {
            var decision = ConfirmExternalChange();
            if (decision == DialogResult.Cancel)
                return;
            if (decision == DialogResult.No)
            {
                path = ChooseSavePath(path);
                if (string.IsNullOrWhiteSpace(path))
                    return;
            }
        }

        if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            WriteTextAtomically(path, SubtitleScheduleText.Export(document, SubtitleFormat.Srt));
        else if (path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            WriteTextAtomically(path, SubtitleScheduleText.Export(document, SubtitleFormat.WebVtt));
        else
            AtomicScheduleWriter.Write(path, document);
        var savedHash = ComputeHash(File.ReadAllBytes(path));
        var normalized = ScheduleNormalizer.Normalize(
            document.Intervals,
            ScheduleOptionsResolver.Resolve(document.Metadata, settings));
        var plan = FilterCompiler.Compile(normalized, blur);

        var markWindowSaved = false;
        var updateRuntime = false;
        ExtensionSettings savedSettings;
        _filterGate.Wait();
        try
        {
            lock (_stateLock)
            {
                if (IsCurrent(saveTicket))
                {
                    markWindowSaved = true;
                    if (sourceMatchesCurrent)
                    {
                        if (plan.Chunks.Count == 0)
                        {
                            _pendingSchedule = null;
                        }
                        else if (_pendingSchedule is not null)
                        {
                            _pendingSchedule = _pendingSchedule with
                            {
                                SchedulePath = path,
                                SourceHash = savedHash,
                                Document = document,
                                Plan = plan,
                                Intervals = normalized,
                            };
                        }
                        else if (_activeSchedule is not null)
                        {
                            _pendingSchedule = new(
                                saveTicket,
                                path,
                                savedHash,
                                document,
                                plan,
                                normalized,
                                []);
                        }
                        if (_activeSchedule is not null)
                        {
                            _activeSchedule = _activeSchedule with
                            {
                                SchedulePath = path,
                                SourceHash = savedHash,
                            };
                        }
                        updateRuntime = true;
                    }
                }
                if (_settings.RememberLastScheduleDirectory)
                {
                    _settings = _settings with
                    {
                        LastScheduleDirectory = Path.GetDirectoryName(path),
                    };
                }
                savedSettings = _settings;
            }
        }
        finally
        {
            _filterGate.Release();
        }
        SaveSettings();
        if (markWindowSaved)
        {
            InvokeWindow(window => window.MarkSaved(path, document, savedSettings));
            if (updateRuntime)
                UpdateWindow("SAVED", saveTicket);
            Show($"Файл {Path.GetFileName(path)} сохранён.", saveTicket);
        }
    }

    private void SeekTo(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        _filterGate.Wait();
        try
        {
            Player.CommandV(
                "seek",
                FormattableString.Invariant($"{milliseconds / 1_000.0:0.###}"),
                "absolute",
                "exact");
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private long? GetCurrentTimeMs()
    {
        if (!_filterGate.Wait(0))
            return null;
        try
        {
            if (!TryGetPropertyDouble("time-pos", out var seconds) ||
                !double.IsFinite(seconds) ||
                seconds < 0)
            {
                return null;
            }
            return checked((long)Math.Round(seconds * 1_000, MidpointRounding.AwayFromZero));
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private void ExportDiagnostics(bool includeSchedule)
    {
        var diagnosticsDirectory = Path.Combine(_localDataRoot, "Diagnostics");
        var path = ChooseDiagnosticsPath(diagnosticsDirectory);
        if (string.IsNullOrWhiteSpace(path))
            return;

        string filters;
        _filterGate.Wait();
        try
        {
            if (!TryGetPropertyString("vf", out filters))
                filters = "";
        }
        finally
        {
            _filterGate.Release();
        }

        ActiveSchedule? active;
        PendingSchedule? pending;
        string? mediaPath;
        ExtensionSettings settings;
        OperationTicket ticket;
        lock (_stateLock)
        {
            active = _activeSchedule;
            pending = _pendingSchedule;
            mediaPath = _currentMediaPath;
            settings = _settings;
            ticket = _revisions.Snapshot();
        }
        var document = pending?.Document ?? active?.Document;
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var sanitizedSettings = settings with
        {
            AdditionalProperties = null,
            LastScheduleDirectory = null,
            Limits = settings.Limits with { AdditionalProperties = null },
            Logging = settings.Logging with
            {
                AdditionalProperties = null,
                IncludePaths = false,
            },
        };
        var snapshot = new DiagnosticsSnapshot(
            JsonSerializer.Serialize(new
            {
                product = "CensorPlayer",
                version = typeof(Extension).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion,
                ticket.MediaSessionId,
                ticket.OperationRevision,
                createdAtUtc = DateTimeOffset.UtcNow,
            }, jsonOptions),
            JsonSerializer.Serialize(sanitizedSettings, jsonOptions),
            JsonSerializer.Serialize(pending?.Diagnostics ?? [], jsonOptions),
            JsonSerializer.Serialize(new
            {
                path = ExtensionLog.ProtectPath(mediaPath, includePath: false),
            }, jsonOptions),
            JsonSerializer.Serialize(new
            {
                sha256 = ComputeHash(Encoding.UTF8.GetBytes(filters)),
                expectedLabels = active?.Plan.Chunks.Select(chunk => chunk.Label) ?? [],
                presentExpectedLabels = active?.Plan.Chunks
                    .Where(chunk => ContainsLabel(filters, chunk.Label))
                    .Select(chunk => chunk.Label) ?? [],
            }, jsonOptions),
            JsonSerializer.Serialize(new
            {
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            }, jsonOptions),
            Directory.Exists(Path.Combine(_localDataRoot, "Logs"))
                ? Directory.GetFiles(Path.Combine(_localDataRoot, "Logs"), "*.log")
                : [],
            document is null ? null : ScheduleText.Serialize(document));
        DiagnosticsExporter.Export(path, snapshot, includeSchedule);
        InvokeWindow(window => window.ShowDiagnosticsResult(
            "Диагностический ZIP-архив сохранён:" + Environment.NewLine + path));
        Show("Диагностика экспортирована в ZIP-архив.");
    }

    private void ReloadSchedule()
    {
        string? path;
        lock (_stateLock)
            path = _activeSchedule?.SchedulePath;
        if (path is null)
        {
            UpdateWindow("NO SCHEDULE TO RELOAD");
            return;
        }

        LoadManualSchedule(path);
    }

    private void ChangeBlurPreset(BlurSettings settings)
    {
        ActiveSchedule? active;
        CancellationToken token;
        OperationTicket ticket;
        CancellationTokenSource previousCancellation;
        lock (_stateLock)
        {
            if (_blurSettings == settings)
                return;

            _blurSettings = settings;
            _settings = _settings with { Blur = settings };
            ticket = _revisions.BeginOperation();
            previousCancellation = _operationCancellation;
            _operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _revisions.SessionToken);
            token = _operationCancellation.Token;
            if (_activeSchedule is not null)
                _activeSchedule = _activeSchedule with { Ticket = ticket };
            active = _activeSchedule;
            _pendingSchedule = null;
        }

        previousCancellation.Cancel();
        previousCancellation.Dispose();
        SaveSettings();

        if (active is null)
        {
            UpdateWindow("NO SCHEDULE", ticket, token);
            return;
        }

        try
        {
            var plan = FilterCompiler.Compile(active.Intervals, settings);
            UpdateWindow("APPLYING", ticket, token);
            _ = ApplyBlurPresetAsync(ticket, active, plan, token);
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            UpdateWindow("ERROR", ticket, token);
        }
    }

    private async Task ApplyBlurPresetAsync(
        OperationTicket ticket,
        ActiveSchedule active,
        FilterPlan plan,
        CancellationToken token)
    {
        try
        {
            await ApplyAsync(
                ticket,
                active.SchedulePath,
                active.SourceHash,
                active.Document,
                plan,
                active.Intervals,
                token).ConfigureAwait(false);
            UpdateWindow("ACTIVE", ticket, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", ticket, token))
                Show("Не удалось изменить степень размытия. Проверьте состояние фильтра.", ticket, token);
        }
    }

    private void DisableSchedule()
    {
        var operation = StartNewOperation();
        if (operation is null)
        {
            UpdateWindow("OPERATION UNAVAILABLE");
            Show("Нельзя отключить расписание: сейчас нет открытого фильма.");
            return;
        }

        _filterGate.Wait();
        var currentOperation = false;
        try
        {
            if (!IsCurrent(operation.Value.Ticket))
                return;
            currentOperation = true;
            ActiveSchedule? active;
            lock (_stateLock)
            {
                active = _activeSchedule;
                _activeSchedule = null;
                _pendingSchedule = null;
            }
            RemoveFilters(active?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
        }
        finally
        {
            try
            {
                if (currentOperation)
                    ReleasePauseIfHeld();
            }
            finally
            {
                _filterGate.Release();
            }
        }

        UpdateWindow("DISABLED", operation.Value.Ticket, operation.Value.Token);
        Show("Расписание для текущего фильма отключено.");
    }

    private (OperationTicket Ticket, CancellationToken Token)? StartNewOperation()
    {
        CancellationTokenSource previous;
        OperationTicket ticket;
        CancellationToken token;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0 || string.IsNullOrEmpty(_currentMediaPath))
                return null;

            ticket = _revisions.BeginOperation();
            previous = _operationCancellation;
            _operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _revisions.SessionToken);
            token = _operationCancellation.Token;
            _pendingSchedule = null;
            if (_activeSchedule is not null)
                _activeSchedule = _activeSchedule with { Ticket = ticket };
        }

        previous.Cancel();
        previous.Dispose();

        return (ticket, token);
    }

    private bool ConfirmDurationMismatch()
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;

        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return false;

        try
        {
            return (bool)window.Invoke(new Func<bool>(window.ConfirmDurationMismatch));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private DialogResult ConfirmExternalChange()
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return DialogResult.Cancel;
        try
        {
            return (DialogResult)window.Invoke(
                new Func<DialogResult>(window.ConfirmExternalChange));
        }
        catch (InvalidOperationException)
        {
            return DialogResult.Cancel;
        }
    }

    private void ShowToolWindow()
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;

        CensorWindow? existing;
        lock (_windowLock)
        {
            existing = _window;
            if (existing is null && _windowThread is null)
            {
                _windowThread = new(() =>
                {
                    try
                    {
                        using var window = new CensorWindow(
                            _settings,
                            Path.Combine(_localDataRoot, "Recovery", "draft.json"));
                        window.ScheduleSelected += path =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, selectedPath) = ((Extension, string))state!;
                                extension.RunSafely(() => extension.LoadManualSchedule(selectedPath));
                            }, (this, path));
                        window.ReloadRequested += () =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var extension = (Extension)state!;
                                extension.RunSafely(extension.ReloadSchedule);
                            }, this);
                        window.ApplyRequested += document =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, draft) = ((Extension, ScheduleDocument))state!;
                                extension.RunSafely(() => extension.ApplyDraftDocument(draft));
                            }, (this, document));
                        window.DisableRequested += () =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var extension = (Extension)state!;
                                extension.RunSafely(extension.DisableSchedule);
                            }, this);
                        window.BlurPresetSelected += settings =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, selectedSettings) = ((Extension, BlurSettings))state!;
                                extension.RunSafely(() => extension.ChangeBlurPreset(selectedSettings));
                            }, (this, settings));
                        window.SettingsChanged += settings =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, changed) = ((Extension, ExtensionSettings))state!;
                                extension.RunSafely(() => extension.ChangeSettings(changed));
                            }, (this, settings));
                        window.SaveRequested += (document, sourcePath, saveAs) =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, draft, path, choosePath) =
                                    ((Extension, ScheduleDocument, string?, bool))state!;
                                extension.RunSafely(() =>
                                    extension.SaveDraft(draft, path, choosePath));
                            }, (this, document, sourcePath, saveAs));
                        window.SeekRequested += milliseconds =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, target) = ((Extension, long))state!;
                                extension.RunSafely(() => extension.SeekTo(target));
                            }, (this, milliseconds));
                        window.PreviewRequested += milliseconds =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, target) = ((Extension, long))state!;
                                extension.RunSafely(() => extension.SeekTo(Math.Max(0, target - 1_000)));
                            }, (this, milliseconds));
                        window.DiagnosticsRequested += includeSchedule =>
                            ThreadPool.QueueUserWorkItem(static state =>
                            {
                                var (extension, include) = ((Extension, bool))state!;
                                extension.RunSafely(() => extension.ExportDiagnostics(include));
                            }, (this, includeSchedule));
                        window.CurrentTimeRequested = GetCurrentTimeMs;
                        window.Shown += (_, _) =>
                        {
                            if (Volatile.Read(ref _stopping) != 0)
                            {
                                window.Shutdown();
                                return;
                            }

                            var current = SnapshotWindow();
                            window.UpdateState(
                                current.MediaPath,
                                current.SchedulePath,
                                current.Status,
                                current.MediaDurationMs,
                                current.Document,
                                current.Diagnostics);
                            DrainWindowActions(window);
                        };

                        lock (_windowLock)
                            _window = window;

                        var snapshot = SnapshotWindow();
                        window.UpdateState(
                            snapshot.MediaPath,
                            snapshot.SchedulePath,
                            snapshot.Status,
                            snapshot.MediaDurationMs,
                            snapshot.Document,
                            snapshot.Diagnostics);

                        if (Volatile.Read(ref _stopping) == 0)
                            Application.Run(window);
                    }
                    catch (Exception exception)
                    {
                        Terminal.WriteError(exception, LogModule);
                    }
                    finally
                    {
                        lock (_windowLock)
                        {
                            _window = null;
                            _windowThread = null;
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "CensorExtension UI",
                };
                _windowThread.SetApartmentState(ApartmentState.STA);
                _windowThread.Start();
                return;
            }
        }

        if (existing is null || existing.IsDisposed || !existing.IsHandleCreated)
            return;

        try
        {
            existing.BeginInvoke(new Action(() =>
            {
                existing.Show();
                existing.WindowState = FormWindowState.Normal;
                existing.Activate();
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool UpdateWindow(
        string status,
        OperationTicket? expectedTicket = null,
        CancellationToken token = default)
    {
        lock (_stateLock)
        {
            if (expectedTicket.HasValue &&
                (Volatile.Read(ref _stopping) != 0 ||
                 !_revisions.IsCurrent(expectedTicket.Value) ||
                 token.IsCancellationRequested))
            {
                return false;
            }

            _windowStatus = status;
        }

        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return true;

        var snapshot = SnapshotWindow();
        try
        {
            window.BeginInvoke(new Action(() =>
                window.UpdateState(
                    snapshot.MediaPath,
                    snapshot.SchedulePath,
                    snapshot.Status,
                    snapshot.MediaDurationMs,
                    snapshot.Document,
                    snapshot.Diagnostics)));
        }
        catch (InvalidOperationException)
        {
        }

        return true;
    }

    private (
        string? MediaPath,
        string? SchedulePath,
        string Status,
        long? MediaDurationMs,
        ScheduleDocument? Document,
        IReadOnlyList<ParseDiagnostic> Diagnostics) SnapshotWindow()
    {
        string? mediaPath;
        long? mediaDurationMs;
        string status;
        PendingSchedule? pending;
        ActiveSchedule? active;
        lock (_stateLock)
        {
            mediaPath = _currentMediaPath;
            mediaDurationMs = _currentDurationMs;
            status = _windowStatus;
            pending = _pendingSchedule;
            active = _activeSchedule;
        }

        return (
            mediaPath,
            pending?.SchedulePath ?? active?.SchedulePath,
            status,
            mediaDurationMs,
            pending?.Document ?? active?.Document,
            pending?.Diagnostics ?? []);
    }

    private string? ChooseSavePath(string? currentPath)
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return null;
        try
        {
            return (string?)window.Invoke(new Func<string?>(() =>
                window.ChooseSavePath(currentPath)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string? ChooseDiagnosticsPath(string directory)
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return null;
        try
        {
            return (string?)window.Invoke(new Func<string?>(() =>
                window.ChooseDiagnosticsPath(directory)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void InvokeWindow(Action<CensorWindow> action)
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return;
        try
        {
            window.BeginInvoke(new Action(() => action(window)));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool QueueWindowAction(Action<CensorWindow> action)
    {
        CensorWindow? window;
        lock (_windowLock)
        {
            window = _window;
            if (window is null || window.IsDisposed || !window.IsHandleCreated)
            {
                _pendingWindowActions.Enqueue(action);
                window = null;
            }
        }

        if (window is null)
        {
            ShowToolWindow();
            return true;
        }
        try
        {
            window.BeginInvoke(new Action(() => action(window)));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void DrainWindowActions(CensorWindow window)
    {
        Action<CensorWindow>[] actions;
        lock (_windowLock)
        {
            actions = _pendingWindowActions.ToArray();
            _pendingWindowActions.Clear();
        }
        foreach (var action in actions)
            action(window);
    }

    private static void WriteTextAtomically(string path, string text)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Путь экспорта должен включать каталог.", nameof(path));
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, text, new UTF8Encoding(false));
            if (File.Exists(fullPath))
                File.Replace(tempPath, fullPath, fullPath + ".bak");
            else
                File.Move(tempPath, fullPath);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original export failure.
            }
            throw;
        }
    }

    private void CloseWindow()
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return;

        try
        {
            window.BeginInvoke(new Action(window.Shutdown));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool IsCurrent(OperationTicket ticket) =>
        Volatile.Read(ref _stopping) == 0 && _revisions.IsCurrent(ticket);

    private ParseResult Parse(string path, string text)
    {
        ParseResult result;
        if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            result = SubtitleScheduleText.Import(text, SubtitleFormat.Srt);
        else if (path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            result = SubtitleScheduleText.Import(text, SubtitleFormat.WebVtt);
        else
            result = ScheduleText.Parse(
                text,
                _settings.Limits.MaxTextFileBytes,
                _settings.Limits.MaxIntervals);

        if (result.IsSuccess &&
            result.Document!.Intervals.Count > _settings.Limits.MaxIntervals)
        {
            return new(null,
            [
                new(
                    DiagnosticSeverity.Error,
                    1,
                    1,
                    $"В расписании больше {_settings.Limits.MaxIntervals} интервалов."),
            ]);
        }

        return result;
    }

    private void SaveSettings()
    {
        try
        {
            ExtensionSettings settings;
            lock (_stateLock)
                settings = _settings;
            ExtensionSettingsStore.Save(_settingsPath, settings);
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            QueueShow("Не удалось сохранить настройки.");
        }
    }

    private void RemoveFilters(IEnumerable<string> labels)
    {
        foreach (var label in labels)
            Player.CommandV("vf", "remove", label);
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            _log.Write(
                "callback-error",
                _revisions.Snapshot(),
                new Dictionary<string, object?> { ["error"] = ProtectError(exception) });
            Show("Ошибка расширения CensorPlayer. Подробности — в журнале mpv.net.");
        }
    }

    private void Show(
        string message,
        OperationTicket? expectedTicket = null,
        CancellationToken token = default)
    {
        if (Volatile.Read(ref _stopping) != 0 || token.IsCancellationRequested)
            return;

        _filterGate.Wait(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _stopping) == 0 &&
                !token.IsCancellationRequested &&
                (!expectedTicket.HasValue || IsCurrent(expectedTicket.Value)))
            {
                Player.CommandV("show-text", $"Censor: {message}", "5000");
            }
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private void QueueShow(string message) =>
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var (extension, text) = ((Extension, string))state!;
            extension.Show(text);
        }, (this, message));

    private void CheckFilters(object? _)
    {
        var entered = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _stopping) != 0)
                return;

            entered = _filterGate.Wait(0);
            if (!entered || Volatile.Read(ref _stopping) != 0)
                return;

            ActiveSchedule? active;
            lock (_stateLock)
                active = _activeSchedule;
            if (active is null ||
                !IsCurrent(active.Ticket))
            {
                return;
            }

            if (!TryGetPropertyString("vf", out var filters))
            {
                _vfReadFailures++;
                if (_vfReadFailures == 1)
                    Terminal.WriteError("Не удалось прочитать vf и определить состояние watchdog.", LogModule);
                if (_vfReadFailures == MaxRecoveryFailures)
                    QueueShow(
                        "Не удалось прочитать цепочку видеофильтров. Автоматическое восстановление остановлено.");
                return;
            }

            _vfReadFailures = 0;
            if (active.Plan.Chunks.All(chunk => ContainsLabel(filters, chunk.Label)))
            {
                _recoveryFailures = 0;
                return;
            }

            UpdateWindow("WARNING", active.Ticket);
            if (_recoveryFailures >= MaxRecoveryFailures)
                return;

            try
            {
                RecoverFilters(active);
                _recoveryFailures = 0;
                UpdateWindow("ACTIVE", active.Ticket);
                _log.Write("watchdog-recovered", active.Ticket);
            }
            catch (Exception exception)
            {
                _recoveryFailures++;
                Terminal.WriteError(exception, LogModule);
                _log.Write(
                    "watchdog-recovery-error",
                    active.Ticket,
                    new Dictionary<string, object?>
                    {
                        ["consecutiveFailures"] = _recoveryFailures,
                        ["error"] = ProtectError(exception, active.SchedulePath),
                    });
                if (_recoveryFailures == MaxRecoveryFailures)
                    QueueShow("Не удалось восстановить фильтры. Автоматическое восстановление остановлено.");
            }
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
        }
        finally
        {
            if (entered)
                _filterGate.Release();
        }
    }

    private string ProtectError(Exception exception, string? additionalPath = null)
    {
        var text = exception.ToString();
        if (_settings.Logging.IncludePaths)
            return text;

        string? mediaPath;
        string? activePath;
        string? pendingPath;
        lock (_stateLock)
        {
            mediaPath = _currentMediaPath;
            activePath = _activeSchedule?.SchedulePath;
            pendingPath = _pendingSchedule?.SchedulePath;
        }
        foreach (var path in new[]
                 {
                     additionalPath,
                     mediaPath,
                     activePath,
                     pendingPath,
                     _localDataRoot,
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            text = text.Replace(
                path!,
                ExtensionLog.ProtectPath(path, includePath: false),
                StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private void RecoverFilters(ActiveSchedule active)
    {
        HoldPauseIfNeeded(active.Intervals);

        var filtersReady = false;
        try
        {
            var labels = active.Plan.Chunks.Select(chunk => chunk.Label).ToArray();
            RemoveFilters(labels);
            foreach (var chunk in active.Plan.Chunks)
            {
                if (!IsCurrent(active.Ticket) ||
                    Volatile.Read(ref _stopping) != 0)
                    return;
                Player.CommandV("vf", "add", chunk.Filter);
            }

            if (!TryGetPropertyString("vf", out var filters) ||
                labels.Any(label => !ContainsLabel(filters, label)))
            {
                throw new InvalidOperationException("После восстановления mpv не сообщил обо всех созданных фильтрах.");
            }

            filtersReady = true;
            Terminal.Write("Восстановлен отсутствующий фильтр цензуры.", LogModule);
        }
        finally
        {
            if (filtersReady || !_revisions.IsCurrentMediaSession(active.Ticket))
                ReleasePauseIfHeld();
        }
    }

    private void HoldPauseIfNeeded(IReadOnlyList<NormalizedInterval> intervals)
    {
        if (_pauseHeldByExtension || !IsIntervalNear(intervals))
            return;
        if (TryGetPropertyBool("pause", out var wasPaused) && !wasPaused)
        {
            Player.SetPropertyBool("pause", true);
            _pauseHeldByExtension = true;
        }
    }

    private void ReleasePauseIfHeld()
    {
        if (!_pauseHeldByExtension)
            return;
        if (Volatile.Read(ref _stopping) != 0)
        {
            _pauseHeldByExtension = false;
            return;
        }
        if (!TryGetPropertyBool("pause", out var paused))
            return;
        if (paused)
            Player.SetPropertyBool("pause", false);
        _pauseHeldByExtension = false;
    }

    private bool IsIntervalNear(IReadOnlyList<NormalizedInterval> intervals)
    {
        if (!TryGetPropertyDouble("time-pos", out var positionSeconds) ||
            !double.IsFinite(positionSeconds) ||
            positionSeconds < 0)
        {
            return true;
        }

        var positionMs = checked((long)Math.Round(positionSeconds * 1_000, MidpointRounding.AwayFromZero));
        return WatchdogPolicy.IsIntervalNear(
            positionMs,
            intervals,
            _settings.EarlyIntervalGuardMs);
    }

    private void StopRuntime(bool removeFilters)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            _stopped.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            try
            {
                _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }

            CancellationTokenSource cancellation;
            lock (_stateLock)
                cancellation = _operationCancellation;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _filterGate.Wait();
            try
            {
                ActiveSchedule? active;
                lock (_stateLock)
                {
                    active = _activeSchedule;
                    _activeSchedule = null;
                    _pendingSchedule = null;
                }
                if (removeFilters)
                    RemoveFilters(active?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
            }
            finally
            {
                _pauseHeldByExtension = false;
                _filterGate.Release();
            }
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
        }
        finally
        {
            _stopped.TrySetResult(true);
        }
    }

    private bool TryGetPropertyString(string name, out string value)
    {
        value = "";
        if (Player.Handle == IntPtr.Zero || Volatile.Read(ref _stopping) != 0)
            return false;

        var error = mpv_get_property(
            Player.Handle,
            GetUtf8Bytes(name),
            mpv_format.MPV_FORMAT_STRING,
            out IntPtr buffer);
        if (error < 0 || buffer == IntPtr.Zero)
            return false;

        try
        {
            value = ConvertFromUtf8(buffer);
            return true;
        }
        finally
        {
            mpv_free(buffer);
        }
    }

    private bool TryGetPropertyDouble(string name, out double value)
    {
        value = 0;
        return Player.Handle != IntPtr.Zero &&
            Volatile.Read(ref _stopping) == 0 &&
            mpv_get_property(
                Player.Handle,
                GetUtf8Bytes(name),
                mpv_format.MPV_FORMAT_DOUBLE,
                out value) == 0;
    }

    private bool TryGetPropertyBool(string name, out bool value)
    {
        value = false;
        if (Player.Handle == IntPtr.Zero || Volatile.Read(ref _stopping) != 0)
            return false;

        var error = mpv_get_property(
            Player.Handle,
            GetUtf8Bytes(name),
            mpv_format.MPV_FORMAT_FLAG,
            out IntPtr raw);
        value = raw != IntPtr.Zero;
        return error == 0;
    }

    private static bool ContainsLabel(string filters, string label) =>
        filters.Contains(label + ":", StringComparison.Ordinal);

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ActiveSchedule(
        OperationTicket Ticket,
        string? SchedulePath,
        string? SourceHash,
        ScheduleDocument Document,
        FilterPlan Plan,
        IReadOnlyList<NormalizedInterval> Intervals);

    private sealed record PendingSchedule(
        OperationTicket Ticket,
        string? SchedulePath,
        string? SourceHash,
        ScheduleDocument Document,
        FilterPlan Plan,
        IReadOnlyList<NormalizedInterval> Intervals,
        IReadOnlyList<ParseDiagnostic> Diagnostics);
}
