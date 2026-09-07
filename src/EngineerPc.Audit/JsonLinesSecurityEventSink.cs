using System.Text;
using System.Text.Json;
using EngineerPc.Security;

namespace EngineerPc.Audit;

public sealed class JsonLinesSecurityEventSink : ISecurityEventSink
{
    private static readonly UTF8Encoding Utf8WithoutByteOrderMark = new(false);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object writeLock = new();
    private readonly string filePath;

    public JsonLinesSecurityEventSink(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("An audit file path is required.", nameof(filePath));
        }

        this.filePath = Path.GetFullPath(filePath);
    }

    public void Record(SecurityEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The audit file path must include a directory.");
        }

        var entry = JsonSerializer.Serialize(securityEvent, SerializerOptions) + Environment.NewLine;
        lock (writeLock)
        {
            Directory.CreateDirectory(directory);
            using var stream = new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.WriteThrough);
            var entryBytes = Utf8WithoutByteOrderMark.GetBytes(entry);
            stream.Write(entryBytes);
            stream.Flush(flushToDisk: true);
        }
    }
}