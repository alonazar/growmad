using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace NomadInbox.Web.Auth;

/// <summary>Single admin account, configured with Auth__Username / Auth__Password (see .env.example).</summary>
public sealed class AdminOptions
{
    public const string Section = "Auth";

    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public sealed class AdminCredentialValidator(IOptions<AdminOptions> options)
{
    public void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(options.Value.Password))
            throw new InvalidOperationException(
                "No admin password is configured. Set the Auth__Password environment variable (NOMADINBOX_ADMIN_PASSWORD in .env).");
    }

    public bool Validate(string? username, string? password)
    {
        var o = options.Value;
        // Compare both fields in constant time so timing doesn't reveal which one was wrong.
        var userOk = FixedTimeEquals(username ?? "", o.Username);
        var passOk = FixedTimeEquals(password ?? "", o.Password);
        return userOk & passOk;
    }

    public ClaimsPrincipal CreatePrincipal()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, options.Value.Username), new Claim(ClaimTypes.Role, "Admin")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
