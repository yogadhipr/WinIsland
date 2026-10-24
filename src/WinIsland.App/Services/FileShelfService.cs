using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland.App.Services
{
    public class FileShelfItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileSizeFormatted { get; set; } = string.Empty;
        public ImageSource? Icon { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }

    public class FileShelfService
    {
        private static readonly Lazy<FileShelfService> _instance = new(() => new FileShelfService());
        public static FileShelfService Instance => _instance.Value;

        public ObservableCollection<FileShelfItem> Items { get; } = new ObservableCollection<FileShelfItem>();

        public event Action? ItemsChanged;

        public void AddFiles(string[] paths)
        {
            if (paths == null) return;

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                // Avoid duplicate items
                bool exists = false;
                foreach (var existing in Items)
                {
                    if (string.Equals(existing.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists) continue;

                try
                {
                    bool isDir = Directory.Exists(path);
                    bool isFile = File.Exists(path);

                    if (!isDir && !isFile) continue;

                    string name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) name = path;

                    string sizeText = "";
                    if (isFile)
                    {
                        long bytes = new FileInfo(path).Length;
                        sizeText = FormatBytes(bytes);
                    }
                    else
                    {
                        sizeText = "Folder";
                    }

                    ImageSource? iconSource = GetFileIcon(path, isDir);

                    var item = new FileShelfItem
                    {
                        FilePath = path,
                        FileName = name,
                        FileSizeFormatted = sizeText,
                        IsDirectory = isDir,
                        Icon = iconSource
                    };

                    Items.Add(item);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FileShelfService] AddFile error: {ex.Message}");
                }
            }

            ItemsChanged?.Invoke();
        }

        public void RemoveItem(FileShelfItem item)
        {
            if (Items.Remove(item))
            {
                ItemsChanged?.Invoke();
            }
        }

        public void Clear()
        {
            Items.Clear();
            ItemsChanged?.Invoke();
        }

        public void StartDragOut(FrameworkElement source, FileShelfItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;

            var dataObj = new DataObject(DataFormats.FileDrop, new string[] { item.FilePath });
            DragDrop.DoDragDrop(source, dataObj, DragDropEffects.Copy | DragDropEffects.Move);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static ImageSource? GetFileIcon(string path, bool isDirectory)
        {
            try
            {
                var shinfo = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

                IntPtr res = SHGetFileInfo(path, attributes, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
                if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    var imgSource = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    imgSource.Freeze();
                    DestroyIcon(shinfo.hIcon);
                    return imgSource;
                }
            }
            catch
            {
                // Fallback to null
            }
            return null;
        }
    }
}
