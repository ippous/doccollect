using DocCollect.Core.Storage;

namespace DocCollect.Core.Tests;

public class StorageTests
{
    [Fact]
    public async Task Local_storage_write_read_delete()
    {
        using var dir = new TempDir();
        var s = new LocalFileSystemStorage(dir.Path);
        await s.WriteAsync("a/b/c.bin", new MemoryStream([1, 2, 3]));
        Assert.True(await s.ExistsAsync("a/b/c.bin"));
        await using (var r = await s.OpenReadAsync("a/b/c.bin"))
        {
            var ms = new MemoryStream();
            await r.CopyToAsync(ms);
            Assert.Equal(new byte[] { 1, 2, 3 }, ms.ToArray());
        }
        await s.DeleteAsync("a/b/c.bin");
        Assert.False(await s.ExistsAsync("a/b/c.bin"));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("")]
    public void Keys_with_traversal_are_rejected(string key)
    {
        Assert.Throws<StorageException>(() => StorageKeys.Normalize(key));
    }

    [Fact]
    public void Keys_are_normalized()
    {
        Assert.Equal("a/b/c", StorageKeys.Normalize(@"\a\\b/c/"));
        Assert.Equal("root/x/y", StorageKeys.Combine("/root/", "x/y"));
        Assert.Equal("x", StorageKeys.Combine(null, "x"));
    }

    [Fact]
    public void Document_key_hides_original_file_name()
    {
        var dossier = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var key = StorageKeys.ForDocument("Étude Martin & Fils", dossier, doc, TestData.Now);
        Assert.Equal($"tude-martin---fils/2026/{dossier:N}/{doc:N}.bin", key);
    }

    [Fact]
    public async Task Storage_tester_reports_success()
    {
        using var dir = new TempDir();
        var result = await new LocalFileSystemStorage(dir.Path).TestAsync();
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Registry_creates_providers_and_validates_required_settings()
    {
        using var dir = new TempDir();
        var registry = new StorageProviderRegistry([new LocalFileSystemStorageProvider()]);
        Assert.Single(registry.Describe());
        var storage = registry.Create("LOCAL", new StorageSettings(new Dictionary<string, string?> { ["RootPath"] = dir.Path }));
        Assert.Equal("local", storage.ProviderType);
        var ex = Assert.Throws<StorageException>(() => registry.Create("local", new StorageSettings()));
        Assert.Contains("Répertoire racine", ex.Message);
        Assert.Throws<StorageException>(() => registry.Get("inconnu"));
    }

    [Fact]
    public void Registry_rejects_duplicate_types()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new StorageProviderRegistry([new LocalFileSystemStorageProvider(), new LocalFileSystemStorageProvider()]));
    }

    [Fact]
    public void Settings_roundtrip_and_typed_access()
    {
        var s = new StorageSettings(new Dictionary<string, string?> { ["Port"] = "2222", ["UseTls"] = "true", ["Vide"] = " " });
        var back = StorageSettings.Deserialize(s.Serialize());
        Assert.Equal(2222, back.GetInt("port", 21));
        Assert.True(back.GetBool("UseTls", false));
        Assert.Null(back.Get("Vide"));
        Assert.Equal(21, back.GetInt("absent", 21));
    }
}
