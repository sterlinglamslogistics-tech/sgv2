namespace SterlingLams.Web.Areas.Admin;

/// <summary>
/// A backend section. <see cref="Group"/> places it under a collapsible heading in the
/// sidebar; <see cref="OwnerOnly"/> items (Roles, Integrations, SEO) are shown only to full
/// Administrators and are intentionally NOT grantable to roles (no privilege escalation).
/// </summary>
public record AdminSection(string Key, string Label, string Controller, string Icon, string Group = "", bool OwnerOnly = false);

public static class AdminSections
{
    /// <summary>
    /// The full backend navigation, in display order, grouped like the Inventory System.
    /// Drives the sidebar. OwnerOnly entries are owner-only; everything else is grantable
    /// to roles (see <see cref="All"/>).
    /// </summary>
    public static readonly List<AdminSection> Nav = new()
    {
        // ── Overview (no heading) ─────────────────────────────────────────────
        new("Dashboard",  "Dashboard",  "Dashboard",  "M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6"),

        // ── Sales ─────────────────────────────────────────────────────────────
        new("Orders",     "Orders",     "Orders",     "M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2", "Sales"),
        new("Customers",  "Customers",  "Customers",  "M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z", "Sales"),
        new("Discounts",  "Discounts",  "Discounts",  "M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4z", "Sales"),
        new("GiftCards",  "Gift Cards", "GiftCards",  "M21 11.25v8.25a1.5 1.5 0 01-1.5 1.5H5.25a1.5 1.5 0 01-1.5-1.5v-8.25M12 4.875A2.625 2.625 0 109.375 7.5H12m0-2.625V7.5m0-2.625A2.625 2.625 0 1114.625 7.5H12m0 0V21m-8.25-13.5h16.5a1.5 1.5 0 011.5 1.5v1.5a1.5 1.5 0 01-1.5 1.5H3.75a1.5 1.5 0 01-1.5-1.5V9a1.5 1.5 0 011.5-1.5z", "Sales"),

        // ── Catalogue ─────────────────────────────────────────────────────────
        new("Products",   "Products",   "Products",   "M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4", "Catalogue"),
        new("Categories", "Categories", "Categories", "M4 6h16M4 10h16M4 14h16M4 18h16", "Catalogue"),
        new("Attributes", "Attributes", "Attributes", "M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4zM13 13h.01M17 17h.01", "Catalogue"),
        new("Inventory",  "Inventory",  "Inventory",  "M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10", "Catalogue"),
        new("Reviews",    "Reviews",    "Reviews",    "M11.049 2.927c.3-.921 1.603-.921 1.902 0l1.519 4.674a1 1 0 00.95.69h4.915c.969 0 1.371 1.24.588 1.81l-3.976 2.888a1 1 0 00-.363 1.118l1.518 4.674c.3.922-.755 1.688-1.538 1.118l-3.976-2.888a1 1 0 00-1.176 0l-3.976 2.888c-.783.57-1.838-.196-1.538-1.118l1.518-4.674a1 1 0 00-.363-1.118l-3.976-2.888c-.783-.57-.38-1.81.588-1.81h4.914a1 1 0 00.951-.69l1.519-4.674z", "Catalogue"),

        // ── Marketing & content ───────────────────────────────────────────────
        new("Marketing",  "Marketing",  "Marketing",  "M11 5.882V19.24a1.76 1.76 0 01-3.417.592l-2.147-6.15M18 13a3 3 0 100-6M5.436 13.683A4.001 4.001 0 017 6h1.832c4.1 0 7.625-1.234 9.168-3v14c-1.543-1.766-5.067-3-9.168-3H7a3.988 3.988 0 01-1.564-.317z", "Marketing"),
        new("Journal",    "Journal",    "Journal",    "M12 6.042A8.967 8.967 0 006 3.75c-1.052 0-2.062.18-3 .512v14.25A8.987 8.987 0 016 18c2.305 0 4.408.867 6 2.292m0-14.25a8.966 8.966 0 016-2.292c1.052 0 2.062.18 3 .512v14.25A8.987 8.987 0 0018 18a8.967 8.967 0 00-6 2.292m0-14.25v14.25", "Marketing"),
        new("Emails",     "Emails",     "EmailCustomizer", "M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z", "Marketing"),
        new("Seo",        "SEO Descriptions", "Seo",  "M7 8h10M7 12h6m-6 8l-4-4V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2H9l-2 2z", "Marketing", OwnerOnly: true),

        // ── Reports ───────────────────────────────────────────────────────────
        new("Reports",    "Reports",    "Reports",    "M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z", "Reports"),

        // ── Settings & administration ─────────────────────────────────────────
        new("Stores",     "Stores",     "Stores",     "M17.657 16.657L13.414 20.9a1.998 1.998 0 01-2.827 0l-4.244-4.243a8 8 0 1111.314 0z", "Settings"),
        new("Users",      "Users",      "Users",      "M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z", "Settings", OwnerOnly: true),
        new("Roles",      "Roles & Permissions", "Roles", "M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z", "Settings", OwnerOnly: true),
        new("Integrations", "Integrations", "Integrations", "M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z", "Settings", OwnerOnly: true),
        new("Subscribe",  "Subscribe",  "Subscribe",  "M3 10h18M7 15h1m4 0h1m-7 4h12a3 3 0 003-3V8a3 3 0 00-3-3H6a3 3 0 00-3 3v8a3 3 0 003 3z", "Settings", OwnerOnly: true),
        new("AuditLog",   "Audit Log",  "AuditLog",   "M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z", "Settings"),
        new("Settings",   "Settings",   "Settings",   "M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z M15 12a3 3 0 11-6 0 3 3 0 016 0z", "Settings"),
    };

    /// <summary>Order the sidebar renders groups in. "" is the top, non-collapsible Overview.</summary>
    public static readonly string[] GroupOrder = { "", "Sales", "Catalogue", "Marketing", "Reports", "Settings" };

    /// <summary>Sections that can be granted to roles (everything that isn't owner-only).</summary>
    public static readonly List<AdminSection> All = Nav.Where(s => !s.OwnerOnly).ToList();

    /// <summary>The built-in full-access role. Always sees everything; cannot be edited/deleted.</summary>
    public const string AdminRole = "Admin";

    /// <summary>Roles that ship by default (besides Admin and Customer).</summary>
    public static readonly string[] DefaultStaffRoles = { "Operations", "Sales", "Inventory", "Social Media" };

    public static bool IsValidSection(string key) => All.Any(s => s.Key == key);
}
