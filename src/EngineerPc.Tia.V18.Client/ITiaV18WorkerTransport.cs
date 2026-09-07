using EngineerPc.Tia.V18.Protocol;

namespace EngineerPc.Tia.V18.Client;

public interface ITiaV18WorkerTransport
{
    Task<string> SendAsync(
        TiaV18WorkerClientOptions options,
        TiaV18WorkerRequest request,
        CancellationToken cancellationToken);
}