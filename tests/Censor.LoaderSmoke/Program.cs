using System.Collections;
using System.Reflection;
using System.Text.Json;

using MpvNet;

if (args is ["--audio-presets", var audioExtensionPath])
{
    var audioAssembly = Assembly.LoadFile(Path.GetFullPath(audioExtensionPath));
    var catalog = audioAssembly.GetType("Censor.Core.AudioCompressionPresets", throwOnError: true)!;
    var all = (IEnumerable)catalog.GetProperty("All")!.GetValue(null)!;
    var presets = all.Cast<object>()
        .Select(item => new
        {
            id = (string)item.GetType().GetProperty("Id")!.GetValue(item)!,
            filter = item.GetType().GetProperty("Filter")!.GetValue(item) as string,
        })
        .Where(item => item.filter is not null);
    Console.WriteLine(JsonSerializer.Serialize(presets));
    return 0;
}

if (args is not [var extensionPath])
{
    Console.Error.WriteLine(
        "usage: Censor.LoaderSmoke <CensorExtension.dll> | --audio-presets <CensorExtension.dll>");
    return 2;
}

_ = typeof(IExtension);
var assembly = Assembly.LoadFile(Path.GetFullPath(extensionPath));
if (assembly.GetReferencedAssemblies().Any(item => item.Name == "Censor.Core"))
    throw new InvalidOperationException("CensorExtension still references the external Censor.Core assembly.");

var extensionTypes = assembly.GetTypes()
    .Where(type => !type.IsAbstract && typeof(IExtension).IsAssignableFrom(type))
    .ToArray();
if (extensionTypes is not [{ FullName: "Censor.MpvNet.Extension.Extension" }])
    throw new InvalidOperationException("The stock mpv.net loader contract did not find exactly one extension type.");

RunUiContractSmoke(assembly);
Console.WriteLine($"loader smoke passed: {extensionTypes[0].FullName}");
return 0;

static void RunUiContractSmoke(Assembly assembly)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "censor-ui-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var settings = Activator.CreateInstance(
                assembly.GetType("Censor.Core.ExtensionSettings", throwOnError: true)!)!;
            using var window = (Form)Activator.CreateInstance(
                assembly.GetType("Censor.MpvNet.Extension.CensorWindow", throwOnError: true)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [settings, dataRoot],
                culture: null)!;
            var warningSinkProperty = window.GetType().GetProperty(
                "WarningSink",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("The modal smoke seam is missing.");
            var trappedModals = new List<string>();
            warningSinkProperty.SetValue(window, (Action<string>)trappedModals.Add);
            var confirmed = (bool)window.GetType().GetMethod("ConfirmDurationMismatch")!
                .Invoke(window, null)!;
            const string ExpectedConfirmation =
                "Длительность в файле интервалов отличается от фильма. Применить всё равно?";
            if (confirmed || trappedModals is not [ExpectedConfirmation])
            {
                throw new InvalidOperationException(
                    "The modal smoke seam returned an unexpected result: " +
                    $"confirmed={confirmed}, messages=[{string.Join(" | ", trappedModals)}].");
            }
            warningSinkProperty.SetValue(
                window,
                (Action<string>)(message => throw new InvalidOperationException(
                    $"Unexpected warning or confirmation modal: {message}")));
            var tabs = Descendants(window).OfType<TabControl>().Single();
            var tabNames = tabs.TabPages.Cast<TabPage>().Select(page => page.Text).ToArray();
            if (tabNames is not ["Интервалы", "Настройки", "Диагностика"])
                throw new InvalidOperationException("The editor must expose exactly three simple tabs.");
            if (!Descendants(window).OfType<Label>().Any(label =>
                    label.Text == "Откройте фильм, чтобы добавить интервалы."))
            {
                throw new InvalidOperationException(
                    "The empty editor points at a disabled manual-entry button.");
            }

            var buttonNames = Descendants(window).OfType<Button>()
                .Select(button => button.Text)
                .ToHashSet(StringComparer.Ordinal);
            ReadOnlySpan<string> requiredButtons =
            [
                "Добавить вручную",
                "Отметить начало (F7)",
                "Отметить конец (F8)",
                "Применить интервалы",
                "Сохранить файл",
                "Дополнительно ▾",
                "Расширенные параметры ▾",
            ];
            foreach (var required in requiredButtons)
            {
                if (!buttonNames.Contains(required))
                    throw new InvalidOperationException($"The editor is missing the required action: {required}");
            }
            var addButton = Descendants(window).OfType<Button>().Single(button =>
                button.Text == "Добавить вручную");
            var applyButton = Descendants(window).OfType<Button>().Single(button =>
                button.Text == "Применить интервалы");
            var markStartButton = Descendants(window).OfType<Button>().Single(button =>
                button.Text == "Отметить начало (F7)");
            if (addButton.Parent != applyButton.Parent || addButton.Parent == markStartButton.Parent)
            {
                throw new InvalidOperationException(
                    "Manual interval entry must be primary and F7/F8 must remain secondary.");
            }
            if (!Descendants(window).OfType<CheckBox>().Any(checkbox =>
                    checkbox.Text == "Автоматически восстанавливать размытие и компрессию звука"))
            {
                throw new InvalidOperationException("The watchdog setting does not describe both protected filters.");
            }

            VerifyEditorWorkflow(assembly, window);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    if (!thread.Join(TimeSpan.FromMinutes(1)))
    {
        throw new InvalidOperationException(
            "The Windows editor contract smoke timed out, likely on a modal dialog.");
    }
    if (failure is not null)
        throw new InvalidOperationException("The Windows editor contract smoke failed.", failure);
}

