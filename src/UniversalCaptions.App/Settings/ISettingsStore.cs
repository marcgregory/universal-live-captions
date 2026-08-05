namespace UniversalCaptions.App.Settings;

/// <summary>
/// Persists <see cref="UserSettings"/> between app launches (TD-005). Injectable seam so the app's
/// settings behavior can be verified deterministically against a temp directory or fake;
/// <see cref="SettingsStore"/> is the file-backed implementation at
/// <c>%LocalAppData%\UniversalCaptions\settings.json</c>.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Loads the persisted settings, or returns all-default settings when no file exists or the file
    /// is unreadable/malformed. Never throws — a settings problem must not fail app startup.
    /// </summary>
    UserSettings Load();

    /// <summary>
    /// Writes the settings atomically (the last write wins). Never throws — a failed write preserves
    /// the last good file and the next save retries.
    /// </summary>
    void Save(UserSettings settings);
}
