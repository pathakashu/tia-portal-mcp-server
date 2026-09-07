using EngineerPc.Contracts;

namespace EngineerPc.Security;

public interface IAuthorizationService
{
    AuthorizationDecision Authorize(
        AuthenticatedPrincipal principal,
        string operation,
        Guid requestId,
        Guid correlationId);
}