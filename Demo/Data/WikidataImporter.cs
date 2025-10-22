using System;
using System.Collections.Generic;
using System.Linq;
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

        // Query template — will be paged with LIMIT/OFFSET
        private const string SparqlTemplate = @"#replaceLineBreaks
SELECT ?item ?itemLabel ?start ?end ?countryLabel ?countryIso ?lat ?lon ?desc ?qid WHERE {
  # include items that are instance of revolution or subclass (P31 / P279*)
  ?item wdt:P31/wdt:P279* wd:Q34770.
  ?item wdt:P580 ?start.                      # start time
  FILTER(YEAR(?start) >= 1900)
  OPTIONAL { ?item wdt:P582 ?end. }          # end time
  OPTIONAL { ?item wdt:P17 ?country. }
  OPTIONAL { ?country wdt:P297 ?countryIso. } # ISO alpha-2
  OPTIONAL { ?item wdt:P625 ?coord. 
             BIND(geof:latitude(?coord) AS ?lat)
             BIND(geof:longitude(?coord) AS ?lon)
  }
  OPTIONAL { ?item schema:description ?desc. FILTER(LANG(?desc) = 'en') }
  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)
  SERVICE wikibase:label { bd:serviceParam wikibase:language 'en'. }
}
LIMIT {limit}
OFFSET {offset}";

        // Page size — keep reasonable to avoid massive single requests
        private const int PageSize = 1000;
        // Delay between pages to be polite to the service
        private static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(1.0);

        public static async Task<int> FetchAndImportAsync(RevolutionContext db, HttpClient? client = null, CancellationToken ct = default)
        {
            var createdClient = false;
            if (client == null)
            {
                client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
                createdClient = true;
            }

            // Must provide a descriptive User-Agent per Wikidata policy
            var userAgent = "RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco/Revolutions; elcosmith@hotmail.com)";
            try
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            }
            catch { /* ignore header parse errors */ }

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

            int totalImported = 0;
            int offset = 0;
            bool more = true;

            while (more && !ct.IsCancellationRequested)
            {
                var query = SparqlTemplate.Replace("#replaceLineBreaks", "")
                                          .Replace("{limit}", PageSize.ToString())
                                          .Replace("{offset}", offset.ToString());

                HttpResponseMessage? resp = null;
                int attempt = 0;
                TimeSpan delay = TimeSpan.FromSeconds(2);

                while (attempt < 4 && !ct.IsCancellationRequested)
                {
                    attempt++;
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                        req.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) });
                        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                        resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                        if (resp.IsSuccessStatusCode)
                            break;

                        if (resp.StatusCode == HttpStatusCode.TooManyRequests || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                        {
                            var retryAfter = resp.Headers.RetryAfter?.Delta ?? delay;
                            Console.Error.WriteLine($"Wikidata import: page offset {offset} attempt {attempt} returned {(int)resp.StatusCode}. Retrying after {retryAfter}.");
                            await Task.Delay(retryAfter, ct);
                            delay = delay * 2;
                            continue;
                        }

                        // non-retriable - log and abort
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        Console.Error.WriteLine($"Wikidata import failed (status {(int)resp.StatusCode}): {resp.ReasonPhrase}");
                        Console.Error.WriteLine("Response body (truncated): " + (body?.Length > 2000 ? body.Substring(0, 2000) + "..." : body));
                        if (createdClient) client.Dispose();
                        return totalImported;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Wikidata import: page offset {offset} attempt {attempt} exception: {ex.Message}");
                        if (attempt >= 4)
                        {
                            if (createdClient) client.Dispose();
                            return totalImported;
                        }
                        await Task.Delay(delay, ct);
                        delay = delay * 2;
                    }
                }

                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    if (createdClient) client.Dispose();
                    return totalImported;
                }

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    !results.TryGetProperty("bindings", out var bindings))
                {
                    // no results structure — stop
                    more = false;
                    break;
                }

                var bindingArray = bindings.EnumerateArray().ToArray();
                if (bindingArray.Length == 0)
                {
                    // done paging
                    more = false;
                    break;
                }

                // Optional logging of first page items (debug)
                if (offset == 0)
                {
                    int preview = 0;
                    foreach (var b in bindingArray.Take(5))
                    {
                        if (b.TryGetProperty("itemLabel", out var lab) && lab.TryGetProperty("value", out var v))
                        {
                            Console.WriteLine("Wikidata sample: " + v.GetString());
                            preview++;
                        }
                    }
                }

                // Process each binding
                foreach (var binding in bindingArray)
                {
                    string get(string name)
                    {
                        if (binding.TryGetProperty(name, out var el) && el.TryGetProperty("value", out var v))
                            return v.GetString() ?? string.Empty;
                        return string.Empty;
                    }

                    var qid = get("qid");
                    // fallback: extract qid from full item URI if qid empty
                    if (string.IsNullOrWhiteSpace(qid))
                    {
                        var itemUri = get("item");
                        if (!string.IsNullOrWhiteSpace(itemUri))
                        {
                            var idx = itemUri.LastIndexOf('/');
                            if (idx >= 0 && idx + 1 < itemUri.Length)
                                qid = itemUri.Substring(idx + 1);
                        }
                    }

                    var name = get("itemLabel");
                    if (string.IsNullOrEmpty(name)) continue;
                    var startStr = get("start");
                    var endStr = get("end");
                    var country = get("countryLabel");
                    var countryIso = get("countryIso");
                    var desc = get("desc");
                    var latStr = get("lat");
                    var lonStr = get("lon");

                    if (!DateTime.TryParse(startStr, out var startDate)) continue;

                    DateTime? endDate = null;
                    if (DateTime.TryParse(endStr, out var tmp)) endDate = tmp;

                    double? lat = null, lon = null;
                    if (double.TryParse(latStr, out var la)) lat = la;
                    if (double.TryParse(lonStr, out var lo)) lon = lo;

                    // Upsert logic: prefer WikidataId, otherwise try name+year to avoid duplicates
                    Revolution? entity = null;
                    if (!string.IsNullOrWhiteSpace(qid))
                    {
                        entity = await db.Revolutions.FirstOrDefaultAsync(r => r.WikidataId == qid, ct);
                    }

                    if (entity == null)
                    {
                        // name + year fallback — conservative de-dup
                        var normName = name.Trim();
                        var year = startDate.Year;
                        entity = await db.Revolutions.FirstOrDefaultAsync(r => r.Name == normName && r.StartDate.Year == year, ct);
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
                    entity.CountryIso = string.IsNullOrEmpty(countryIso) ? null : countryIso.ToUpperInvariant();
                    entity.Latitude = lat;
                    entity.Longitude = lon;
                    entity.Description = desc ?? string.Empty;
                    entity.Type = "Revolution/Uprising";
                    entity.Sources = "Wikidata";

                    totalImported++;
                }

                // Save after each page to keep progress
                await db.SaveChangesAsync(ct);

                // Advance paging
                offset += PageSize;

                // If fewer results than page size we are done
                if (bindingArray.Length < PageSize)
                {
                    more = false;
                    break;
                }

                // Respectful pause between pages
                await Task.Delay(PageDelay, ct);
            } // while more

            if (createdClient) client.Dispose();
            return totalImported;
        }
    }
}
