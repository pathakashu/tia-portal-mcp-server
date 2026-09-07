namespace EngineerPc.Tia.V19.Client;

public sealed record TiaV19WorkerClientOptions(
    string WorkerExecutablePath,
    string ConfigurationPath,
    TimeSpan RequestTimeout);

public sealed record TiaV19WorkerClientOptionsValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class TiaV19WorkerClientOptionsValidator
{
    public static TiaV19WorkerClientOptionsValidationResult Validate(TiaV19WorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        ValidatePath(options.WorkerExecutablePath, "TIA V19 worker executable", ".exe", errors);
        ValidatePath(options.ConfigurationPath, "TIA V19 worker configuration", null, errors);

        if (options.RequestTimeout < TimeSpan.FromSeconds(1) ||
            options.RequestTimeout > TimeSpan.FromMinutes(5))
        {
            errors.Add("TIA V19 worker request timeout must be between 1 second and 5 minutes.");
        }

        return new TiaV19WorkerClientOptionsValidationResult(errors);
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