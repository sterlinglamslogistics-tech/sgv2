using System.Net.Http.Headers;
using System.Text.Json;
using SterlingLams.Web.Services.ERPNext;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Connectivity self-test for the configured ERPNext instance. Confirms the server is
/// reachable, that the API key/secret authenticate, and auto-discovers the Company and
/// Warehouses so they don't have to be looked up by hand. Usage: <c>dotnet run -- erpnext-ping</c>.
/// </summary>
public static class ErpNextPing
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ERPNextSettings>();

        Console.WriteLine($"ERPNext base URL : {settings.BaseUrl}");
        Console.WriteLine($"API key set      : {(string.IsNullOrWhiteSpace(settings.ApiKey) ? "NO" : "yes")}");
        Console.WriteLine(new string('-', 60));

        using var http = new HttpClient { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("token", $"{settings.ApiKey}:{settings.ApiSecret}");

        // 1. Reachability + auth
        try
        {
            var who = await http.GetAsync("/api/method/frappe.auth.get_logged_user");
            var body = await who.Content.ReadAsStringAsync();
            if (!who.IsSuccessStatusCode)
            {
                Console.WriteLine($"✗ Reachable but auth FAILED ({(int)who.StatusCode} {who.StatusCode}).");
                Console.WriteLine("  → Check ApiKey/ApiSecret in appsettings.Development.json.");
                Console.WriteLine($"  Server said: {Trim(body)}");
                return;
            }
            var user = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString();
            Console.WriteLine($"✓ Connected. Authenticated as: {user}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Could NOT reach {settings.BaseUrl}");
            Console.WriteLine($"  {ex.GetBaseException().Message}");
            Console.WriteLine("  → Is ERPNext running and the URL correct?");
            return;
        }

        // 2. Discover companies, warehouses, item count
        await PrintList(http, "Companies", "/api/resource/Company?limit_page_length=0");
        await PrintList(http, "Warehouses", "/api/resource/Warehouse?fields=[\"name\"]&limit_page_length=0");

        try
        {
            var resp = await http.GetAsync("/api/resource/Item?limit_page_length=0&fields=[\"name\"]");
            var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
            Console.WriteLine($"Items in ERPNext : {data.GetArrayLength()}");
        }
        catch { Console.WriteLine("Items in ERPNext : (could not read)"); }

        Console.WriteLine(new string('-', 60));
        Console.WriteLine("Ping complete.");
    }

    private static async Task PrintList(HttpClient http, string label, string url)
    {
        try
        {
            var resp = await http.GetAsync(url);
            var data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
            var names = data.EnumerateArray().Select(e => e.GetProperty("name").GetString());
            Console.WriteLine($"{label} ({data.GetArrayLength()}): {string.Join(", ", names)}");
        }
        catch (Exception ex) { Console.WriteLine($"{label}: (error — {ex.GetBaseException().Message})"); }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
