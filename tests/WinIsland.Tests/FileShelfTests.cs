using System.IO;
using WinIsland.App.Services;
using Xunit;

namespace WinIsland.Tests
{
    public class FileShelfTests
    {
        [Fact]
        public void FileShelfService_AddFiles_IgnoresDuplicates_AndStoresValidFiles()
        {
            var service = new FileShelfService();

            // Create temp dummy files
            string tempFile1 = Path.GetTempFileName();
            string tempFile2 = Path.GetTempFileName();

            try
            {
                service.AddFiles(new[] { tempFile1, tempFile2 });
                Assert.Equal(2, service.Items.Count);

                // Add duplicate
                service.AddFiles(new[] { tempFile1 });
                Assert.Equal(2, service.Items.Count);

                // Remove one item
                service.RemoveItem(service.Items[0]);
                Assert.Single(service.Items);

                // Clear
                service.Clear();
                Assert.Empty(service.Items);
            }
            finally
            {
                if (File.Exists(tempFile1)) File.Delete(tempFile1);
                if (File.Exists(tempFile2)) File.Delete(tempFile2);
            }
        }
    }
}
