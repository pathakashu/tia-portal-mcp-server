using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Mcp;

public sealed record ExecuteCreateBlockRequest(
    EngineeringTransaction Transaction,
    CreateBlockOperation Operation);
