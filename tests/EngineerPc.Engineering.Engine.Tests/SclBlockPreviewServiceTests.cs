using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Validation;

namespace EngineerPc.Engineering.Engine.Tests;

public sealed class SclBlockPreviewServiceTests
{
    private static readonly AuthenticatedIdentity Identity = new("engineer-1", "client-1");

    [Fact]
    public void Generate_WithValidFunctionBlock_ReturnsDeterministicSclPreviewAndAudit()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = CreateService(auditSink);

        var result = service.Generate(CreateOperation(), Identity);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "FUNCTION_BLOCK \"FB_Motor\"\nVAR_INPUT\n    Start : Bool;\nEND_VAR\nVAR_OUTPUT\n    Running : Bool;\nEND_VAR\nBEGIN\n    Running := Start;\nEND_FUNCTION_BLOCK",
            result.SourceText?.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(EngineeringAuditEventType.SclPreviewGenerated, Assert.Single(auditSink.Events).EventType);
    }

    [Fact]
    public void Generate_WithNonSclOperation_RejectsPreviewAndAuditsOutcome()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = CreateService(auditSink);

        var result = service.Generate(CreateOperation() with { Language = ProgrammingLanguage.Lad }, Identity);

        Assert.False(result.IsSuccess);
        Assert.Contains("SCL source generation requires the Scl programming language.", result.Errors);
        Assert.Equal(EngineeringAuditEventType.SclPreviewRejected, Assert.Single(auditSink.Events).EventType);
    }

    [Fact]
    public void Generate_WithUnsafeDataTypeToken_RejectsPreview()
    {
        var service = CreateService();
        var operation = CreateOperation() with
        {
            Interface = new BlockInterface([new BlockParameter("Start", "Bool; END_VAR")])
        };

        var result = service.Generate(operation, Identity);

        Assert.False(result.IsSuccess);
        Assert.Contains("Input parameter 'Start' has an unsupported SCL data type token.", result.Errors);
    }

    [Fact]
    public void Generate_WithUnsafeAssignmentSource_RejectsPreview()
    {
        var service = CreateService();
        var operation = CreateOperation() with
        {
            Statements = [new SclAssignment("Running", "Start; END_VAR")]
        };

        var result = service.Generate(operation, Identity);

        Assert.False(result.IsSuccess);
        Assert.Contains("SCL assignment source must be an identifier.", result.Errors);
    }

    private static SclBlockPreviewService CreateService(IEngineeringAuditSink? auditSink = null) => new(
        new EngineeringOperationPlanner(new CreateBlockOperationValidator()),
        new DefaultEngineeringPolicy(),
        auditSink);

    private static CreateBlockOperation CreateOperation() => new(
        Guid.Parse("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
        new ProjectContext("project-1", "snapshot-1"),
        "preview-fb-motor",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface(
            [new BlockParameter("Start", "Bool")],
            [new BlockParameter("Running", "Bool")]),
        [new SclAssignment("Running", "Start")]);
}