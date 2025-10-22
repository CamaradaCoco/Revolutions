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
app.MapGet("/api/revolutions", async (RevolutionContext db, string? country, string? countryIso) =>
{
    if (string.IsNullOrWhiteSpace(country) && string.IsNullOrWhiteSpace(countryIso))
        return Results.BadRequest(new { error = "Provide countryIso or country" });

    // small iso3 -> iso2 map you can extend
    static string? Iso3ToIso2(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3)) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "USA","US" }, { "GBR","GB" }, { "RUS","RU" }, { "CHN","CN" },
            { "FRA","FR" }, { "DEU","DE" }, { "ESP","ES" }, { "ITA","IT" },
            { "CAN","CA" }, { "AUS","AU" }, { "BRA","BR" }, { "MEX","MX" },
            { "JPN","JP" }, { "KOR","KR" }, { "IND","IN" }, { "IRN","IR" },
            { "SYR","SY" } // <---- add this line so iso3 SYR maps to alpha-2 SY
        };
        return map.TryGetValue(iso3.ToUpperInvariant(), out var v) ? v : null;
    }

    // Try exact ISO first (accepts iso2 or iso3)
    if (!string.IsNullOrWhiteSpace(countryIso) || (!string.IsNullOrWhiteSpace(country) && (country.Length == 2 || country.Length == 3)))
    {
        var iso = (countryIso ?? country)!.Trim().ToUpperInvariant();
        var isoCandidates = new List<string> { iso };
        if (iso.Length == 3)
        {
            var iso2 = Iso3ToIso2(iso);
            if (!string.IsNullOrEmpty(iso2) && !isoCandidates.Contains(iso2)) isoCandidates.Add(iso2);
        }
        // also try matching stored alpha-3 values (if any)
        var isoMatches = await db.Revolutions
            .Where(r => r.StartDate.Year >= 1950 && r.CountryIso != null && isoCandidates.Contains(r.CountryIso!.ToUpper()))
            .OrderByDescending(r => r.StartDate)
            .Select(r => new {
                r.Id, r.Name, r.StartDate, r.EndDate, r.Country, r.CountryIso, r.Latitude, r.Longitude, r.Type, r.Description, r.WikidataId
            })
            .ToListAsync();

        if (isoMatches.Any()) return Results.Ok(isoMatches);
        // fall through to name match if no iso results
    }

    // Exact normalized country-name match as fallback (no fuzzy contains)
    if (!string.IsNullOrWhiteSpace(country))
    {
        var searchNorm = NormalizeName(country);
        if (!string.IsNullOrEmpty(searchNorm))
        {
            var rows = await db.Revolutions
                .Where(r => r.StartDate.Year >= 1950 && r.Country != null)
                .Select(r => new {
                    r.Id, r.Name, r.StartDate, r.EndDate, r.Country, r.CountryIso, r.Latitude, r.Longitude, r.Type, r.Description, r.WikidataId
                })
                .ToListAsync();

            var exactMatches = rows
                .Where(r => !string.IsNullOrEmpty(r.Country) && NormalizeName(r.Country) == searchNorm)
                .OrderByDescending(r => r.StartDate)
                .ToList();

            return Results.Ok(exactMatches);
        }
    }

    // nothing matched
    return Results.Ok(Array.Empty<object>());
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

// Add this helper method (place above the Map/Get route registrations)
static string NormalizeName(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return string.Empty;
    s = s!.Trim();
    // remove diacritics
    var normalizedForm = s.Normalize(NormalizationForm.FormD);
    var sb = new System.Text.StringBuilder();
    foreach (var ch in normalizedForm)
    {
        var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
        if (uc != UnicodeCategory.NonSpacingMark)
            sb.Append(ch);
    }
    var noDiacritics = sb.ToString().Normalize(NormalizationForm.FormC);

    // remove punctuation, replace non-word with space, collapse whitespace
    var cleaned = System.Text.RegularExpressions.Regex.Replace(noDiacritics, @"[^\w\s]", " ");
    cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    return cleaned.ToLowerInvariant();
}
