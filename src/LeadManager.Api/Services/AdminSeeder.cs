using LeadManager.Api.Models;
using Microsoft.AspNetCore.Identity;

namespace LeadManager.Api.Services;

/// <summary>
/// Creates admin accounts from configuration (task 8028). Replaces the hardcoded accounts that used to
/// live in Program.cs, which also reset the password of an existing admin to the known value on every
/// restart.
///
/// Configuration (environment form uses double underscores):
///   Seed:Admins:0:Email, Seed:Admins:0:Password, Seed:Admins:0:FirstName, Seed:Admins:0:LastName
///   Seed:ResetExistingPasswords=true   emergency recovery only: reset the configured password of an
///                                      account that already exists. Default false.
///
/// Without configuration nothing is created and no account gets a default password. Existing accounts
/// keep their password; they only gain the Admin role if they were configured as admin.
/// </summary>
public static class AdminSeeder
{
    public static async Task SeedAsync(UserManager<ApplicationUser> users, IConfiguration config, ILogger logger)
    {
        var admins = config.GetSection("Seed:Admins").GetChildren().ToList();
        var reset = config.GetValue<bool>("Seed:ResetExistingPasswords");

        if (admins.Count == 0)
        {
            logger.LogInformation("No Seed:Admins configured: no admin accounts are created or changed.");
            return;
        }

        foreach (var section in admins)
        {
            var email = section["Email"]?.Trim();
            var password = section["Password"];
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            {
                logger.LogWarning("Seed:Admins entry {Index} skipped: Email and Password are both required.", section.Key);
                continue;
            }

            var user = await users.FindByEmailAsync(email);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    FirstName = section["FirstName"] ?? "Admin",
                    LastName = section["LastName"] ?? "",
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true,
                    EmailConfirmed = true,
                };
                var created = await users.CreateAsync(user, password);
                if (!created.Succeeded)
                {
                    // Never log the password itself, only why it was refused.
                    logger.LogError("Admin {Email} not created: {Errors}", email,
                        string.Join("; ", created.Errors.Select(e => e.Description)));
                    continue;
                }
                logger.LogInformation("Admin {Email} created.", email);
            }
            else if (reset)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var result = await users.ResetPasswordAsync(user, token, password);
                if (result.Succeeded)
                    logger.LogWarning("Admin {Email}: password reset because Seed:ResetExistingPasswords is true.", email);
                else
                    logger.LogError("Admin {Email}: password reset failed: {Errors}", email,
                        string.Join("; ", result.Errors.Select(e => e.Description)));
            }

            if (!await users.IsInRoleAsync(user, "Admin"))
                await users.AddToRoleAsync(user, "Admin");
        }
    }
}
