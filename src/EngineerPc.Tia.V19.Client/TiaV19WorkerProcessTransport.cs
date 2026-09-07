using System.Diagnostics;
using System.Text.Json;
using EngineerPc.Tia.V19.Protocol;

namespace EngineerPc.Tia.V19.Client;

public sealed class TiaV19WorkerProcessTransport : ITiaV19WorkerTransport
{
    public async Task<string> SendAsync(
        TiaV19WorkerClientOptions options,
        TiaV19WorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        var validation = TiaV19WorkerClientOptionsValidator.Validate(options);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validation.Errors), nameof(options));
        }

        var startInfo = new ProcessStartInfo(options.WorkerExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(options.ConfigurationPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("TIA V19 worker process could not be started.");
        }

        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(options.RequestTimeout);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)).WaitAsync(requestCancellation.Token);
            await process.StandardInput.FlushAsync().WaitAsync(requestCancellation.Token);
            process.StandardInput.Close();

            var response = await process.StandardOutput.ReadLineAsync().WaitAsync(requestCancellation.Token);
            await process.WaitForExitAsync(requestCancellation.Token);

            var trailingOutput = await process.StandardOutput.ReadToEndAsync().WaitAsync(requestCancellation.Token);
            var standardError = await standardErrorTask.WaitAsync(requestCancellation.Token);

            if (response is null)
            {
                throw new InvalidOperationException(CreateWorkerFailureMessage("did not return a response", standardError));
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(CreateWorkerFailureMessage($"exited with code {process.ExitCode}", standardError));
            }

            if (!string.IsNullOrWhiteSpace(trailingOutput))
            {
                throw new InvalidOperationException("TIA V19 worker process returned more than one response.");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            throw new TimeoutException("TIA V19 worker request timed out.");
        }
        catch
        {
            Terminate(process);
            throw;
        }
    }

    private static string CreateWorkerFailureMessage(string failure, string standardError)
    {
        var sanitizedError = standardError.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return string.IsNullOrEmpty(sanitizedError)
            ? $"TIA V19 worker process {failure}."
            : $"TIA V19 worker process {failure}: {sanitizedError[..Math.Min(sanitizedError.Length, 512)]}";
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}