using System.Security.Cryptography;
using System.Reflection;
using System.Text;

if (args.Length == 4 && args[0] == "inspect")
{
    var assembly = Assembly.LoadFrom(Path.GetFullPath(args[1]));
    foreach (var (resourceName, expectedPath) in new[]
             {
                 ("update-key.pem", args[2]),
                 ("telegram-worker.js", args[3]),
             })
    {
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            Console.Error.WriteLine($"Embedded resource missing: {resourceName}");
            return 5;
        }

        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        if (!memory.ToArray().SequenceEqual(File.ReadAllBytes(Path.GetFullPath(expectedPath))))
        {
            Console.Error.WriteLine($"Embedded resource differs from source: {resourceName}");
            return 6;
        }
    }

    Console.WriteLine("embedded update key and Worker match their source files");
    return 0;
}

if (args.Length == 3 && args[0] == "verify")
{
    var privatePem = File.ReadAllText(Path.GetFullPath(args[1]));
    var publicPem = File.ReadAllText(Path.GetFullPath(args[2]));
    var payload = Encoding.ASCII.GetBytes(
        "MonkeyUpdate.v1\n1.2.3\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n");

    using var privateKey = ECDsa.Create();
    privateKey.ImportFromPem(privatePem);
    var signature = privateKey.SignData(
        payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    using var publicKey = ECDsa.Create();
    publicKey.ImportFromPem(publicPem);
    if (!publicKey.VerifyData(
            payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence))
    {
        Console.Error.WriteLine("The public and private update keys do not match.");
        return 4;
    }

    Console.WriteLine("update key signature self-test passed");
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: UpdateKeyTool <private.pem> <public.pem>");
    Console.Error.WriteLine("   or: UpdateKeyTool verify <private.pem> <public.pem>");
    Console.Error.WriteLine("   or: UpdateKeyTool inspect <service.dll> <public.pem> <worker.js>");
    return 2;
}

var privatePath = Path.GetFullPath(args[0]);
var publicPath = Path.GetFullPath(args[1]);
if (File.Exists(privatePath) || File.Exists(publicPath))
{
    Console.Error.WriteLine("Refusing to overwrite an existing update key.");
    return 3;
}

Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);
return 0;
