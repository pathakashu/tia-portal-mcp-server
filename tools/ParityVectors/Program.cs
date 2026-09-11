using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Engine;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Transactions;
using EngineerPc.Engineering.Validation;
using EngineerPc.Tia.Mock;
using EngineerPc.Tia.V19.Protocol;

// Emits golden behavioural vectors from the C# implementation so the Python port can
// assert byte-for-byte parity instead of relying on assumptions. Run with:
//   dotnet run --project tools/ParityVectors -- <output-path>

var outputPath = args.Length > 0 ? args[0] : "python/tests/parity_vectors.json";

// The planner hashes with plain Web defaults: enums serialise as INTEGERS here.
var hashOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// The MCP host serialises API payloads with Web defaults PLUS string enums.
var apiOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
apiOptions.Converters.Add(new JsonStringEnumConverter());

var planner = new EngineeringOperationPlanner(new CreateBlockOperationValidator());

var operations = new Dictionary<string, CreateBlockOperation>
{
    ["minimal"] = new(
        Guid.Parse("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
        new ProjectContext("project-1", "snapshot-1"),
        "idempotency-1",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([])),
    ["with_inputs"] = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new ProjectContext("project-1", "snapshot-1"),
        "idempotency-2",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([new BlockParameter("Start", "Bool")])),
    ["full"] = new(
        Guid.Parse("deadbeef-0000-1111-2222-333344445555"),
        new ProjectContext("test-project", "38C0156A746AAD5998E6A201E6B7A13D17D403E4D2698AC450EF7AB0980D3BE2"),
        "idempotency-3",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface(
            [new BlockParameter("Start", "Bool"), new BlockParameter("Speed", "Int")],
            [new BlockParameter("Running", "Bool")]),
        [new SclAssignment("Running", "Start")],
        "CIA_0001"),
    ["function_no_outputs"] = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        new ProjectContext("p", "s"),
        "k",
        "FC_Safety",
        BlockType.Function,
        ProgrammingLanguage.Scl,
        new BlockInterface([new BlockParameter("In1", "Bool")])),
    ["organization_lad"] = new(
        Guid.Parse("00000000-0000-0000-0000-000000000002"),
        new ProjectContext("p", "s"),
        "k",
        "OB_Main",
        BlockType.OrganizationBlock,
        ProgrammingLanguage.Lad,
        new BlockInterface([])),
};

var operationVectors = new List<object>();
foreach (var (key, operation) in operations)
{
    var serialized = JsonSerializer.Serialize(operation, hashOptions);
    var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    var planned = planner.Plan(operation);

    // Self-check: our local serialisation must reproduce the planner's real hash,
    // otherwise the emitted JSON is not the true hash input.
    if (planned.IsSuccess && planned.Plan!.OperationHash != expectedHash)
    {
        throw new InvalidOperationException($"Vector '{key}' does not reproduce the planner hash.");
    }

    operationVectors.Add(new
    {
        key,
        apiJson = JsonSerializer.Serialize(operation, apiOptions),
        hashJson = serialized,
        operationHash = planned.IsSuccess ? planned.Plan!.OperationHash : null,
        planErrors = planned.Errors,
        preview = planned.IsSuccess ? planned.Plan!.Preview : null,
        sclValidationErrors = SclSourceRenderer.Validate(operation),
        sclSource = SclSourceRenderer.Validate(operation).Count == 0 ? SclSourceRenderer.Render(operation) : null,
    });
}

// Project snapshot hashing (worker protocol), including the empty-version case that
// real V19 projects actually produce.
var snapshotVectors = new List<object>();
var snapshotInputs = new (string ProjectId, string Name, string Path, DateTime Modified, string Version)[]
{
    ("project-1", "Main", @"C:\Projects\Main.ap19", new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc), "19.0"),
    ("project-1", "Main", @"C:\Projects\Main.ap19", new DateTime(2026, 9, 7, 12, 0, 1, DateTimeKind.Utc), "19.0"),
    ("test-project", "CIA_0001_V19", @"C:\Gautam\CIA_0001_V19\CIA_0001_V19.ap19", new DateTime(2025, 11, 24, 17, 1, 4, DateTimeKind.Utc), ""),
    ("p", "n", @"C:\x.ap19", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), ""),
};
foreach (var input in snapshotInputs)
{
    snapshotVectors.Add(new
    {
        projectId = input.ProjectId,
        projectName = input.Name,
        projectFilePath = input.Path,
        lastModifiedUtcTicks = input.Modified.Ticks,
        version = input.Version,
        snapshotHash = TiaV19ProjectSnapshot.Calculate(
            input.ProjectId, input.Name, input.Path, input.Modified, input.Version),
    });
}

