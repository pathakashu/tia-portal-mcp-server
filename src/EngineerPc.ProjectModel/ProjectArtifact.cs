namespace EngineerPc.ProjectModel;

public sealed record ProjectArtifact(
    string ArtifactId,
    string Name,
    string Path,
    ProjectArtifactType Type,
    string SourceHash,
    IReadOnlyDictionary<string, string> Metadata);

public enum ProjectArtifactType
{
    Project,
    Device,
    Plc,
    Block,
    DataBlock,
    UserDataType,
    Tag,
    HardwareModule,
    HmiDevice,
    HmiScreen,
    Connection,
    CrossReference,
    Library
}