using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace WinIsland.App.Services
{
    public sealed class ParsedToastNotification
    {
        public string UniqueKey { get; set; } = string.Empty;
        public long Order { get; set; }
        public uint Id { get; set; }
        public string AppName { get; set; } = "Windows";
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string AccentHex { get; set; } = "#0A84FF";
        public DateTime ArrivalTime { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Reads Windows 10/11 Toast Notifications from both the native WpnUserService SQLite database
    /// (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db via system winsqlite3.dll)
    /// and WinRT UserNotificationListener, extracting full XML text elements (including subgroups)
    /// for third-party apps like WhatsApp, Telegram, Discord, Browsers, and VS Code.
    /// </summary>
    public static class WindowsNotificationReaderService
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_OPEN_READONLY = 0x00000001;

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zvfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        private static readonly string WpnFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications");

        private static readonly string TempSnapshotFolder = Path.Combine(
            Path.GetTempPath(), "WinIsland_WpnSnapshot");

        /// <summary>
        /// Reads the latest toast notifications directly from Windows' wpndatabase.db (+ wal).
        /// </summary>
        public static List<ParsedToastNotification> ReadFromWpnDatabase(int limit = 30)
        {
            var results = new List<ParsedToastNotification>();
            string dbPath = Path.Combine(WpnFolder, "wpndatabase.db");
            if (!File.Exists(dbPath))
            {
                return results;
            }

            try
            {
                Directory.CreateDirectory(TempSnapshotFolder);
                string tempDbPath = Path.Combine(TempSnapshotFolder, "wpndatabase.db");
                string tempWalPath = Path.Combine(TempSnapshotFolder, "wpndatabase.db-wal");

                CopyFileShared(dbPath, tempDbPath);

                string walPath = Path.Combine(WpnFolder, "wpndatabase.db-wal");
                if (File.Exists(walPath))
                {
                    CopyFileShared(walPath, tempWalPath);
                }
                else if (File.Exists(tempWalPath))
                {
                    try { File.Delete(tempWalPath); } catch { }
                }

                IntPtr db = IntPtr.Zero;
                IntPtr stmt = IntPtr.Zero;
                try
                {
                    byte[] pathBytes = ToNullTerminatedUtf8(tempDbPath);
                    if (sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
                    {
                        return results;
                    }

                    string sql = $@"
                        SELECT n.[Order], n.Id, n.Payload, h.PrimaryId, n.ArrivalTime
                        FROM Notification n
                        LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                        WHERE n.Type = 'toast' AND n.Payload IS NOT NULL
                        ORDER BY n.[Order] DESC
                        LIMIT {limit};";

                    byte[] sqlBytes = ToNullTerminatedUtf8(sql);
                    if (sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out stmt, IntPtr.Zero) != SQLITE_OK)
                    {
                        return results;
                    }

                    while (sqlite3_step(stmt) == SQLITE_ROW)
                    {
                        long order = sqlite3_column_int64(stmt, 0);
                        uint id = (uint)sqlite3_column_int64(stmt, 1);
                        string payloadXml = ReadColumnUtf8(stmt, 2);
                        string handlerId = ReadColumnUtf8(stmt, 3);
                        long arrivalFileTime = sqlite3_column_int64(stmt, 4);

                        if (string.IsNullOrWhiteSpace(payloadXml)) continue;

                        var parsed = ParseToastXml(id, order, handlerId, payloadXml, arrivalFileTime);
                        if (parsed != null)
                        {
                            results.Add(parsed);
                        }
                    }
                }
                finally
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                    if (db != IntPtr.Zero) sqlite3_close(db);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsNotificationReaderService] ReadFromWpnDatabase error: {ex.Message}");
            }

            // Return oldest-to-newest so caller can process in chronological order
            results.Reverse();
            return results;
        }

        public static ParsedToastNotification? ParseToastXml(uint id, long order, string? handlerOrAppName, string xmlPayload, long arrivalFileTime = 0)
        {
            try
            {
                var (appName, accentHex) = ResolveAppIdentity(handlerOrAppName);

                var doc = XDocument.Parse(xmlPayload);
                var binding = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "binding");
                if (binding == null) return null;

                // 1. Primary <text> elements directly under <binding> (not inside <group>/<subgroup>)
                var primaryTexts = new List<string>();
                // 2. Secondary <text> elements inside <group>/<subgroup> (e.g. unread count or multi-line chat history)
                var subgroupTexts = new List<string>();
                // 3. Attribution text (if any)
                var attributionTexts = new List<string>();

                foreach (var textNode in binding.Descendants().Where(e => e.Name.LocalName == "text"))
                {
                    string val = CleanUnicodeControlChars(textNode.Value);
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    string? placement = textNode.Attribute("placement")?.Value;
                    bool inSubgroup = textNode.Ancestors().Any(a => a.Name.LocalName == "subgroup" || a.Name.LocalName == "group");

                    if (string.Equals(placement, "attribution", StringComparison.OrdinalIgnoreCase))
                    {
                        attributionTexts.Add(val);
                    }
                    else if (inSubgroup)
                    {
                        subgroupTexts.Add(val);
                    }
                    else
                    {
                        primaryTexts.Add(val);
                    }
                }

                if (primaryTexts.Count == 0 && subgroupTexts.Count == 0)
                {
                    return null;
                }

                string title;
                var bodyParts = new List<string>();

                if (primaryTexts.Count > 0)
                {
                    title = primaryTexts[0];
                    if (primaryTexts.Count > 1)
                    {
                        bodyParts.AddRange(primaryTexts.Skip(1));
                    }
                }
                else
                {
                    title = subgroupTexts[0];
                    if (subgroupTexts.Count > 1)
                    {
                        bodyParts.AddRange(subgroupTexts.Skip(1));
                    }
                    subgroupTexts.Clear();
                }

                foreach (var sub in subgroupTexts)
                {
                    if (!bodyParts.Contains(sub, StringComparer.OrdinalIgnoreCase))
                    {
                        bodyParts.Add(sub);
                    }
                }

                foreach (var attr in attributionTexts)
                {
                    if (!bodyParts.Contains(attr, StringComparer.OrdinalIgnoreCase))
                    {
                        bodyParts.Add(attr);
                    }
                }

                string rawBody = bodyParts.Count > 0 ? string.Join(" • ", bodyParts) : title;
                string normalizedBody = NormalizeNotificationText(appName, title, rawBody);

                DateTime arrival = DateTime.Now;
                if (arrivalFileTime > 0)
                {
                    try
                    {
                        arrival = DateTime.FromFileTime(arrivalFileTime);
                    }
                    catch { }
                }

                return new ParsedToastNotification
                {
                    UniqueKey = $"{id}|{appName}|{title}|{rawBody}",
                    Order = order,
                    Id = id,
                    AppName = appName,
                    Title = title,
                    Body = normalizedBody,
                    AccentHex = accentHex,
                    ArrivalTime = arrival
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsNotificationReaderService] ParseToastXml error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cleans invisible directional marks (\u200E, \u200F, \u202F) from notification text.
        /// </summary>
        public static string NormalizeNotificationText(string appName, string title, string rawMessage)
        {
            string cleaned = CleanUnicodeControlChars(rawMessage);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return CleanUnicodeControlChars(title);
            }

            // Strip any previously appended preview hint if present in saved notifications.json
            int dashHintIdx = cleaned.IndexOf("— Aktifkan 'Show preview", StringComparison.OrdinalIgnoreCase);
            if (dashHintIdx >= 0)
            {
                cleaned = cleaned.Substring(0, dashHintIdx).Trim();
            }

            int parenHintIdx = cleaned.IndexOf("(Isi pesan disembunyikan oleh pengaturan privasi WhatsApp", StringComparison.OrdinalIgnoreCase);
            if (parenHintIdx >= 0)
            {
                cleaned = cleaned.Substring(0, parenHintIdx).Trim();
            }

            int parenHintIdx2 = cleaned.IndexOf("(Aktifkan 'Show preview", StringComparison.OrdinalIgnoreCase);
            if (parenHintIdx2 >= 0)
            {
                cleaned = cleaned.Substring(0, parenHintIdx2).Trim();
            }

            return cleaned;
        }

        public static string CleanUnicodeControlChars(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                // Strip LTR/RTL directional formatting marks (\u200E, \u200F, \u202A-\u202E, \u2066-\u2069)
                if (c == '\u200E' || c == '\u200F' || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069'))
                {
                    continue;
                }
                // Replace narrow no-break space (\u202F) and non-breaking space (\u00A0) with standard space
                if (c == '\u202F' || c == '\u00A0')
                {
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        public static (string AppName, string AccentHex) ResolveAppIdentity(string? rawHandlerOrName)
        {
            if (string.IsNullOrWhiteSpace(rawHandlerOrName))
            {
                return ("Windows", "#0A84FF");
            }

            string h = rawHandlerOrName.Trim();

            if (h.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                return ("WhatsApp", "#25D366");
            if (h.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
                return ("Telegram", "#24A1DE");
            if (h.Contains("Discord", StringComparison.OrdinalIgnoreCase))
                return ("Discord", "#5865F2");
            if (h.Contains("Instagram", StringComparison.OrdinalIgnoreCase))
                return ("Instagram", "#E1306C");
            if (h.Contains("Slack", StringComparison.OrdinalIgnoreCase))
                return ("Slack", "#E01E5A");
            if (h.Contains("Teams", StringComparison.OrdinalIgnoreCase))
                return ("Microsoft Teams", "#6264A7");
            if (h.Contains("VisualStudioCode", StringComparison.OrdinalIgnoreCase) || h.Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase))
                return ("VS Code", "#007ACC");
            if (h.Contains("ScreenSketch", StringComparison.OrdinalIgnoreCase) || h.Contains("Snipping", StringComparison.OrdinalIgnoreCase))
                return ("Snipping Tool", "#FF9F0A");
            if (h.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                return ("Spotify", "#1DB954");
            if (h.Contains("Outlook", StringComparison.OrdinalIgnoreCase) || h.Contains("windowscommunicationsapps", StringComparison.OrdinalIgnoreCase))
                return ("Outlook Mail", "#0078D4");
            if (h.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                return ("Google Chrome", "#4285F4");
            if (h.Contains("MSEdge", StringComparison.OrdinalIgnoreCase) || h.Contains("Microsoft.MicrosoftEdge", StringComparison.OrdinalIgnoreCase))
                return ("Microsoft Edge", "#0078D7");
            if (h.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
                return ("Firefox", "#FF7139");
            if (h.Contains("ArmouryCrate", StringComparison.OrdinalIgnoreCase) || h.Contains("Armoury Crate", StringComparison.OrdinalIgnoreCase))
                return ("Armoury Crate", "#FF2D55");
            if (h.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                return ("Realtek Audio", "#0A84FF");
            if (h.Contains("immersivecontrolpanel", StringComparison.OrdinalIgnoreCase) || h.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                return ("Windows Settings", "#0A84FF");

            // If it's a UWP AppUserModelId like "Publisher.AppName_hash!App", extract "AppName"
            int bangIdx = h.IndexOf('!');
            if (bangIdx > 0)
            {
                h = h.Substring(0, bangIdx);
            }
            int underscoreIdx = h.IndexOf('_');
            if (underscoreIdx > 0)
            {
                h = h.Substring(0, underscoreIdx);
            }
            int dotIdx = h.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < h.Length - 1)
            {
                h = h.Substring(dotIdx + 1);
            }

            return (string.IsNullOrWhiteSpace(h) ? "Windows" : h, "#0A84FF");
        }

        private static void CopyFileShared(string sourcePath, string destPath)
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            sourceStream.CopyTo(destStream);
        }

        private static byte[] ToNullTerminatedUtf8(string text)
        {
            int byteCount = Encoding.UTF8.GetByteCount(text);
            byte[] bytes = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
            bytes[byteCount] = 0;
            return bytes;
        }

        private static string ReadColumnUtf8(IntPtr stmt, int col)
        {
            IntPtr ptr = sqlite3_column_blob(stmt, col);
            int len = sqlite3_column_bytes(stmt, col);
            if (ptr == IntPtr.Zero || len <= 0) return string.Empty;

            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }
}
