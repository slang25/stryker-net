using System;
using System.Globalization;
using System.IO.Pipes;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using Stryker.MSBuildHost;

// Args: <pipeName> [cultureName]
// We talk RPC over a named pipe (named after a GUID supplied by the client).
// Stdout/stderr are reserved for diagnostics; we close stdin immediately so MSBuild
// tasks that try to read from the console cannot deadlock.
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: Stryker.MSBuildHost <pipeName> [cultureName]");
    return 1;
}

var pipeName = args[0];
if (args.Length >= 2 && !string.IsNullOrEmpty(args[1]))
{
    try { CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(args[1]); }
    catch (CultureNotFoundException) { /* fall back to default */ }
}

try { Console.OpenStandardInput().Dispose(); } catch { /* best effort */ }

using var pipe = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    maxNumberOfServerInstances: 1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);

await pipe.WaitForConnectionAsync().ConfigureAwait(false);

var host = new StrykerMSBuildHost();
var formatter = new SystemTextJsonFormatter();
var handler = new LengthHeaderMessageHandler(pipe.UsePipe(), formatter);
using var rpc = new JsonRpc(handler, host);
rpc.StartListening();
await rpc.Completion.ConfigureAwait(false);
return 0;
