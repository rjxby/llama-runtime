namespace LlamaRuntime.Presentation.Grpc.Auth;

public static class AuthConstants
{
    public const string AuthenticationScheme = "x-api-key";

    public static class ClaimTypes
    {
        public const string ApiKey = "api_key";
        public const string Subject = System.Security.Claims.ClaimTypes.Name;
    }

    public static class Claims
    {
        public const string ApiKeyUserName = "apikey-user";
    }
}
