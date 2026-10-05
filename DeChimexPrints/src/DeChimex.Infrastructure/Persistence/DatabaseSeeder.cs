using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Domain.ValueObjects;
using DeChimex.Infrastructure.Options;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeChimex.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds essential system reference data: permissions, roles (with permission maps),
/// the Owner account, and application settings on an empty database.
/// Categories, products, and services are not seeded and start empty until created by an administrator.
/// Automatically cleans up legacy starter products, categories, and services if previously seeded in existing databases.
/// A role's canonical permission map is applied only when the role is first created; afterwards the
/// admin's checklist edits at /admin/users/roles are the source of truth and survive restarts.
/// Idempotent and safe to run on every startup.
/// </summary>
public sealed class DatabaseSeeder(
    DeChimexDbContext db,
    IPasswordHasher hasher,
    IOptions<SeedOptions> seedOptions,
    ILogger<DatabaseSeeder> logger,
    Microsoft.Extensions.Hosting.IHostEnvironment environment,
    ITechnicalEventRecorder technicalEvents)
{
    private readonly SeedOptions _seed = seedOptions.Value;

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (db.Database.IsRelational())
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);
            if (pending.Count > 0)
                technicalEvents.Record(new TechnicalEventEntry(TechnicalSeverity.Information, TechnicalCategory.Database,
                    TechnicalEventType.DatabaseMigration, $"Applied {pending.Count} migration(s): {string.Join(", ", pending)}.",
                    Source: nameof(DatabaseSeeder)));
        }
        else
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        await SeedPermissionsAsync(ct);
        await SeedRolesAsync(ct);
        await SeedOwnerAsync(ct);
        await SeedDeveloperAsync(ct);
        await SeedSettingsAsync(ct);
        await SeedCatalogAsync(ct);

        logger.LogInformation("Database seeding completed.");
    }

    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await db.Permissions.Select(p => p.Name).ToListAsync(ct);
        // Business permissions + the separate Developer Portal permission set.
        var missing = Permissions.All().Concat(Permissions.Developer.All()).Except(existing).ToList();
        if (missing.Count == 0) return;
        foreach (var name in missing)
            await db.Permissions.AddAsync(Permission.Create(name), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        var allPermissions = await db.Permissions.ToDictionaryAsync(p => p.Name, p => p.Id, ct);

        var roleDefinitions = new Dictionary<string, IReadOnlyList<string>>
        {
            [RoleNames.Owner] = Permissions.All(),
            [RoleNames.Manager] = Permissions.All().Where(p => !p.StartsWith("users.") && p != Permissions.Activity.View).ToList(),
            [RoleNames.SalesStaff] = new[]
            {
                Permissions.Orders.View, Permissions.Orders.ViewAll, Permissions.Orders.Create, Permissions.Orders.UpdateStatus,
                Permissions.Customers.View, Permissions.Customers.Manage, Permissions.Products.View, Permissions.Services.View,
                Permissions.Messages.View
            },
            [RoleNames.InventoryStaff] = new[]
            {
                Permissions.Inventory.View, Permissions.Inventory.Adjust, Permissions.Inventory.StockIn, Permissions.Inventory.StockOut,
                Permissions.Products.View, Permissions.Procurement.View, Permissions.Procurement.Receive,
                Permissions.Messages.View
            },
            [RoleNames.Designer] = new[]
            {
                Permissions.Gallery.View, Permissions.Gallery.Manage, Permissions.Products.View, Permissions.Orders.View,
                Permissions.Messages.View
            },
            [RoleNames.Customer] = new[]
            {
                Permissions.Orders.Create
            },
            // Technical role: developer permissions ONLY (no business permissions, no PII
            // screens, no pricing/refund/settings/user-management). Owner/Manager do NOT get
            // these: Permissions.All() excludes Permissions.Developer.
            [RoleNames.Developer] = Permissions.Developer.All()
        };

        foreach (var (roleName, permissionNames) in roleDefinitions)
        {
            var role = await db.Roles.Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.NormalizedName == roleName.ToUpperInvariant(), ct);

            if (role is null)
            {
                // The canonical permission map is applied only when the role is
                // first created. After that the admin's checklist edits at
                // /admin/users/roles are the source of truth and survive restarts;
                // re-syncing here would silently wipe every admin change.
                role = Role.Create(roleName, $"{roleName} role", isSystemRole: true);
                var desired = permissionNames.Where(allPermissions.ContainsKey).Select(n => allPermissions[n]).ToList();
                role.SetPermissions(desired);
                await db.Roles.AddAsync(role, ct);
            }
        }

        // OWNER DIRECTIVE 2026-09-08: marketing broadcasts are allowed for Owner and
        // Manager BY DEFAULT. Roles created before the marketing.* permissions existed
        // (or before they were granted through the checklist) never receive them,
        // because the canonical map above only applies at role creation. This targeted
        // backfill ADDS ONLY the missing marketing permissions on these two roles —
        // nothing is ever removed, so admin checklist edits to every other permission
        // still survive restarts.
        var marketingDefaults = new[] { Permissions.Marketing.View, Permissions.Marketing.Manage };
        foreach (var roleName in new[] { RoleNames.Owner, RoleNames.Manager })
        {
            var role = await db.Roles.Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.NormalizedName == roleName.ToUpperInvariant(), ct);
            if (role is null) continue;

            var granted = role.Permissions.Select(rp => rp.PermissionId).ToHashSet();
            var missingMarketing = marketingDefaults
                .Where(allPermissions.ContainsKey)
                .Select(name => allPermissions[name])
                .Where(id => !granted.Contains(id))
                .ToList();
            if (missingMarketing.Count > 0)
                role.SetPermissions(granted.Concat(missingMarketing));
        }

        // Developer role: additively ensure every dev.* permission (new dev permissions added in
        // later releases reach existing databases). Business permissions an Owner may have
        // granted to this role through the checklist are never removed.
        var developerRole = await db.Roles.Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.NormalizedName == RoleNames.Developer.ToUpperInvariant(), ct);
        if (developerRole is not null)
        {
            var granted = developerRole.Permissions.Select(rp => rp.PermissionId).ToHashSet();
            var missingDev = Permissions.Developer.All()
                .Where(allPermissions.ContainsKey)
                .Select(name => allPermissions[name])
                .Where(id => !granted.Contains(id))
                .ToList();
            if (missingDev.Count > 0)
                developerRole.SetPermissions(granted.Concat(missingDev));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seeds the Developer Portal account when Seed:DeveloperEmail and Seed:DeveloperPassword are
    /// configured (idempotent by email; password stored hashed). Outside Development the account
    /// must change its password on first login. An existing account with that email that does
    /// NOT already hold the Developer role is left untouched (never silently escalated).
    /// </summary>
    private async Task SeedDeveloperAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_seed.DeveloperEmail) || string.IsNullOrWhiteSpace(_seed.DeveloperPassword))
            return;

        var email = _seed.DeveloperEmail.Trim().ToLowerInvariant();
        var developerRole = await db.Roles.FirstAsync(r => r.NormalizedName == RoleNames.Developer.ToUpperInvariant(), ct);
        var existing = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Email.Value == email, ct);
        if (existing is not null)
        {
            if (!existing.Roles.Any(r => r.RoleId == developerRole.Id))
                logger.LogWarning("Seed:DeveloperEmail {Email} belongs to an existing non-developer account; it was not modified.", email);
            return;
        }

        var developer = User.CreateStaff(email, hasher.Hash(_seed.DeveloperPassword), _seed.DeveloperFirstName,
            _seed.DeveloperLastName, Username.SuggestFromEmail(email));
        if (!string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
            developer.SetMustChangePassword();
        developer.AssignRole(developerRole.Id);
        await db.Users.AddAsync(developer, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Seeded Developer Portal account {Email} ({Environment}).", email, environment.EnvironmentName);
    }

    private async Task SeedOwnerAsync(CancellationToken ct)
    {
        var email = _seed.OwnerEmail.Trim().ToLowerInvariant();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email.Value == email, ct);
        if (existing is not null)
        {
            // Defensive: if an owner account from an older seed still uses the known
            // bootstrap password, force a change on the next login.
            if (!existing.MustChangePassword && hasher.Verify(_seed.OwnerPassword, existing.PasswordHash))
            {
                existing.SetMustChangePassword();
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Existing Owner account {Email} still uses the bootstrap password; password change forced on next login.", email);
            }
            return;
        }

        var ownerRole = await db.Roles.FirstAsync(r => r.NormalizedName == RoleNames.Owner.ToUpperInvariant(), ct);
        var owner = User.CreateStaff(email, hasher.Hash(_seed.OwnerPassword), _seed.OwnerFirstName, _seed.OwnerLastName,
            Username.SuggestFromEmail(email));
        // The bootstrap owner must choose a new password on first login before using
        // the rest of the admin area. Cleared by the first successful password change.
        owner.SetMustChangePassword();
        owner.AssignRole(ownerRole.Id);
        await db.Users.AddAsync(owner, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Seeded Owner account {Email}. A password change is required on first login.", email);
    }

    /// <summary>One-time, idempotent username adoption for staff that predate
    /// usernames: each staff account without one receives a deterministic name
    /// derived from its email local-part (de-duplicated with a numeric suffix,
    /// never silently confusing), and its historical staff-authored chat messages
    /// — previously labelled with the staff email — are relabelled to the
    /// username so old conversations render the same identity as new ones.
    /// Customer accounts are untouched (usernames are staff-only).</summary>
    private async Task SeedStaffUsernamesAsync(CancellationToken ct)
    {
        var staffRoleNames = RoleNames.StaffRoles().Select(r => r.ToUpperInvariant()).ToHashSet();
        var staffRoleIds = await db.Roles
            .Where(r => staffRoleNames.Contains(r.NormalizedName))
            .Select(r => r.Id)
            .ToListAsync(ct);
        var staffUserIds = await db.UserRoles
            .Where(ur => staffRoleIds.Contains(ur.RoleId))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);
        if (staffUserIds.Count == 0) return;

        var taken = (await db.Users
            .Where(u => u.Username != null)
            .Select(u => u.Username!.Value)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var nameless = await db.Users
            .Where(u => staffUserIds.Contains(u.Id) && u.Username == null)
            .ToListAsync(ct);
        foreach (var user in nameless)
        {
            var candidate = Username.SuggestFromEmail(user.Email.Value);
            for (var n = 2; taken.Contains(candidate); n++)
                candidate = $"{Username.SuggestFromEmail(user.Email.Value)}{n}";
            user.SetUsername(candidate);
            taken.Add(candidate);
        }
        if (nameless.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Assigned usernames to {Count} existing staff account(s).", nameless.Count);
        }

        // Historical staff messages carry the author's email as AuthorName; point
        // them at the author's current username (unknown authors get the generic
        // label, never their stale email).
        var staffNames = await db.Users
            .Where(u => staffUserIds.Contains(u.Id) && u.Username != null)
            .Select(u => new { u.Id, Name = u.Username!.Value })
            .ToListAsync(ct);
        var byUserId = staffNames.ToDictionary(x => x.Id, x => x.Name);
        var stale = await db.OrderMessages
            .Where(m => m.AuthorRole == "staff")
            .ToListAsync(ct);
        var relabelled = 0;
        foreach (var message in stale)
        {
            var want = message.AuthorUserId is Guid authorId && byUserId.TryGetValue(authorId, out var name)
                ? name
                : "Staff";
            if (message.AuthorName != want)
            {
                message.RenameAuthor(want);
                relabelled++;
            }
        }
        if (relabelled > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Relabelled {Count} historical staff message(s) to usernames.", relabelled);
        }
    }

    private async Task SeedSettingsAsync(CancellationToken ct)
    {
        var defaults = new Dictionary<string, (string value, string desc)>
        {
            ["business.name"] = ("DE-CHIMEX PRINT HUB", "Business display name"),
            ["business.motto"] = ("Creativity with Ease.", "Brand motto"),
            ["business.currency"] = ("NGN", "Default currency"),
            ["delivery.default_fee"] = ("1500", "Default delivery fee (NGN)"),
            ["service_fee.percent"] = ("100", "Service fee (percentage of the Paystack transaction fee charged to the customer)"),
            ["tax.vat_rate"] = ("0", "VAT rate percentage (future)"),
            ["business.email"] = ("chimexgraphics@gmail.com", "Public support email"),
            ["business.phone"] = ("+234 906 205 1934, +234 903 424 5882", "Public phone numbers"),
            ["business.address"] = ("Nepa Road, Sapon, Abeokuta, Ogun State.", "Studio location address"),
            ["business.pickup_address"] = ("Awe Crescent, Elediye, Somolu, Lagos State.", "Pickup office address"),
            ["business.instagram"] = ("https://www.instagram.com/chimex_graphics?igsh=MW16OHkxOWxlajJ4YQ==", "Instagram profile link"),
            ["business.tiktok"] = ("https://www.tiktok.com/@bosewa?_r=1&_t=ZS-986YrmcyL00", "TikTok profile link"),
            // WhatsApp stores the number only (digits, country code). The wa.me link
            // is generated on the consumer side, never hand-built in the UI.
            ["business.whatsapp"] = ("2349062051934", "WhatsApp number (country code, digits only)"),
            // Marketing stats shown on the public site. Editable in /admin/settings.
            ["business.stat_projects"] = ("2,400+", "Marketing stat: projects delivered"),
            ["business.stat_brands"] = ("180+", "Marketing stat: happy brands"),
            ["business.stat_services"] = ("12+", "Marketing stat: services offered"),
            ["business.stat_turnaround"] = ("48h", "Marketing stat: average turnaround")
        };

        var existing = await db.ApplicationSettings.ToListAsync(ct);
        var existingMap = existing.ToDictionary(s => s.Key, s => s);

        foreach (var (key, (value, desc)) in defaults)
        {
            if (!existingMap.TryGetValue(key, out var setting))
            {
                await db.ApplicationSettings.AddAsync(ApplicationSetting.Create(key, value, desc), ct);
            }
            else
            {
                // Update legacy placeholder values if present
                if (setting.Value == "+234 800 000 0000" ||
                    setting.Value == "hello@dechimexprinthub.com" ||
                    setting.Value == "Abeokuta, Ogun State, Nigeria")
                {
                    setting.SetValue(value);
                }
            }
        }

        // PWA Web Push (2026-10-04): VAPID keys generated once and stored as settings.
        // The public key is served to browsers for pushManager.subscribe; the private key
        // never leaves the server. Existing installations keep their keys (only added
        // when absent), so subscriptions survive redeploys.
        if (!existingMap.ContainsKey("push.vapid.public_key"))
        {
            var vapid = WebPush.VapidHelper.GenerateVapidKeys();
            await db.ApplicationSettings.AddAsync(ApplicationSetting.Create(
                "push.vapid.public_key", vapid.PublicKey, "Web Push VAPID public key (served to browsers)"), ct);
            await db.ApplicationSettings.AddAsync(ApplicationSetting.Create(
                "push.vapid.private_key", vapid.PrivateKey, "Web Push VAPID private key (server secret)"), ct);
            await db.ApplicationSettings.AddAsync(ApplicationSetting.Create(
                "push.vapid.subject", "mailto:hello@dechimexprinthub.com", "Web Push VAPID subject"), ct);
        }

        // One-off migration: older seeds stored business.whatsapp as a wa.me URL.
        // The setting now holds the number only; the link is generated on the fly.
        var wa = existingMap.TryGetValue("business.whatsapp", out var waSetting) ? waSetting : null;
        if (wa is not null &&
            (wa.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
             wa.Value.Contains("wa.me", StringComparison.OrdinalIgnoreCase)))
        {
            var normalized = DeChimex.Shared.WhatsApp.NormalizeNumber(wa.Value);
            if (normalized.Length > 0)
                wa.SetValue(normalized);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedCatalogAsync(CancellationToken ct)
    {
        // Purge any legacy starter dummy products if present from earlier builds in existing databases
        var starterSkus = new[] { "MUG-001", "PEN-007", "FLK-021", "JER-014", "NBK-003", "GFT-099" };
        var legacyDummyProducts = await db.Products.Include(p => p.Images)
            .Where(p => starterSkus.Contains(p.Sku))
            .ToListAsync(ct);

        if (legacyDummyProducts.Count > 0)
        {
            var dummyIds = legacyDummyProducts.Select(p => p.Id).ToList();
            var dummyInvs = await db.Inventories.Where(i => dummyIds.Contains(i.ProductId)).ToListAsync(ct);
            db.Inventories.RemoveRange(dummyInvs);
            db.Products.RemoveRange(legacyDummyProducts);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} legacy sample products from database.", legacyDummyProducts.Count);
        }

        // Purge any legacy starter dummy categories if present from earlier builds in existing databases
        var starterCategories = new[] { "Fashion", "Gift Items", "Office Supplies", "School Supplies", "Corporate Gifts" };
        var legacyCategories = await db.Categories
            .Where(c => starterCategories.Contains(c.Name))
            .ToListAsync(ct);

        if (legacyCategories.Count > 0)
        {
            db.Categories.RemoveRange(legacyCategories);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} legacy starter categories from database.", legacyCategories.Count);
        }

        // Purge any legacy starter dummy services if present from earlier builds in existing databases
        var starterServices = new[] { "Mug Printing", "Jersey Printing", "Engraving", "Vehicle Branding" };
        var legacyServices = await db.Services
            .Where(s => starterServices.Contains(s.Name))
            .ToListAsync(ct);

        if (legacyServices.Count > 0)
        {
            db.Services.RemoveRange(legacyServices);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} legacy starter services from database.", legacyServices.Count);
        }
    }
}
