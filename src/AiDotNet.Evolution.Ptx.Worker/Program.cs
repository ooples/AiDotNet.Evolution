using System.Text.Json;
using AiDotNet.Evolution.Ptx;
using AiDotNet.Evolution.Ptx.Worker;

// The isolated PTX worker: one JSON request on standard input, one JSON response on standard output, then exit.
// Running candidates here, never in the host, means a hung or faulting kernel can only take down this process; the
// host's watchdog kills it at its deadline. Embedded in AiDotNet.Evolution.Ptx and written out at run time.
const long MaximumRequestBytes = 512L * 1024 * 1024;
CudaDriver.RegisterResolver();
var options = new JsonSerializerOptions { MaxDepth = 16 };
PtxWorkerResponse response;
try
{
    using Stream input = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    var chunk = new byte[81920];
    int read;
    while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
    {
        if (buffer.Length + read > MaximumRequestBytes) throw new InvalidDataException("The request exceeds its byte bound.");
        buffer.Write(chunk, 0, read);
    }
    PtxWorkerRequest request = JsonSerializer.Deserialize<PtxWorkerRequest>(buffer.ToArray(), options)
        ?? throw new InvalidDataException("The request is empty.");
    response = new PtxWorkerExecutor(request).Execute();
}
catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
{
    response = new PtxWorkerResponse { Status = "invalid-request", Message = exception.GetType().Name };
}
catch (Exception exception)
{
    // Report, rather than crash with a stack trace the host cannot parse; the host still bounds what it reads.
    string message = exception.GetType().Name + ": " + exception.Message;
    response = new PtxWorkerResponse { Status = "worker-error", Message = message.Length > 1024 ? message.Substring(0, 1024) : message };
}
using (Stream output = Console.OpenStandardOutput())
{
    JsonSerializer.Serialize(output, response, options);
    output.Flush();
}
return 0;