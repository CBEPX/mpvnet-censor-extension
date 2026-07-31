using System.Reflection;

using MpvNet;

if (args is not [var extensionPath])
{
    Console.Error.WriteLine("usage: Censor.LoaderSmoke <CensorExtension.dll>");
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
