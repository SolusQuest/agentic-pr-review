using System.Runtime.InteropServices;

if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
    Environment.Version.ToString() != "10.0.13" || args.Length != 2)
    return 1;
var accepted = args[1] == "accept";
if (args[1] is not ("accept" or "reject") || args[0] is not
    ("api.github.com" or "codeload.github.com" or "api.deepseek.com" or "apr-d5.blob.core.windows.net" or
     "wrong-host.example" or "untrusted-root.example")) return 1;
using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(5) };
try
{
    var body = await client.GetStringAsync("https://" + args[0] + "/d5-tls-prerequisite");
    if (!accepted || body != "D5 synthetic HTTPS\n") return 1;
    Console.WriteLine("APR_D5_TLS_ACCEPTED");
    return 0;
}
catch (HttpRequestException error) when (!accepted && error.HttpRequestError == HttpRequestError.SecureConnectionError)
{
    // A refused TCP connection or failed DNS lookup cannot satisfy this control.
    Console.WriteLine("APR_D5_TLS_REJECTED");
    return 0;
}
catch { return 1; }
