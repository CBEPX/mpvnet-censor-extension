#:property RestorePackagesWithLockFile=false
#:property RestoreLockedMode=false
#:property PublishAot=false

using System.Security.Cryptography;
using System.Text.Json;

if (args is ["read", var lockPath, var platform])
{
    using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
    var mpvNet = document.RootElement.GetProperty("mpvNet");
    var hashes = mpvNet.GetProperty("compileReferenceSha256");
    var expectedHash = hashes.TryGetProperty(platform, out var hash)
        ? hash.GetString() ?? ""
        : "";
    Console.WriteLine(string.Join('\t',
        mpvNet.GetProperty("version").GetString(),
        mpvNet.GetProperty("tag").GetString(),
        mpvNet.GetProperty("sourceCommit").GetString(),
        expectedHash));
    return 0;
}

if (args is ["sha256", var path])
{
    using var stream = File.OpenRead(path);
    Console.WriteLine(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    return 0;
}

Console.Error.WriteLine("Usage: deps-lock.cs read <lock-file> <platform> | sha256 <file>");
return 2;
