using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Policy;

public interface IEngineeringPolicy
{
    PolicyDecision Evaluate(EngineeringOperation operation);
}