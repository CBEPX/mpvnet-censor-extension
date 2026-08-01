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
    private const int MaxPendingWindowActions = 32;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan OsdGateTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(5);

    // Nested runtime acquisition order: _filterGate -> _stateLock -> _windowLock.
    // _settingsWriteLock may wrap _stateLock, but never nests with _filterGate or _windowLock.
    private readonly Lock _stateLock = new();
    private readonly Lock _windowLock = new();
    private readonly Lock _settingsWriteLock = new();
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
    private volatile ExtensionSettings _settings;
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
        _log = new(Path.Combine(_localDataRoot, "Logs"), _settings.Logging.RetentionDays);
        if (!_log.IsEnabled)
            Terminal.WriteError("Журнал CensorPlayer отключён: не удалось открыть каталог для записи.", LogModule);
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
        lock (_stateLock)
            _operationCancellation.Dispose();
        _revisions.Dispose();
        // Queued callbacks can still observe _stopping after Dispose, so keep their gate alive.
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStartFile() => RunSafely(() => BeginSession("NO SCHEDULE"));

    private void OnFileLoaded() =>
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var extension = (Extension)state!;
            extension.RunSafely(extension.LoadCurrentSidecar);
            extension.RunSafely(extension.EnsureSavedAudioCompression);
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
            ShowToolWindow();
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
            var capturedTimeMs = command is
                "mark-start" or "mark-end" or "set-start" or "set-end"
                    ? GetCurrentTimeMs()
                    : null;
            QueueWindowAction(window =>
                window.HandleAuthoringCommand(command, capturedTimeMs));
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
            Interlocked.Exchange(ref _recoveryFailures, 0);
            Interlocked.Exchange(ref _vfReadFailures, 0);
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
            if (Volatile.Read(ref _stopping) != 0)
                return;
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
                _settings.ResolveNormalizationOptions(document.Metadata));
            var blurSettings = _settings.Blur;
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
                if (ClearSchedule(ticket) && UpdateWindow("EMPTY SCHEDULE", ticket, token))
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
                diagnostics,
                token).ConfigureAwait(false);
            if (UpdateWindow("ACTIVE", ticket, token))
            {
                Show(
                    $"Расписание из файла {Path.GetFileName(schedulePath)} применено. Интервалов: {plan.IntervalCount}.",
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
        IReadOnlyList<ParseDiagnostic> diagnostics,
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
                _activeSchedule = new(
                    ticket,
                    schedulePath,
                    sourceHash,
                    document,
                    plan,
                    intervals,
                    diagnostics);
                _pendingSchedule = null;
            }
            Interlocked.Exchange(ref _recoveryFailures, 0);
            Interlocked.Exchange(ref _vfReadFailures, 0);
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
                    ["sigma"] = plan.Blur.Sigma,
                    ["steps"] = plan.Blur.Steps,
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
                // Fail closed until either the new graph or its rollback is verified.
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

        lock (_stateLock)
            return _currentDurationMs is { } actualMs &&
                Math.Abs(actualMs - expectedMs.Value) <= _settings.DurationToleranceMs;
    }

    private void LoadManualSchedule(string schedulePath)
    {
        if (!File.Exists(schedulePath) ||
            !ScheduleFileKinds.IsSupportedPath(schedulePath))
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

        var scheduleDirectory = Path.GetDirectoryName(schedulePath);
        var saveSettings = false;
        lock (_stateLock)
        {
            if (_settings.RememberLastScheduleDirectory &&
                !string.Equals(
                    _settings.LastScheduleDirectory,
                    scheduleDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                _settings = _settings with { LastScheduleDirectory = scheduleDirectory };
                saveSettings = true;
            }
        }
        if (saveSettings)
            SaveSettings();

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
            if (Volatile.Read(ref _stopping) != 0)
                return;
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
                pending.Diagnostics,
                token).ConfigureAwait(false);
            if (UpdateWindow("ACTIVE", pending.Ticket, token))
                Show($"Расписание применено. Интервалов: {pending.Plan.IntervalCount}.", pending.Ticket, token);
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
        var validation = ScheduleDraft.Validate(
            document,
            _settings.Limits.MaxIntervals,
            _settings.Limits.MaxTextFileBytes);
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
            _settings.ResolveNormalizationOptions(document.Metadata));
        var plan = FilterCompiler.Compile(normalized, _settings.Blur);
        if (plan.Chunks.Count == 0)
        {
            if (ClearSchedule(operation.Value.Ticket) &&
                UpdateWindow("EMPTY SCHEDULE", operation.Value.Ticket, operation.Value.Token))
            {
                Show("В расписании нет активных интервалов.", operation.Value.Ticket, operation.Value.Token);
            }
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

    private void ChangeSettings(SettingsFormValues values)
    {
        ExtensionSettings previousSettings;
        ExtensionSettings settings;
        bool watchdogChanged;
        IReadOnlyList<string> warnings;
        lock (_stateLock)
        {
            previousSettings = _settings;
            settings = _settings with
            {
                AutoLoadSidecar = values.AutoLoadSidecar,
                WatchdogEnabled = values.WatchdogEnabled,
                WatchdogIntervalMs = values.WatchdogIntervalMs,
                LeadInMs = values.LeadInMs,
                LeadOutMs = values.LeadOutMs,
                MergeGapMs = values.MergeGapMs,
                DurationToleranceMs = values.DurationToleranceMs,
                EarlyIntervalGuardMs = values.EarlyIntervalGuardMs,
            };
            watchdogChanged =
                _settings.WatchdogEnabled != settings.WatchdogEnabled ||
                _settings.WatchdogIntervalMs != settings.WatchdogIntervalMs;
            warnings = ExtensionSettingsStore.Validate(settings);
            if (warnings.Count == 0)
            {
                _settings = settings;
                if (watchdogChanged)
                {
                    Interlocked.Exchange(ref _recoveryFailures, 0);
                    Interlocked.Exchange(ref _vfReadFailures, 0);
                }
            }
        }
        if (warnings.Count > 0)
        {
            InvokeWindow(window => window.SetSettings(previousSettings));
            Show(warnings[0]);
            return;
        }

        InvokeWindow(window => window.SetSettings(settings));
        _watchdog.Change(
            settings.WatchdogEnabled ? settings.WatchdogIntervalMs : Timeout.Infinite,
            settings.WatchdogEnabled ? settings.WatchdogIntervalMs : Timeout.Infinite);
        if (SaveSettings())
            Show("Настройки сохранены.");
    }

    private void RepairSettings()
    {
        ExtensionSettings previous;
        ExtensionSettings settings;
        lock (_stateLock)
        {
            if (_settings.Schema == 1)
                return;
            previous = _settings;
            settings = ExtensionSettingsStore.ConvertToCurrentSchema(previous);
            _settings = settings;
        }

        InvokeWindow(window => window.SetSettings(settings));
        if (SaveSettings())
        {
            Show("Файл настроек перезаписан в формате этой версии CensorPlayer.");
            return;
        }

        var reverted = false;
        lock (_stateLock)
        {
            if (ReferenceEquals(_settings, settings))
            {
                _settings = previous;
                reverted = true;
            }
        }
        if (reverted)
            InvokeWindow(window => window.SetSettings(previous));
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
        }
        choosePath |= !sourceMatchesCurrent;
        if (ScheduleFileKinds.TryGetSubtitleFormat(currentPath, out _))
        {
            var baseName = Path.GetFileNameWithoutExtension(currentPath);
            if (baseName.EndsWith(".censor", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^".censor".Length];
            currentPath = Path.Combine(
                Path.GetDirectoryName(currentPath) ?? "",
                baseName + ScheduleFileKinds.CanonicalSuffix);
            choosePath = true;
        }

        var path = choosePath || string.IsNullOrWhiteSpace(currentPath)
            ? ChooseSavePath(currentPath)
            : currentPath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!ScheduleFileKinds.IsSupportedPath(path))
        {
            Show("Расписание не сохранено: выберите файл .censor.txt, .srt или .vtt.");
            return;
        }

        if (!choosePath &&
            expectedHash is not null &&
            File.Exists(path) &&
            !string.Equals(
                expectedHash,
                ComputeHash(File.ReadAllBytes(path)),
                StringComparison.Ordinal))
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

        if (ScheduleFileKinds.TryGetSubtitleFormat(path, out var exportFormat))
        {
            if (!ConfirmSubtitleExport())
                return;
            AtomicFile.WriteUtf8Text(
                path,
                SubtitleScheduleText.Export(document, exportFormat),
                Path.GetFullPath(path) + ".bak");
            Show(
                $"Файл {Path.GetFileName(path)} экспортирован. Черновик не отмечен как сохранённый.",
                saveTicket);
            return;
        }

        var normalized = ScheduleNormalizer.Normalize(
            document.Intervals,
            settings.ResolveNormalizationOptions(document.Metadata));
        var plan = FilterCompiler.Compile(normalized, settings.Blur);
        AtomicScheduleWriter.Write(
            path,
            document,
            settings.Limits.MaxIntervals,
            settings.Limits.MaxTextFileBytes);
        var savedHash = ComputeHash(File.ReadAllBytes(path));

        var markWindowSaved = false;
        var updateRuntime = false;
        var saveSettings = false;
        string? savedScheduleDirectory;
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
                    var directory = Path.GetDirectoryName(path);
                    if (!string.Equals(
                            _settings.LastScheduleDirectory,
                            directory,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _settings = _settings with { LastScheduleDirectory = directory };
                        saveSettings = true;
                    }
                }
                savedScheduleDirectory = _settings.LastScheduleDirectory;
            }
        }
        finally
        {
            _filterGate.Release();
        }
        if (saveSettings)
            SaveSettings();
        if (markWindowSaved)
        {
            InvokeWindow(window =>
                window.MarkSaved(path, document, savedScheduleDirectory));
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

        string videoFilters;
        string audioFilters;
        _filterGate.Wait();
        try
        {
            if (!TryGetPropertyString("vf", out videoFilters))
                videoFilters = "";
            if (!TryGetPropertyString("af", out audioFilters))
                audioFilters = "";
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
            JsonSerializer.Serialize(
                pending?.Diagnostics ?? active?.Diagnostics ?? [],
                jsonOptions),
            JsonSerializer.Serialize(new
            {
                path = ExtensionLog.ProtectPath(mediaPath, includePath: false),
            }, jsonOptions),
            JsonSerializer.Serialize(new
            {
                sha256 = ComputeHash(Encoding.UTF8.GetBytes(videoFilters)),
                expectedLabels = active?.Plan.Chunks.Select(chunk => chunk.Label) ?? [],
                presentExpectedLabels = active?.Plan.Chunks
                    .Where(chunk => ContainsLabel(videoFilters, chunk.Label))
                    .Select(chunk => chunk.Label) ?? [],
            }, jsonOptions),
            JsonSerializer.Serialize(new
            {
                sha256 = ComputeHash(Encoding.UTF8.GetBytes(audioFilters)),
                presetId = settings.AudioCompressionPreset,
                expectedLabel = AudioCompressionPresets.FilterLabel,
                present = ContainsLabel(audioFilters, AudioCompressionPresets.FilterLabel),
            }, jsonOptions),
            JsonSerializer.Serialize(new
            {
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            }, jsonOptions),
            ExtensionLog.FindFiles(Path.Combine(_localDataRoot, "Logs")),
            includeSchedule && document is not null ? ScheduleText.Serialize(document) : null);
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
        PendingSchedule? pending;
        CancellationToken token;
        OperationTicket ticket;
        CancellationTokenSource previousCancellation;
        while (true)
        {
            PendingSchedule? observedPending;
            lock (_stateLock)
            {
                if (Volatile.Read(ref _stopping) != 0 || _settings.Blur == settings)
                    return;
                observedPending = _pendingSchedule;
            }

            var pendingPlan = observedPending is null
                ? null
                : FilterCompiler.Compile(observedPending.Intervals, settings);

            lock (_stateLock)
            {
                if (Volatile.Read(ref _stopping) != 0 || _settings.Blur == settings)
                    return;
                if (!ReferenceEquals(_pendingSchedule, observedPending))
                    continue;

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
                if (_pendingSchedule is not null)
                {
                    _pendingSchedule = _pendingSchedule with
                    {
                        Ticket = ticket,
                        Plan = pendingPlan!,
                    };
                }
                pending = _pendingSchedule;
            }
            break;
        }

        previousCancellation.Cancel();
        previousCancellation.Dispose();
        SaveSettings();

        if (pending is not null)
        {
            UpdateWindow("READY TO APPLY", ticket, token);
            return;
        }

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
                active.Diagnostics,
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

    private void ChangeAudioCompressionPreset(string presetId)
    {
        var preset = AudioCompressionPresets.Find(presetId) ??
            throw new ArgumentException("Неизвестный пресет компрессии звука.", nameof(presetId));
        AudioCompressionPresetDefinition previousPreset;
        bool hasMedia;
        Exception? applyError = null;
        Exception? rollbackError = null;
        _filterGate.Wait();
        try
        {
            lock (_stateLock)
            {
                if (_settings.AudioCompressionPreset == preset.Id)
                    return;
                previousPreset = AudioCompressionPresets.Find(
                    _settings.AudioCompressionPreset)!;
                hasMedia = !string.IsNullOrWhiteSpace(_currentMediaPath);
                if (!hasMedia)
                    _settings = _settings with { AudioCompressionPreset = preset.Id };
            }

            if (hasMedia)
            {
                try
                {
                    ApplyAudioCompressionPreset(preset);
                    lock (_stateLock)
                        _settings = _settings with { AudioCompressionPreset = preset.Id };
                }
                catch (Exception exception)
                {
                    applyError = exception;
                    try
                    {
                        ApplyAudioCompressionPreset(previousPreset);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackError = rollbackException;
                    }
                }
            }
        }
        finally
        {
            _filterGate.Release();
        }

        if (!hasMedia)
        {
            SaveSettings();
            _log.Write(
                "audio-preset-saved",
                _revisions.Snapshot(),
                new Dictionary<string, object?> { ["presetId"] = preset.Id });
            Show($"Пресет «{preset.DisplayName}» будет применён после открытия фильма.");
            return;
        }

        if (applyError is null)
        {
            SaveSettings();
            _log.Write(
                "audio-preset-applied",
                _revisions.Snapshot(),
                new Dictionary<string, object?> { ["presetId"] = preset.Id });
            Show($"Компрессия звука: {preset.DisplayName}.");
            return;
        }

        InvokeWindow(window => window.SetAudioCompressionPreset(previousPreset.Id));
        Terminal.WriteError(applyError, LogModule);
        if (rollbackError is not null)
            Terminal.WriteError(rollbackError, LogModule);
        _log.Write(
            "audio-preset-error",
            _revisions.Snapshot(),
            new Dictionary<string, object?>
            {
                ["presetId"] = preset.Id,
                ["error"] = ProtectError(applyError),
                ["rollbackError"] = rollbackError is null ? null : ProtectError(rollbackError),
            });
        Show(rollbackError is null
            ? $"Не удалось применить компрессию звука. Возвращён пресет «{previousPreset.DisplayName}»."
            : "Не удалось применить или восстановить компрессию звука. Проверьте аудиофильтры.");
    }

    private void EnsureSavedAudioCompression()
    {
        AudioCompressionPresetDefinition? preset = null;
        try
        {
            _filterGate.Wait();
            try
            {
                lock (_stateLock)
                {
                    if (string.IsNullOrWhiteSpace(_currentMediaPath))
                        return;
                    preset = AudioCompressionPresets.Find(
                        _settings.AudioCompressionPreset)!;
                }
                ApplyAudioCompressionPreset(preset);
            }
            finally
            {
                _filterGate.Release();
            }
            _log.Write(
                "audio-preset-restored",
                _revisions.Snapshot(),
                new Dictionary<string, object?> { ["presetId"] = preset!.Id });
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            _log.Write(
                "audio-preset-restore-error",
                _revisions.Snapshot(),
                new Dictionary<string, object?>
                {
                    ["presetId"] = preset?.Id,
                    ["error"] = ProtectError(exception),
                });
            Show(preset is null
                ? "Не удалось применить сохранённый пресет компрессии звука."
                : $"Не удалось применить пресет компрессии звука «{preset.DisplayName}».");
        }
    }

    private void ApplyAudioCompressionPreset(AudioCompressionPresetDefinition preset)
    {
        if (!TryGetPropertyString("af", out var filters))
            throw new InvalidOperationException("mpv не сообщил текущее состояние компрессии звука.");

        if (ContainsLabel(filters, AudioCompressionPresets.FilterLabel))
        {
            Player.CommandV("af", "remove", AudioCompressionPresets.FilterLabel);
            if (!TryGetPropertyString("af", out filters) ||
                ContainsLabel(filters, AudioCompressionPresets.FilterLabel))
            {
                throw new InvalidOperationException(
                    "mpv не подтвердил удаление прежнего фильтра компрессии звука.");
            }
        }
        if (preset.Filter is null)
            return;

        Player.CommandV("af", "add", preset.Filter);
        if (!TryGetPropertyString("af", out filters) ||
            !ContainsLabel(filters, AudioCompressionPresets.FilterLabel))
        {
            throw new InvalidOperationException(
                "mpv не подтвердил применение фильтра компрессии звука.");
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

        if (!ClearSchedule(operation.Value.Ticket))
            return;

        UpdateWindow("DISABLED", operation.Value.Ticket, operation.Value.Token);
        Show("Расписание для текущего фильма отключено.", operation.Value.Ticket, operation.Value.Token);
    }

    private bool ClearSchedule(OperationTicket ticket)
    {
        _filterGate.Wait();
        var currentOperation = false;
        try
        {
            if (!IsCurrent(ticket) || Volatile.Read(ref _stopping) != 0)
                return false;
            currentOperation = true;
            ActiveSchedule? active;
            lock (_stateLock)
            {
                active = _activeSchedule;
                _activeSchedule = null;
                _pendingSchedule = null;
            }
            RemoveFilters(active?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
            return true;
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

    private bool ConfirmSubtitleExport()
    {
        CensorWindow? window;
        lock (_windowLock)
            window = _window;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
            return false;
        try
        {
            return (bool)window.Invoke(new Func<bool>(window.ConfirmSubtitleExport));
        }
        catch (InvalidOperationException)
        {
            return false;
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
                            _localDataRoot);
                        window.ScheduleSelected += path =>
                            QueueSafely(() => LoadManualSchedule(path));
                        window.ReloadRequested += () => QueueSafely(ReloadSchedule);
                        window.ApplyRequested += document =>
                            QueueSafely(() => ApplyDraftDocument(document));
                        window.DisableRequested += () => QueueSafely(DisableSchedule);
                        window.BlurPresetSelected += settings =>
                            QueueSafely(() => ChangeBlurPreset(settings));
                        window.AudioCompressionPresetSelected += presetId =>
                            QueueSafely(() => ChangeAudioCompressionPreset(presetId));
                        window.SettingsChanged += settings =>
                            QueueSafely(() => ChangeSettings(settings));
                        window.SaveRequested += (document, sourcePath, saveAs) =>
                            QueueSafely(() => SaveDraft(document, sourcePath, saveAs));
                        window.SeekRequested += milliseconds =>
                            QueueSafely(() => SeekTo(milliseconds));
                        window.PreviewRequested += milliseconds =>
                            QueueSafely(() => SeekTo(Math.Max(0, milliseconds - 1_000)));
                        window.DiagnosticsRequested += includeSchedule =>
                            QueueSafely(() => ExportDiagnostics(includeSchedule));
                        window.SettingsRepairRequested += () => QueueSafely(RepairSettings);
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
                                current.Diagnostics,
                                current.HasActiveSchedule);
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
                            snapshot.Diagnostics,
                            snapshot.HasActiveSchedule);

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
                    snapshot.Diagnostics,
                    snapshot.HasActiveSchedule)));
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
        IReadOnlyList<ParseDiagnostic> Diagnostics,
        bool HasActiveSchedule) SnapshotWindow()
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
            pending?.Diagnostics ?? active?.Diagnostics ?? [],
            active is not null);
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
            window.BeginInvoke(new Action(() => RunSafely(() => action(window))));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool QueueWindowAction(Action<CensorWindow> action)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return false;

        CensorWindow? window;
        lock (_windowLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                return false;
            window = _window;
            if (window is null || window.IsDisposed || !window.IsHandleCreated)
            {
                if (_pendingWindowActions.Count == MaxPendingWindowActions)
                    _pendingWindowActions.Dequeue();
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
            window.BeginInvoke(new Action(() => RunSafely(() => action(window))));
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
            RunSafely(() => action(window));
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
        var limits = _settings.Limits;
        ParseResult result;
        if (ScheduleFileKinds.TryGetSubtitleFormat(path, out var subtitleFormat))
            result = SubtitleScheduleText.Import(
                text,
                subtitleFormat,
                limits.MaxTextFileBytes,
                limits.MaxIntervals);
        else
            result = ScheduleText.Parse(
                text,
                limits.MaxTextFileBytes,
                limits.MaxIntervals);

        if (result.IsSuccess &&
            result.Document!.Intervals.Count > limits.MaxIntervals)
        {
            return new(null,
            [
                new(
                    DiagnosticSeverity.Error,
                    1,
                    1,
                    $"В расписании больше {limits.MaxIntervals} интервалов."),
            ]);
        }

        return result;
    }

    private bool SaveSettings()
    {
        try
        {
            lock (_settingsWriteLock)
            {
                ExtensionSettings settings;
                lock (_stateLock)
                    settings = _settings;
                ExtensionSettingsStore.Save(_settingsPath, settings);
            }
            return true;
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            int schema;
            lock (_stateLock)
                schema = _settings.Schema;
            QueueShow(schema == 1
                ? "Не удалось сохранить настройки."
                : "Настройки не сохранены: файл создан более новой версией CensorPlayer. Чтобы перезаписать его, откройте вкладку «Диагностика».");
            return false;
        }
    }

    private void RemoveFilters(IEnumerable<string> labels)
    {
        // Pinned mpv.net CommandV logs per-command errors instead of throwing,
        // so an absent label cannot stop removal of the remaining labels.
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
            try
            {
                if (Volatile.Read(ref _stopping) == 0)
                {
                    _log.Write(
                        "callback-error",
                        _revisions.Snapshot(),
                        new Dictionary<string, object?> { ["error"] = ProtectError(exception) });
                    Show("Ошибка расширения CensorPlayer. Подробности — в журнале mpv.net.");
                }
            }
            catch (Exception reportingException)
            {
                Terminal.WriteError(reportingException, LogModule);
            }
        }
    }

    private void QueueSafely(Action action) =>
        ThreadPool.QueueUserWorkItem(_ => RunSafely(action));

    private void Show(
        string message,
        OperationTicket? expectedTicket = null,
        CancellationToken token = default)
    {
        if (Volatile.Read(ref _stopping) != 0 || token.IsCancellationRequested)
            return;

        if (!_filterGate.Wait(OsdGateTimeout, CancellationToken.None))
            return;
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
                var readFailures = Interlocked.Increment(ref _vfReadFailures);
                if (readFailures == 1)
                    Terminal.WriteError("Не удалось прочитать vf и определить состояние автовосстановления.", LogModule);
                if (readFailures == MaxRecoveryFailures)
                    QueueShow(
                        "Не удалось прочитать цепочку видеофильтров. Автовосстановление остановлено.");
                return;
            }

            Interlocked.Exchange(ref _vfReadFailures, 0);
            if (active.Plan.Chunks.All(chunk => ContainsLabel(filters, chunk.Label)))
            {
                Interlocked.Exchange(ref _recoveryFailures, 0);
                bool clearWarning;
                lock (_stateLock)
                    clearWarning = _windowStatus == "WARNING";
                if (clearWarning)
                    UpdateWindow("ACTIVE", active.Ticket);
                return;
            }

            UpdateWindow("WARNING", active.Ticket);
            bool watchdogEnabled;
            lock (_stateLock)
                watchdogEnabled = _settings.WatchdogEnabled;
            if (!watchdogEnabled)
                return;
            if (Volatile.Read(ref _recoveryFailures) >= MaxRecoveryFailures)
                return;

            try
            {
                if (!RecoverFilters(active))
                    return;
                Interlocked.Exchange(ref _recoveryFailures, 0);
                UpdateWindow("ACTIVE", active.Ticket);
                _log.Write("watchdog-recovered", active.Ticket);
            }
            catch (Exception exception)
            {
                var recoveryFailures = Interlocked.Increment(ref _recoveryFailures);
                Terminal.WriteError(exception, LogModule);
                _log.Write(
                    "watchdog-recovery-error",
                    active.Ticket,
                    new Dictionary<string, object?>
                    {
                        ["consecutiveFailures"] = recoveryFailures,
                        ["error"] = ProtectError(exception, active.SchedulePath),
                    });
                if (recoveryFailures == MaxRecoveryFailures)
                    QueueShow("Не удалось восстановить фильтры. Автовосстановление остановлено.");
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
        bool includePaths;
        string? mediaPath;
        string? activePath;
        string? pendingPath;
        lock (_stateLock)
        {
            includePaths = _settings.Logging.IncludePaths;
            mediaPath = _currentMediaPath;
            activePath = _activeSchedule?.SchedulePath;
            pendingPath = _pendingSchedule?.SchedulePath;
        }
        if (includePaths)
            return text;

        text = DiagnosticsExporter.RedactRootedPaths(text);
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

    private bool RecoverFilters(ActiveSchedule active)
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
                    return false;
                Player.CommandV("vf", "add", chunk.Filter);
            }

            if (!TryGetPropertyString("vf", out var filters) ||
                labels.Any(label => !ContainsLabel(filters, label)))
            {
                throw new InvalidOperationException("После восстановления mpv не сообщил обо всех созданных фильтрах.");
            }

            filtersReady = true;
            Terminal.Write("Восстановлен отсутствующий фильтр цензуры.", LogModule);
            return true;
        }
        finally
        {
            // Fail closed until either the recovered graph or its rollback is verified.
            if (filtersReady || !_revisions.IsCurrentMediaSession(active.Ticket))
                ReleasePauseIfHeld();
        }
    }

    private void HoldPauseIfNeeded(IReadOnlyList<NormalizedInterval> intervals)
    {
        if (_pauseHeldByExtension || !IsIntervalNear(intervals))
            return;
        if (!TryGetPropertyBool("pause", out var wasPaused))
            throw new InvalidOperationException("mpv не сообщил состояние паузы перед заменой фильтров.");
        if (wasPaused)
            return;
        if (!TrySetPropertyBool("pause", true))
            throw new InvalidOperationException("mpv не принял паузу перед заменой фильтров.");
        _pauseHeldByExtension = true;
        if (!TryGetPropertyBool("pause", out var paused) || !paused)
            throw new InvalidOperationException("mpv не подтвердил паузу перед заменой фильтров.");
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
        if (paused &&
            (!TrySetPropertyBool("pause", false) ||
             !TryGetPropertyBool("pause", out paused) ||
             paused))
        {
            return;
        }
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
            _stopped.Task.Wait(ShutdownWaitTimeout);
            return;
        }

        try
        {
            lock (_windowLock)
                _pendingWindowActions.Clear();

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

            if (!_filterGate.Wait(ShutdownWaitTimeout))
            {
                Terminal.WriteError(
                    "Фильтр занят дольше 5 секунд. CensorPlayer больше не ждёт его освобождения.",
                    LogModule);
                return;
            }
            try
            {
                ActiveSchedule? active;
                bool removeAudioFilter;
                lock (_stateLock)
                {
                    active = _activeSchedule;
                    _activeSchedule = null;
                    _pendingSchedule = null;
                    removeAudioFilter =
                        AudioCompressionPresets.Find(_settings.AudioCompressionPreset)?.Filter is not null;
                }
                if (removeFilters)
                {
                    RemoveFilters(active?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
                    if (removeAudioFilter)
                        Player.CommandV("af", "remove", AudioCompressionPresets.FilterLabel);
                }
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
        if (error < 0)
            return false;
        value = raw.ToInt32() != 0;
        return true;
    }

    private bool TrySetPropertyBool(string name, bool value)
    {
        if (Player.Handle == IntPtr.Zero || Volatile.Read(ref _stopping) != 0)
            return false;

        long raw = value ? 1 : 0;
        return mpv_set_property(
            Player.Handle,
            GetUtf8Bytes(name),
            mpv_format.MPV_FORMAT_FLAG,
            ref raw) >= 0;
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
        IReadOnlyList<NormalizedInterval> Intervals,
        IReadOnlyList<ParseDiagnostic> Diagnostics);

    private sealed record PendingSchedule(
        OperationTicket Ticket,
        string? SchedulePath,
        string? SourceHash,
        ScheduleDocument Document,
        FilterPlan Plan,
        IReadOnlyList<NormalizedInterval> Intervals,
        IReadOnlyList<ParseDiagnostic> Diagnostics);
}
