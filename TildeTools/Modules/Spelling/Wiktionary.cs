using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TildeTools.Modules.Spelling;

// Only with LookUpOnline on, see DefineWindow.Look
internal static partial class Wiktionary
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Wikimedia asks every client to name itself and say where to reach it
    static Wiktionary() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd($"TildeTools/{typeof(Wiktionary).Assembly.GetName().Version?.ToString(3)} (https://github.com/Tildemancer/TildeTools)");

    // Page names are case-sensitive: paris and Paris are two pages, "Callipygian" is a 404
    internal static async Task<List<Entry>> Define(string word)
    {
        var lower = word.ToLowerInvariant();
        string[] titles = [.. new[] { lower, char.ToUpperInvariant(lower[0]) + lower[1..], word }.Distinct()];
        var found = await Task.WhenAll(titles.Select(Fetch));

        return [.. titles.Zip(found, (title, senses) => new Entry(title, senses)).Where(entry => entry.Senses.Count > 0)];
    }

    private static async Task<List<Sense>> Fetch(string title)
    {
        using var response = await Client.GetAsync($"https://en.wiktionary.org/api/rest_v1/page/definition/{Uri.EscapeDataString(title)}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        List<Sense> senses = [];
        if (!json.RootElement.TryGetProperty("en", out var english))
            return senses;

        foreach (var entry in english.EnumerateArray())
        {
            var pos = entry.TryGetProperty("partOfSpeech", out var p) ? p.GetString()?.ToLowerInvariant() ?? "" : "";

            if (entry.TryGetProperty("definitions", out var definitions))
                foreach (var definition in definitions.EnumerateArray())
                    if (definition.TryGetProperty("definition", out var d) && Plain(d.GetString()) is { Length: > 0 } gloss)
                        senses.Add(new Sense(title, pos, gloss));
        }

        return senses;
    }

    private static string Plain(string? html) => Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html ?? "", "")), " ").Trim();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
