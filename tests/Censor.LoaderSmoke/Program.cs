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

Console.WriteLine($"loader smoke passed: {extensionTypes[0].FullName}");
return 0;
