using ConnectedOps.Application;

namespace ConnectedOps.Infrastructure;

public sealed class DemoUserDirectory : IDemoUserDirectory
{
    private static readonly IReadOnlyList<DemoAccount> Accounts =
    [
        new(
            "operator",
            "operate-demo",
            "Alex Operator",
            [AuthRoles.Operator]),
        new(
            "approver",
            "approve-demo",
            "Jordan Approver",
            [AuthRoles.Approver]),
        new(
            "admin",
            "admin-demo",
            "Sam Admin",
            [AuthRoles.Operator, AuthRoles.Approver])
    ];

    public AuthUser? Authenticate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var account = Accounts.FirstOrDefault(item =>
            string.Equals(item.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Password, password, StringComparison.Ordinal));
        return account?.ToUser();
    }

    public AuthUser? FindByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return Accounts
            .FirstOrDefault(item =>
                string.Equals(item.Username, username.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.ToUser();
    }

    public static IReadOnlyList<AuthUser> ListUsers()
    {
        return Accounts.Select(account => account.ToUser()).ToArray();
    }

    private sealed record DemoAccount(
        string Username,
        string Password,
        string DisplayName,
        IReadOnlyList<string> Roles)
    {
        public AuthUser ToUser() => new(Username, DisplayName, Roles);
    }
}
