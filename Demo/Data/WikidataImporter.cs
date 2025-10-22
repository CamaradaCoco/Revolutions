using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Demo.Models;

namespace Demo.Data
{
    public static class WikidataImporter
    {
        private const string Endpoint = "https://query.wikidata.org/sparql";

        // Simple SPARQL for testing: filter by country label (case-insensitive contains) and year >= 1950
        private const string SparqlTemplate = @"SELECT DISTINCT ?item ?itemLabel ?start ?end ?countryLabel ?countryIso ?qid WHERE {
  ?item wdt:P31/wdt:P279* wd:Q34770.
  ?item wdt:P580 ?start.
  FILTER(YEAR(?start) >= 1950)
  OPTIONAL { ?item wdt:P582 ?end. }
  OPTIONAL {
    ?item wdt:P17 ?country.
    ?country rdfs:label ?countryLabel FILTER(LANG(?countryLabel) = 'en').
    OPTIONAL { ?country wdt:P297 ?countryIso. }
    FILTER(CONTAINS(LCASE(?countryLabel), LCASE(""{country}"" )))
  }
  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)
  SERVICE wikibase:label { bd:serviceParam wikibase:language 'en'. }
}
LIMIT {limit}";

        /// <summary>
        /// Convenience wrapper used by Program.cs. Performs a few small test imports by country.
        /// Adjust the sampleCountries or implement a full import if you want production behavior.
        /// </summary>
        public static async Task<int> FetchAndImportAsync(RevolutionContext db, HttpClient? client = null, CancellationToken ct = default)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));

            var createdClient = false;
            try
            {
                if (client == null)
                {
                    client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                    {
                        Timeout = TimeSpan.FromSeconds(120)
                    };
                    createdClient = true;
                }

                // set a descriptive User-Agent per WDQS policy (replace with your contact)
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco/Revolutions; elcosmith@hotmail.com)"); } catch { }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                // Small sample set for testing — expand or replace with full import routine later
                var sampleCountries = new[] { "United States", "France", "Russia", "China", "Iran" };
                var total = 0;
                foreach (var c in sampleCountries)
                {
                    // small limit to keep test quick; increase if you need more coverage
                    var imported = await FetchAndImportForCountryAsync(db, c, limit: 25, client: client, ct: ct);
                    total += imported;
                    // polite pause between country queries
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                return total;
            }
            finally
            {
                if (createdClient)
                {
                    try { client?.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Simple, single-page importer for testing. Searches Wikidata for revolutions since 1950
        /// whose country label contains <paramref name="countryName"/> (case-insensitive).
        /// </summary>
        public static async Task<int> FetchAndImportForCountryAsync(
            RevolutionContext db,
            string countryName,
            int limit = 50,
            HttpClient? client = null,
            CancellationToken ct = default)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(countryName)) throw new ArgumentException("countryName required", nameof(countryName));
            if (limit <= 0) limit = 50;

            var createdClient = false;
            try
            {
                if (client == null)
                {
                    client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                    {
                        Timeout = TimeSpan.FromSeconds(60)
                    };
                    createdClient = true;
                }

                // Set a descriptive User-Agent per WDQS policy (replace with your contact)
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco/Revolutions; elcosmith@hotmail.com)"); } catch { }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                // basic escaping for embedded literal (sufficient for test)
                static string SparqlEscape(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? string.Empty;

                var query = SparqlTemplate
                    .Replace("{country}", SparqlEscape(countryName))
                    .Replace("{limit}", limit.ToString());

                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) })
                };
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    Console.Error.WriteLine($"Wikidata import failed (status {(int)resp.StatusCode}): {resp.ReasonPhrase}");
                    Console.Error.WriteLine("Response body (truncated): " + (body?.Length > 2000 ? body.Substring(0, 2000) + "..." : body));
                    return 0;
                }

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings))
                {
                    Console.Error.WriteLine("Wikidata import: unexpected response structure.");
                    return 0;
                }

                var imported = 0;
                foreach (var b in bindings.EnumerateArray())
                {
                    string get(string name)
                    {
                        if (b.TryGetProperty(name, out var el) && el.TryGetProperty("value", out var v))
                            return v.GetString() ?? string.Empty;
                        return string.Empty;
                    }

                    var qid = get("qid");
                    var name = get("itemLabel");
                    var startStr = get("start");
                    var endStr = get("end");
                    var country = get("countryLabel");
                    var countryIso = get("countryIso");

                    if (!DateTime.TryParse(startStr, out var startDate)) continue;

                    DateTime? endDate = null;
                    if (DateTime.TryParse(endStr, out var tmp)) endDate = tmp;

                    // Upsert: prefer WikidataId; fallback to Name+StartYear
                    Revolution? entity = null;
                    if (!string.IsNullOrWhiteSpace(qid))
                    {
                        entity = await db.Revolutions.FirstOrDefaultAsync(r => r.WikidataId == qid, ct);
                    }

                    if (entity == null)
                    {
                        entity = await db.Revolutions.FirstOrDefaultAsync(r => r.Name == name && r.StartDate.Year == startDate.Year, ct);
                    }

                    if (entity == null)
                    {
                        entity = new Revolution { WikidataId = string.IsNullOrWhiteSpace(qid) ? null : qid };
                        db.Revolutions.Add(entity);
                    }
                    else if (string.IsNullOrWhiteSpace(entity.WikidataId) && !string.IsNullOrWhiteSpace(qid))
                    {
                        entity.WikidataId = qid;
                    }

                    entity.Name = name;
                    entity.StartDate = startDate;
                    entity.EndDate = endDate;
                    entity.Country = country ?? string.Empty;
                    entity.CountryIso = string.IsNullOrWhiteSpace(countryIso) ? null : countryIso.ToUpperInvariant();
                    entity.Description = string.Empty;
                    entity.Type = "Revolution/Uprising";
                    entity.Sources = "Wikidata";

                    imported++;
                }

                await db.SaveChangesAsync(ct);
                Console.WriteLine($"WikidataImporter: imported {imported} items for country '{countryName}'.");
                return imported;
            }
            finally
            {
                if (createdClient)
                {
                    try { client?.Dispose(); } catch { }
                }
            }
        }
    }
}
