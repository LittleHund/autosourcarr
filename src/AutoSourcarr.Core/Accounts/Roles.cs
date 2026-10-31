namespace AutoSourcarr.Core.Accounts;

/// <summary>
/// AUTH-6: the one role system. Higher values include the rights of lower ones for the web app (owner ⊃ admin ⊃
/// guest); reader devices and scripts are separate kinds of callers with their own reach (see AccessPolicy).
/// </summary>
public enum Role
{
    None = 0,
    Reader = 1,
    Guest = 2,
    Script = 3,
    Admin = 4,
    Owner = 5,
}

public static class Roles
{
    public static string Name(Role role) => role switch
    {
        Role.Owner => "owner",
        Role.Admin => "admin",
        Role.Guest => "guest",
        Role.Reader => "reader",
        Role.Script => "script",
        _ => "none",
    };

    public static Role Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "owner" => Role.Owner,
        "admin" => Role.Admin,
        "guest" => Role.Guest,
        "reader" => Role.Reader,
        "script" => Role.Script,
        _ => Role.None,
    };

    /// <summary>Admin-level reach: owner, admin, and script keys created with admin rights.</summary>
    public static bool IsAdmin(Role role) => role is Role.Owner or Role.Admin;
}

/// <summary>Who is calling, worked out once per request (Api/Security/AccessControl).</summary>
/// <param name="Via">How the caller proved who it is: session, basic, external, admin-token, key.</param>
/// <param name="ScriptAdmin">A script key created with admin rights.</param>
public sealed record Caller(Role Role, string Name, string Via, string? KeyId = null, bool ScriptAdmin = false)
{
    public static readonly Caller Anonymous = new(Role.None, "", "none");
    public bool AdminReach => Roles.IsAdmin(Role) || Role == Role.Script && ScriptAdmin;
}
