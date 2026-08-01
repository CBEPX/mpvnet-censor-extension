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

static IEnumerable<Control> Descendants(Control root)
{
    foreach (Control child in root.Controls)
    {
        yield return child;
        foreach (var descendant in Descendants(child))
            yield return descendant;
    }
}
