namespace ConnectedOps.Application;

public static class AuthRoles
{
    public const string Operator = "Operator";
    public const string Approver = "Approver";
}

public static class AuthPolicies
{
    public const string CanOperate = "CanOperate";
    public const string CanApprove = "CanApprove";
}

public sealed record AuthUser(
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles);

public sealed record AuthTokenResult(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    AuthUser User);

public sealed record LoginRequest(string Username, string Password);

public interface IDemoUserDirectory
{
    AuthUser? Authenticate(string username, string password);
    AuthUser? FindByUsername(string username);
}

public interface IAuthTokenService
{
    AuthTokenResult IssueToken(AuthUser user);
}
