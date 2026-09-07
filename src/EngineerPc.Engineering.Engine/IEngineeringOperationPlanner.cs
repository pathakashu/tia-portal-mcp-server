using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Engine;

public interface IEngineeringOperationPlanner
{
    PlanningResult Plan(CreateBlockOperation operation);
}