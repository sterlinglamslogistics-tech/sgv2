using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class SettingsController : AdminBaseController
{
    protected override string Section => "Settings";
    // Access is enforced per settings-group below, not by a blanket :manage on write.
    protected override bool EnforceManageOnWrite => false;

    private readonly ISettingsService _settings;
    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.IStorefrontCache _storefrontCache;
    private readonly IPermissionService _perms;

    public SettingsController(ISettingsService settings, ApplicationDbContext db,
        SterlingLams.Web.Services.IStorefrontCache storefrontCache, IPermissionService perms)
    {
        _settings = settings;
        _db = db;
        _storefrontCache = storefrontCache;
        _perms = perms;
    }

    /// <summary>True if the current user may see/edit the given settings group.</summary>
    private async Task<bool> CanEditGroupAsync(string? group)
    {
        var allowed = await _perms.GetAllowedSettingsGroupsAsync(User);
        return allowed == null || (group != null && allowed.Contains(group));
    }

    public async Task<IActionResult> Index(string tab = "General")
    {
        ViewData["Title"] = "Settings";
        ViewData["ActiveTab"] = tab;
        // For "category"-type settings (a dropdown of categories).
        ViewBag.Categories = await _db.Categories
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Name, c.Slug })
            .ToListAsync();
        // POS receipt settings live in the Inventory System; email settings live in the Email
        // Customizer — hide both groups here so each has a single home.
        // Payment keys + SMTP credentials live in the full-admin-only Integrations screen — never
        // expose them here (any staff with the Settings section could otherwise read/edit them).
        // Billing / API-connector settings are owner-only and managed on the full-admin Subscribe page.
        var all = (await _settings.GetAllAsync())
            .Where(s => s.Group != "POS / Pos" && s.Group != "Emails"
                     && s.Group != "Payments" && s.Group != "SMTP" && s.Group != "Billing").ToList();
        // Granular: a role may be granted only specific settings groups. null = all groups.
        var allowedGroups = await _perms.GetAllowedSettingsGroupsAsync(User);
        if (allowedGroups != null)
            all = all.Where(s => allowedGroups.Contains(s.Group)).ToList();
        return View(all);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string group, IFormCollection form)
    {
        if (!await CanEditGroupAsync(group))
        {
            TempData["Error"] = "You don't have access to that settings group.";
            return RedirectToAction(nameof(Index));
        }
        var updates = new Dictionary<string, string>();
        var all = await _settings.GetAllAsync();
        var groupSettings = all.Where(s => s.Group == group).ToList();

        // Collect every setting key that belongs to this group
        foreach (var s in groupSettings)
        {
            if (s.Type == "boolean")
                updates[s.Key] = form.ContainsKey(s.Key) ? "true" : "false";
            else if (form.ContainsKey(s.Key))
                updates[s.Key] = s.Type == "html"
                    ? ProductHtml.Sanitize(form[s.Key].ToString())
                    : form[s.Key].ToString();
        }

        // Before/after diff of only the keys that actually changed (values truncated for readability).
        var oldByKey = groupSettings.ToDictionary(s => s.Key, s => s.Value ?? "");
        string Short(string v) => v.Length > 120 ? v[..120] + "…" : v;
        var changed = updates
            .Where(kv => (oldByKey.TryGetValue(kv.Key, out var ov) ? ov : "") != (kv.Value ?? ""))
            .Select(kv => (kv.Key,
                (object?)Short(oldByKey.TryGetValue(kv.Key, out var ov) ? ov : ""),
                (object?)Short(kv.Value ?? "")))
            .ToArray();
        var changes = SterlingLams.Web.Services.AuditChanges.Build(changed);

        await _settings.SaveManyAsync(updates);
        await _storefrontCache.EvictAsync(); // homepage/announcement/merchandising settings are cached
        await LogAsync("Update", "Setting", null,
            $"Updated {group} settings ({(changed.Length)} changed of {updates.Count})", changes);
        TempData["Success"] = $"{group} settings saved.";
        return RedirectToAction(nameof(Index), new { tab = group });
    }

    // Persist a single setting immediately (used by the image uploader so the
    // value is saved without waiting for the user to click "Save").
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOne(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "Missing setting key." });

        var group = (await _settings.GetAllAsync()).FirstOrDefault(s => s.Key == key)?.Group;
        if (!await CanEditGroupAsync(group))
            return StatusCode(403, new { error = "No access to that settings group." });

        await _settings.SaveManyAsync(new Dictionary<string, string> { [key] = value ?? string.Empty });
        await _storefrontCache.EvictAsync();
        await LogAsync("Update", "Setting", key, $"Updated setting '{key}'");
        return Ok(new { success = true });
    }
}
