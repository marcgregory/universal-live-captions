using System.IO;
using System.Text.Json;

namespace UniversalCaptions.App.Settings;

/// <summary>
/// File-backed <see cref="ISettingsStore"/> (TD-005) persisting user preferences to
/// <c>%LocalAppData%\UniversalCaptions\settings.json</c> (per-user, no elevation, no in-repo data).
/// Loads are tolerant: a missing, malformed, or wrong-typed file yields all-default settings and never
/// fails startup. Saves are atomic: the JSON is written to a sibling <c>.tmp</c> file and then moved
/// over the target, so a crash or failed write never leaves a partial settings file and the last good
/// file survives. A lock serializes writes so concurrent/rapid saves cannot tear the file.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private const string FileName = "settings.json";
    private const string TempExtension = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // System.Text.Json ignores unknown properties by default, so a settings file written by a
        // newer app version loads into an older one without error or data loss (forward compatible).
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePath;

    /// <summary>
    /// Creates a store rooted at <c>%LocalAppData%\UniversalCaptions</c>.
    /// </summary>
    public SettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniversalCaptions"))
    {
    }

    /// <summary>
    /// Creates a store rooted at the given directory (test seam).
    /// </summary>
    /// <param name="directory">The directory that will contain <c>settings.json</c>. Must not be null.</param>
    public SettingsStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _filePath = Path.Combine(directory, FileName);
    }

    /// <inheritdoc />
    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new UserSettings();
            }

            string json = File.ReadAllText(_filePath);
            UserSettings? settings = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);
            return settings ?? new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing/corrupt/unreadable settings file must never break startup (TD-005): fall back
            // to built-in defaults and leave the file untouched for the user to inspect.
            return new UserSettings();
        }
    }

    /// <inheritdoc />
    public void Save(UserSettings settings)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string tempPath = _filePath + TempExtension;
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed write must never crash the app (TD-005): the last good file is preserved
                // and the next Save retries.
            }
        }
    }
}
