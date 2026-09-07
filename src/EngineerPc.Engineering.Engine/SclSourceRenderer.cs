using System.Text;
using System.Text.RegularExpressions;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Engine;

public static partial class SclSourceRenderer
{
    public static IReadOnlyList<string> Validate(CreateBlockOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var errors = new List<string>();
        if (operation.Language != ProgrammingLanguage.Scl)
        {
            errors.Add("SCL source generation requires the Scl programming language.");
        }

        if (operation.BlockType is not BlockType.Function and not BlockType.FunctionBlock)
        {
            errors.Add("SCL source generation supports only Function and FunctionBlock block types.");
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

    public static string Render(CreateBlockOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SclTypePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SclIdentifierPattern();
}
