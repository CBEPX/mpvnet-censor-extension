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
    private readonly SemaphoreSlim _filterGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Threading.Timer _watchdog;
    private CancellationTokenSource _sessionCancellation = new();
    private ActiveSchedule? _activeSchedule;
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
        Global.Player.Shutdown -= OnShutdown;

        StopRuntime(removeFilters: true);
        _watchdog.Dispose();
        _sessionCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStartFile() => RunSafely(BeginSession);

    private void OnFileLoaded() => RunSafely(LoadCurrentSidecar);

    private void OnEndFile(mpv_end_file_reason _) => RunSafely(BeginSession);

    private void OnShutdown() => StopRuntime(removeFilters: false);

    private void OnFiltersChanged(string _) =>
        ThreadPool.QueueUserWorkItem(static state => ((Extension)state!).CheckFilters(null), this);

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
                return;

            var sidecars = SidecarLocator.Find(mediaPath);
            if (sidecars.Count == 0)
                return;
            if (sidecars.Count > 1)
            {
                Show("Multiple sidecars found; select one manually.");
                return;
            }

            var sidecarPath = sidecars[0];
            var text = await File.ReadAllTextAsync(sidecarPath, StrictUtf8, token).ConfigureAwait(false);
            var parsed = Parse(sidecarPath, text);
            if (!parsed.IsSuccess)
            {
                Show($"Schedule error: {parsed.Diagnostics.First(item => item.Severity == DiagnosticSeverity.Error).Message}");
                return;
            }

            var document = parsed.Document!;
            if (!MatchesCurrentDuration(document.Metadata.MediaDurationMs))
            {
                Show("Sidecar duration differs from the current media; automatic apply blocked.");
                return;
            }

            var defaults = new NormalizationOptions();
            var normalized = ScheduleNormalizer.Normalize(
                document.Intervals,
                new(
                    document.Metadata.LeadInMs ?? defaults.LeadInMs,
                    document.Metadata.LeadOutMs ?? defaults.LeadOutMs,
                    document.Metadata.OffsetMs ?? defaults.OffsetMs,
                    defaults.MergeGapMs));
            var plan = FilterCompiler.Compile(normalized, new());
            if (plan.Chunks.Count == 0)
            {
                Show("Schedule contains no active intervals.");
                return;
            }

            await ApplyAsync(sessionId, plan, normalized, token).ConfigureAwait(false);
            if (IsCurrent(sessionId))
                Show($"Applied {plan.IntervalCount} intervals from {Path.GetFileName(sidecarPath)}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminal.WriteError(exception, LogModule);
            if (IsCurrent(sessionId))
                Show("Failed to load or apply the sidecar. See the terminal log.");
        }
    }

    private async Task ApplyAsync(
        long sessionId,
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

            _activeSchedule = new(sessionId, plan, intervals);
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

    private bool IsCurrent(long sessionId)
    {
        lock (_stateLock)
            return Volatile.Read(ref _stopping) == 0 && _sessionId == sessionId;
    }

    private static ParseResult Parse(string path, string text)
    {
        if (path.EndsWith(".censor.srt", StringComparison.OrdinalIgnoreCase))
            return SubtitleScheduleText.Import(text, SubtitleFormat.Srt);
        if (path.EndsWith(".censor.vtt", StringComparison.OrdinalIgnoreCase))
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

    private void Show(string message)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;

        _filterGate.Wait();
        try
        {
            if (Volatile.Read(ref _stopping) == 0)
                Player.CommandV("show-text", $"Censor: {message}", "5000");
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
        FilterPlan Plan,
        IReadOnlyList<NormalizedInterval> Intervals);
}
