using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WinIsland.App.ViewModels;

namespace WinIsland.App.Services
{
    public class PrayerLocationSettings
    {
        public string City { get; set; } = string.Empty;
        public string Country { get; set; } = "Indonesia";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public bool IsCustomCity { get; set; }
    }

    public class DailyPrayerResult
    {
        public string City { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string HijriDate { get; set; } = string.Empty;
        public List<PrayerTimeEntry> Items { get; set; } = new();
    }

    /// <summary>
    /// Provides location-based Islamic prayer schedules (Imsak, Subuh, Terbit, Dzuhur, Ashar, Maghrib, Isya)
    /// using Aladhan API (Method 20: Kementerian Agama RI) with automatic IP geolocation,
    /// Open-Meteo city geocoding search, and offline astronomical calculation fallback.
    /// </summary>
    public static class PrayerTimeService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/1.0 (https://github.com/yogadhipr/WinIsland)");
            return client;
        }

        private static string GetSettingsFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "WinIsland");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return Path.Combine(folder, "prayer_location.json");
        }

        public static PrayerLocationSettings? LoadSavedLocation()
        {
            try
            {
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<PrayerLocationSettings>(File.ReadAllText(path));
                    if (loaded != null && !string.IsNullOrWhiteSpace(loaded.City))
                    {
                        return loaded;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void SaveLocation(PrayerLocationSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetSettingsFilePath(), json);
            }
            catch { }
        }

        /// <summary>
        /// Automatically detects the user's location via IP geolocation, or falls back to local timezone coordinates.
        /// </summary>
        public static async Task<PrayerLocationSettings> DetectLocationAsync()
        {
            // 1. Try ipwho.is (HTTPS, fast, no API key required)
            try
            {
                using var resp = await Http.GetAsync("https://ipwho.is/");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("success", out var succ) && succ.ValueKind == JsonValueKind.True)
                    {
                        string? city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
                        string? country = root.TryGetProperty("country", out var co) ? co.GetString() : "Indonesia";
                        double lat = root.TryGetProperty("latitude", out var la) && la.TryGetDouble(out double dLat) ? dLat : 0;
                        double lon = root.TryGetProperty("longitude", out var lo) && lo.TryGetDouble(out double dLon) ? dLon : 0;

                        if (!string.IsNullOrWhiteSpace(city) && (Math.Abs(lat) > 0.01 || Math.Abs(lon) > 0.01))
                        {
                            var loc = new PrayerLocationSettings
                            {
                                City = city,
                                Country = country ?? "Indonesia",
                                Latitude = lat,
                                Longitude = lon,
                                IsCustomCity = false
                            };
                            SaveLocation(loc);
                            return loc;
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback to ip-api.com
            try
            {
                using var resp = await Http.GetAsync("http://ip-api.com/json/?fields=status,country,city,lat,lon");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var root = doc.RootElement;
                    string? status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        string? city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
                        string? country = root.TryGetProperty("country", out var co) ? co.GetString() : "Indonesia";
                        double lat = root.TryGetProperty("lat", out var la) && la.TryGetDouble(out double dLat) ? dLat : 0;
                        double lon = root.TryGetProperty("lon", out var lo) && lo.TryGetDouble(out double dLon) ? dLon : 0;

                        if (!string.IsNullOrWhiteSpace(city))
                        {
                            var loc = new PrayerLocationSettings
                            {
                                City = city,
                                Country = country ?? "Indonesia",
                                Latitude = lat,
                                Longitude = lon,
                                IsCustomCity = false
                            };
                            SaveLocation(loc);
                            return loc;
                        }
                    }
                }
            }
            catch { }

            // 3. Timezone-based fallback if offline
            double offsetHours = TimeZoneInfo.Local.BaseUtcOffset.TotalHours;
            if (Math.Abs(offsetHours - 8.0) < 0.5)
            {
                return new PrayerLocationSettings { City = "Denpasar / Makassar", Country = "Indonesia", Latitude = -8.6500, Longitude = 115.2167 };
            }
            if (Math.Abs(offsetHours - 9.0) < 0.5)
            {
                return new PrayerLocationSettings { City = "Jayapura", Country = "Indonesia", Latitude = -2.5337, Longitude = 140.7181 };
            }
            return new PrayerLocationSettings { City = "Jakarta", Country = "Indonesia", Latitude = -6.2088, Longitude = 106.8456 };
        }

        /// <summary>
        /// Resolves a city query (e.g. "Surabaya", "Bandung", "Yogyakarta") into coordinates using Open-Meteo Geocoding API.
        /// </summary>
        public static async Task<PrayerLocationSettings?> SearchCityAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string cleanQuery = query.Trim();

            try
            {
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cleanQuery)}&count=1&language=id&format=json";
                using var resp = await Http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array &&
                        results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        string name = first.TryGetProperty("name", out var n) ? (n.GetString() ?? cleanQuery) : cleanQuery;
                        string country = first.TryGetProperty("country", out var c) ? (c.GetString() ?? "Indonesia") : "Indonesia";
                        double lat = first.TryGetProperty("latitude", out var la) ? la.GetDouble() : -6.2088;
                        double lon = first.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : 106.8456;

                        var loc = new PrayerLocationSettings
                        {
                            City = name,
                            Country = country,
                            Latitude = lat,
                            Longitude = lon,
                            IsCustomCity = true
                        };
                        SaveLocation(loc);
                        return loc;
                    }
                }
            }
            catch { }

            // If Open-Meteo geocoding fails, still save city name so Aladhan timingsByCity can resolve it
            var fallbackLoc = new PrayerLocationSettings
            {
                City = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanQuery.ToLowerInvariant()),
                Country = "Indonesia",
                Latitude = 0,
                Longitude = 0,
                IsCustomCity = true
            };
            SaveLocation(fallbackLoc);
            return fallbackLoc;
        }

        /// <summary>
        /// Fetches today's prayer schedule for the given location from Aladhan API (Method 20: Kemenag RI),
        /// with offline astronomical calculation fallback.
        /// </summary>
        public static async Task<DailyPrayerResult> FetchPrayerTimesAsync(PrayerLocationSettings location, DateTime date)
        {
            string dateStr = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

            try
            {
                string url;
                if (Math.Abs(location.Latitude) > 0.001 || Math.Abs(location.Longitude) > 0.001)
                {
                    url = string.Create(CultureInfo.InvariantCulture,
                        $"https://api.aladhan.com/v1/timings/{dateStr}?latitude={location.Latitude:F4}&longitude={location.Longitude:F4}&method=20");
                }
                else
                {
                    url = $"https://api.aladhan.com/v1/timingsByCity/{dateStr}?city={Uri.EscapeDataString(location.City)}&country={Uri.EscapeDataString(location.Country)}&method=20";
                }

                using var resp = await Http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        var timings = data.GetProperty("timings");
                        string hijriStr = FormatHijriDate(data);

                        var entries = new List<PrayerTimeEntry>
                        {
                            CreateEntry("Imsak", ExtractTime(timings, "Imsak"), isObligatory: false),
                            CreateEntry("Subuh", ExtractTime(timings, "Fajr"), isObligatory: true),
                            CreateEntry("Terbit", ExtractTime(timings, "Sunrise"), isObligatory: false),
                            CreateEntry("Dzuhur", ExtractTime(timings, "Dhuhr"), isObligatory: true),
                            CreateEntry("Ashar", ExtractTime(timings, "Asr"), isObligatory: true),
                            CreateEntry("Maghrib", ExtractTime(timings, "Maghrib"), isObligatory: true),
                            CreateEntry("Isya", ExtractTime(timings, "Isha"), isObligatory: true)
                        };

                        return new DailyPrayerResult
                        {
                            City = location.City,
                            Country = location.Country,
                            HijriDate = hijriStr,
                            Items = entries
                        };
                    }
                }
            }
            catch { }

            // Offline Astronomical Calculation Fallback
            return CalculateOfflinePrayerTimes(location, date);
        }

        private static string ExtractTime(JsonElement timings, string key)
        {
            if (timings.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                string raw = prop.GetString() ?? "00:00";
                int spaceIdx = raw.IndexOf(' ');
                if (spaceIdx > 0) raw = raw.Substring(0, spaceIdx);
                return raw.Trim();
            }
            return "00:00";
        }

        private static string FormatHijriDate(JsonElement data)
        {
            try
            {
                if (data.TryGetProperty("date", out var d) && d.TryGetProperty("hijri", out var h))
                {
                    string day = h.TryGetProperty("day", out var dy) ? (dy.GetString() ?? "") : "";
                    string month = h.TryGetProperty("month", out var m) && m.TryGetProperty("en", out var men) ? (men.GetString() ?? "") : "";
                    string year = h.TryGetProperty("year", out var yr) ? (yr.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(day) && !string.IsNullOrWhiteSpace(month))
                    {
                        return $"{day} {month} {year} H";
                    }
                }
            }
            catch { }

            return FormatLocalHijriDate(DateTime.Today);
        }

        public static string FormatLocalHijriDate(DateTime date)
        {
            try
            {
                var hijri = new HijriCalendar();
                int day = hijri.GetDayOfMonth(date);
                int month = hijri.GetMonth(date);
                int year = hijri.GetYear(date);
                string[] months =
                {
                    "Muharram", "Safar", "Rabiul Awal", "Rabiul Akhir",
                    "Jumadil Awal", "Jumadil Akhir", "Rajab", "Sya'ban",
                    "Ramadhan", "Syawal", "Dzulqa'dah", "Dzulhijjah"
                };
                string mName = (month >= 1 && month <= 12) ? months[month - 1] : "";
                return $"{day} {mName} {year} H";
            }
            catch
            {
                return date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            }
        }

        private static PrayerTimeEntry CreateEntry(string name, string timeStr, bool isObligatory)
        {
            TimeSpan parsed = TimeSpan.Zero;
            var parts = timeStr.Split(':');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int h) &&
                int.TryParse(parts[1], out int m))
            {
                parsed = new TimeSpan(Math.Clamp(h, 0, 23), Math.Clamp(m, 0, 59), 0);
            }

            return new PrayerTimeEntry
            {
                Name = name,
                TimeString = $"{parsed.Hours:D2}:{parsed.Minutes:D2}",
                TimeOfDay = parsed,
                IsObligatory = isObligatory
            };
        }

        private static DailyPrayerResult CalculateOfflinePrayerTimes(PrayerLocationSettings location, DateTime date)
        {
            double lat = Math.Abs(location.Latitude) > 0.001 ? location.Latitude : -6.2088;
            double lon = Math.Abs(location.Longitude) > 0.001 ? location.Longitude : 106.8456;
            double tz = TimeZoneInfo.Local.BaseUtcOffset.TotalHours;

            int dayOfYear = date.DayOfYear;
            double gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1);

            // Equation of time (in hours) & solar declination (in radians)
            double eqTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
                            - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma)) / 60.0;
            double decl = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
                          - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma)
                          - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);

            double latRad = lat * Math.PI / 180.0;

            double HourAngleForAltitude(double deg)
            {
                double altRad = deg * Math.PI / 180.0;
                double cosH = (Math.Sin(altRad) - Math.Sin(latRad) * Math.Sin(decl)) / (Math.Cos(latRad) * Math.Cos(decl));
                cosH = Math.Clamp(cosH, -1.0, 1.0);
                return Math.Acos(cosH) * (180.0 / Math.PI) / 15.0;
            }

            double dhuhr = 12.0 + tz - (lon / 15.0) - eqTime + (2.0 / 60.0); // +2m ihtiyati
            double sunrise = dhuhr - HourAngleForAltitude(-0.833) - (2.0 / 60.0);
            double maghrib = dhuhr + HourAngleForAltitude(-0.833) + (2.0 / 60.0);
            double fajr = dhuhr - HourAngleForAltitude(-20.0); // Kemenag 20 deg
            double imsak = fajr - (10.0 / 60.0);
            double isha = dhuhr + HourAngleForAltitude(-18.0); // Kemenag 18 deg

            // Asr (Shafi'i shadow factor = 1)
            double asrAlt = Math.Atan(1.0 / (1.0 + Math.Tan(Math.Abs(latRad - decl)))) * (180.0 / Math.PI);
            double asr = dhuhr + HourAngleForAltitude(asrAlt);

            static string ToHm(double hours)
            {
                hours = ((hours % 24.0) + 24.0) % 24.0;
                int h = (int)Math.Floor(hours);
                int m = (int)Math.Round((hours - h) * 60.0);
                if (m >= 60) { h = (h + 1) % 24; m -= 60; }
                return $"{h:D2}:{m:D2}";
            }

            return new DailyPrayerResult
            {
                City = string.IsNullOrWhiteSpace(location.City) ? "Lokasi Lokal" : location.City,
                Country = location.Country,
                HijriDate = FormatLocalHijriDate(date),
                Items = new List<PrayerTimeEntry>
                {
                    CreateEntry("Imsak", ToHm(imsak), false),
                    CreateEntry("Subuh", ToHm(fajr), true),
                    CreateEntry("Terbit", ToHm(sunrise), false),
                    CreateEntry("Dzuhur", ToHm(dhuhr), true),
                    CreateEntry("Ashar", ToHm(asr), true),
                    CreateEntry("Maghrib", ToHm(maghrib), true),
                    CreateEntry("Isya", ToHm(isha), true)
                }
            };
        }
    }
}

