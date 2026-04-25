namespace LlamaRuntime.Presentation.Grpc.Auth;

public static partial class AuthConstants
{
    public static class ClaimTypes
    {
        public const string ApiKey = "api_key";
        public const string Subject = System.Security.Claims.ClaimTypes.Name;
    }
}