static void VerifyEditorWorkflow(Assembly assembly, Form window)
{
    const string TransferableDraftMessage =
        "Сначала сохраните изменения, используйте их для открытого фильма или удалите.";
    var parse = assembly.GetType("Censor.Core.ScheduleText", throwOnError: true)!
        .GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
    var parsed = parse.Invoke(
        null,
        [
            "# censor-timeline: 1\n# title: Film\n# media-duration-ms: 6000\n\n" +
            "# future-key: keep\n" +
            "00:00:01.000 --> 00:00:02.000\n",
            2 * 1024 * 1024,
            10_000,
        ])!;
    var parsedType = parsed.GetType();
    var document = parsedType.GetProperty("Document")!.GetValue(parsed)!;
    var diagnostics = parsedType.GetProperty("Diagnostics")!.GetValue(parsed)!;
    window.GetType().GetMethod("UpdateState")!.Invoke(
        window,
        [
            @"C:\Video\Film.mkv",
            1L,
            @"C:\Video\Film.mkv.censor.txt",
            "hash",
            "READY",
            6_000L,
            document,
            null,
            diagnostics,
            false,
        ]);

    var grid = Descendants(window).OfType<DataGridView>().Single();
    if (grid.Rows.Count != 1)
        throw new InvalidOperationException("The discard smoke could not seed one interval.");
    var warning = Descendants(window).OfType<ListBox>().Single().Items
        .Cast<object>()
        .Select(Convert.ToString)
        .Single();
    if (warning is null ||
        !warning.StartsWith("Предупреждение, строка файла 5:", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Runtime warning does not identify its file-line coordinate.");
    }

    window.Show();
    window.Hide();
    window.GetType().GetMethod("HandleAuthoringCommand")!.Invoke(
        window,
        ["mark-start", 1_500L]);
    if (!window.Visible)
        throw new InvalidOperationException("A hidden editor did not reopen for an authoring command.");

    window.GetType()
        .GetMethod("DiscardDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, null);

    var enabledPrimaryActions = Descendants(window).OfType<Button>()
        .Where(button => button.Text is "Применить интервалы" or "Сохранить файл")
        .Where(button => button.Enabled)
        .Select(button => button.Text)
        .ToArray();
    if (grid.Rows.Count != 0 || enabledPrimaryActions.Length != 0)
        throw new InvalidOperationException("Discard left stale intervals or enabled primary actions.");
    if (!Descendants(window).OfType<Label>().Any(label =>
            label.Text == "Интервалов пока нет. Нажмите «Добавить вручную» и укажите время сцены."))
    {
        throw new InvalidOperationException(
            "The empty editor does not direct the user to the primary action.");
    }

    var currentTimeProperty = window.GetType().GetProperty("CurrentTimeRequested")!;
    currentTimeProperty.SetValue(window, (Func<long?>)(() => null));
    var addButton = Descendants(window).OfType<Button>().Single(button =>
        button.Text == "Добавить вручную");
    addButton.PerformClick();
    if (grid.Rows.Count != 1 ||
        grid.CurrentCell?.OwningColumn?.Name != "start" ||
        grid.CurrentCell.Value as string != "00:00:00.000" ||
        !grid.IsCurrentCellInEditMode)
    {
        throw new InvalidOperationException(
            "Manual entry did not create a row and start editing its start time.");
    }

    grid.CurrentCell.Value = "00:00:03";
    window.GetType().GetMethod("UpdateState")!.Invoke(
        window,
        [
            @"C:\Video\Film.mkv",
            1L,
            null,
            null,
            "READY",
            6_000L,
            null,
            null,
            diagnostics,
            false,
        ]);
    if (grid.Rows[0].Cells["start"].Value as string != "00:00:03.000")
    {
        throw new InvalidOperationException(
            "A runtime update discarded or failed to normalize the active cell edit.");
    }

    grid.BeginEdit(selectAll: true);
    grid.CurrentCell!.Value = "00:00:04";
    window.GetType().GetMethod(
        "RenderDraft",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
    if (grid.Rows[0].Cells["start"].Value as string != "00:00:04.000")
    {
        throw new InvalidOperationException(
            "A full render discarded or failed to normalize the active cell edit.");
    }

    currentTimeProperty.SetValue(window, (Func<long?>)(() => 5_000));
    addButton.PerformClick();
    if (grid.Rows.Count != 2 ||
        grid.CurrentCell?.Value as string != "00:00:05.000")
    {
        throw new InvalidOperationException(
            "Manual entry ignored an available playback position.");
    }

    var replacement = parse.Invoke(
        null,
        [
            "# censor-timeline: 1\n# title: Film\n# media-duration-ms: 6000\n\n" +
            "00:00:01.000 --> 00:00:02.000\n" +
            "00:00:02.000 --> 00:00:03.000\n",
            2 * 1024 * 1024,
            10_000,
        ])!;
    var replacementDocument = parsedType.GetProperty("Document")!.GetValue(replacement)!;
    var replacementDiagnostics = parsedType.GetProperty("Diagnostics")!.GetValue(replacement)!;
    window.GetType().GetMethod("UpdateState")!.Invoke(
        window,
        [
            @"C:\Video\Film.mkv",
            1L,
            @"C:\Video\Film.mkv.censor.txt",
            "replacement-hash",
            "READY",
            6_000L,
            replacementDocument,
            null,
            replacementDiagnostics,
            false,
        ]);
    grid.CurrentCell = grid.Rows[0].Cells["start"];
    grid.BeginEdit(selectAll: true);
    grid.CurrentCell.Value = "00:00:09";
    window.GetType().GetMethod(
        "ReplaceDraftFromRuntime",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    var draftField = window.GetType().GetField(
        "_draft",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var replacedDraft = draftField.GetValue(window)!;
    if (grid.Rows.Count != 2 ||
        grid.Rows[0].Cells["start"].Value as string != "00:00:01.000" ||
        (bool)replacedDraft.GetType().GetProperty("IsDirty")!.GetValue(replacedDraft)!)
    {
        throw new InvalidOperationException(
            "Replacing the draft copied an active edit into clean runtime intervals.");
    }

    grid.CurrentCell = grid.Rows[0].Cells["start"];
    grid.BeginEdit(selectAll: true);
    grid.CurrentCell.Value = "00:00:04";
    grid.EndEdit();
    Application.DoEvents();
    if (grid.Rows[0].Cells["start"].Value as string != "00:00:04.000")
    {
        throw new InvalidOperationException(
            "A deferred edit did not render its normalized value.");
    }
    grid.BeginEdit(selectAll: true);
    grid.CurrentCell.Value = "invalid";
    window.GetType().GetMethod(
        "ReplaceDraftFromRuntime",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    Application.DoEvents();
    replacedDraft = draftField.GetValue(window)!;
    if ((bool)replacedDraft.GetType().GetProperty("IsDirty")!.GetValue(replacedDraft)!)
    {
        throw new InvalidOperationException(
            "Replacing after an invalid edit left the runtime draft dirty.");
    }
    if (Descendants(window).OfType<Label>().Any(label =>
            label.Text.StartsWith("Введите время", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "A deferred edit leaked stale validation status into the editor header.");
    }

    grid.ClearSelection();
    grid.Rows[^1].Selected = true;
    window.GetType().GetMethod(
        "DeleteSelected",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    var detachedDraft = draftField.GetValue(window)!;
    if (grid.Rows.Count != 1 ||
        !(bool)detachedDraft.GetType().GetProperty("IsDirty")!.GetValue(detachedDraft)!)
    {
        throw new InvalidOperationException(
            "The detached-state smoke could not create a non-empty dirty draft.");
    }

    window.GetType().GetMethod("UpdateState")!.Invoke(
        window,
        [
            null,
            2L,
            null,
            null,
            "IDLE",
            null,
            null,
            null,
            diagnostics,
            false,
        ]);
    var detachedHint = Descendants(window).OfType<Label>().SingleOrDefault(label =>
        label.Text.Contains("Сейчас фильм не открыт.", StringComparison.Ordinal));
    if (detachedHint?.Visible != true || addButton.Enabled)
    {
        throw new InvalidOperationException(
            "A detached draft does not explain why manual entry is unavailable.");
    }

    window.GetType().GetMethod("UpdateState")!.Invoke(
        window,
        [
            @"C:\Video\Other.mkv",
            3L,
            null,
            null,
            "READY",
            6_000L,
            null,
            null,
            diagnostics,
            false,
        ]);
    var transferableHint = Descendants(window).OfType<Label>().SingleOrDefault(label =>
        label.Text.Contains("Сейчас открыт «Other».", StringComparison.Ordinal));
    var transferButton = Descendants(window).OfType<Button>().SingleOrDefault(button =>
        button.Text.StartsWith("Использовать для «", StringComparison.Ordinal));
    if (transferableHint?.Visible != true ||
        transferButton?.Enabled != true ||
        addButton.Enabled)
    {
        throw new InvalidOperationException(
            "A detached draft does not offer transfer when a target film is open.");
    }

    var notifications = new List<string>();
    var notificationEvent = window.GetType().GetEvent("AuthoringNotificationRequested")!;
    Action<string> notificationHandler = notifications.Add;
    notificationEvent.AddEventHandler(window, notificationHandler);
    var handleCommand = window.GetType().GetMethod("HandleAuthoringCommand")!;
    var draftState = (Label)window.GetType().GetField(
        "_draftState",
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    var authoringNotice = (Label)window.GetType().GetField(
        "_authoringNotice",
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    try
    {
        var detachedState = draftState.Text;
        handleCommand.Invoke(window, ["set-start", 1_000L]);
        window.Hide();
        handleCommand.Invoke(window, ["previous", null]);
        if (window.Visible ||
            draftState.Text != detachedState ||
            authoringNotice.Text != TransferableDraftMessage ||
            !notifications.SequenceEqual(
                [
                    TransferableDraftMessage,
                    TransferableDraftMessage,
                ]))
        {
            throw new InvalidOperationException(
                "Hidden authoring commands did not publish detached-draft OSD feedback.");
        }

        window.GetType().GetMethod("UpdateState")!.Invoke(
            window,
            [
                @"C:\Video\Other.mkv",
                3L,
                null,
                null,
                "LOADING",
                6_000L,
                null,
                null,
                diagnostics,
                false,
            ]);
        if (authoringNotice.Text != TransferableDraftMessage)
        {
            throw new InvalidOperationException(
                "A background status update erased detached-draft feedback.");
        }

        window.Show();
        detachedDraft.GetType().GetMethod("Delete")!.Invoke(
            detachedDraft,
            [Enumerable.Repeat(0, 1)]);
        window.GetType().GetMethod(
            "RenderDraft",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
        var emptyState = (Label)window.GetType().GetField(
            "_emptyState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        if (grid.Rows.Count != 0 ||
            emptyState.Visible != true ||
            emptyState.Text != TransferableDraftMessage ||
            authoringNotice.Visible != true ||
            authoringNotice.Text != TransferableDraftMessage)
        {
            throw new InvalidOperationException(
                "An empty detached draft did not retain its actionable guidance.");
        }

        window.GetType().GetMethod(
            "UseDraftForCurrentMedia",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        if (authoringNotice.Text.Length != 0)
        {
            throw new InvalidOperationException(
                "Reattaching a draft did not clear stale authoring feedback.");
        }
        notifications.Clear();
        var emptyDraftState = draftState.Text;
        handleCommand.Invoke(window, ["set-start", 1_000L]);
        handleCommand.Invoke(window, ["previous", null]);
        if (draftState.Text != emptyDraftState ||
            authoringNotice.Text != "Сначала добавьте интервал." ||
            !notifications.SequenceEqual(
                [
                    "Сначала добавьте интервал.",
                    "Сначала добавьте интервал.",
                ]))
        {
            throw new InvalidOperationException(
                "Authoring commands did not explain that the draft is empty.");
        }

        addButton.PerformClick();
        grid.EndEdit();
        Application.DoEvents();
        grid.CurrentCell = null;
        grid.ClearSelection();
        notifications.Clear();
        // Exercise the shared selection guard directly: activating a public
        // hotkey command can let WinForms restore CurrentCell before dispatch.
        window.GetType().GetMethod(
            "DuplicateSelected",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        if (authoringNotice.Text != "Сначала выберите интервал." ||
            !notifications.SequenceEqual(["Сначала выберите интервал."]))
        {
            throw new InvalidOperationException(
                "Row actions did not explain that no interval is selected.");
        }
    }
    finally
    {
        notificationEvent.RemoveEventHandler(window, notificationHandler);
    }
}

static IEnumerable<Control> Descendants(Control root)
{
    foreach (Control child in root.Controls)
    {
        yield return child;
        foreach (var descendant in Descendants(child))
            yield return descendant;
    }
}
