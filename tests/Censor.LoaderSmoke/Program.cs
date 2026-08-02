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
            var tabs = Descendants(window).OfType<TabControl>().Single();
            var tabNames = tabs.TabPages.Cast<TabPage>().Select(page => page.Text).ToArray();
            if (tabNames is not ["Интервалы", "Настройки", "Диагностика"])
                throw new InvalidOperationException("The editor must expose exactly three simple tabs.");

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

            VerifyDiscardClearsEditor(assembly, window);
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
    thread.Start();
    thread.Join();
    if (failure is not null)
        throw new InvalidOperationException("The Windows editor contract smoke failed.", failure);
}

static void VerifyDiscardClearsEditor(Assembly assembly, Form window)
{
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

    window.GetType().GetProperty("CurrentTimeRequested")!.SetValue(
        window,
        (Func<long?>)(() => null));
    Descendants(window).OfType<Button>().Single(button =>
        button.Text == "Добавить вручную").PerformClick();
    if (grid.Rows.Count != 1 ||
        grid.CurrentCell?.OwningColumn?.Name != "start" ||
        grid.CurrentCell.Value as string != "00:00:00.000" ||
        !grid.IsCurrentCellInEditMode)
    {
        throw new InvalidOperationException(
            "Manual entry did not create a row and start editing its start time.");
    }

    grid.CurrentCell.Value = "00:00:03";
    window.GetType().GetMethod(
        "RenderDraft",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
    if (grid.Rows[0].Cells["start"].Value as string != "00:00:03.000")
    {
        throw new InvalidOperationException(
            "A full render discarded or failed to normalize the active cell edit.");
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
