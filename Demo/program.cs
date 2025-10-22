using Microsoft.EntityFrameworkCore;
using Demo.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

builder.Services.AddDbContext<RevolutionContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// Run migrations and import (dev)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RevolutionContext>();
    db.Database.Migrate();

    // Import from Wikidata (best effort)
    try
    {
        await WikidataImporter.FetchAndImportAsync(db);
    }
    catch (Exception ex)
    {
        // log or ignore — don't crash app on import failure for dev convenience
        Console.WriteLine("Wikidata import failed: " + ex.Message);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

// Simple JSON API endpoint for the client map
app.MapGet("/api/revolutions", async (RevolutionContext db) =>
{
    var items = await db.Revolutions
        .Where(r => r.StartDate.Year >= 1900)
        .Select(r => new {
            r.Id, r.Name, r.StartDate, r.EndDate, r.Country, r.Latitude, r.Longitude, r.Type, r.Description, r.WikidataId
        })
        .ToListAsync();
    return Results.Ok(items);
});

app.Run();
