using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize]   // must be signed in; section access is enforced per-controller below
    public abstract class AdminBaseController : Controller
    {
        /// <summary>
        /// The admin section this controller belongs to (e.g. "Orders"). Override in each
        /// controller. Null means Admin-only (no staff role can reach it) — used for Roles.
        /// </summary>
        protected virtual string? Section => null;

        /// <summary>
        /// When true (default), write requests (POST/PUT/DELETE/PATCH) require the "<Section>:manage"
        /// permission, while reads only need view. Controllers that enforce finer access themselves
        /// (e.g. Settings, per settings-group) override this to false.
        /// </summary>
        protected virtual bool EnforceManageOnWrite => true;

        /// <summary>
        /// Enforces section-based access before every action. Administrators bypass all checks.
        /// Staff roles must have the section granted; otherwise they're sent to Access Denied.
        /// Also exposes the user's allowed sections to the layout for sidebar filtering.
        /// </summary>
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var perms = HttpContext.RequestServices.GetRequiredService<IPermissionService>();

            // Expose allowed sections to the shared layout (sidebar)
            var allowed = await perms.GetAllowedSectionsAsync(User);
            ViewData["AllowedSections"] = allowed;
            ViewData["IsFullAdmin"] = User.IsInRole(AdminSections.AdminRole);

            // Inventory-team staff operate in the dedicated Inventory System, not the website admin.
            if (!User.IsInRole(AdminSections.AdminRole) && User.IsInRole("Inventory"))
            {
                context.Result = RedirectToAction("Index", "Overview", new { area = "Inventory" });
                return;
            }

            // Social Media / Marketing staff operate in the dedicated Marketing Hub.
            if (!User.IsInRole(AdminSections.AdminRole) && User.IsInRole("Social Media"))
            {
                context.Result = RedirectToAction("Index", "Dashboard", new { area = "Marketing" });
                return;
            }

            var section = Section;

            // Admin-only controllers (Section == null): only full admins pass
            if (section == null)
            {
                if (!User.IsInRole(AdminSections.AdminRole))
                {
                    context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
                    return;
                }
            }
            else
            {
                var method = context.HttpContext.Request.Method;
                var isWrite = method == "POST" || method == "PUT" || method == "DELETE" || method == "PATCH";
                var ok = isWrite && EnforceManageOnWrite
                    ? await perms.CanManageAsync(User, section)   // write needs manage
                    : await perms.CanAccessAsync(User, section);  // read needs view
                if (!ok)
                {
                    context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
                    return;
                }
            }

            await next();
        }

        /// <summary>
        /// Records an admin action to the audit log. Best-effort — never throws.
        /// </summary>
        protected async Task LogAsync(string action, string entityType, string? entityId, string description, string? changes = null)
        {
            try
            {
                var audit = HttpContext.RequestServices.GetRequiredService<IAuditService>();
                await audit.LogAsync(action, entityType, entityId, description, changes);
            }
            catch
            {
                // Swallow — auditing must never break the operation.
            }
        }
    }
}
