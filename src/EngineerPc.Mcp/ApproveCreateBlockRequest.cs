using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Mcp;

public sealed record ApproveCreateBlockRequest(
    EngineeringTransaction Transaction,
    DateTimeOffset ExpiresAtUtc);
