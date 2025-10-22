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

// Run migrations and import (dev)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RevolutionContext>();
    try
    {
        db.Database.Migrate();

        // Import from Wikidata (best effort)
        try
        {
            await WikidataImporter.FetchAndImportAsync(db);
        }
        catch (Exception ex)
        {
            // log import failure but don't crash startup
            Console.Error.WriteLine("Wikidata import failed: " + ex.Message);
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

// Helper: normalize a string (remove diacritics, punctuation, collapse whitespace, lower)
static string NormalizeName(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return string.Empty;
    s = s!.Trim();
    // remove diacritics
    var normalizedForm = s.Normalize(NormalizationForm.FormD);
    var sb = new StringBuilder();
    foreach (var ch in normalizedForm)
    {
        var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
        if (uc != UnicodeCategory.NonSpacingMark)
            sb.Append(ch);
    }
    var noDiacritics = sb.ToString().Normalize(NormalizationForm.FormC);

    // remove punctuation, replace dashes/slashes with spaces, collapse spaces
    var cleaned = Regex.Replace(noDiacritics, @"[^\w\s]", " ");
    cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
    return cleaned.ToLowerInvariant();
}

// API endpoint for the client map (register before Razor Pages)
app.MapGet("/api/revolutions", async (RevolutionContext db, string? country, string? countryIso) =>
{
    // Fast path: exact ISO match if provided
    if (!string.IsNullOrWhiteSpace(countryIso))
    {
        var iso = countryIso.Trim().ToUpperInvariant();
        var isoMatches = await db.Revolutions
            .Where(r => r.CountryIso != null && r.CountryIso.ToUpper() == iso && r.StartDate.Year >= 1900)
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
        return Results.Ok(isoMatches);
    }

    // If a textual country name was supplied, perform tolerant matching.
    if (!string.IsNullOrWhiteSpace(country))
    {
        var searchNorm = NormalizeName(country);

        // Pull candidate rows from DB (only columns we need). Keep reasonable limit/perf.
        var rows = await db.Revolutions
            .Where(r => r.StartDate.Year >= 1900)
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

        // In-memory tolerant filter
        var matches = rows.Where(r =>
        {
            var countryVal = NormalizeName(r.Country);
            if (string.IsNullOrEmpty(countryVal)) return false;

            // exact normalized equality or normalized contains
            if (countryVal == searchNorm) return true;
            if (countryVal.Contains(searchNorm)) return true;

            // also try reverse: search contains part of countryVal (handles long geojson names)
            if (searchNorm.Contains(countryVal)) return true;

            // handle common prefixes like "Republic of", "Kingdom of" by stripping short words
            var stripped = Regex.Replace(countryVal, @"\b(republic|kingdom|state|the|federation|of)\b", " ", RegexOptions.IgnoreCase);
            stripped = Regex.Replace(stripped, @"\s+", " ").Trim();
            if (!string.IsNullOrEmpty(stripped) && stripped.Contains(searchNorm)) return true;

            return false;
        }).OrderByDescending(r => r.StartDate).ToList();

        return Results.Ok(matches);
    }

    // No filter: return all (bounded)
    var all = await db.Revolutions
        .Where(r => r.StartDate.Year >= 1900)
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

    return Results.Ok(all);
}).WithName("GetRevolutions");

// Diagnostic: sample rows
app.MapGet("/debug/revolutions/sample", async (RevolutionContext db, int take = 20) =>
{
    var rows = await db.Revolutions
        .OrderByDescending(r => r.StartDate)
        .Take(take)
        .Select(r => new { r.Id, r.Name, r.Country, r.CountryIso, r.StartDate, r.Latitude, r.Longitude })
        .ToListAsync();
    return Results.Ok(rows);
});

// Diagnostic: counts grouped by CountryIso
app.MapGet("/debug/revolutions/counts", async (RevolutionContext db) =>
{
    var counts = await db.Revolutions
        .GroupBy(r => r.CountryIso ?? "(null)")
        .Select(g => new { CountryIso = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .ToListAsync();
    return Results.Ok(counts);
});

// Diagnostic: query by iso directly
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
