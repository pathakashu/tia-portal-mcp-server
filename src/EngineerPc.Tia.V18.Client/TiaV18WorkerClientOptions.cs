namespace EngineerPc.Tia.V18.Client;

public sealed record TiaV18WorkerClientOptions(
    string WorkerExecutablePath,
    string ConfigurationPath,
    TimeSpan RequestTimeout);

public sealed record TiaV18WorkerClientOptionsValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class TiaV18WorkerClientOptionsValidator
{
    public static TiaV18WorkerClientOptionsValidationResult Validate(TiaV18WorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        ValidatePath(options.WorkerExecutablePath, "TIA V18 worker executable", ".exe", errors);
        ValidatePath(options.ConfigurationPath, "TIA V18 worker configuration", null, errors);

        if (options.RequestTimeout < TimeSpan.FromSeconds(1) ||
            options.RequestTimeout > TimeSpan.FromMinutes(5))
        {
            errors.Add("TIA V18 worker request timeout must be between 1 second and 5 minutes.");
        }

        return new TiaV18WorkerClientOptionsValidationResult(errors);
    }

    private static void ValidatePath(string path, string displayName, string? requiredExtension, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            errors.Add($"{displayName} path must be absolute.");
            return;
        }

        if (!string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{displayName} path must be canonical.");
        }

        if (requiredExtension is not null && !string.Equals(Path.GetExtension(path), requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{displayName} path must use the {requiredExtension} extension.");
        }

        if (!File.Exists(path))
        {
            errors.Add($"{displayName} was not found.");
        }
    }
}