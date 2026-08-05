using System.IO;
using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies <see cref="SettingsStore"/> persistence (TD-005): the six acceptance criteria — save/load
/// round-trip, missing file → defaults, malformed/wrong-type file → safe defaults, unknown/new fields
/// ignored (backward compatibility), atomic writes (failed write preserves the last good file), and
/// concurrent/rapid saves settling without torn state. All tests run against a unique temp directory
/// so they are deterministic and never touch the real %LocalAppData% settings file.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ulc_settings_" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _store = new SettingsStore(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Save_then_Load_round_trips_all_fields()
    {
        var original = new UserSettings
        {
            DeviceId = "device-123",
            Language = "ja",
            TranslationEnabled = true,
            TargetLanguage = "tl",
            Opacity = 0.8,
            FontSize = 24.0,
            ClickThrough = true,
            OverlayLeft = 120.0,
            OverlayTop = 340.0,
            OverlayExpanded = false,
        };

        _store.Save(original);

        Assert.Equal(original, _store.Load());
    }

    [Fact]
    public void Load_missing_file_returns_defaults_without_throwing()
    {
        UserSettings loaded = _store.Load();

        Assert.Equal(new UserSettings(), loaded);
    }

    [Fact]
    public void Load_malformed_or_wrong_type_file_returns_defaults_without_throwing()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);

        File.WriteAllText(path, "{ this is not valid JSON !!!");
        Assert.Equal(new UserSettings(), _store.Load());

        File.WriteAllText(path, @"{ ""Opacity"": ""not-a-number"", ""Version"": ""one"" }");
        Assert.Equal(new UserSettings(), _store.Load());
    }

    [Fact]
    public void Load_ignores_unknown_fields_and_keeps_known_fields()
    {
        string path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, @"{ ""Version"": 99, ""DeviceId"": ""saved-device"", ""FutureField"": 42, ""NewNested"": { ""x"": 1 } }");

        UserSettings loaded = _store.Load();

        Assert.Equal("saved-device", loaded.DeviceId);
        Assert.Equal(99, loaded.Version);
        Assert.Equal(new UserSettings() with { Version = 99, DeviceId = "saved-device" }, loaded);
    }

    [Fact]
    public void Save_writes_atomically_and_failed_save_preserves_last_good_file()
    {
        _store.Save(new UserSettings { DeviceId = "good" });

        Assert.True(File.Exists(Path.Combine(_directory, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "settings.json.tmp")));

        // Block the temp path with a directory so the next save's WriteAllText fails deterministically
        // and must preserve the last good file without throwing.
        Directory.CreateDirectory(Path.Combine(_directory, "settings.json.tmp"));

        _store.Save(new UserSettings { DeviceId = "bad" });

        Assert.Equal("good", _store.Load().DeviceId);
    }

    [Fact]
    public async Task Concurrent_and_rapid_saves_settle_without_torn_state()
    {
        string[] ids = Enumerable.Range(0, 25).Select(i => $"device-{i}").ToArray();
        Task[] saves = ids.Select(id => Task.Run(() => _store.Save(new UserSettings { DeviceId = id }))).ToArray();
        await Task.WhenAll(saves);

        // The serialized saves always land a complete, parseable file holding one of the written
        // values — never a torn/interleaved one.
        UserSettings loaded = _store.Load();
        Assert.Contains(loaded.DeviceId, ids);
        Assert.Equal(1, loaded.Version);

        // A final sequential save settles last-write-wins.
        _store.Save(new UserSettings { DeviceId = "last-wins" });
        Assert.Equal("last-wins", _store.Load().DeviceId);
    }
}
