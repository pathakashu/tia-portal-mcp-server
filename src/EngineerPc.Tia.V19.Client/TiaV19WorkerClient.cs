using System.Text.Json;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.V19.Protocol;

namespace EngineerPc.Tia.V19.Client;

public sealed class TiaV19WorkerClient : ITiaAdapter, IProjectBlockCatalogReader, IDisposable
{
    private readonly TiaV19WorkerClientOptions options;
    private readonly ITiaV19WorkerTransport transport;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private bool disposed;

    public TiaV19WorkerClient(
        TiaV19WorkerClientOptions options,
        ITiaV19WorkerTransport? transport = null)
    {
        var validation = TiaV19WorkerClientOptionsValidator.Validate(options);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validation.Errors), nameof(options));
        }

        this.options = options;
        this.transport = transport ?? new TiaV19WorkerProcessTransport();
    }

    public async Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        await requestGate.WaitAsync(cancellationToken);
        try
        {
            var request = new TiaV19WorkerRequest(
                TiaV19WorkerProtocol.Version,
                Guid.NewGuid().ToString("N"),
                TiaV19WorkerProtocol.GetProjectContextMethod,
                projectId);
            var responsePayload = await transport.SendAsync(options, request, cancellationToken);
            var response = DeserializeResponse(responsePayload);

            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("TIA V19 worker response did not match the request ID.");
            }

            if (response.Error is not null)
            {
                return null;
            }

            if (!string.Equals(response.ProjectId, projectId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(response.SnapshotHash))
            {
                throw new InvalidOperationException("TIA V19 worker response is not a valid project context.");
            }

            return new ProjectContext(response.ProjectId!, response.SnapshotHash);
        }
        finally
        {
            requestGate.Release();
        }
    }

    public Task<TiaAdapterExecutionResult> CreateBlockAsync(
        CreateBlockOperation operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new TiaAdapterExecutionResult(
            null,
            ["TIA Portal V19 block creation is unavailable through the worker client."]));
    }

    public async Task<ProjectBlockCatalogPage?> GetBlockCatalogPageAsync(
        string projectId,
        int startIndex,
        int maximumBlockCount,
        string? expectedSnapshotHash,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (maximumBlockCount <= 0 || maximumBlockCount > TiaV19WorkerProtocol.MaximumBlockCatalogBlockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBlockCount));
        }

        if (startIndex > 0 && string.IsNullOrWhiteSpace(expectedSnapshotHash))
        {
            throw new ArgumentException("Expected snapshot hash is required for catalog continuation.", nameof(expectedSnapshotHash));
        }

        await requestGate.WaitAsync(cancellationToken);
        try
        {
            var request = new TiaV19WorkerRequest(
                TiaV19WorkerProtocol.Version,
                Guid.NewGuid().ToString("N"),
                TiaV19WorkerProtocol.GetBlockCatalogMethod,
                projectId,
                startIndex,
                maximumBlockCount,
                expectedSnapshotHash);
            var responsePayload = await transport.SendAsync(options, request, cancellationToken);
            var response = DeserializeBlockCatalogResponse(responsePayload);

            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("TIA V19 worker response did not match the request ID.");
            }

            if (response.Error is not null)
            {
                if (response.ErrorCode == TiaV19BlockCatalogErrorCode.SnapshotChanged)
                {
                    throw new ProjectBlockCatalogSnapshotChangedException();
                }

                return null;
            }

            if (response.ErrorCode != TiaV19BlockCatalogErrorCode.None)
            {
                throw new InvalidOperationException("TIA V19 worker returned an invalid block catalog error code.");
            }

            if (!string.Equals(response.ProjectId, projectId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(response.SnapshotHash) ||
                response.Blocks is null ||
                response.TotalBlockCount is null ||
                response.TotalBlockCount < 0 ||
                startIndex > response.TotalBlockCount ||
                response.Blocks.Count > maximumBlockCount ||
                response.Blocks.Count > response.TotalBlockCount - startIndex ||
                response.Blocks.Any(block =>
                    string.IsNullOrWhiteSpace(block.ControllerName) ||
                    string.IsNullOrWhiteSpace(block.Name) ||
                    block.Namespace is null ||
                    block.Number < 0 ||
                    string.IsNullOrWhiteSpace(block.ProgrammingLanguage)))
            {
                throw new InvalidOperationException("TIA V19 worker response is not a valid block catalog.");
            }

            var expectedNextStartIndex = (long)startIndex + response.Blocks.Count < response.TotalBlockCount
                ? startIndex + response.Blocks.Count
                : (int?)null;
            if (response.NextStartIndex != expectedNextStartIndex ||
                response.NextStartIndex is not null && response.NextStartIndex <= startIndex)
            {
                throw new InvalidOperationException("TIA V19 worker response has an invalid block catalog continuation.");
            }

            return new ProjectBlockCatalogPage(
                new ProjectContext(response.ProjectId!, response.SnapshotHash),
                response.Blocks.Select(block => new ProjectBlock(
                    block.ControllerName,
                    block.Name,
                    block.Namespace,
                    block.Number,
                    block.ProgrammingLanguage)).ToArray(),
                response.TotalBlockCount.Value,
                response.NextStartIndex);
        }
        finally
        {
            requestGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        requestGate.Dispose();
        disposed = true;
    }

    private static TiaV19ProjectContextResponse DeserializeResponse(string responsePayload)
    {
        try
        {
            return JsonSerializer.Deserialize<TiaV19ProjectContextResponse>(
                responsePayload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("TIA V19 worker returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("TIA V19 worker returned invalid JSON.", exception);
        }
    }

    private static TiaV19BlockCatalogResponse DeserializeBlockCatalogResponse(string responsePayload)
    {
        try
        {
            return JsonSerializer.Deserialize<TiaV19BlockCatalogResponse>(
                responsePayload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("TIA V19 worker returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("TIA V19 worker returned invalid JSON.", exception);
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(TiaV19WorkerClient));
        }
    }
}