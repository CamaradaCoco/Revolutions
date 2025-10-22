using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Demo.Models;

namespace Demo.Data
{
    public static class WikidataImporter
    {
        private const string Endpoint = "https://query.wikidata.org/sparql";

        // Minimal SPARQL: items instance of revolution/uprising with start date >= 1900
        private const string SparqlQuery = @"#replaceLineBreaks
SELECT ?item ?itemLabel ?start ?end ?countryLabel ?lat ?lon ?desc ?qid WHERE {
  VALUES ?type { wd:Q34770 wd:Q209809 } # revolution, uprising (adjust as needed)
  ?item wdt:P31 ?type.
  ?item wdt:P580 ?start.                      # start time
  FILTER(YEAR(?start) >= 1900)
  OPTIONAL { ?item wdt:P582 ?end. }          # end time
  OPTIONAL { ?item wdt:P17 ?country. }
  OPTIONAL { ?item wdt:P625 ?coord. 
             BIND(geof:latitude(?coord) AS ?lat)
             BIND(geof:longitude(?coord) AS ?lon)
  }
  OPTIONAL { ?item schema:description ?desc. FILTER(LANG(?desc) = 'en') }
  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)
  SERVICE wikibase:label { bd:serviceParam wikibase:language 'en'. }
}
LIMIT 1000";

        // Fetch and import/upsert into the DB
        public static async Task<int> FetchAndImportAsync(RevolutionContext db, HttpClient? client = null, CancellationToken ct = default)
        {
            client ??= new HttpClient();
            // SPARQL endpoint expects query via ?query param and Accept: application/sparql-results+json
            var url = Endpoint + "?query=" + Uri.EscapeDataString(SparqlQuery.Replace("#replaceLineBreaks", ""));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings))
                return 0;

            var imported = 0;

            foreach (var binding in bindings.EnumerateArray())
            {
                string get(string name)
                {
                    if (binding.TryGetProperty(name, out var el) && el.TryGetProperty("value", out var v))
                        return v.GetString() ?? string.Empty;
                    return string.Empty;
                }

                var qid = get("qid"); // e.g. Q12345
                var name = get("itemLabel");
                if (string.IsNullOrEmpty(name)) continue;
                var startStr = get("start");
                var endStr = get("end");
                var country = get("countryLabel");
                var desc = get("desc");
                var latStr = get("lat");
                var lonStr = get("lon");

                DateTime startDate;
                if (!DateTime.TryParse(startStr, out startDate)) continue;

                DateTime? endDate = null;
                if (DateTime.TryParse(endStr, out var tmp)) endDate = tmp;

                double? lat = null, lon = null;
                if (double.TryParse(latStr, out var la)) lat = la;
                if (double.TryParse(lonStr, out var lo)) lon = lo;

                // Upsert by WikidataId if present
                Revolution? entity = null;
                if (!string.IsNullOrEmpty(qid))
                {
                    entity = await db.Revolutions.FirstOrDefaultAsync(r => r.WikidataId == qid, ct);
                }
                if (entity == null)
                {
                    entity = new Revolution { WikidataId = qid };
                    db.Revolutions.Add(entity);
                }

                entity.Name = name;
                entity.StartDate = startDate;
                entity.EndDate = endDate;
                entity.Country = country ?? string.Empty;
                entity.Latitude = lat;
                entity.Longitude = lon;
                entity.Description = desc ?? string.Empty;
                entity.Type = "Revolution/Uprising";
                entity.Sources = "Wikidata";

                imported++;
            }

            await db.SaveChangesAsync(ct);
            return imported;
        }
    }
}
