using System.Text.Json;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.V18.Client;
using EngineerPc.Tia.V18.Protocol;

namespace EngineerPc.Tia.V18.Client.Tests;

public sealed class TiaV18WorkerClientTests : IDisposable
{
    private readonly string workerExecutablePath = Path.ChangeExtension(Path.GetTempFileName(), ".exe");
    private readonly string configurationPath = Path.GetTempFileName();

    public TiaV18WorkerClientTests()
    {
        File.Move(Path.ChangeExtension(workerExecutablePath, ".tmp"), workerExecutablePath);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithMatchingWorkerResponse_ReturnsProjectContext()
    {
        var transport = new ControlledWorkerTransport(request => Success(request, "project-1", "snapshot-1"));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        var result = await client.GetProjectContextAsync("project-1", CancellationToken.None);

        Assert.Equal(new ProjectContext("project-1", "snapshot-1"), result);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(TiaV18WorkerProtocol.Version, request.ProtocolVersion);
        Assert.Equal(TiaV18WorkerProtocol.GetProjectContextMethod, request.Method);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithWorkerError_ReturnsNoContext()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV18ProjectContextResponse(request.RequestId, null, null, "The configured TIA V18 project was not found.")));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        var result = await client.GetProjectContextAsync("unknown", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithMismatchedResponseId_RejectsWorkerResponse()
    {
        var transport = new ControlledWorkerTransport(_ => JsonSerializer.Serialize(
            new TiaV18ProjectContextResponse("other-request", "project-1", "snapshot-1", null)));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetProjectContextAsync("project-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetProjectContextAsync_SerializesWorkerRequests()
    {
        var transport = new ControlledWorkerTransport(request => Success(request, request.ProjectId!, "snapshot"), TimeSpan.FromMilliseconds(25));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        await Task.WhenAll(
            client.GetProjectContextAsync("project-1", CancellationToken.None),
            client.GetProjectContextAsync("project-2", CancellationToken.None));

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(1, transport.MaximumConcurrentRequests);
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithMatchingWorkerResponse_ReturnsReadOnlyBlockMetadata()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV18BlockCatalogResponse(
                request.RequestId,
                "project-1",
                "snapshot-1",
                [new TiaV18BlockDefinition("PLC_1", "FB_Motor", "", 1, "Scl")],
                2,
                1,
                TiaV18BlockCatalogErrorCode.None,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        var result = await client.GetBlockCatalogPageAsync("project-1", 0, 1, null, CancellationToken.None);

        Assert.Equal(new ProjectContext("project-1", "snapshot-1"), result?.ProjectContext);
        Assert.Equal(new ProjectBlock("PLC_1", "FB_Motor", "", 1, "Scl"), Assert.Single(result!.Blocks));
        Assert.Equal(2, result.TotalBlockCount);
        Assert.Equal(1, result.NextStartIndex);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(TiaV18WorkerProtocol.GetBlockCatalogMethod, request.Method);
        Assert.Equal(0, request.BlockCatalogStartIndex);
        Assert.Equal(1, request.BlockCatalogMaximumBlockCount);
        Assert.Null(request.BlockCatalogExpectedSnapshotHash);
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithWorkerError_ReturnsNoCatalog()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV18BlockCatalogResponse(request.RequestId, null, null, [], null, null, TiaV18BlockCatalogErrorCode.None, "The configured TIA V18 project was not found.")));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        var result = await client.GetBlockCatalogPageAsync("unknown", 0, 10, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithNonProgressingContinuation_RejectsWorkerResponse()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV18BlockCatalogResponse(
                request.RequestId,
                "project-1",
                "snapshot-1",
                [],
                2,
                0,
                TiaV18BlockCatalogErrorCode.None,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetBlockCatalogPageAsync("project-1", 0, 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithPageBeyondDeclaredTotal_RejectsWorkerResponse()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV18BlockCatalogResponse(
                request.RequestId,
                "project-1",
                "snapshot-1",
                [
                    new TiaV18BlockDefinition("PLC_1", "FB_Motor", "", 1, "Scl"),
                    new TiaV18BlockDefinition("PLC_1", "FC_Motor", "", 2, "Scl")
                ],
                1,
                null,
                TiaV18BlockCatalogErrorCode.None,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetBlockCatalogPageAsync("project-1", 0, 2, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithChangedSnapshot_RejectsContinuation()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV18BlockCatalogResponse(
                request.RequestId,
                null,
                null,
                [],
                null,
                null,
                TiaV18BlockCatalogErrorCode.SnapshotChanged,
                "The configured TIA V18 project snapshot has changed."),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<ProjectBlockCatalogSnapshotChangedException>(() =>
            client.GetBlockCatalogPageAsync("project-1", 1, 1, "snapshot-1", CancellationToken.None));
    }

    [Fact]
    public async Task CreateBlockAsync_DoesNotDispatchAWriteToTheWorker()
    {
        var transport = new ControlledWorkerTransport(request => Success(request, "project-1", "snapshot-1"));
        using var client = new TiaV18WorkerClient(CreateOptions(), transport);

        var result = await client.CreateBlockAsync(CreateOperation(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("TIA Portal V18 block creation is unavailable through the worker client.", result.Errors);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void OptionsValidator_RejectsNoncanonicalOrMissingWorkerPaths()
    {
        var options = new TiaV18WorkerClientOptions(
            Path.Combine(Path.GetDirectoryName(workerExecutablePath)!, ".", Path.GetFileName(workerExecutablePath)),
            "missing.json",
            TimeSpan.Zero);

        var validation = TiaV18WorkerClientOptionsValidator.Validate(options);

        Assert.False(validation.IsValid);
        Assert.Contains("TIA V18 worker executable path must be canonical.", validation.Errors);
        Assert.Contains("TIA V18 worker configuration path must be absolute.", validation.Errors);
        Assert.Contains("TIA V18 worker request timeout must be between 1 second and 5 minutes.", validation.Errors);
    }

    public void Dispose()
    {
        File.Delete(workerExecutablePath);
        File.Delete(configurationPath);
    }

    private TiaV18WorkerClientOptions CreateOptions() => new(
        workerExecutablePath,
        configurationPath,
        TimeSpan.FromSeconds(5));

    private static string Success(TiaV18WorkerRequest request, string projectId, string snapshotHash) => JsonSerializer.Serialize(
        new TiaV18ProjectContextResponse(request.RequestId, projectId, snapshotHash, null),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CreateBlockOperation CreateOperation() => new(
        Guid.NewGuid(),
        new ProjectContext("project-1", "snapshot-1"),
        "idempotency-key",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([]));

    private sealed class ControlledWorkerTransport(
        Func<TiaV18WorkerRequest, string> responseFactory,
        TimeSpan? delay = null) : ITiaV18WorkerTransport
    {
        private int activeRequests;
        private int maximumConcurrentRequests;

        public List<TiaV18WorkerRequest> Requests { get; } = [];

        public int MaximumConcurrentRequests => maximumConcurrentRequests;

        public async Task<string> SendAsync(
            TiaV18WorkerClientOptions options,
            TiaV18WorkerRequest request,
            CancellationToken cancellationToken)
        {
            var activeRequestCount = Interlocked.Increment(ref activeRequests);
            InterlockedExtensions.Max(ref maximumConcurrentRequests, activeRequestCount);
            try
            {
                if (delay is not null)
                {
                    await Task.Delay(delay.Value, cancellationToken);
                }

                Requests.Add(request);
                return responseFactory(request);
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}