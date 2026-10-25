using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WinIsland.App.ViewModels;

namespace WinIsland.App.Services
{
    /// <summary>
    /// Fetches time-synced (LRC) or plain lyrics from the free LRCLIB API.
    /// Includes smart title/artist normalization for Spotify, YouTube, Apple Music, and browser SMTC sessions.
    /// </summary>
    public static class LyricsService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Regex LrcLine = new(@"\[(\d+):(\d+(?:\.\d+)?)\]\s*(.*)", RegexOptions.Compiled);
        private static readonly Regex BracketNoise = new(@"\s*[\(\[](?:official|lyric|lyrics|audio|video|mv|visualizer|hd|hq|4k|remaster|live|feat\.?|ft\.?).*?[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SuffixNoise = new(@"\s*-\s*(?:YouTube(?:\s+Music)?|Spotify|Official\s+.*|Lyrics?.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/1.0 (https://github.com/winisland)");
            return c;
        }

        public static (string cleanTitle, string cleanArtist) NormalizeTrackMetadata(string title, string artist)
        {
            string t = (title ?? string.Empty).Trim();
            string a = (artist ?? string.Empty).Trim();

            t = SuffixNoise.Replace(t, "").Trim();
            t = BracketNoise.Replace(t, "").Trim();

            if (string.Equals(a, "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "WinIsland Audio", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "Ready for playback", StringComparison.OrdinalIgnoreCase))
            {
                a = string.Empty;
            }

            // If artist is "Artist - Album" (common in some players), take the first part
            if (a.Contains(" - "))
            {
                var parts = a.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && !string.Equals(parts[0], t, StringComparison.OrdinalIgnoreCase))
                {
                    a = parts[0].Trim();
                }
            }

            // If title is "Artist - Song Title" (common in YouTube/browser sessions)
            if (t.Contains(" - "))
            {
                var parts = t.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    string possibleArtist = parts[0].Trim();
                    string possibleTitle = parts[1].Trim();
                    if (string.IsNullOrEmpty(a) ||
                        a.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                        a.Contains("Official", StringComparison.OrdinalIgnoreCase) ||
                        a.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                        a.Contains("Channel", StringComparison.OrdinalIgnoreCase) ||
                        !t.Equals(a, StringComparison.OrdinalIgnoreCase))
                    {
                        a = possibleArtist;
                        t = possibleTitle;
                    }
                }
            }

            // Strip any remaining trailing parenthetical if still noisy
            string stripped = Regex.Replace(t, @"\s*[\(\[].*?[\)\]]", "").Trim();
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                t = stripped;
            }

            return (t, a);
        }

        public static async Task<List<LyricLine>?> FetchAsync(string title, string artist, TimeSpan duration)
        {
            try
            {
                var (cleanTitle, cleanArtist) = NormalizeTrackMetadata(title, artist);
                if (string.IsNullOrWhiteSpace(cleanTitle)) return null;

                string? synced = null;
                string? plain = null;

                // 1. Direct lookup if we have both title and artist
                if (!string.IsNullOrWhiteSpace(cleanArtist))
                {
                    string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(cleanTitle)}&artist_name={Uri.EscapeDataString(cleanArtist)}";
                    if (duration > TimeSpan.FromSeconds(5))
                    {
                        url += $"&duration={(int)duration.TotalSeconds}";
                    }

                    using var resp = await Http.GetAsync(url);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        synced = GetStringProp(doc.RootElement, "syncedLyrics");
                        plain = GetStringProp(doc.RootElement, "plainLyrics");
                    }
                }

                // 2. Fallback to search query (handles slight artist/duration mismatches)
                if (string.IsNullOrWhiteSpace(synced))
                {
                    string q = string.IsNullOrWhiteSpace(cleanArtist)
                        ? cleanTitle
                        : $"{cleanTitle} {cleanArtist}";

                    using var resp = await Http.GetAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(q)}");
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                synced ??= GetStringProp(el, "syncedLyrics");
                                plain ??= GetStringProp(el, "plainLyrics");
                                if (!string.IsNullOrWhiteSpace(synced)) break;
                            }
                        }
                    }
                }

                // 3. Second search fallback using only cleanTitle if artist caused 0 results
                if (string.IsNullOrWhiteSpace(synced) && string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(cleanArtist))
                {
                    using var resp = await Http.GetAsync($"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanTitle)}");
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                synced ??= GetStringProp(el, "syncedLyrics");
                                plain ??= GetStringProp(el, "plainLyrics");
                                if (!string.IsNullOrWhiteSpace(synced)) break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(synced))
                {
                    var parsed = Parse(synced);
                    if (parsed.Count > 0) return parsed;
                }

                if (!string.IsNullOrWhiteSpace(plain))
                {
                    var plainLines = ParsePlain(plain, duration);
                    if (plainLines.Count > 0) return plainLines;
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LyricsService] {ex.Message}");
                return null;
            }
        }

        private static string? GetStringProp(JsonElement el, string propName) =>
            el.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        public static List<LyricLine> Parse(string lrc)
        {
            var list = new List<LyricLine>();
            foreach (var raw in lrc.Split('\n'))
            {
                var m = LrcLine.Match(raw.Trim());
                if (!m.Success) continue;
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                string text = m.Groups[3].Value.Trim();
                if (text.Length == 0) text = "♪";
                list.Add(new LyricLine(TimeSpan.FromSeconds(min * 60 + sec), text));
            }
            return list.OrderBy(l => l.Time).ToList();
        }

        public static List<LyricLine> ParsePlain(string plain, TimeSpan duration)
        {
            var rawLines = plain
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var list = new List<LyricLine>();
            if (rawLines.Count == 0) return list;

            double totalSeconds = duration.TotalSeconds > 15 ? duration.TotalSeconds : rawLines.Count * 4.0;
            double step = totalSeconds / rawLines.Count;

            for (int i = 0; i < rawLines.Count; i++)
            {
                list.Add(new LyricLine(TimeSpan.FromSeconds(i * step), rawLines[i]));
            }
            return list;
        }
    }
}
