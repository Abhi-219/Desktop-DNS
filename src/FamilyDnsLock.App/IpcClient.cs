using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.App;

public sealed class IpcClient
{
    private const string PipeName = "FamilyDnsLockPipe";

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };

            var requestJson = JsonSerializer.Serialize(request);
            await writer.WriteLineAsync(requestJson).ConfigureAwait(false);

            var responseJson = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return new IpcResponse(false, "Received empty response from background service.");
            }

            return JsonSerializer.Deserialize<IpcResponse>(responseJson)
                ?? new IpcResponse(false, "Failed to parse response from service.");
        }
        catch (TimeoutException)
        {
            return new IpcResponse(false, "Connection timed out. Family DNS Lock service may not be running.");
        }
        catch (Exception ex)
        {
            return new IpcResponse(false, $"Service unavailable: {ex.Message}");
        }
    }
}
