using System.Text.Json;
using EngineerPc.Tia.V19;
using EngineerPc.Tia.V19.Protocol;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var configurationPath = GetConfigurationPath(args);
var configuration = LoadConfiguration(configurationPath, jsonOptions);
TiaV19AssemblyResolver.Configure();
using var adapter = new TiaV19Adapter(new TiaV19ProjectCatalog(configuration.Projects));

string? requestLine;
while ((requestLine = Console.ReadLine()) is not null)
{
    var response = HandleRequest(requestLine, adapter, jsonOptions);
    Console.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
}

static object HandleRequest(
    string requestLine,
    TiaV19Adapter adapter,
    JsonSerializerOptions jsonOptions)
{
    TiaV19WorkerRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<TiaV19WorkerRequest>(requestLine, jsonOptions);
    }
    catch (JsonException)
    {
        return new TiaV19ProjectContextResponse(string.Empty, null, null, "Worker request is not valid JSON.");
    }

    if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
    {
        return new TiaV19ProjectContextResponse(string.Empty, null, null, "Worker request ID is required.");
    }

    if (!string.Equals(request.ProtocolVersion, TiaV19WorkerProtocol.Version, StringComparison.Ordinal))
    {
        return new TiaV19ProjectContextResponse(request.RequestId, null, null, "Worker protocol version is unsupported.");
    }

    if (string.IsNullOrWhiteSpace(request.ProjectId))
    {
        return new TiaV19ProjectContextResponse(request.RequestId, null, null, "Worker method is not supported.");
    }

    return request.Method switch
    {
        TiaV19WorkerProtocol.GetProjectContextMethod => adapter.ReadProjectContext(request.RequestId, request.ProjectId!),
        TiaV19WorkerProtocol.GetBlockCatalogMethod => ReadBlockCatalog(adapter, request),
        _ => new TiaV19ProjectContextResponse(request.RequestId, null, null, "Worker method is not supported.")
    };
}

static TiaV19BlockCatalogResponse ReadBlockCatalog(TiaV19Adapter adapter, TiaV19WorkerRequest request)
{
    var startIndex = request.BlockCatalogStartIndex.GetValueOrDefault();
    var maximumBlockCount = request.BlockCatalogMaximumBlockCount.GetValueOrDefault();
    if (request.BlockCatalogStartIndex is null ||
        request.BlockCatalogMaximumBlockCount is null ||
        startIndex < 0 ||
        maximumBlockCount <= 0 ||
        maximumBlockCount > TiaV19WorkerProtocol.MaximumBlockCatalogBlockCount ||
        startIndex > 0 && string.IsNullOrWhiteSpace(request.BlockCatalogExpectedSnapshotHash))
    {
        return new TiaV19BlockCatalogResponse(
            request.RequestId,
            null,
            null,
            [],
            null,
            null,
            TiaV19BlockCatalogErrorCode.None,
            "Worker block catalog page parameters are invalid.");
    }

    return adapter.ReadBlockCatalog(
        request.RequestId,
        request.ProjectId!,
        startIndex,
        maximumBlockCount,
        request.BlockCatalogExpectedSnapshotHash);
}

static string GetConfigurationPath(string[] arguments)
{
    if (arguments.Length != 2 || !string.Equals(arguments[0], "--configuration", StringComparison.Ordinal))
    {
        throw new ArgumentException("Usage: EngineerPc.Tia.V19.Worker --configuration <absolute-path>");
    }

    if (!Path.IsPathRooted(arguments[1]))
    {
        throw new ArgumentException("Worker configuration path must be absolute.");
    }

    return arguments[1];
}

static TiaV19WorkerConfiguration LoadConfiguration(string configurationPath, JsonSerializerOptions jsonOptions)
{
    if (!File.Exists(configurationPath))
    {
        throw new FileNotFoundException("TIA V19 worker configuration was not found.", configurationPath);
    }

    var configuration = JsonSerializer.Deserialize<TiaV19WorkerConfiguration>(File.ReadAllText(configurationPath), jsonOptions);
    if (configuration?.Projects is null)
    {
        throw new InvalidOperationException("TIA V19 worker configuration requires project definitions.");
    }

    return configuration;
}