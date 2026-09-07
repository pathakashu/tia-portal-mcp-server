using EngineerPc.Tia.V19.Protocol;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace EngineerPc.Tia.V19;

public sealed class TiaV19Adapter : IDisposable
{
    private readonly TiaV19ProjectCatalog projectCatalog;
    private TiaPortal? tiaPortal;
    private bool disposed;

    public TiaV19Adapter(TiaV19ProjectCatalog projectCatalog)
    {
        this.projectCatalog = projectCatalog ?? throw new ArgumentNullException(nameof(projectCatalog));
    }

    public TiaV19ProjectContextResponse ReadProjectContext(string requestId, string projectId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("Request ID is required.", nameof(requestId));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (!projectCatalog.TryGetProject(projectId, out var projectDefinition) || projectDefinition is null)
        {
            return new TiaV19ProjectContextResponse(requestId, null, null, "The configured TIA V19 project was not found.");
        }

        if (!File.Exists(projectDefinition.ProjectFilePath))
        {
            return new TiaV19ProjectContextResponse(requestId, null, null, "The configured TIA V19 project file was not found.");
        }

        Project? project = null;
        try
        {
            project = GetTiaPortal().Projects.Open(new FileInfo(projectDefinition.ProjectFilePath));
            var snapshotHash = TiaV19ProjectSnapshot.Calculate(
                projectDefinition.ProjectId,
                project.Name,
                project.Path.FullName,
                project.LastModified.ToUniversalTime(),
                project.Size,
                project.Version);
            return new TiaV19ProjectContextResponse(requestId, projectDefinition.ProjectId, snapshotHash, null);
        }
        catch (Exception exception)
        {
            return new TiaV19ProjectContextResponse(
                requestId,
                null,
                null,
                $"TIA Portal V19 project context read failed: {exception.GetType().Name}.");
        }
        finally
        {
            project?.Close();
        }
    }

    public TiaV19BlockCatalogResponse ReadBlockCatalog(
        string requestId,
        string projectId,
        int startIndex,
        int maximumBlockCount,
        string? expectedSnapshotHash)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("Request ID is required.", nameof(requestId));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (maximumBlockCount <= 0 || maximumBlockCount > TiaV19WorkerProtocol.MaximumBlockCatalogBlockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBlockCount));
        }

        if (!projectCatalog.TryGetProject(projectId, out var projectDefinition) || projectDefinition is null)
        {
            return FailedBlockCatalog(requestId, "The configured TIA V19 project was not found.");
        }

        if (!File.Exists(projectDefinition.ProjectFilePath))
        {
            return FailedBlockCatalog(requestId, "The configured TIA V19 project file was not found.");
        }

        Project? project = null;
        try
        {
            project = GetTiaPortal().Projects.Open(new FileInfo(projectDefinition.ProjectFilePath));
            var snapshotHash = TiaV19ProjectSnapshot.Calculate(
                projectDefinition.ProjectId,
                project.Name,
                project.Path.FullName,
                project.LastModified.ToUniversalTime(),
                project.Size,
                project.Version);
            if (startIndex > 0 && !string.Equals(expectedSnapshotHash, snapshotHash, StringComparison.Ordinal))
            {
                return FailedBlockCatalog(
                    requestId,
                    TiaV19BlockCatalogErrorCode.SnapshotChanged,
                    "The configured TIA V19 project snapshot has changed.");
            }
            var blocks = EnumerateBlocks(project)
                .OrderBy(block => block.ControllerName, StringComparer.Ordinal)
                .ThenBy(block => block.Namespace, StringComparer.Ordinal)
                .ThenBy(block => block.Name, StringComparer.Ordinal)
                .ThenBy(block => block.Number)
                .ToArray();
            var page = blocks.Skip(startIndex).Take(maximumBlockCount).ToArray();
            var nextStartIndex = (long)startIndex + page.Length < blocks.Length
                ? startIndex + page.Length
                : (int?)null;
            return new TiaV19BlockCatalogResponse(
                requestId,
                projectDefinition.ProjectId,
                snapshotHash,
                page,
                blocks.Length,
                nextStartIndex,
                TiaV19BlockCatalogErrorCode.None,
                null);
        }
        catch (Exception exception)
        {
            return FailedBlockCatalog(requestId, $"TIA Portal V19 block catalog read failed: {exception.GetType().Name}.");
        }
        finally
        {
            project?.Close();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        tiaPortal?.Dispose();
        disposed = true;
    }

    private TiaPortal GetTiaPortal() => tiaPortal ?? (tiaPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface));

    private static TiaV19BlockCatalogResponse FailedBlockCatalog(
        string requestId,
        string error) => FailedBlockCatalog(requestId, TiaV19BlockCatalogErrorCode.None, error);

    private static TiaV19BlockCatalogResponse FailedBlockCatalog(
        string requestId,
        TiaV19BlockCatalogErrorCode errorCode,
        string error) => new(requestId, null, null, [], null, null, errorCode, error);

    private static IEnumerable<TiaV19BlockDefinition> EnumerateBlocks(Project project)
    {
        foreach (var device in project.Devices)
        {
            foreach (var block in EnumerateBlocks(device))
            {
                yield return block;
            }
        }

        foreach (var deviceGroup in project.DeviceGroups)
        {
            foreach (var device in deviceGroup.Devices)
            {
                foreach (var block in EnumerateBlocks(device))
                {
                    yield return block;
                }
            }
        }
    }

    private static IEnumerable<TiaV19BlockDefinition> EnumerateBlocks(Device device)
    {
        foreach (var block in EnumerateBlocks(device.GetService<SoftwareContainer>()))
        {
            yield return block;
        }

        foreach (var deviceItem in device.DeviceItems)
        {
            foreach (var block in EnumerateBlocks(deviceItem))
            {
                yield return block;
            }
        }
    }

    private static IEnumerable<TiaV19BlockDefinition> EnumerateBlocks(DeviceItem deviceItem)
    {
        foreach (var block in EnumerateBlocks(deviceItem.GetService<SoftwareContainer>()))
        {
            yield return block;
        }

        foreach (var childDeviceItem in deviceItem.DeviceItems)
        {
            foreach (var block in EnumerateBlocks(childDeviceItem))
            {
                yield return block;
            }
        }
    }

    private static IEnumerable<TiaV19BlockDefinition> EnumerateBlocks(SoftwareContainer? softwareContainer)
    {
        if (softwareContainer?.Software is not PlcSoftware plcSoftware)
        {
            yield break;
        }

        foreach (var block in EnumerateBlocks(plcSoftware.Name, plcSoftware.BlockGroup))
        {
            yield return block;
        }
    }

    private static IEnumerable<TiaV19BlockDefinition> EnumerateBlocks(string controllerName, PlcBlockGroup blockGroup)
    {
        foreach (var block in blockGroup.Blocks)
        {
            yield return new TiaV19BlockDefinition(
                controllerName,
                block.Name,
                block.Namespace,
                block.Number,
                block.ProgrammingLanguage.ToString());
        }

        foreach (var childGroup in blockGroup.Groups)
        {
            foreach (var block in EnumerateBlocks(controllerName, childGroup))
            {
                yield return block;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(TiaV19Adapter));
        }
    }
}