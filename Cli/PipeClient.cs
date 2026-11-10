using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// ============================================================================
// PipeClient — one request/response round-trip to the service. No elevation
// needed: the pipe DACL admits any interactive user. null means the service
// is not reachable (not installed or not running).
// ============================================================================
internal static class PipeClient
{
    public const int QueryTimeoutMs = 10_000;
    public const int CommandTimeoutMs = 180_000; // share/subnet start can take a while

    public static PipeResponse? Send(PipeRequest request, int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(3000);
            // leaveOpen: the pipe is disposed once, by its own using — reader/writer
            // disposal must not close it (writer flush would then throw).
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(request));
            var read = reader.ReadLineAsync();
            if (!read.Wait(timeoutMs))
                return new PipeResponse
                {
                    Ok = false,
                    Message = $"The service did not answer within {timeoutMs / 1000}s. " +
                              "It may still be working — check 'Anything status' and the service log.",
                };
            string? line = read.Result;
            if (line is null)
                return new PipeResponse { Ok = false, Message = "The service closed the connection without answering." };
            return JsonSerializer.Deserialize<PipeResponse>(line)
                ?? new PipeResponse { Ok = false, Message = "Empty response from the service." };
        }
        catch (TimeoutException) { return null; }
        catch (UnauthorizedAccessException)
        {
            return new PipeResponse { Ok = false, Message = "Access to the control pipe was denied for this user." };
        }
        catch (Exception ex)
        {
            return new PipeResponse { Ok = false, Message = "Pipe error: " + ex.Message };
        }
    }
}