using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private const ulong FilterObserverUserData = 0x43454E534F52UL;
    private const string ReadyProperty = "user-data/censor/ready";
    private const string FutureSettingsMessage =
        "Настройки не сохранены: файл создан более новой версией CensorPlayer. " +
        "Чтобы перезаписать его, откройте вкладку «Диагностика».";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OsdGateTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(5);

    // Nested runtime acquisition order: _filterGate -> _stateLock -> _windowLock.
    // _settingsWriteLock may wrap _stateLock, but never nests with _filterGate or _windowLock.
    // Session-token cancellation callbacks must never acquire _stateLock.
    private readonly Lock _stateLock = new();
    private readonly Lock _windowLock = new();
    private readonly Lock _settingsWriteLock = new();
    private readonly Queue<(Action<CensorWindow> Run, Action? Cancel)>
        _pendingWindowActions = new();
    private readonly MediaSessionCoordinator _revisions = new();
    private readonly SemaphoreSlim _filterGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Timer _watchdog;
    private readonly ExtensionLog _log;
    private readonly string _localDataRoot;
    private readonly string _settingsPath;
    private readonly byte[] _pathHashKey;
    private CancellationTokenSource _operationCancellation = new();
    private Task _sessionCleanup = Task.CompletedTask;
    private ActiveSchedule? _activeSchedule;
    private PendingSchedule? _pendingSchedule;
    private OperationTicket? _scheduleLoadTicket;
    private volatile ExtensionSettings _settings;
    private long? _currentDurationMs;
    private string? _currentMediaPath;
    private CensorWindow? _window;
    private Thread? _windowThread;
    private string _windowStatus = "NO SCHEDULE";
    private int _afReadFailures;
    private int _audioMismatchReported;
    private int _audioRecoveryFailures;
    private int _disposed;
    private bool _pauseHeldByExtension;
    private bool _pendingWindowActionDropReported;
    private int _recoveryFailures;
    private int _stopping;
    private int _vfReadFailures;

    public Extension()
    {
        _localDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CensorPlayer");
        _settingsPath = Path.Combine(_localDataRoot, "settings.json");
        _pathHashKey = ExtensionLog.LoadOrCreatePathHashKey(
            Path.Combine(_localDataRoot, "path-hash.key"));
        var loadedSettings = ExtensionSettingsStore.Load(_settingsPath);
        _settings = loadedSettings.Settings;
        _log = new(
            Path.Combine(_localDataRoot, "Logs"),
            _settings.Logging.RetentionDays,
            _settings.Logging.Level);
        if (!_log.IsEnabled)
            Terminal.WriteError("Журнал CensorPlayer отключён: не удалось открыть каталог для записи.", LogModule);
        foreach (var warning in loadedSettings.Warnings)
            Terminal.WriteError(warning, LogModule);

        try
        {
            Player = Global.Player.CreateNewPlayer("censor");
        }
        catch
        {
            _log.Dispose();
            throw;
        }
        System.Threading.Timer? watchdog = null;
        try
        {
            watchdog = new(CheckFilters, null, Timeout.Infinite, Timeout.Infinite);
            _watchdog = watchdog;
            Player.StartFile += OnStartFile;
            Player.FileLoaded += OnFileLoaded;
            Player.EndFile += OnEndFile;
            Player.Shutdown += OnShutdown;
            Player.ClientMessage += OnClientMessage;
            Global.Player.Shutdown += OnShutdown;
            ObserveFilterProperty("vf");
            ObserveFilterProperty("af");
            if (_settings.WatchdogEnabled)
                _watchdog.Change(_settings.WatchdogIntervalMs, _settings.WatchdogIntervalMs);
            Player.SetPropertyString(ReadyProperty, "yes");
        }
        catch
        {
            UnsubscribePlayerEvents();
            watchdog?.Dispose();
            _log.Dispose();
            throw;
        }
    }

    public MpvClient Player { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        UnsubscribePlayerEvents();

        StopRuntime(removeFilters: true);
        CloseWindow(waitForExit: true);
        _watchdog.Dispose();
        lock (_stateLock)
        {
            _operationCancellation.Dispose();
            _revisions.Dispose();
        }
        // mpv.net creates one Extension per process. Queued callbacks can still observe
        // _stopping after Dispose, so keep their gate alive for the remaining process lifetime.
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private void UnsubscribePlayerEvents()
    {
        Player.StartFile -= OnStartFile;
        Player.FileLoaded -= OnFileLoaded;
        Player.EndFile -= OnEndFile;
        Player.Shutdown -= OnShutdown;
        Player.ClientMessage -= OnClientMessage;
        Global.Player.Shutdown -= OnShutdown;
        lock (Player.StringPropChangeActions)
        {
            if (Player.StringPropChangeActions.TryGetValue("vf", out var actions))
                actions.Remove(OnFiltersChanged);
            if (Player.StringPropChangeActions.TryGetValue("af", out actions))
                actions.Remove(OnFiltersChanged);
        }
        if (Player.Handle != IntPtr.Zero)
            _ = mpv_unobserve_property(Player.Handle, FilterObserverUserData);
    }

    private void ObserveFilterProperty(string name)
    {
        lock (Player.StringPropChangeActions)
        {
            if (Player.StringPropChangeActions.ContainsKey(name))
                throw new InvalidOperationException($"Наблюдение свойства mpv «{name}» уже зарегистрировано.");
            var error = mpv_observe_property(
                Player.Handle,
                FilterObserverUserData,
                name,
                mpv_format.MPV_FORMAT_STRING);
            if (error < 0)
                throw new InvalidOperationException(
                    $"Не удалось наблюдать свойство mpv «{name}»: {GetError(error)}");
            Player.StringPropChangeActions[name] = [OnFiltersChanged];
        }
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

    private void OnShutdown() => RunSafely(() =>
    {
        StopRuntime(removeFilters: false);
        CloseWindow(waitForExit: false);
    });

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
        if (CensorClientMessage.Parse(args) is not { } message)
            return;

        switch (message.Kind)
        {
            case CensorClientCommandKind.Open:
                ShowToolWindow();
                break;
            case CensorClientCommandKind.Pick:
                ShowToolWindow();
                QueueWindowAction(window => window.OpenSchedulePicker());
                break;
            case CensorClientCommandKind.Load:
                if (message.Argument is { } path)
                    LoadManualSchedule(path);
                else
                    ShowToolWindow();
                break;
            case CensorClientCommandKind.Reload:
                ReloadSchedule();
                break;
            case CensorClientCommandKind.Apply:
                ApplyPendingSchedule();
                break;
            case CensorClientCommandKind.Disable:
                DisableSchedule();
                break;
            case CensorClientCommandKind.Authoring:
                var capturedTimeMs = message.Argument is
                    "mark-start" or "mark-end" or "set-start" or "set-end"
                    ? GetCurrentTimeMs()
                    : null;
                QueueWindowAction(window =>
                    window.HandleAuthoringCommand(message.Argument!, capturedTimeMs));
                break;
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
            _scheduleLoadTicket = null;
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
            ResetAudioWatchdogState();
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
            token = _revisions.SessionToken;
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

        bool autoLoadSidecar;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0 ||
                !_revisions.IsCurrentMediaSession(ticket) ||
                token.IsCancellationRequested)
            {
                return;
            }
            _currentMediaPath = mediaPath;
            _currentDurationMs = durationMs;
            ticket = _revisions.Snapshot();
            token = _operationCancellation.Token;
            autoLoadSidecar = _settings.AutoLoadSidecar;
            if (autoLoadSidecar)
                _scheduleLoadTicket = ticket;
        }
        if (!autoLoadSidecar)
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
        catch (Exception exception) when (
            token.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", ticket, token))
                Show("Не удалось загрузить расписание рядом с фильмом. Подробности — в журнале mpv.net.", ticket, token);
        }
        finally
        {
            FinishScheduleLoad(ticket);
        }
    }

    private async Task<string?> ChooseSidecarAsync(
        IReadOnlyList<string> paths,
        CancellationToken token)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCancellation.CancelAfter(DialogTimeout);
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
        }, () => result.TrySetResult(null)))
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
        CancellationToken token,
        string readyStatus = "READY TO APPLY")
    {
        try
        {
            var settings = _settings;
            var totalTimer = Stopwatch.StartNew();
            UpdateWindow("LOADING", ticket, token);
            if (new FileInfo(schedulePath).Length > settings.Limits.MaxTextFileBytes)
                throw new InvalidDataException(
                    $"Размер расписания превышает {settings.Limits.MaxTextFileBytes} байт.");
            var bytes = await File.ReadAllBytesAsync(schedulePath, token).ConfigureAwait(false);
            var text = StrictUtf8.GetString(bytes);
            var sourceHash = ComputeHash(bytes);
            token.ThrowIfCancellationRequested();
            var parseTimer = Stopwatch.StartNew();
            var parsed = ScheduleFileKinds.Parse(schedulePath, text, settings.Limits);
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
            if (!MatchesCurrentDuration(
                    document.Metadata.MediaDurationMs,
                    settings.DurationToleranceMs))
            {
                token.ThrowIfCancellationRequested();
                var applyAnyway = !automatic &&
                    await ConfirmDurationMismatchAsync(token).ConfigureAwait(false);
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
                settings.ResolveNormalizationOptions(document.Metadata));
            var blurSettings = settings.Blur;
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
                        settings.Logging.IncludePaths,
                        _pathHashKey),
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
                UpdateWindow(readyStatus, ticket, token);
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
        catch (Exception exception) when (
            token.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            _log.Write(
                "schedule-load-error",
                ticket,
                new Dictionary<string, object?> { ["error"] = ProtectError(exception, schedulePath) },
                ExtensionLogLevel.Error);
            if (UpdateWindow("ERROR", ticket, token))
            {
                Show(
                    exception is DecoderFallbackException
                        ? "Не удалось прочитать расписание: сохраните файл в кодировке UTF-8."
                        : "Не удалось загрузить или применить расписание. Если воспроизведение осталось на паузе, выберите «Цензура → Отключить». Подробности — в журнале mpv.net.",
                    ticket,
                    token);
            }
        }
        finally
        {
            if (!automatic)
                FinishScheduleLoad(ticket);
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
                !FilterReadback.MatchesBlurPlan(filters, plan))
            {
                throw new InvalidOperationException("После применения mpv не подтвердил точный набор фильтров цензуры.");
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
                        _settings.Logging.IncludePaths,
                        _pathHashKey),
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
                    if (!TryGetPropertyString("vf", out var restoredFilters) ||
                        !FilterReadback.MatchesBlurPlan(restoredFilters, previous.Plan))
                    {
                        throw new InvalidOperationException(
                            "После отката mpv не подтвердил точный набор прежних фильтров.");
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
                    },
                    ExtensionLogLevel.Error);
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

    private bool MatchesCurrentDuration(long? expectedMs, long toleranceMs)
    {
        if (expectedMs is null)
            return true;

        lock (_stateLock)
            return _currentDurationMs is { } actualMs &&
                Math.Abs(actualMs - expectedMs.Value) <= toleranceMs;
    }

    private void LoadManualSchedule(
        string schedulePath,
        string readyStatus = "READY TO APPLY")
    {
        if (!File.Exists(schedulePath) ||
            !ScheduleFileKinds.IsSupportedPath(schedulePath))
        {
            UpdateWindow("INVALID SCHEDULE PATH");
            Show("Не удалось открыть расписание: путь некорректен или формат файла не поддерживается.");
            return;
        }

        var operation = StartNewOperation(scheduleLoad: true);
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
            SaveSettings(skipUnsupportedSchema: true);

        UpdateWindow("LOADING", operation.Value.Ticket, operation.Value.Token);
        _ = LoadScheduleAsync(
            operation.Value.Ticket,
            schedulePath,
            automatic: false,
            operation.Value.Token,
            readyStatus);
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
        catch (Exception exception) when (
            token.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", pending.Ticket, token))
            {
                Show(
                    "Не удалось применить расписание. Если воспроизведение осталось на паузе, выберите «Цензура → Отключить».",
                    pending.Ticket,
                    token);
            }
        }
    }

    private void ApplyDraftDocument(ScheduleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = _settings;
        var validation = ScheduleDraft.Validate(
            document,
            settings.Limits.MaxIntervals,
            settings.Limits.MaxTextFileBytes);
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
            settings.ResolveNormalizationOptions(document.Metadata));
        var plan = FilterCompiler.Compile(normalized, settings.Blur);
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
        bool normalizationChanged;
        bool hasSchedule;
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
            normalizationChanged =
                _settings.LeadInMs != settings.LeadInMs ||
                _settings.LeadOutMs != settings.LeadOutMs ||
                _settings.MergeGapMs != settings.MergeGapMs;
            hasSchedule = _activeSchedule is not null || _pendingSchedule is not null;
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
            Show(settings.Schema == 1 ? warnings[0] : FutureSettingsMessage);
            return;
        }

        InvokeWindow(window => window.SetSettings(settings));
        try
        {
            _watchdog.Change(
                settings.WatchdogEnabled ? settings.WatchdogIntervalMs : Timeout.Infinite,
                settings.WatchdogEnabled ? settings.WatchdogIntervalMs : Timeout.Infinite);
        }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _disposed) != 0)
        {
        }
        if (SaveSettings())
        {
            Show(normalizationChanged && hasSchedule
                ? "Настройки сохранены. Запас до и после интервала и порог объединения применятся после перезагрузки расписания."
                : "Настройки сохранены.");
        }
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
        if (SaveSettings(preserveBeforeRepair: true))
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
        if (ScheduleDraft.Validate(
                document,
                settings.Limits.MaxIntervals,
                settings.Limits.MaxTextFileBytes)
            .Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            Show("Расписание не сохранено: исправьте ошибки черновика.");
            return;
        }
        choosePath |= !sourceMatchesCurrent || expectedHash is null;
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

        if (!choosePath && File.Exists(path))
        {
            if (!TryReadScheduleHash(path, out var currentHash))
                return;
            if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
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
        }

        if (ScheduleFileKinds.TryGetSubtitleFormat(path, out var exportFormat))
        {
            if (!ConfirmSubtitleExport())
                return;
            try
            {
                AtomicFile.WriteUtf8Text(
                    path,
                    SubtitleScheduleText.Export(document, exportFormat),
                    Path.GetFullPath(path) + ".bak");
            }
            catch (Exception exception) when (IsFileWriteException(exception))
            {
                ReportFileWriteError(
                    "schedule-export-error",
                    "Не удалось экспортировать файл. Проверьте доступ к папке и свободное место.",
                    exception,
                    path);
                return;
            }
            Show($"Файл {Path.GetFileName(path)} экспортирован. Черновик не отмечен как сохранённый.");
            return;
        }

        var normalized = ScheduleNormalizer.Normalize(
            document.Intervals,
            settings.ResolveNormalizationOptions(document.Metadata));
        var plan = FilterCompiler.Compile(normalized, settings.Blur);
        byte[] savedBytes;
        try
        {
            savedBytes = AtomicScheduleWriter.Write(
                path,
                document,
                settings.Limits.MaxIntervals,
                settings.Limits.MaxTextFileBytes);
        }
        catch (Exception exception) when (IsFileWriteException(exception))
        {
            ReportFileWriteError(
                "schedule-save-error",
                "Не удалось сохранить расписание. Проверьте доступ к файлу, блокировку другой программой и свободное место.",
                exception,
                path);
            return;
        }
        var savedHash = ComputeHash(savedBytes);

        var markWindowSaved = false;
        var saveSettings = false;
        string? savedScheduleDirectory;
        // This gate prevents ApplyAsync from committing between the save decision
        // and the corresponding pending/active state update.
        _filterGate.Wait();
        try
        {
            lock (_stateLock)
            {
                var decision = DraftReconciliation.DecideAfterSave(
                    IsCurrent(saveTicket),
                    sourceMatchesCurrent,
                    plan.Chunks.Count == 0,
                    _pendingSchedule is not null,
                    _activeSchedule is not null);
                markWindowSaved = decision.MarkWindowSaved;
                switch (decision.RuntimeAction)
                {
                    case SavedDraftRuntimeAction.ClearPending:
                        _pendingSchedule = null;
                        break;
                    case SavedDraftRuntimeAction.UpdatePending when _pendingSchedule is { } pending:
                        _pendingSchedule = pending with
                        {
                            SchedulePath = path,
                            SourceHash = savedHash,
                            Document = document,
                            Plan = plan,
                            Intervals = normalized,
                        };
                        break;
                    case SavedDraftRuntimeAction.StageFromActive:
                        _pendingSchedule = new(
                            saveTicket,
                            path,
                            savedHash,
                            document,
                            plan,
                            normalized,
                            []);
                        break;
                }
                if (decision.UpdateRuntime && _activeSchedule is { } active)
                {
                    _activeSchedule = active with
                    {
                        SchedulePath = path,
                        SourceHash = savedHash,
                    };
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
            SaveSettings(skipUnsupportedSchema: true);
        if (markWindowSaved)
        {
            InvokeWindow(window =>
            {
                if (IsCurrent(saveTicket))
                    window.MarkSaved(path, document, savedScheduleDirectory);
            });
        }
        Show(markWindowSaved
            ? $"Файл {Path.GetFileName(path)} сохранён."
            : $"Файл {Path.GetFileName(path)} сохранён, но текущее расписание в проигрывателе уже изменилось.");
    }

    private bool TryReadScheduleHash(string path, out string hash)
    {
        try
        {
            hash = ComputeHash(File.ReadAllBytes(path));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            hash = "";
            Terminal.WriteError(exception, LogModule);
            _log.Write(
                "schedule-hash-read-error",
                _revisions.Snapshot(),
                new Dictionary<string, object?>
                {
                    ["error"] = ProtectError(exception, path),
                },
                ExtensionLogLevel.Error);
            Show("Расписание не сохранено: не удалось проверить, не изменён ли файл другой программой.");
            return false;
        }
    }

    private void SeekTo(long milliseconds)
    {
        milliseconds = Math.Max(0, milliseconds);
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
                path = ExtensionLog.ProtectPath(
                    mediaPath,
                    includePath: false,
                    _pathHashKey),
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
        try
        {
            DiagnosticsExporter.Export(path, snapshot, includeSchedule, _pathHashKey);
        }
        catch (Exception exception) when (IsFileWriteException(exception))
        {
            ReportFileWriteError(
                "diagnostics-export-error",
                "Не удалось сохранить диагностический ZIP-архив. Проверьте доступ к папке и свободное место.",
                exception,
                path);
            return;
        }
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

        LoadManualSchedule(path, "RELOADED");
    }

    private void ChangeBlurPreset(BlurSettings settings) =>
        ChangeBlurPreset(settings, useCurrentSelection: false);

    private void ChangeBlurPreset(
        BlurSettings? requestedSettings,
        bool useCurrentSelection)
    {
        ActiveSchedule? active = null;
        PendingSchedule? pending = null;
        FilterPlan? activePlan = null;
        BlurSettings previousBlur;
        BlurSettings settings;
        CancellationToken token = default;
        OperationTicket ticket = default;
        CancellationTokenSource? previousCancellation = null;
        bool hasMedia;
        bool loadInProgress;
        bool selectionChanged;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                return;
            settings = useCurrentSelection
                ? _settings.Blur
                : requestedSettings ?? throw new ArgumentNullException(nameof(requestedSettings));
            selectionChanged = _settings.Blur != settings;
            if (!useCurrentSelection && !selectionChanged)
                return;

            previousBlur = _activeSchedule?.Plan.Blur ?? _settings.Blur;
            hasMedia = !string.IsNullOrWhiteSpace(_currentMediaPath);
            loadInProgress = _scheduleLoadTicket is { } loadTicket &&
                _revisions.IsCurrent(loadTicket);
            if (!hasMedia || loadInProgress)
            {
                ticket = _revisions.Snapshot();
                token = _operationCancellation.Token;
            }
            else if (_pendingSchedule is { } currentPending)
            {
                if (useCurrentSelection && currentPending.Plan.Blur == settings)
                    return;
                // ponytail: Compiling under the state lock is bounded and trivial for
                // normal 10–20-scene plans; revisit only if measured workloads grow.
                var pendingPlan = FilterCompiler.Compile(currentPending.Intervals, settings);
                // Invalidate an ApplyPendingScheduleAsync snapshot before replacing its plan.
                var operation = BeginOperationUnsafe(clearPending: false, scheduleLoad: false);
                ticket = operation.Ticket;
                token = operation.Token;
                previousCancellation = operation.PreviousCancellation;
                _pendingSchedule = currentPending with
                {
                    Ticket = ticket,
                    Plan = pendingPlan,
                };
                pending = _pendingSchedule;
            }
            else if (_activeSchedule is { } currentActive)
            {
                if (useCurrentSelection && currentActive.Plan.Blur == settings)
                    return;
                activePlan = FilterCompiler.Compile(currentActive.Intervals, settings);
                var operation = BeginOperationUnsafe(clearPending: false, scheduleLoad: false);
                ticket = operation.Ticket;
                token = operation.Token;
                previousCancellation = operation.PreviousCancellation;
                active = _activeSchedule;
            }
            else
            {
                ticket = _revisions.Snapshot();
                token = _operationCancellation.Token;
            }
            if (!useCurrentSelection)
                _settings = _settings with { Blur = settings };
        }

        if (previousCancellation is not null)
        {
            previousCancellation.Cancel();
            previousCancellation.Dispose();
        }

        if (loadInProgress)
        {
            if (selectionChanged && SaveSettings())
            {
                Show("Пресет размытия сохранён. Текущая загрузка расписания продолжится.");
            }
            return;
        }

        if (!hasMedia)
        {
            if (selectionChanged && SaveSettings())
            {
                Show("Пресет размытия сохранён и применится после открытия фильма.");
            }
            return;
        }

        if (pending is not null)
        {
            var saved = !selectionChanged || SaveSettings();
            UpdateWindow("READY TO APPLY", ticket, token);
            if (selectionChanged && saved)
            {
                Show(
                    "Новый пресет размытия сохранён. Чтобы применить его к подготовленному расписанию, нажмите «Применить».",
                    ticket,
                    token);
            }
            return;
        }

        if (active is null)
        {
            if (selectionChanged)
                SaveSettings();
            return;
        }

        UpdateWindow("APPLYING", ticket, token);
        _ = ApplyBlurPresetAsync(
            ticket,
            active,
            activePlan!,
            settings,
            previousBlur,
            token);
    }

    private void FinishScheduleLoad(OperationTicket ticket)
    {
        var reapplyBlur = false;
        lock (_stateLock)
        {
            if (_scheduleLoadTicket != ticket)
                return;
            _scheduleLoadTicket = null;
            if (!_revisions.IsCurrent(ticket))
                return;
            var appliedBlur = _pendingSchedule?.Plan.Blur ?? _activeSchedule?.Plan.Blur;
            reapplyBlur = appliedBlur is not null && appliedBlur != _settings.Blur;
        }

        if (reapplyBlur)
            ChangeBlurPreset(requestedSettings: null, useCurrentSelection: true);
    }

    private async Task ApplyBlurPresetAsync(
        OperationTicket ticket,
        ActiveSchedule active,
        FilterPlan plan,
        BlurSettings requestedBlur,
        BlurSettings previousBlur,
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
            if (UpdateWindow("ACTIVE", ticket, token))
                SaveSettings();
            else
                SaveBlurPresetIfStillSelected(requestedBlur);
        }
        catch (Exception exception) when (
            token.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException)
        {
            SaveBlurPresetIfStillSelected(requestedBlur);
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            var reverted = RevertBlurPreset(ticket, requestedBlur, previousBlur);
            if (UpdateWindow("ERROR", ticket, token))
            {
                Show(
                    reverted
                        ? "Не удалось изменить степень размытия. Возвращена предыдущая настройка; проверьте состояние фильтра."
                        : "Не удалось изменить степень размытия. Проверьте состояние фильтра.",
                    ticket,
                    token);
            }
        }
    }

    private bool RevertBlurPreset(
        OperationTicket ticket,
        BlurSettings requestedBlur,
        BlurSettings previousBlur)
    {
        lock (_stateLock)
        {
            if (!IsCurrent(ticket) || _settings.Blur != requestedBlur)
                return false;
            _settings = _settings with { Blur = previousBlur };
        }

        InvokeWindow(window => window.SetBlurPreset(previousBlur));
        SaveSettings();
        return true;
    }

    private void SaveBlurPresetIfStillSelected(BlurSettings requestedBlur)
    {
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0 || _settings.Blur != requestedBlur)
                return;
        }
        SaveSettings();
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
                    _settings.AudioCompressionPreset) ?? AudioCompressionPresets.Off;
                hasMedia = !string.IsNullOrWhiteSpace(_currentMediaPath);
                if (!hasMedia)
                {
                    _settings = _settings with { AudioCompressionPreset = preset.Id };
                    ResetAudioWatchdogState();
                }
            }

            if (hasMedia)
            {
                try
                {
                    ApplyAudioCompressionPreset(preset);
                    ResetAudioWatchdogState();
                    lock (_stateLock)
                        _settings = _settings with { AudioCompressionPreset = preset.Id };
                }
                catch (Exception exception)
                {
                    applyError = exception;
                    try
                    {
                        ApplyAudioCompressionPreset(previousPreset);
                        ResetAudioWatchdogState();
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
            if (!SaveSettings())
                return;
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
            },
            ExtensionLogLevel.Error);
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
                    preset = AudioCompressionPresets.Find(
                        _settings.AudioCompressionPreset) ?? AudioCompressionPresets.Off;
                }
                var matches = TryGetPropertyString("af", out var filters) &&
                    (preset.Filter is null
                        ? !ContainsLabel(filters, AudioCompressionPresets.FilterLabel)
                        : FilterReadback.MatchesSingle(
                            filters,
                            AudioCompressionPresets.FilterLabel,
                            preset.Filter));
                if (!matches)
                    ApplyAudioCompressionPreset(preset);
                ResetAudioWatchdogState();
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
                },
                ExtensionLogLevel.Error);
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
            !FilterReadback.MatchesSingle(
                filters,
                AudioCompressionPresets.FilterLabel,
                preset.Filter))
        {
            throw new InvalidOperationException(
                "mpv не подтвердил точный фильтр компрессии звука.");
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

    private (OperationTicket Ticket, CancellationToken Token)? StartNewOperation(
        bool scheduleLoad = false)
    {
        (OperationTicket Ticket, CancellationToken Token, CancellationTokenSource PreviousCancellation)
            operation;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0 || string.IsNullOrEmpty(_currentMediaPath))
                return null;

            operation = BeginOperationUnsafe(
                clearPending: true,
                scheduleLoad: scheduleLoad);
        }

        operation.PreviousCancellation.Cancel();
        operation.PreviousCancellation.Dispose();

        return (operation.Ticket, operation.Token);
    }

    // Caller must hold _stateLock. Cancellation stays outside the lock.
    private (
        OperationTicket Ticket,
        CancellationToken Token,
        CancellationTokenSource PreviousCancellation) BeginOperationUnsafe(
            bool clearPending,
            bool scheduleLoad)
    {
        var ticket = _revisions.BeginOperation();
        var previousCancellation = _operationCancellation;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _revisions.SessionToken);
        _scheduleLoadTicket = scheduleLoad ? ticket : null;
        if (clearPending)
            _pendingSchedule = null;
        else if (_pendingSchedule is { } pending)
            _pendingSchedule = pending with { Ticket = ticket };
        if (_activeSchedule is { } active)
            _activeSchedule = active with { Ticket = ticket };
        return (ticket, _operationCancellation.Token, previousCancellation);
    }

    private async Task<bool> ConfirmDurationMismatchAsync(CancellationToken token)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCancellation.CancelAfter(DialogTimeout);
        var dialogToken = timeoutCancellation.Token;
        var result = new TaskCompletionSource<bool>(
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
                result.TrySetResult(window.ConfirmDurationMismatch());
            }
            catch (Exception exception)
            {
                result.TrySetException(exception);
            }
        }, () => result.TrySetResult(false)))
        {
            return false;
        }

        using var registration =
            dialogToken.Register(() => result.TrySetCanceled(dialogToken));
        try
        {
            return await result.Task.WaitAsync(dialogToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
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
                    var messageLoopCompleted = false;
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
                        window.PersistenceError += (message, exception) =>
                            QueueSafely(() => ReportWindowPersistenceError(message, exception));
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
                        {
                            Application.Run(window);
                            messageLoopCompleted = true;
                        }
                    }
                    catch (Exception exception)
                    {
                        Terminal.WriteError(exception, LogModule);
                    }
                    finally
                    {
                        bool restart;
                        Action[] canceledActions;
                        lock (_windowLock)
                        {
                            _window = null;
                            _windowThread = null;
                            if (!messageLoopCompleted)
                            {
                                canceledActions = _pendingWindowActions
                                    .Select(item => item.Cancel)
                                    .OfType<Action>()
                                    .ToArray();
                                _pendingWindowActions.Clear();
                                _pendingWindowActionDropReported = false;
                            }
                            else
                            {
                                canceledActions = [];
                            }
                            restart = messageLoopCompleted &&
                                Volatile.Read(ref _stopping) == 0 &&
                                _pendingWindowActions.Count > 0;
                        }
                        foreach (var cancel in canceledActions)
                            RunSafely(cancel);
                        if (restart)
                            ShowToolWindow();
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

        try
        {
            window.BeginInvoke(new Action(() =>
            {
                var snapshot = SnapshotWindow();
                window.UpdateState(
                    snapshot.MediaPath,
                    snapshot.SchedulePath,
                    snapshot.Status,
                    snapshot.MediaDurationMs,
                    snapshot.Document,
                    snapshot.Diagnostics,
                    snapshot.HasActiveSchedule);
            }));
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

    private bool QueueWindowAction(
        Action<CensorWindow> action,
        Action? cancel = null)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return false;

        CensorWindow? window;
        var reportDroppedAction = false;
        Action? cancelDroppedAction = null;
        lock (_windowLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                return false;
            window = _window;
            if (window is null || window.IsDisposed || !window.IsHandleCreated)
            {
                if (_pendingWindowActions.Count == MaxPendingWindowActions)
                {
                    cancelDroppedAction = _pendingWindowActions.Dequeue().Cancel;
                    if (!_pendingWindowActionDropReported)
                    {
                        _pendingWindowActionDropReported = true;
                        reportDroppedAction = true;
                    }
                }
                _pendingWindowActions.Enqueue((action, cancel));
                window = null;
            }
        }

        if (cancelDroppedAction is not null)
            RunSafely(cancelDroppedAction);
        if (reportDroppedAction)
        {
            // ponytail: Keep startup input bounded; report the first drop instead
            // of retaining an unbounded queue when WinForms cannot start.
            _log.Write(
                "window-action-dropped",
                _revisions.Snapshot(),
                level: ExtensionLogLevel.Warning);
            QueueShow("Окно открывается слишком долго: одна из ранних команд пропущена.");
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
        (Action<CensorWindow> Run, Action? Cancel)[] actions;
        lock (_windowLock)
        {
            actions = _pendingWindowActions.ToArray();
            _pendingWindowActions.Clear();
            _pendingWindowActionDropReported = false;
        }
        foreach (var action in actions)
            RunSafely(() => action.Run(window));
    }

    private void CloseWindow(bool waitForExit)
    {
        CensorWindow? window;
        Thread? windowThread;
        lock (_windowLock)
        {
            window = _window;
            windowThread = _windowThread;
        }

        if (window is not null && !window.IsDisposed && window.IsHandleCreated)
        {
            try
            {
                window.BeginInvoke(new Action(window.Shutdown));
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (waitForExit &&
            windowThread is not null &&
            windowThread != Thread.CurrentThread &&
            windowThread.IsAlive &&
            !windowThread.Join(ShutdownWaitTimeout))
        {
            Terminal.WriteError(
                "Окно CensorPlayer не закрылось за 5 секунд; последние изменения черновика могли не сохраниться.",
                LogModule);
        }
    }

    private bool IsCurrent(OperationTicket ticket) =>
        Volatile.Read(ref _stopping) == 0 && _revisions.IsCurrent(ticket);

    private bool SaveSettings(
        bool preserveBeforeRepair = false,
        bool skipUnsupportedSchema = false)
    {
        try
        {
            lock (_settingsWriteLock)
            {
                ExtensionSettings settings;
                lock (_stateLock)
                    settings = _settings;
                if (skipUnsupportedSchema && settings.Schema != 1)
                    return true;
                if (preserveBeforeRepair)
                    ExtensionSettingsStore.SaveAfterRepair(_settingsPath, settings);
                else
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
                : FutureSettingsMessage);
            return false;
        }
    }

    private void RemoveFilters(
        IEnumerable<string> labels,
        bool allowReadDuringShutdown = false)
    {
        // Pinned mpv.net CommandV logs per-command errors instead of throwing,
        // so an absent label cannot stop removal of the remaining labels.
        var ownedLabels = labels.ToHashSet(StringComparer.Ordinal);
        if (TryGetPropertyString("vf", out var filters, allowReadDuringShutdown))
            ownedLabels.UnionWith(FilterReadback.FindOwnedBlurLabels(filters));
        foreach (var label in ownedLabels)
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
                        new Dictionary<string, object?> { ["error"] = ProtectError(exception) },
                        ExtensionLogLevel.Error);
                    QueueShow("Ошибка расширения CensorPlayer. Подробности — в журнале mpv.net.");
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
        {
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (extension, text, ticket, cancellationToken) =
                    ((Extension, string, OperationTicket?, CancellationToken))state!;
                extension.ShowDeferred(text, ticket, cancellationToken);
            }, (this, message, expectedTicket, token));
            return;
        }
        try
        {
            ShowUnderGate(message, expectedTicket, token);
        }
        finally
        {
            _filterGate.Release();
        }
    }

    private void ShowDeferred(
        string message,
        OperationTicket? expectedTicket,
        CancellationToken token)
    {
        try
        {
            if (Volatile.Read(ref _stopping) != 0 || token.IsCancellationRequested)
                return;
            if (!_filterGate.Wait(ShutdownWaitTimeout, CancellationToken.None))
            {
                Terminal.WriteError(
                    "Не удалось показать сообщение: цепочка фильтров занята дольше 5 секунд.",
                    LogModule);
                return;
            }
            try
            {
                ShowUnderGate(message, expectedTicket, token);
            }
            finally
            {
                _filterGate.Release();
            }
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
        }
    }

    private void ShowUnderGate(
        string message,
        OperationTicket? expectedTicket,
        CancellationToken token)
    {
        if (Volatile.Read(ref _stopping) == 0 &&
            !token.IsCancellationRequested &&
            (!expectedTicket.HasValue || IsCurrent(expectedTicket.Value)))
        {
            Player.CommandV("show-text", $"Censor: {message}", "5000");
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
            AudioCompressionPresetDefinition audioPreset;
            bool watchdogEnabled;
            bool hasMedia;
            lock (_stateLock)
            {
                active = _activeSchedule;
                audioPreset = AudioCompressionPresets.Find(
                    _settings.AudioCompressionPreset) ?? AudioCompressionPresets.Off;
                watchdogEnabled = _settings.WatchdogEnabled;
                hasMedia = !string.IsNullOrWhiteSpace(_currentMediaPath);
            }
            var ticket = _revisions.Snapshot();

            if (active is not null && IsCurrent(active.Ticket))
                CheckVideoFilters(active, watchdogEnabled);
            if (hasMedia && audioPreset.Filter is not null)
                CheckAudioFilter(audioPreset, ticket, watchdogEnabled);
            else
                ResetAudioWatchdogState();
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

    private void CheckVideoFilters(ActiveSchedule active, bool watchdogEnabled)
    {
        // ponytail: String readback plus per-chunk comparison is trivial for
        // normal 10–20-scene plans; precompute only after measured growth.
        if (!TryGetPropertyString("vf", out var filters))
        {
            var readFailures = Interlocked.Increment(ref _vfReadFailures);
            if (readFailures == 1)
                Terminal.WriteError("Не удалось прочитать vf и определить состояние автовосстановления.", LogModule);
            if (readFailures == MaxRecoveryFailures)
                QueueShow(
                    "Не удалось прочитать цепочку видеофильтров. Автовосстановление продолжит попытки.");
            return;
        }

        Interlocked.Exchange(ref _vfReadFailures, 0);
        if (FilterReadback.MatchesBlurPlan(filters, active.Plan))
        {
            Interlocked.Exchange(ref _recoveryFailures, 0);
            bool clearWarning;
            lock (_stateLock)
                clearWarning = _windowStatus == "WARNING";
            if (clearWarning)
                UpdateWindow("ACTIVE", active.Ticket);
            return;
        }

        bool warningAlreadyShown;
        lock (_stateLock)
            warningAlreadyShown = _windowStatus == "WARNING";
        if (!warningAlreadyShown && UpdateWindow("WARNING", active.Ticket))
        {
            _log.Write(
                "watchdog-mismatch",
                active.Ticket,
                new Dictionary<string, object?> { ["autoRecoveryEnabled"] = watchdogEnabled },
                ExtensionLogLevel.Warning);
            if (!watchdogEnabled)
                QueueShow("Цепочка фильтров цензуры изменена. Автовосстановление выключено.");
        }
        if (!watchdogEnabled || Volatile.Read(ref _recoveryFailures) >= MaxRecoveryFailures)
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
                },
                ExtensionLogLevel.Error);
            if (recoveryFailures == MaxRecoveryFailures)
            {
                QueueShow(
                    "Не удалось восстановить фильтры. Автовосстановление остановлено. " +
                    "Если воспроизведение осталось на паузе, выберите «Цензура → Отключить».");
            }
        }
    }

    private void CheckAudioFilter(
        AudioCompressionPresetDefinition preset,
        OperationTicket ticket,
        bool watchdogEnabled)
    {
        if (!_revisions.IsCurrentMediaSession(ticket))
            return;
        if (!TryGetPropertyString("af", out var filters))
        {
            var readFailures = Interlocked.Increment(ref _afReadFailures);
            if (readFailures == 1)
                Terminal.WriteError("Не удалось прочитать af и проверить компрессию звука.", LogModule);
            if (readFailures == MaxRecoveryFailures)
                QueueShow("Не удалось прочитать цепочку аудиофильтров. Автовосстановление продолжит попытки.");
            return;
        }

        Interlocked.Exchange(ref _afReadFailures, 0);
        if (FilterReadback.MatchesSingle(
                filters,
                AudioCompressionPresets.FilterLabel,
                preset.Filter!))
        {
            Interlocked.Exchange(ref _audioMismatchReported, 0);
            Interlocked.Exchange(ref _audioRecoveryFailures, 0);
            return;
        }

        var firstMismatch = Interlocked.Exchange(ref _audioMismatchReported, 1) == 0;
        if (firstMismatch)
        {
            _log.Write(
                "audio-watchdog-mismatch",
                ticket,
                new Dictionary<string, object?>
                {
                    ["presetId"] = preset.Id,
                    ["autoRecoveryEnabled"] = watchdogEnabled,
                },
                ExtensionLogLevel.Warning);
        }
        if (!watchdogEnabled)
        {
            if (firstMismatch)
                QueueShow("Фильтр компрессии звука изменён. Автовосстановление выключено.");
            return;
        }
        if (Volatile.Read(ref _audioRecoveryFailures) >= MaxRecoveryFailures)
            return;

        try
        {
            if (!_revisions.IsCurrentMediaSession(ticket))
                return;
            ApplyAudioCompressionPreset(preset);
            ResetAudioWatchdogState();
            _log.Write(
                "audio-watchdog-recovered",
                ticket,
                new Dictionary<string, object?> { ["presetId"] = preset.Id });
            QueueShow($"Компрессия звука восстановлена: {preset.DisplayName}.");
        }
        catch (Exception exception)
        {
            var recoveryFailures = Interlocked.Increment(ref _audioRecoveryFailures);
            Terminal.WriteError(exception, LogModule);
            _log.Write(
                "audio-watchdog-recovery-error",
                ticket,
                new Dictionary<string, object?>
                {
                    ["presetId"] = preset.Id,
                    ["consecutiveFailures"] = recoveryFailures,
                    ["error"] = ProtectError(exception),
                },
                ExtensionLogLevel.Error);
            if (recoveryFailures == MaxRecoveryFailures)
                QueueShow("Не удалось восстановить компрессию звука. Автовосстановление остановлено.");
        }
    }

    private void ResetAudioWatchdogState()
    {
        Interlocked.Exchange(ref _afReadFailures, 0);
        Interlocked.Exchange(ref _audioMismatchReported, 0);
        Interlocked.Exchange(ref _audioRecoveryFailures, 0);
    }

    private string ProtectError(Exception exception, string? additionalPath = null)
    {
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
        return DiagnosticsExporter.ProtectError(
            exception,
            includePaths,
            _pathHashKey,
            [additionalPath, mediaPath, activePath, pendingPath, _localDataRoot]);
    }

    private void ReportWindowPersistenceError(string message, Exception exception)
    {
        Terminal.WriteError(exception, LogModule);
        _log.Write(
            "ui-persistence-error",
            _revisions.Snapshot(),
            new Dictionary<string, object?> { ["error"] = ProtectError(exception) },
            ExtensionLogLevel.Error);
        Show(message + " Подробности — в журнале mpv.net.");
    }

    private void ReportFileWriteError(
        string eventName,
        string message,
        Exception exception,
        string path)
    {
        Terminal.WriteError(exception, LogModule);
        _log.Write(
            eventName,
            _revisions.Snapshot(),
            new Dictionary<string, object?> { ["error"] = ProtectError(exception, path) },
            ExtensionLogLevel.Error);
        Show(message + " Подробности — в журнале mpv.net.");
    }

    private static bool IsFileWriteException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException;

    private bool RecoverFilters(ActiveSchedule active)
    {
        var filtersReady = false;
        try
        {
            HoldPauseIfNeeded(active.Intervals);
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
                !FilterReadback.MatchesBlurPlan(filters, active.Plan))
            {
                throw new InvalidOperationException("После восстановления mpv не подтвердил точный набор фильтров цензуры.");
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
            Action[] canceledWindowActions;
            lock (_windowLock)
            {
                canceledWindowActions = _pendingWindowActions
                    .Select(item => item.Cancel)
                    .OfType<Action>()
                    .ToArray();
                _pendingWindowActions.Clear();
                _pendingWindowActionDropReported = false;
            }
            foreach (var cancel in canceledWindowActions)
                RunSafely(cancel);

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
                    RemoveFilters(
                        active?.Plan.Chunks.Select(chunk => chunk.Label) ?? [],
                        allowReadDuringShutdown: true);
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

    private bool TryGetPropertyString(
        string name,
        out string value,
        bool allowDuringShutdown = false)
    {
        value = "";
        if (Player.Handle == IntPtr.Zero ||
            (!allowDuringShutdown && Volatile.Read(ref _stopping) != 0))
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

        var error = mpv_get_property_flag(
            Player.Handle,
            GetUtf8Bytes(name),
            mpv_format.MPV_FORMAT_FLAG,
            out var raw);
        if (error < 0)
            return false;
        value = raw != 0;
        return true;
    }

    private bool TrySetPropertyBool(string name, bool value)
    {
        if (Player.Handle == IntPtr.Zero || Volatile.Read(ref _stopping) != 0)
            return false;

        var raw = value ? 1 : 0;
        return mpv_set_property_flag(
            Player.Handle,
            GetUtf8Bytes(name),
            mpv_format.MPV_FORMAT_FLAG,
            ref raw) >= 0;
    }

    // MpvClient marshals flag values as bool; libmpv's MPV_FORMAT_FLAG ABI uses int.
    [DllImport(
        "libmpv-2.dll",
        EntryPoint = "mpv_get_property",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_get_property_flag(
        nint handle,
        byte[] name,
        mpv_format format,
        out int value);

    [DllImport(
        "libmpv-2.dll",
        EntryPoint = "mpv_set_property",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int mpv_set_property_flag(
        nint handle,
        byte[] name,
        mpv_format format,
        ref int value);

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
