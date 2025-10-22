using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Demo.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// Add CORS for local development (adjust for production)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Register DbContext
builder.Services.AddDbContext<RevolutionContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Run migrations and import (dev) - keep safe default import if desired
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RevolutionContext>();
    try
    {
        db.Database.Migrate();

        try
        {
            var imported = await WikidataImporter.FetchAndImportAsync(db);
            Console.WriteLine($"WikidataImporter: imported {imported} items.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Wikidata import failed: " + ex);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Database migration failed: " + ex);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthorization();

// Health
app.MapGet("/ping", () => Results.Text("pong"));

// Route listing
app.MapGet("/routes", (EndpointDataSource ds) =>
{
    var list = ds.Endpoints
        .OfType<RouteEndpoint>()
        .Select(e => new { Pattern = e.RoutePattern.RawText, e.DisplayName })
        .OrderBy(e => e.Pattern)
        .ToList();
    return Results.Json(list);
});

// API endpoint for the client map (exact ISO match only)
app.MapGet("/api/revolutions", async (RevolutionContext db, string? countryIso) =>
{
    if (string.IsNullOrWhiteSpace(countryIso))
    {
        // require exact ISO for correctness
        return Results.BadRequest(new { error = "countryIso query parameter required (alpha-2 or alpha-3)" });
    }

    var iso = countryIso.Trim().ToUpperInvariant();

    // small iso3->iso2 mapping for common cases; extend as needed
    static string? Iso3ToIso2(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3)) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "USA", "US" }, { "RUS", "RU" }, { "CHN", "CN" }, { "IRN", "IR" },
            { "FRA", "FR" }, { "GBR", "GB" }, { "DEU", "DE" }, { "ESP", "ES" },
            { "ITA", "IT" }, { "KOR", "KR" }, { "JPN", "JP" }, { "CAN", "CA" },
            { "AUS", "AU" }, { "BRA", "BR" }, { "MEX", "MX" }, { "IND", "IN" }
        };
        return map.TryGetValue(iso3.ToUpperInvariant(), out var v) ? v : null;
    }

    var isoCandidates = new List<string> { iso };
    if (iso.Length == 3)
    {
        var iso2 = Iso3ToIso2(iso);
        if (!string.IsNullOrEmpty(iso2)) isoCandidates.Add(iso2);
    }

    var items = await db.Revolutions
        .Where(r => r.StartDate.Year >= 1950 && r.CountryIso != null && isoCandidates.Contains(r.CountryIso!.ToUpper()))
        .OrderByDescending(r => r.StartDate)
        .Select(r => new {
            r.Id,
            r.Name,
            r.StartDate,
            r.EndDate,
            r.Country,
            r.CountryIso,
            r.Latitude,
            r.Longitude,
            r.Type,
            r.Description,
            r.WikidataId
        })
        .ToListAsync();

    return Results.Ok(items);
}).WithName("GetRevolutions");

// Diagnostic endpoints (unchanged)
app.MapGet("/debug/revolutions/sample", async (RevolutionContext db, int take = 20) =>
{
    var rows = await db.Revolutions
        .OrderByDescending(r => r.StartDate)
        .Take(take)
        .Select(r => new { r.Id, r.Name, r.Country, r.CountryIso, r.StartDate, r.Latitude, r.Longitude })
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapGet("/debug/revolutions/counts", async (RevolutionContext db) =>
{
    var counts = await db.Revolutions
        .GroupBy(r => r.CountryIso ?? "(null)")
        .Select(g => new { CountryIso = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .ToListAsync();
    return Results.Ok(counts);
});

app.MapGet("/debug/revolutions/byiso/{iso}", async (RevolutionContext db, string iso) =>
{
    var items = await db.Revolutions
        .Where(r => r.CountryIso != null && r.CountryIso.ToUpper() == iso.ToUpper())
        .Select(r => new { r.Id, r.Name, r.Country, r.CountryIso, r.StartDate })
        .ToListAsync();
    return Results.Ok(items);
});

// Static assets and Razor Pages
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
