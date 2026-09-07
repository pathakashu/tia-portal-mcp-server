namespace EngineerPc.Security;

public interface ISecurityEventSink
{
    void Record(SecurityEvent securityEvent);
}