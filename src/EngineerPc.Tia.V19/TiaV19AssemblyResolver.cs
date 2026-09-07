using System.Reflection;

namespace EngineerPc.Tia.V19;

public static class TiaV19AssemblyResolver
{
    private const string DefaultPublicApiDirectory = @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19";
    private const string PublicApiDirectoryEnvironmentVariable = "TIA_V19_PUBLIC_API_DIRECTORY";
    private static readonly object SyncRoot = new();
    private static string? publicApiDirectory;

    public static void Configure()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable(PublicApiDirectoryEnvironmentVariable);
        var resolvedDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? DefaultPublicApiDirectory
            : configuredDirectory;
        var fullDirectory = Path.GetFullPath(resolvedDirectory);
        var engineeringAssemblyPath = Path.Combine(fullDirectory, "Siemens.Engineering.dll");

        if (!File.Exists(engineeringAssemblyPath))
        {
            throw new FileNotFoundException("The Siemens TIA Portal V19 PublicAPI assembly was not found.", engineeringAssemblyPath);
        }

        lock (SyncRoot)
        {
            if (publicApiDirectory is not null)
            {
                if (!string.Equals(publicApiDirectory, fullDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("TIA Portal V19 PublicAPI directory is already configured.");
                }

                return;
            }

            publicApiDirectory = fullDirectory;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }
    }

    private static Assembly? ResolveAssembly(object? sender, ResolveEventArgs arguments)
    {
        var assemblyName = new AssemblyName(arguments.Name).Name;
        if (string.IsNullOrWhiteSpace(assemblyName) ||
            !assemblyName.StartsWith("Siemens.Engineering", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = publicApiDirectory;
        if (directory is null)
        {
            return null;
        }

        var assemblyPath = Path.Combine(directory, $"{assemblyName}.dll");
        return File.Exists(assemblyPath) ? Assembly.LoadFrom(assemblyPath) : null;
    }
}