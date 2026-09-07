using System.Text.Json;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.V19.Client;
using EngineerPc.Tia.V19.Protocol;

namespace EngineerPc.Tia.V19.Client.Tests;

public sealed class TiaV19WorkerClientTests : IDisposable
{
    private readonly string workerExecutablePath = Path.ChangeExtension(Path.GetTempFileName(), ".exe");
    private readonly string configurationPath = Path.GetTempFileName();

    public TiaV19WorkerClientTests()
    {
        File.Move(Path.ChangeExtension(workerExecutablePath, ".tmp"), workerExecutablePath);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithMatchingWorkerResponse_ReturnsProjectContext()
    {
        var transport = new ControlledWorkerTransport(request => Success(request, "project-1", "snapshot-1"));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.GetProjectContextAsync("project-1", CancellationToken.None);

        Assert.Equal(new ProjectContext("project-1", "snapshot-1"), result);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(TiaV19WorkerProtocol.Version, request.ProtocolVersion);
        Assert.Equal(TiaV19WorkerProtocol.GetProjectContextMethod, request.Method);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithWorkerError_ReturnsNoContext()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV19ProjectContextResponse(request.RequestId, null, null, "The configured TIA V19 project was not found.")));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.GetProjectContextAsync("unknown", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithMismatchedResponseId_RejectsWorkerResponse()
    {
        var transport = new ControlledWorkerTransport(_ => JsonSerializer.Serialize(
            new TiaV19ProjectContextResponse("other-request", "project-1", "snapshot-1", null)));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetProjectContextAsync("project-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetProjectContextAsync_SerializesWorkerRequests()
    {
        var transport = new ControlledWorkerTransport(request => Success(request, request.ProjectId!, "snapshot"), TimeSpan.FromMilliseconds(25));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

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
            new TiaV19BlockCatalogResponse(
                request.RequestId,
                "project-1",
                "snapshot-1",
                [new TiaV19BlockDefinition("PLC_1", "FB_Motor", "", 1, "Scl")],
                2,
                1,
                TiaV19BlockCatalogErrorCode.None,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.GetBlockCatalogPageAsync("project-1", 0, 1, null, CancellationToken.None);

        Assert.Equal(new ProjectContext("project-1", "snapshot-1"), result?.ProjectContext);
        Assert.Equal(new ProjectBlock("PLC_1", "FB_Motor", "", 1, "Scl"), Assert.Single(result!.Blocks));
        Assert.Equal(2, result.TotalBlockCount);
        Assert.Equal(1, result.NextStartIndex);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(TiaV19WorkerProtocol.GetBlockCatalogMethod, request.Method);
        Assert.Equal(0, request.BlockCatalogStartIndex);
        Assert.Equal(1, request.BlockCatalogMaximumBlockCount);
        Assert.Null(request.BlockCatalogExpectedSnapshotHash);
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithWorkerError_ReturnsNoCatalog()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV19BlockCatalogResponse(request.RequestId, null, null, [], null, null, TiaV19BlockCatalogErrorCode.None, "The configured TIA V19 project was not found.")));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.GetBlockCatalogPageAsync("unknown", 0, 10, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithNonProgressingContinuation_RejectsWorkerResponse()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV19BlockCatalogResponse(
                request.RequestId,
                "project-1",
                "snapshot-1",
                [],
                2,
                0,
                TiaV19BlockCatalogErrorCode.None,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetBlockCatalogPageAsync("project-1", 0, 1, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithPageBeyondDeclaredTotal_RejectsWorkerResponse()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV19BlockCatalogResponse(
                request.RequestId,
                "project-1",
                "snapshot-1",
                [
                    new TiaV19BlockDefinition("PLC_1", "FB_Motor", "", 1, "Scl"),
                    new TiaV19BlockDefinition("PLC_1", "FC_Motor", "", 2, "Scl")
                ],
                1,
                null,
                TiaV19BlockCatalogErrorCode.None,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetBlockCatalogPageAsync("project-1", 0, 2, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetBlockCatalogPageAsync_WithChangedSnapshot_RejectsContinuation()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV19BlockCatalogResponse(
                request.RequestId,
                null,
                null,
                [],
                null,
                null,
                TiaV19BlockCatalogErrorCode.SnapshotChanged,
                "The configured TIA V19 project snapshot has changed."),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        await Assert.ThrowsAsync<ProjectBlockCatalogSnapshotChangedException>(() =>
            client.GetBlockCatalogPageAsync("project-1", 1, 1, "snapshot-1", CancellationToken.None));
    }

    [Fact]
    public async Task CreateBlockAsync_WithoutControllerName_FailsWithoutDispatch()
    {
        var transport = new ControlledWorkerTransport(request => CreateBlockSuccess(request, "project-1", "snapshot-2"));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.CreateBlockAsync(CreateOperation(controllerName: null), CancellationToken.None, "FUNCTION_BLOCK \"FB_Motor\"");

        Assert.False(result.IsSuccess);
        Assert.Contains("TIA V19 block creation requires the operation to declare a controller name.", result.Errors);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task CreateBlockAsync_WithoutRenderedSource_FailsWithoutDispatch()
    {
        var transport = new ControlledWorkerTransport(request => CreateBlockSuccess(request, "project-1", "snapshot-2"));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.CreateBlockAsync(CreateOperation(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            "TIA V19 block creation only supports the constrained Scl Function/FunctionBlock source surface.",
            result.Errors);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task CreateBlockAsync_WithMatchingWorkerResponse_ReturnsUpdatedContext()
    {
        var transport = new ControlledWorkerTransport(request => CreateBlockSuccess(request, "project-1", "snapshot-2"));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.CreateBlockAsync(CreateOperation(), CancellationToken.None, "FUNCTION_BLOCK \"FB_Motor\"");

        Assert.True(result.IsSuccess);
        Assert.Equal(new ProjectContext("project-1", "snapshot-2"), result.UpdatedProjectContext);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(TiaV19WorkerProtocol.CreateBlockMethod, request.Method);
        Assert.Equal("PLC_1", request.CreateBlockControllerName);
        Assert.Equal("FB_Motor", request.CreateBlockName);
        Assert.Equal("FUNCTION_BLOCK \"FB_Motor\"", request.CreateBlockSourceText);
        Assert.Equal("snapshot-1", request.CreateBlockExpectedSnapshotHash);
    }

    [Fact]
    public async Task CreateBlockAsync_WithWorkerError_ReturnsFailureWithoutThrowing()
    {
        var transport = new ControlledWorkerTransport(request => JsonSerializer.Serialize(
            new TiaV19CreateBlockResponse(
                request.RequestId,
                null,
                null,
                TiaV19CreateBlockErrorCode.BlockAlreadyExists,
                "Block 'FB_Motor' already exists under controller 'PLC_1'.")));
        using var client = new TiaV19WorkerClient(CreateOptions(), transport);

        var result = await client.CreateBlockAsync(CreateOperation(), CancellationToken.None, "FUNCTION_BLOCK \"FB_Motor\"");

        Assert.False(result.IsSuccess);
        Assert.Contains("Block 'FB_Motor' already exists under controller 'PLC_1'.", result.Errors);
    }

    [Fact]
    public void OptionsValidator_RejectsNoncanonicalOrMissingWorkerPaths()
    {
        var options = new TiaV19WorkerClientOptions(
            Path.Combine(Path.GetDirectoryName(workerExecutablePath)!, ".", Path.GetFileName(workerExecutablePath)),
            "missing.json",
            TimeSpan.Zero);

        var validation = TiaV19WorkerClientOptionsValidator.Validate(options);

        Assert.False(validation.IsValid);
        Assert.Contains("TIA V19 worker executable path must be canonical.", validation.Errors);
        Assert.Contains("TIA V19 worker configuration path must be absolute.", validation.Errors);
        Assert.Contains("TIA V19 worker request timeout must be between 1 second and 5 minutes.", validation.Errors);
    }

    public void Dispose()
    {
        File.Delete(workerExecutablePath);
        File.Delete(configurationPath);
    }

    private TiaV19WorkerClientOptions CreateOptions() => new(
        workerExecutablePath,
        configurationPath,
        TimeSpan.FromSeconds(5));

    private static string Success(TiaV19WorkerRequest request, string projectId, string snapshotHash) => JsonSerializer.Serialize(
        new TiaV19ProjectContextResponse(request.RequestId, projectId, snapshotHash, null),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string CreateBlockSuccess(TiaV19WorkerRequest request, string projectId, string snapshotHash) => JsonSerializer.Serialize(
        new TiaV19CreateBlockResponse(request.RequestId, projectId, snapshotHash, TiaV19CreateBlockErrorCode.None, null),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CreateBlockOperation CreateOperation(string? controllerName = "PLC_1") => new(
        Guid.NewGuid(),
        new ProjectContext("project-1", "snapshot-1"),
        "idempotency-key",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([]),
        ControllerName: controllerName);

    private sealed class ControlledWorkerTransport(
        Func<TiaV19WorkerRequest, string> responseFactory,
        TimeSpan? delay = null) : ITiaV19WorkerTransport
    {
        private int activeRequests;
        private int maximumConcurrentRequests;

        public List<TiaV19WorkerRequest> Requests { get; } = [];

        public int MaximumConcurrentRequests => maximumConcurrentRequests;

        public async Task<string> SendAsync(
            TiaV19WorkerClientOptions options,
            TiaV19WorkerRequest request,
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