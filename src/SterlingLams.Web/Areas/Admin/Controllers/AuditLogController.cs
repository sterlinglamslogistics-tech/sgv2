using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class AuditLogController : AdminBaseController
    {
        protected override string Section => "AuditLog";

        private readonly ApplicationDbContext _db;
        private const int PageSize = 50;

        public AuditLogController(ApplicationDbContext db)
        {
            _db = db;
        }

        // NOTE: the action-type filter is named "act", NOT "action" — an "action" parameter binds
        // from the MVC route value {action} ("Index"), which silently filtered out every row.
        public async Task<IActionResult> Index(int page = 1, string act = "", string entity = "",
            string dateFrom = "", string dateTo = "", string q = "")
        {
            ViewData["Title"] = "Audit Log";

            var query = BuildQuery(act, entity, dateFrom, dateTo, q);

            var total = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(l => new AuditLogRow
                {
                    Action = l.Action, EntityType = l.EntityType, EntityId = l.EntityId,
                    Description = l.Description, PerformedBy = l.PerformedBy,
                    IpAddress = l.IpAddress, CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            var availableActions  = await _db.AuditLogs.Select(l => l.Action).Distinct().OrderBy(a => a).ToListAsync();
            var availableEntities = await _db.AuditLogs.Select(l => l.EntityType).Distinct().OrderBy(e => e).ToListAsync();

            return View(new AdminAuditLogViewModel
            {
                Logs = logs,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize),
                ActionFilter = act, EntityFilter = entity, DateFrom = dateFrom, DateTo = dateTo,
                SearchQuery = q,
                AvailableActions = availableActions, AvailableEntities = availableEntities
            });
        }

        public async Task<IActionResult> ExportCsv(string act = "", string entity = "",
            string dateFrom = "", string dateTo = "", string q = "")
        {
            var logs = await BuildQuery(act, entity, dateFrom, dateTo, q)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Timestamp (UTC),Action,Entity Type,Entity ID,Description,Performed By,IP Address");
            foreach (var l in logs)
            {
                sb.AppendLine(string.Join(",",
                    $"\"{l.CreatedAt:yyyy-MM-dd HH:mm:ss}\"",
                    $"\"{l.Action}\"",
                    $"\"{l.EntityType}\"",
                    $"\"{l.EntityId}\"",
                    $"\"{l.Description.Replace("\"", "\"\"")}\"",
                    $"\"{l.PerformedBy}\"",
                    $"\"{l.IpAddress}\""));
            }

            await LogAsync("Export", "AuditLog", null, $"Exported {logs.Count} audit log entries to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"audit_log_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }

        private IQueryable<AuditLog> BuildQuery(string action, string entity, string dateFrom, string dateTo, string q)
        {
            var query = _db.AuditLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(action))
                query = query.Where(l => l.Action == action);

            if (!string.IsNullOrWhiteSpace(entity))
                query = query.Where(l => l.EntityType == entity);

            if (DateTime.TryParse(dateFrom, out var from))
                query = query.Where(l => l.CreatedAt >= from.ToUniversalTime());

            if (DateTime.TryParse(dateTo, out var to))
                query = query.Where(l => l.CreatedAt < to.ToUniversalTime().AddDays(1));

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(l =>
                    EF.Functions.ILike(l.Description, $"%{q}%") ||
                    EF.Functions.ILike(l.PerformedBy, $"%{q}%"));

            return query;
        }
    }
}