// Deterministic MCP request id: SHA256 then .NET's mixed-endian Guid byte layout.
var requestIdVectors = new List<object>();
foreach (var (session, rawId) in new (string, string)[]
{
    ("526ec86b11c94dbdb66b07f3c2275051", "\"1\""),
    ("526ec86b11c94dbdb66b07f3c2275051", "\"2\""),
    ("00000000000000000000000000000000", "42"),
    ("0e1ab139394c4d169783bb14573d7d16", "\"request-id\""),
})
{
    var sessionId = Guid.ParseExact(session, "N");
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId:N}:{rawId}"));
    requestIdVectors.Add(new
    {
        sessionId = session,
        rawJsonRpcId = rawId,
        requestId = new Guid(hash.AsSpan(0, 16)).ToString(),
    });
}

// Mock adapter snapshot hashing, captured through its real public API.
var mockVectors = new List<object>();
{
    var adapter = new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]);
    foreach (var blockName in new[] { "FB_Motor", "FC_Safety", "AB_First" })
    {
        var context = await adapter.GetProjectContextAsync("project-1", CancellationToken.None);
        var operation = new CreateBlockOperation(
            Guid.NewGuid(),
            context!,
            "k",
            blockName,
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface([]));
        var result = await adapter.CreateBlockAsync(operation, CancellationToken.None);
        mockVectors.Add(new
        {
            addedBlock = blockName,
            snapshotHash = result.UpdatedProjectContext!.SnapshotHash,
        });
    }
}

// Worker protocol request wire format the existing C# worker.exe must be able to parse.
var workerRequestVectors = new List<object>
{
    new
    {
        key = "get_project_context",
        json = JsonSerializer.Serialize(new TiaV19WorkerRequest(
            TiaV19WorkerProtocol.Version, "req-1", TiaV19WorkerProtocol.GetProjectContextMethod, "test-project"), hashOptions),
    },
    new
    {
        key = "get_block_catalog",
        json = JsonSerializer.Serialize(new TiaV19WorkerRequest(
            TiaV19WorkerProtocol.Version, "req-2", TiaV19WorkerProtocol.GetBlockCatalogMethod, "test-project", 0, 50, null), hashOptions),
    },
    new
    {
        key = "create_block",
        json = JsonSerializer.Serialize(new TiaV19WorkerRequest(
            TiaV19WorkerProtocol.Version,
            "req-3",
            TiaV19WorkerProtocol.CreateBlockMethod,
            "test-project",
            CreateBlockControllerName: "CIA_0001",
            CreateBlockName: "FB_Motor",
            CreateBlockType: "FunctionBlock",
            CreateBlockSourceText: "FUNCTION_BLOCK \"FB_Motor\"\r\nEND_FUNCTION_BLOCK",
            CreateBlockExpectedSnapshotHash: "snapshot-1"), hashOptions),
    },
};

// A transaction as the MCP API serialises it (string enums, ISO-8601 timestamps).
var transaction = new EngineeringTransaction(
    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
    Guid.Parse("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
    "OPERATIONHASH",
    new ProjectContext("project-1", "snapshot-1"),
    "idempotency-1",
    TransactionState.AwaitingApproval,
    new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

// String escaping: .NET's default JavaScriptEncoder is more aggressive than Python's
// json.dumps, so the Python writer has to reproduce it exactly for hash parity.
var escapingVectors = new List<object>();
foreach (var sample in new[]
{
    "plain",
    "quote\"inside",
    "back\\slash",
    "slash/inside",
    "amp&persand",
    "apo'strophe",
    "plus+sign",
    "angle<brackets>",
    "back`tick",
    "tab\there",
    "newline\nhere",
    "carriage\rreturn",
    "back\bspace",
    "form\ffeed",
    "controlchar",
    "accented-é",
    "japanese-日本語",
    "emoji-\U0001F600",
    "equals=and:colon;semi,comma",
    "brackets[]{}()|~^@!#$%*-._?",
})
{
    escapingVectors.Add(new
    {
        raw = sample,
        serialized = JsonSerializer.Serialize(sample, hashOptions),
    });
}

var document = new
{
    generatedBy = "tools/ParityVectors",
    stringEscaping = escapingVectors,
    protocolVersion = TiaV19WorkerProtocol.Version,
    operations = operationVectors,
    projectSnapshots = snapshotVectors,
    deterministicRequestIds = requestIdVectors,
    mockAdapterSnapshots = mockVectors,
    workerRequests = workerRequestVectors,
    transactionApiJson = JsonSerializer.Serialize(transaction, apiOptions),
};

var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

File.WriteAllText(
    outputPath,
    JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

Console.WriteLine($"Wrote parity vectors to {Path.GetFullPath(outputPath)}");
