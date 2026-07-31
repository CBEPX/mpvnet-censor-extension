using System.Text;
using Censor.Core;
using MpvNet;
using static MpvNet.Native.LibMpv;

namespace Censor.MpvNet.Extension;

public sealed class Extension : IExtension, IDisposable
{
    private const long DurationToleranceMs = 2_000;
    private const long EarlyIntervalGuardMs = 3_000;
    private const string LogModule = "CensorExtension";
    private const int MaxRecoveryFailures = 3;
    private const int MaxSuccessfulRecoveries = 3;
    private const int WatchdogIntervalMs = 1_000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Lock _stateLock = new();
    private readonly Lock _windowLock = new();
    private readonly SemaphoreSlim _filterGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Timer _watchdog;
    private CancellationTokenSource _sessionCancellation = new();
    private ActiveSchedule? _activeSchedule;
    private BlurSettings _blurSettings = BlurSettings.Balanced;
    private string? _currentMediaPath;
    private CensorWindow? _window;
    private Thread? _windowThread;
    private string _windowStatus = "NO SCHEDULE";
    private int _disposed;
    private int _recoveryFailures;
    private int _successfulRecoveries;
    private int _stopping;
    private int _vfReadFailures;
    private long _sessionId;

    public Extension()
    {
        Player = Global.Player.CreateNewPlayer("censor");
        _watchdog = new(CheckFilters, null, Timeout.Infinite, Timeout.Infinite);
        Player.StartFile += OnStartFile;
        Player.FileLoaded += OnFileLoaded;
        Player.EndFile += OnEndFile;
        Player.Shutdown += OnShutdown;
        Player.ClientMessage += OnClientMessage;
        Global.Player.Shutdown += OnShutdown;
        Player.ObservePropertyString("vf", OnFiltersChanged);
        _watchdog.Change(WatchdogIntervalMs, WatchdogIntervalMs);
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
        _sessionCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStartFile() => RunSafely(BeginSession);

    private void OnFileLoaded() => RunSafely(LoadCurrentSidecar);

    private void OnEndFile(mpv_end_file_reason _) => RunSafely(BeginSession);

    private void OnShutdown()
    {
        StopRuntime(removeFilters: false);
        CloseWindow();
    }

    private void OnClientMessage(string[] args) => RunSafely(() => HandleClientMessage(args));

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
        else if (args[0].Equals("censor-disable", StringComparison.OrdinalIgnoreCase))
        {
            DisableSchedule();
        }
    }

    private void BeginSession()
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;

        CancellationTokenSource previous;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
                return;

