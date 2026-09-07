namespace EngineerPc.Audit;

public interface IEngineeringAuditSink
{
    void Record(EngineeringAuditEvent auditEvent);
}