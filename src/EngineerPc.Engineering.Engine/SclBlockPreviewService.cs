using System.Text;
using System.Text.RegularExpressions;
using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;

namespace EngineerPc.Engineering.Engine;

public sealed partial class SclBlockPreviewService : ISclBlockPreviewService
{
    private readonly IEngineeringOperationPlanner planner;
    private readonly IEngineeringPolicy policy;
    private readonly IEngineeringAuditSink auditSink;
    private readonly TimeProvider timeProvider;

    public SclBlockPreviewService(
        IEngineeringOperationPlanner planner,
        IEngineeringPolicy policy,
        IEngineeringAuditSink? auditSink = null,
        TimeProvider? timeProvider = null)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.auditSink = auditSink ?? NullEngineeringAuditSink.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SclBlockPreviewResult Generate(CreateBlockOperation operation, AuthenticatedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(identity);

        var planning = planner.Plan(operation);
        if (!planning.IsSuccess)
        {
            return Reject(operation, identity, null, null, planning.Errors, "SCL preview validation failed.");
        }

        var policyDecision = policy.Evaluate(operation);
        if (!policyDecision.IsAllowed)
        {
            return Reject(
                operation,
                identity,
                planning.Plan,
                policyDecision,
                [policyDecision.DenialReason ?? "Operation is not permitted by policy."],
                "SCL preview was denied by policy.");
        }

        var errors = ValidateSclPreview(operation);
        if (errors.Count > 0)
        {
            return Reject(operation, identity, planning.Plan, policyDecision, errors, "SCL preview source constraints failed.");
        }

        var sourceText = Render(operation);
        Record(EngineeringAuditEventType.SclPreviewGenerated, operation, identity, "Constrained SCL source preview was generated.");
        return new SclBlockPreviewResult(planning.Plan, policyDecision, sourceText, []);
    }

    private static IReadOnlyList<string> ValidateSclPreview(CreateBlockOperation operation)
    {
        var errors = new List<string>();
        if (operation.Language != ProgrammingLanguage.Scl)
        {
            errors.Add("SCL preview requires the Scl programming language.");
        }

        if (operation.BlockType is not BlockType.Function and not BlockType.FunctionBlock)
        {
            errors.Add("SCL preview supports only Function and FunctionBlock block types.");
        }

        foreach (var input in operation.Interface.Inputs)
        {
            if (!SclTypePattern().IsMatch(input.DataType))
            {
                errors.Add($"Input parameter '{input.Name}' has an unsupported SCL data type token.");
            }
        }

        foreach (var output in operation.Interface.Outputs ?? [])
        {
            if (!SclTypePattern().IsMatch(output.DataType))
            {
                errors.Add($"Output parameter '{output.Name}' has an unsupported SCL data type token.");
            }
        }

        var inputNames = new HashSet<string>(
            operation.Interface.Inputs.Select(input => input.Name),
            StringComparer.Ordinal);
        var outputNames = new HashSet<string>(
            (operation.Interface.Outputs ?? []).Select(output => output.Name),
            StringComparer.Ordinal);
        foreach (var statement in operation.Statements ?? [])
        {
            if (!SclIdentifierPattern().IsMatch(statement.Target))
            {
                errors.Add("SCL assignment target must be an identifier.");
            }
            else if (!outputNames.Contains(statement.Target))
            {
                errors.Add($"SCL assignment target '{statement.Target}' must be a declared output.");
            }

            if (!SclIdentifierPattern().IsMatch(statement.Source))
            {
                errors.Add("SCL assignment source must be an identifier.");
            }
            else if (!inputNames.Contains(statement.Source) && !outputNames.Contains(statement.Source))
            {
                errors.Add($"SCL assignment source '{statement.Source}' must be a declared input or output.");
            }
        }

        return errors;
    }

    private static string Render(CreateBlockOperation operation)
    {
        var builder = new StringBuilder();
        if (operation.BlockType == BlockType.Function)
        {
            builder.Append("FUNCTION \"").Append(operation.Name).AppendLine("\" : Void");
        }
        else
        {
            builder.Append("FUNCTION_BLOCK \"").Append(operation.Name).AppendLine("\"");
        }

        AppendDeclarationSection(builder, "VAR_INPUT", operation.Interface.Inputs);
        if (operation.Interface.Outputs is { Count: > 0 } outputs)
        {
            AppendDeclarationSection(builder, "VAR_OUTPUT", outputs);
        }

        builder.AppendLine("BEGIN");
        foreach (var statement in operation.Statements ?? [])
        {
            builder.Append("    ").Append(statement.Target).Append(" := ").Append(statement.Source).AppendLine(";");
        }

        builder.Append(operation.BlockType == BlockType.Function ? "END_FUNCTION" : "END_FUNCTION_BLOCK");
        return builder.ToString();
    }

    private static void AppendDeclarationSection(
        StringBuilder builder,
        string sectionName,
        IReadOnlyList<BlockParameter> parameters)
    {
        builder.AppendLine(sectionName);
        foreach (var parameter in parameters)
        {
            builder.Append("    ").Append(parameter.Name).Append(" : ").Append(parameter.DataType).AppendLine(";");
        }

        builder.AppendLine("END_VAR");
    }

    private SclBlockPreviewResult Reject(
        CreateBlockOperation operation,
        AuthenticatedIdentity identity,
        EngineeringOperationPlan? plan,
        PolicyDecision? policyDecision,
        IReadOnlyList<string> errors,
        string detail)
    {
        Record(EngineeringAuditEventType.SclPreviewRejected, operation, identity, detail);
        return new SclBlockPreviewResult(plan, policyDecision, null, errors);
    }

    private void Record(
        EngineeringAuditEventType eventType,
        CreateBlockOperation operation,
        AuthenticatedIdentity identity,
        string detail) => auditSink.Record(new EngineeringAuditEvent(
            eventType,
            timeProvider.GetUtcNow(),
            operation.OperationId,
            null,
            operation.ProjectContext.ProjectId,
            operation.ProjectContext.SnapshotHash,
            identity,
            detail));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SclTypePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SclIdentifierPattern();
}