            _sessionId++;
            _currentMediaPath = null;
            previous = _sessionCancellation;
            _sessionCancellation = new();
        }

        previous.Cancel();
        previous.Dispose();

        _filterGate.Wait();
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
                return;

            RemoveFilters(_activeSchedule?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
            _activeSchedule = null;
            _recoveryFailures = 0;
            _successfulRecoveries = 0;
            _vfReadFailures = 0;
        }
        finally
        {
            _filterGate.Release();
        }

        UpdateWindow("NO SCHEDULE");
    }

    private void LoadCurrentSidecar()
    {
        long sessionId;
        CancellationToken token;
        lock (_stateLock)
        {
            sessionId = _sessionId;
            token = _sessionCancellation.Token;
        }

        string mediaPath;
        _filterGate.Wait();
        try
        {
            if (!TryGetPropertyString("path", out mediaPath))
                return;
        }
        finally
        {
            _filterGate.Release();
        }

        lock (_stateLock)
            _currentMediaPath = mediaPath;
        UpdateWindow("LOOKING FOR SIDECAR");
        _ = LoadCurrentSidecarAsync(sessionId, mediaPath, token);
    }

    private async Task LoadCurrentSidecarAsync(
        long sessionId,
        string mediaPath,
        CancellationToken token)
    {
        try
        {
            if (!File.Exists(mediaPath))
            {
                token.ThrowIfCancellationRequested();
                UpdateWindow("NO LOCAL MEDIA", sessionId, token);
                return;
            }

            var sidecars = SidecarLocator.Find(mediaPath);
            token.ThrowIfCancellationRequested();
            if (sidecars.Count == 0)
            {
                UpdateWindow("NO SCHEDULE", sessionId, token);
                return;
            }
            if (sidecars.Count > 1)
            {
                if (UpdateWindow(
                    $"MULTIPLE SIDECARS: {string.Join(", ", sidecars.Select(Path.GetFileName))}",
                    sessionId,
                    token))
                {
                    Show("Multiple sidecars found; select one manually.", sessionId, token);
                }
                return;
            }

            await LoadScheduleAsync(sessionId, sidecars[0], automatic: true, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", sessionId, token))
                Show("Failed to load the sidecar. See the terminal log.", sessionId, token);
        }
    }

    private async Task LoadScheduleAsync(
        long sessionId,
        string schedulePath,
        bool automatic,
        CancellationToken token)
    {
        try
        {
            UpdateWindow("LOADING", sessionId, token);
            var text = await File.ReadAllTextAsync(schedulePath, StrictUtf8, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var parsed = Parse(schedulePath, text);
            token.ThrowIfCancellationRequested();
            if (!parsed.IsSuccess)
            {
                if (UpdateWindow("ERROR", sessionId, token))
                {
                    Show(
                        $"Schedule error: {parsed.Diagnostics.First(item => item.Severity == DiagnosticSeverity.Error).Message}",
                        sessionId,
                        token);
                }
                return;
            }

            var document = parsed.Document!;
            token.ThrowIfCancellationRequested();
            if (!MatchesCurrentDuration(document.Metadata.MediaDurationMs))
            {
                token.ThrowIfCancellationRequested();
                var applyAnyway = !automatic && ConfirmDurationMismatch();
                token.ThrowIfCancellationRequested();
                if (!applyAnyway)
                {
                    if (UpdateWindow("DURATION MISMATCH", sessionId, token))
                    {
                        Show(
                            "Schedule duration differs from the current media; apply blocked.",
                            sessionId,
                            token);
                    }
                    return;
                }
            }
            token.ThrowIfCancellationRequested();

            var defaults = new NormalizationOptions();
            var normalized = ScheduleNormalizer.Normalize(
                document.Intervals,
                new(
                    document.Metadata.LeadInMs ?? defaults.LeadInMs,
                    document.Metadata.LeadOutMs ?? defaults.LeadOutMs,
                    document.Metadata.OffsetMs ?? defaults.OffsetMs,
                    defaults.MergeGapMs));
            BlurSettings blurSettings;
            lock (_stateLock)
                blurSettings = _blurSettings;
            var plan = FilterCompiler.Compile(normalized, blurSettings);
            token.ThrowIfCancellationRequested();
            if (plan.Chunks.Count == 0)
            {
                if (UpdateWindow("EMPTY SCHEDULE", sessionId, token))
                    Show("Schedule contains no active intervals.", sessionId, token);
                return;
            }

            await ApplyAsync(sessionId, schedulePath, document, plan, normalized, token).ConfigureAwait(false);
            if (UpdateWindow("ACTIVE", sessionId, token))
            {
                Show(
                    $"Applied {plan.IntervalCount} intervals from {Path.GetFileName(schedulePath)}.",
                    sessionId,
                    token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (UpdateWindow("ERROR", sessionId, token))
                Show("Failed to load or apply the schedule. See the terminal log.", sessionId, token);
        }
    }

    private async Task ApplyAsync(
        long sessionId,
        string schedulePath,
        ScheduleDocument document,
        FilterPlan plan,
        IReadOnlyList<NormalizedInterval> intervals,
        CancellationToken token)
    {
        await _filterGate.WaitAsync(token).ConfigureAwait(false);
        var labels = plan.Chunks.Select(chunk => chunk.Label).ToArray();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(sessionId))
                return;

            foreach (var chunk in plan.Chunks)
            {
                Player.CommandV("vf", "add", chunk.Filter);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(sessionId))
                    throw new OperationCanceledException(token);
            }

            if (!TryGetPropertyString("vf", out var filters) ||
                labels.Any(label => !ContainsLabel(filters, label)))
            {
                throw new InvalidOperationException("mpv did not report every generated filter after apply.");
            }

            _activeSchedule = new(sessionId, schedulePath, document, plan, intervals);
            _recoveryFailures = 0;
            _successfulRecoveries = 0;
            _vfReadFailures = 0;
        }
        catch
        {
            RemoveFilters(labels);
            throw;
        }
        finally
        {
            _filterGate.Release();
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
            return Math.Abs(actualMs - expectedMs.Value) <= DurationToleranceMs;
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
            Show("Schedule path is invalid or unsupported.");
            return;
        }

        var operation = StartNewOperation();
        if (operation is null)
        {
            UpdateWindow("NO CURRENT MEDIA");
            Show("No current media is available for manual loading.");
            return;
        }

        UpdateWindow("LOADING");
        _ = LoadScheduleAsync(
            operation.Value.SessionId,
            schedulePath,
            automatic: false,
            operation.Value.Token);
    }

    private void ReloadSchedule()
    {
        var path = _activeSchedule?.SchedulePath;
        if (path is null)
        {
            UpdateWindow("NO SCHEDULE TO RELOAD");
            return;
        }

        LoadManualSchedule(path);
    }

    private void ChangeBlurPreset(BlurSettings settings)
    {
        lock (_stateLock)
        {
            if (_blurSettings == settings)
                return;

            _blurSettings = settings;
        }

        if (_activeSchedule is not null)
            ReloadSchedule();
    }

    private void DisableSchedule()
    {
        if (StartNewOperation() is null)
        {
            UpdateWindow("OPERATION UNAVAILABLE");
            Show("No current media is available to disable.");
            return;
        }

        UpdateWindow("DISABLED");
        Show("Schedule disabled for the current media.");
    }

    private (long SessionId, CancellationToken Token)? StartNewOperation()
    {
        CancellationTokenSource previous;
        long sessionId;
        CancellationToken token;
        lock (_stateLock)
        {
            if (Volatile.Read(ref _stopping) != 0 || string.IsNullOrEmpty(_currentMediaPath))
                return null;

            sessionId = ++_sessionId;
            previous = _sessionCancellation;
            _sessionCancellation = new();
            token = _sessionCancellation.Token;
        }

        previous.Cancel();
        previous.Dispose();

        _filterGate.Wait();
        try
        {
            if (!IsCurrent(sessionId))
                return null;

            RemoveFilters(_activeSchedule?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
            _activeSchedule = null;
            _recoveryFailures = 0;
            _successfulRecoveries = 0;
            _vfReadFailures = 0;
        }
        finally
        {
            _filterGate.Release();
        }

        return (sessionId, token);
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
                        using var window = new CensorWindow();
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
                                current.Intervals);
                        };

                        lock (_windowLock)
                            _window = window;

                        var snapshot = SnapshotWindow();
                        window.UpdateState(
                            snapshot.MediaPath,
                            snapshot.SchedulePath,
                            snapshot.Status,
                            snapshot.Intervals);

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
        long? expectedSessionId = null,
        CancellationToken token = default)
    {
        lock (_stateLock)
        {
            if (expectedSessionId.HasValue &&
                (Volatile.Read(ref _stopping) != 0 ||
                 _sessionId != expectedSessionId.Value ||
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
                    snapshot.Intervals)));
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
        IReadOnlyList<CensorInterval> Intervals) SnapshotWindow()
    {
        string? mediaPath;
        string status;
        lock (_stateLock)
        {
            mediaPath = _currentMediaPath;
            status = _windowStatus;
        }

        var active = _activeSchedule;
        return (
            mediaPath,
            active?.SchedulePath,
            status,
            active?.Document.Intervals ?? []);
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

    private bool IsCurrent(long sessionId)
    {
        lock (_stateLock)
            return Volatile.Read(ref _stopping) == 0 && _sessionId == sessionId;
    }

    private static ParseResult Parse(string path, string text)
    {
        if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            return SubtitleScheduleText.Import(text, SubtitleFormat.Srt);
        if (path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            return SubtitleScheduleText.Import(text, SubtitleFormat.WebVtt);
        return ScheduleText.Parse(text);
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
            Show("Censor extension error. See the terminal log.");
        }
    }

    private void Show(
        string message,
        long? expectedSessionId = null,
        CancellationToken token = default)
    {
        if (Volatile.Read(ref _stopping) != 0 || token.IsCancellationRequested)
            return;

        _filterGate.Wait(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _stopping) == 0 &&
                !token.IsCancellationRequested &&
                (!expectedSessionId.HasValue || IsCurrent(expectedSessionId.Value)))
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

            var active = _activeSchedule;
            if (active is null ||
                !IsCurrent(active.SessionId) ||
                _recoveryFailures >= MaxRecoveryFailures ||
                _successfulRecoveries >= MaxSuccessfulRecoveries ||
                _vfReadFailures >= MaxRecoveryFailures)
            {
                return;
            }

            if (!TryGetPropertyString("vf", out var filters))
            {
                _vfReadFailures++;
                if (_vfReadFailures == 1)
                    Terminal.WriteError("Unable to read vf; watchdog state is indeterminate.", LogModule);
                if (_vfReadFailures == MaxRecoveryFailures)
                    QueueShow("vf could not be read; automatic recovery stopped.");
                return;
            }

            _vfReadFailures = 0;
            if (active.Plan.Chunks.All(chunk => ContainsLabel(filters, chunk.Label)))
                return;

            try
            {
                RecoverFilters(active);
                _recoveryFailures = 0;
                _successfulRecoveries++;
                if (_successfulRecoveries == 1)
                    QueueShow("Recovered a removed filter.");
                if (_successfulRecoveries == MaxSuccessfulRecoveries)
                    QueueShow("Recovery limit reached; further automatic recovery stopped.");
            }
            catch (Exception exception)
            {
                _recoveryFailures++;
                Terminal.WriteError(exception, LogModule);
                if (_recoveryFailures == MaxRecoveryFailures)
                    QueueShow("Filters could not be maintained; automatic recovery stopped.");
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

    private void RecoverFilters(ActiveSchedule active)
    {
        var pauseForRecovery = IsIntervalNear(active.Intervals);
        var pausedByExtension = false;
        if (pauseForRecovery)
        {
            if (!TryGetPropertyBool("pause", out var wasPaused))
                throw new InvalidOperationException("mpv pause state is unavailable.");
            if (!wasPaused)
            {
                Player.SetPropertyBool("pause", true);
                pausedByExtension = true;
            }
        }

        try
        {
            var labels = active.Plan.Chunks.Select(chunk => chunk.Label).ToArray();
            RemoveFilters(labels);
            foreach (var chunk in active.Plan.Chunks)
            {
                if (!IsCurrent(active.SessionId) || Volatile.Read(ref _stopping) != 0)
                    return;
                Player.CommandV("vf", "add", chunk.Filter);
            }

            if (!TryGetPropertyString("vf", out var filters) ||
                labels.Any(label => !ContainsLabel(filters, label)))
            {
                throw new InvalidOperationException("mpv did not report every generated filter after recovery.");
            }

            Terminal.Write("Recovered missing censor filter.", LogModule);
        }
        finally
        {
            if (pausedByExtension && Volatile.Read(ref _stopping) == 0)
                Player.SetPropertyBool("pause", false);
        }
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
        return WatchdogPolicy.IsIntervalNear(positionMs, intervals, EarlyIntervalGuardMs);
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
                cancellation = _sessionCancellation;

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
                if (removeFilters)
                    RemoveFilters(_activeSchedule?.Plan.Chunks.Select(chunk => chunk.Label) ?? []);
                _activeSchedule = null;
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

    private sealed record ActiveSchedule(
        long SessionId,
        string SchedulePath,
        ScheduleDocument Document,
        FilterPlan Plan,
        IReadOnlyList<NormalizedInterval> Intervals);
}
