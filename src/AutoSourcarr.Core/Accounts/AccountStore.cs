using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Accounts;

public sealed record Account(string Id, string Name, Role Role, string Source, long Created, bool HasPassword, bool Disabled);

/// <param name="Kind">admin (the permanent admin key), reader (one per device), script.</param>
/// <param name="Role">The rights the key carries: admin or guest for scripts, reader for devices, admin for the admin key.</param>
public sealed record ApiKey(string Id, string Name, string Kind, Role Role, string Prefix, long Created, long? LastUsed, bool Revoked);

public sealed class AccountException(string message) : Exception(message);

/// <summary>
/// Accounts, API keys and the automatic admin token (AUTH-2, AUTH-4), kept in <c>config/accounts/accounts.json</c>
/// (owner-only file, 0600). Passwords are stored as salted hashes, keys as SHA-256 fingerprints; a key's secret is
/// shown once when it is created. The automatic admin token is generated on first use and can be regenerated.
/// One process owns the file; reads use the in-memory copy, every change is written at once.
/// </summary>
public sealed class AccountStore
{
    private const string FileName = "accounts.json";
    private readonly PrivateDocumentStore _documents;
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private JsonObject _data;
    private long _lastUseFlush;

    public AccountStore(string configurationRoot, Func<long>? clock = null)
    {
        _documents = new PrivateDocumentStore(Path.Combine(configurationRoot, "accounts"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _data = Load();
        lock (_gate)
            if (Text(_data, "admin_token").Length == 0 || Text(_data, "session_key").Length == 0)
            {
                if (Text(_data, "admin_token").Length == 0) _data["admin_token"] = PasswordHashing.NewSecret("asa_");
                if (Text(_data, "session_key").Length == 0) _data["session_key"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                Save();
            }
    }

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Sign-in session for the forms login: <c>v1.id.epoch.expires.mac</c>, HMAC-SHA256 with this instance's key, so
    /// sessions survive restarts. Changing the password, removing the account or signing out everywhere bumps the
    /// account's epoch and ends its sessions.
    /// </summary>
    public string IssueSession(string accountId)
    {
        lock (_gate)
        {
            var user = Users().FirstOrDefault(row => Text(row, "id") == accountId) ?? throw new AccountException("Unknown account.");
            var expires = _clock() + (long)SessionLifetime.TotalSeconds;
            var body = "v1." + accountId + "." + Epoch(user).ToString(CultureInfo.InvariantCulture) + "." + expires.ToString(CultureInfo.InvariantCulture);
            return body + "." + Mac(body);
        }
    }

    /// <summary>The account of a valid session, and whether the session should be renewed (past half its life).</summary>
    public (Account Account, bool Renew)? ValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1" || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)) return null;
        var body = string.Join('.', parts[..4]);
        lock (_gate)
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(body)), Encoding.ASCII.GetBytes(parts[4]))) return null;
            var now = _clock();
            if (expires <= now) return null;
            var user = Users().FirstOrDefault(row => Text(row, "id") == parts[1]);
            if (user is null || Epoch(user) != epoch || user["disabled"]?.GetValue<bool>() == true || Text(user, "password_hash").Length == 0) return null;
            return (ToAccount(user), expires - now < (long)SessionLifetime.TotalSeconds / 2);
        }
    }

    /// <summary>Ends every session of the account.</summary>
    public void EndSessions(string accountId)
    {
        lock (_gate)
        {
            var user = Users().FirstOrDefault(row => Text(row, "id") == accountId);
            if (user is null) return;
            user["session_epoch"] = Epoch(user) + 1;
            Save();
        }
    }

    private static long Epoch(JsonObject user) => user["session_epoch"] is JsonValue value && value.TryGetValue<long>(out var epoch) ? epoch : 0;

    private string Mac(string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(Text(_data, "session_key")), Encoding.ASCII.GetBytes(body)));

    public bool OwnerExists { get { lock (_gate) return Users().Any(user => Roles.Parse(Text(user, "role")) == Role.Owner); } }
    /// <summary>True until the owner has a password (forms/basic) or is bound to an external account.</summary>
    public bool SetupRequired
    {
        get
        {
            lock (_gate)
                return !Users().Any(user => Roles.Parse(Text(user, "role")) == Role.Owner
                    && (Text(user, "password_hash").Length > 0 || Text(user, "source") == "external"));
        }
    }

    public string AutomaticAdminToken { get { lock (_gate) return Text(_data, "admin_token"); } }

    /// <summary>Increases with every change (callers drop cached sign-ins when it moves).</summary>
    public long Version { get { lock (_gate) return _version; } }
    private long _version;

    /// <summary>After a server-side owner reset, setup needs this one-time code from the server log.</summary>
    public bool SetupCodeRequired { get { lock (_gate) return Text(_data, "setup_code").Length > 0; } }

    /// <summary>AUTH_RESET_OWNER was applied for the current run of the variable (applied once, not on every start).</summary>
    public bool ResetApplied { get { lock (_gate) return _data["reset_applied"]?.GetValue<bool>() == true; } }

    public void ClearResetMarker()
    {
        lock (_gate)
        {
            if (!ResetApplied) return;
            _data["reset_applied"] = false;
            Save();
        }
    }

    public string RegenerateAdminToken()
    {
        lock (_gate)
        {
            _data["admin_token"] = PasswordHashing.NewSecret("asa_");
            Save();
            return Text(_data, "admin_token");
        }
    }

    public IReadOnlyList<Account> Accounts() { lock (_gate) return Users().Select(ToAccount).ToArray(); }

    /// <summary>First boot: the owner (AUTH-2). Refused once an owner with a password or external binding exists.</summary>
    public Account CreateOwner(string name, string password, string? setupCode = null)
    {
        name = CleanName(name);
        if (PasswordHashing.Problem(password) is { } problem) throw new AccountException(problem);
        lock (_gate)
        {
            if (!SetupRequired) throw new AccountException("The owner account already exists.");
            var code = Text(_data, "setup_code");
            if (code.Length > 0 && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(setupCode?.Trim() ?? ""), Encoding.UTF8.GetBytes(code)))
                throw new AccountException("Enter the setup code from the server log (written when the owner sign-in was reset).");
            var owner = Users().FirstOrDefault(user => Roles.Parse(Text(user, "role")) == Role.Owner);
            if (Users().Any(user => !ReferenceEquals(user, owner) && string.Equals(Text(user, "name"), name, StringComparison.OrdinalIgnoreCase)))
                throw new AccountException("Another account already uses this name.");
            _data["setup_code"] = "";
            if (owner is null)
            {
                owner = NewUser(name, Role.Owner, "local");
                ((JsonArray)_data["users"]!).Add(owner);
            }
            owner["name"] = name;
            owner["source"] = "local";
            owner["password_hash"] = PasswordHashing.Hash(password);
            Save();
            return ToAccount(owner);
        }
    }

    /// <summary>First boot with external sign-in: the named provider account becomes owner (never a group mapping).</summary>
    public Account BindExternalOwner(string name)
    {
        name = CleanName(name);
        lock (_gate)
        {
            if (!SetupRequired) throw new AccountException("The owner account already exists.");
            var owner = Users().FirstOrDefault(user => Roles.Parse(Text(user, "role")) == Role.Owner);
            if (owner is null) { owner = NewUser(name, Role.Owner, "external"); ((JsonArray)_data["users"]!).Add(owner); }
            owner["name"] = name; owner["source"] = "external"; owner["password_hash"] = "";
            Save();
            return ToAccount(owner);
        }
    }

    /// <summary>AUTH-10: the owner keeps their account and all data; only the password goes, so setup asks for a new one.</summary>
    /// <returns>The one-time setup code (for the server log), or null when there is no owner.</returns>
    public string? ResetOwnerLogin()
    {
        lock (_gate)
        {
            var owner = Users().FirstOrDefault(user => Roles.Parse(Text(user, "role")) == Role.Owner);
            if (owner is null) return null;
            owner["password_hash"] = ""; owner["source"] = "local";
            owner["session_epoch"] = Epoch(owner) + 1;
            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            _data["setup_code"] = code;
            _data["reset_applied"] = true;
            Save();
            return code;
        }
    }

    public Account CreateUser(string name, string password, Role role)
    {
        name = CleanName(name);
        if (role is not (Role.Admin or Role.Guest)) throw new AccountException("New accounts are admins or guests.");
        if (PasswordHashing.Problem(password) is { } problem) throw new AccountException(problem);
        lock (_gate)
        {
            if (Users().Any(user => string.Equals(Text(user, "name"), name, StringComparison.OrdinalIgnoreCase)))
                throw new AccountException("An account with this name exists.");
            var user = NewUser(name, role, "local");
            user["password_hash"] = PasswordHashing.Hash(password);
            ((JsonArray)_data["users"]!).Add(user);
            Save();
            return ToAccount(user);
        }
    }

    public bool DeleteUser(string id)
    {
        lock (_gate)
        {
            var users = (JsonArray)_data["users"]!;
            var user = users.OfType<JsonObject>().FirstOrDefault(row => Text(row, "id") == id);
            if (user is null) return false;
            if (Roles.Parse(Text(user, "role")) == Role.Owner) throw new AccountException("The owner account cannot be deleted.");
            users.Remove(user);
            Save();
            return true;
        }
    }

    public void ChangePassword(string id, string current, string replacement)
    {
        if (PasswordHashing.Problem(replacement) is { } problem) throw new AccountException(problem);
        lock (_gate)
        {
            var user = Users().FirstOrDefault(row => Text(row, "id") == id) ?? throw new AccountException("Unknown account.");
            if (!PasswordHashing.Verify(current, Text(user, "password_hash"))) throw new AccountException("The current password is wrong.");
            user["password_hash"] = PasswordHashing.Hash(replacement);
            user["session_epoch"] = Epoch(user) + 1;
            Save();
        }
    }

    /// <summary>Forms and basic sign-in. Unknown names still spend one hash so timing does not reveal them.</summary>
    public Account? VerifyPassword(string name, string password)
    {
        JsonObject? user;
        lock (_gate) user = Users().FirstOrDefault(row => string.Equals(Text(row, "name"), name?.Trim(), StringComparison.OrdinalIgnoreCase));
        var hash = user is null ? Dummy.Value : Text(user, "password_hash");
        var ok = PasswordHashing.Verify(password ?? "", hash.Length == 0 ? Dummy.Value : hash);
        if (user is null || hash.Length == 0 || !ok || user["disabled"]?.GetValue<bool>() == true) return null;
        return ToAccount(user);
    }

    public Account? FindByName(string name)
    {
        lock (_gate)
            return Users().Where(row => string.Equals(Text(row, "name"), name?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(ToAccount).FirstOrDefault();
    }

    public IReadOnlyList<ApiKey> Keys() { lock (_gate) return KeyRows().Select(ToKey).ToArray(); }

    /// <summary>Creates a key; the secret is returned only here.</summary>
    public (ApiKey Key, string Secret) CreateKey(string name, string kind, Role role)
    {
        name = CleanName(name);
        (kind, role) = kind switch
        {
            "admin" => ("admin", Role.Admin),
            "reader" => ("reader", Role.Reader),
            "script" when role is Role.Admin or Role.Guest => ("script", role),
            _ => throw new AccountException("Key kind is admin, reader or script (script keys are admin or guest).")
        };
        var secret = PasswordHashing.NewSecret(kind switch { "reader" => "asr_", "script" => "ass_", _ => "ask_" });
        lock (_gate)
        {
            if (kind == "admin" && KeyRows().Any(row => Text(row, "kind") == "admin" && row["revoked"]?.GetValue<bool>() != true))
                throw new AccountException("There is already a permanent admin key; revoke it first.");
            var row = new JsonObject
            {
                ["id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                ["name"] = name, ["kind"] = kind, ["role"] = Roles.Name(role),
                ["fingerprint"] = PasswordHashing.Fingerprint(secret), ["prefix"] = secret[..8],
                ["created"] = _clock(), ["last_used"] = null, ["revoked"] = false
            };
            ((JsonArray)_data["keys"]!).Add(row);
            Save();
            return (ToKey(row), secret);
        }
    }

    public bool RevokeKey(string id)
    {
        lock (_gate)
        {
            var row = KeyRows().FirstOrDefault(key => Text(key, "id") == id);
            if (row is null || row["revoked"]?.GetValue<bool>() == true) return false;
            row["revoked"] = true;
            Save();
            return true;
        }
    }

    /// <summary>A live key with this secret, or null. Last use is recorded (written at most every five minutes).</summary>
    public ApiKey? FindKey(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 256) return null;
        var fingerprint = PasswordHashing.Fingerprint(secret);
        lock (_gate)
        {
            var row = KeyRows().FirstOrDefault(key => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Text(key, "fingerprint")), Encoding.ASCII.GetBytes(fingerprint)));
            if (row is null || row["revoked"]?.GetValue<bool>() == true) return null;
            var now = _clock();
            row["last_used"] = now;
            if (now - _lastUseFlush > 300) { _lastUseFlush = now; Save(); }
            return ToKey(row);
        }
    }

    private static readonly Lazy<string> Dummy = new(() => PasswordHashing.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    private JsonObject NewUser(string name, Role role, string source) => new()
    {
        ["id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
        ["name"] = name, ["role"] = Roles.Name(role), ["source"] = source, ["password_hash"] = "",
        ["created"] = _clock(), ["disabled"] = false
    };

    private static string CleanName(string? name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 64 || name.Any(character => char.IsControl(character)))
            throw new AccountException("Use a name of 1 to 64 characters without line breaks.");
        return name;
    }

    private IEnumerable<JsonObject> Users() => ((JsonArray)_data["users"]!).OfType<JsonObject>();
    private IEnumerable<JsonObject> KeyRows() => ((JsonArray)_data["keys"]!).OfType<JsonObject>();

    private static Account ToAccount(JsonObject row) => new(Text(row, "id"), Text(row, "name"), Roles.Parse(Text(row, "role")),
        Text(row, "source"), row["created"]?.GetValue<long>() ?? 0, Text(row, "password_hash").Length > 0, row["disabled"]?.GetValue<bool>() == true);

    private static ApiKey ToKey(JsonObject row) => new(Text(row, "id"), Text(row, "name"), Text(row, "kind"), Roles.Parse(Text(row, "role")),
        Text(row, "prefix"), row["created"]?.GetValue<long>() ?? 0, (row["last_used"] is JsonValue used && used.TryGetValue<long>(out var at) ? (long?)at : null), row["revoked"]?.GetValue<bool>() == true);

    private static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private JsonObject Load()
    {
        var bytes = _documents.Locked(FileName, document => document.Read(4 * 1024 * 1024));
        JsonObject data;
        if (bytes is null || bytes.Length == 0) data = new JsonObject();
        else data = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("accounts.json is not a JSON object.");
        data["version"] ??= 1;
        if (data["users"] is not JsonArray) data["users"] = new JsonArray();
        if (data["keys"] is not JsonArray) data["keys"] = new JsonArray();
        return data;
    }

    private void Save()
    {
        _version++;
        var bytes = Encoding.UTF8.GetBytes(_data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _documents.Locked(FileName, document => { document.Write(bytes); return 0; });
    }

    public override string ToString() => "Accounts (" + Accounts().Count.ToString(CultureInfo.InvariantCulture) + ")";
}
