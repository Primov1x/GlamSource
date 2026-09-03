using GlamSource.Core;

namespace Tests;

public class ItemImageServiceCacheTests
{
    [Fact]
    public void Evicts_oldest_files_first_until_under_80_percent_of_budget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GlamSourceCacheTest_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // 3 files, 40 bytes each (120 total), budget 100 -> must evict down to <= 80
            var names = new[] { "1.img", "2.img", "3.img" };
            foreach (var name in names)
            {
                File.WriteAllBytes(Path.Combine(dir, name), new byte[40]);
                Thread.Sleep(10); // distinct LastWriteTimeUtc so "oldest" is unambiguous
            }

            ItemImageService.EvictOldestIfOverBudget(dir, maxBytes: 100);

            var remaining = Directory.GetFiles(dir, "*.img").Select(Path.GetFileName).ToHashSet();
            Assert.DoesNotContain("1.img", remaining); // oldest, must go
            Assert.Contains("3.img", remaining); // newest, must survive
            Assert.True(remaining.Sum(n => new FileInfo(Path.Combine(dir, n!)).Length) <= 100);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Under_budget_deletes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GlamSourceCacheTest_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "1.img"), new byte[10]);
            ItemImageService.EvictOldestIfOverBudget(dir, maxBytes: 100);
            Assert.Single(Directory.GetFiles(dir, "*.img"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
