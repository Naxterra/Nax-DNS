using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Ipc;

public sealed class ControlClient(string pipeName = ControlClient.PipeName)
{
    public const string PipeName = "BetterDNS.Control";
    public JsonSerializerOptions JsonOptions => JsonSettings.Wire;

    public async Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken = requestTimeout.Token;

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var request = new Request(command, JsonSerializer.SerializeToElement(payload ?? false, JsonOptions));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        var responseLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<Response>(responseLine ?? string.Empty, JsonOptions)
            ?? throw new InvalidDataException("Service returned an empty response.");
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "The BetterDNS service rejected the request.");
        }

        return response.Data.Deserialize<T>(JsonOptions)
            ?? throw new InvalidDataException("Service response payload was empty.");
    }

    private sealed record Request(string Command, JsonElement Payload);
    private sealed record Response(bool Success, JsonElement Data, string? Error);
}
