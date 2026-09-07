using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Validation;

public interface IEngineeringOperationValidator
{
    ValidationResult Validate(CreateBlockOperation operation);
}