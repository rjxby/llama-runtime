using System.Security.Claims;
using Microsoft.Extensions.Options;

using LlamaRuntime.Presentation.Grpc.Configuration;

namespace LlamaRuntime.Presentation.Grpc.Auth;

public class InMemoryApiKeyValidator : IApiKeyValidator
{
    private readonly HashSet<string> _keys;

    public InMemoryApiKeyValidator(IOptions<ApiKeyOptions> options)
    {
        _keys = [.. options.Value?.Keys ?? []];
    }

    public Task<ClaimsPrincipal?> ValidateAsync(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return Task.FromResult<ClaimsPrincipal?>(null);

        if (!_keys.Contains(apiKey))
            return Task.FromResult<ClaimsPrincipal?>(null);

        var claims = new[]
        {
            new Claim(AuthConstants.ClaimTypes.Subject, AuthConstants.Claims.ApiKeyUserName),
            new Claim(AuthConstants.ClaimTypes.ApiKey, apiKey)
        };

        var identity = new ClaimsIdentity(claims, AuthConstants.AuthenticationScheme);
        return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(identity));
    }
}
