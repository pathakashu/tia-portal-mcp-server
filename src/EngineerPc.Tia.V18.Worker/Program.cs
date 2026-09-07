using System.Text.Json;
using EngineerPc.Tia.V18;
using EngineerPc.Tia.V18.Protocol;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var configurationPath = GetConfigurationPath(args);
var configuration = LoadConfiguration(configurationPath, jsonOptions);
TiaV18AssemblyResolver.Configure();
using var adapter = new TiaV18Adapter(new TiaV18ProjectCatalog(configuration.Projects));

string? requestLine;
while ((requestLine = Console.ReadLine()) is not null)
{
    var response = HandleRequest(requestLine, adapter, jsonOptions);
    Console.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
}

static object HandleRequest(
    string requestLine,
    TiaV18Adapter adapter,
    JsonSerializerOptions jsonOptions)
{
    TiaV18WorkerRequest? request;
    try
    {
        request = JsonSerializer.Deserialize<TiaV18WorkerRequest>(requestLine, jsonOptions);
    }
    catch (JsonException)
    {
        return new TiaV18ProjectContextResponse(string.Empty, null, null, "Worker request is not valid JSON.");
    }

    if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
    {
        return new TiaV18ProjectContextResponse(string.Empty, null, null, "Worker request ID is required.");
    }

    if (!string.Equals(request.ProtocolVersion, TiaV18WorkerProtocol.Version, StringComparison.Ordinal))
    {
        return new TiaV18ProjectContextResponse(request.RequestId, null, null, "Worker protocol version is unsupported.");
    }

    if (string.IsNullOrWhiteSpace(request.ProjectId))
    {
        return new TiaV18ProjectContextResponse(request.RequestId, null, null, "Worker method is not supported.");
    }

    return request.Method switch
    {
        TiaV18WorkerProtocol.GetProjectContextMethod => adapter.ReadProjectContext(request.RequestId, request.ProjectId!),
        TiaV18WorkerProtocol.GetBlockCatalogMethod => ReadBlockCatalog(adapter, request),
        _ => new TiaV18ProjectContextResponse(request.RequestId, null, null, "Worker method is not supported.")
    };
}

static TiaV18BlockCatalogResponse ReadBlockCatalog(TiaV18Adapter adapter, TiaV18WorkerRequest request)
{
    var startIndex = request.BlockCatalogStartIndex.GetValueOrDefault();
    var maximumBlockCount = request.BlockCatalogMaximumBlockCount.GetValueOrDefault();
    if (request.BlockCatalogStartIndex is null ||
        request.BlockCatalogMaximumBlockCount is null ||
        startIndex < 0 ||
        maximumBlockCount <= 0 ||
        maximumBlockCount > TiaV18WorkerProtocol.MaximumBlockCatalogBlockCount ||
        startIndex > 0 && string.IsNullOrWhiteSpace(request.BlockCatalogExpectedSnapshotHash))
    {
        return new TiaV18BlockCatalogResponse(
            request.RequestId,
            null,
            null,
            [],
            null,
            null,
            TiaV18BlockCatalogErrorCode.None,
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
        throw new ArgumentException("Usage: EngineerPc.Tia.V18.Worker --configuration <absolute-path>");
    }

    if (!Path.IsPathRooted(arguments[1]))
    {
        throw new ArgumentException("Worker configuration path must be absolute.");
    }

    return arguments[1];
}

static TiaV18WorkerConfiguration LoadConfiguration(string configurationPath, JsonSerializerOptions jsonOptions)
{
    if (!File.Exists(configurationPath))
    {
        throw new FileNotFoundException("TIA V18 worker configuration was not found.", configurationPath);
    }

    var configuration = JsonSerializer.Deserialize<TiaV18WorkerConfiguration>(File.ReadAllText(configurationPath), jsonOptions);
    if (configuration?.Projects is null)
    {
        throw new InvalidOperationException("TIA V18 worker configuration requires project definitions.");
    }

    return configuration;
}