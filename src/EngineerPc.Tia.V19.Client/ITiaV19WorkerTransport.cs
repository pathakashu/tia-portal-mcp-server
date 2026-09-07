using EngineerPc.Tia.V19.Protocol;

namespace EngineerPc.Tia.V19.Client;

public interface ITiaV19WorkerTransport
{
    Task<string> SendAsync(
        TiaV19WorkerClientOptions options,
        TiaV19WorkerRequest request,
        CancellationToken cancellationToken);
}