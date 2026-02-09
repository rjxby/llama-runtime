using System.Security.Claims;

namespace LlamaRuntime.Presentation.Grpc.Auth;

public interface IApiKeyValidator
{
    Task<ClaimsPrincipal?> ValidateAsync(string apiKey);
}
