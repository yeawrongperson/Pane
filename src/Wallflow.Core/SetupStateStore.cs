using System.Text.Json;

namespace Wallflow.Core;

public sealed record SetupStateLoadResult(
    PaneSetupState State,
    bool WasMigrated,
    bool CanSave,
    Exception? Failure);

public sealed class SetupStateStore(
    string setupStatePath,
    string legacyProfilesPath,
    string legacySetupNamePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<SetupStateLoadResult> LoadOrCreateAsync(CancellationToken token = default)
    {
        if (File.Exists(setupStatePath))
        {
            try
            {
                var (state, wasMigrated) = await ReadStateAsync(token).ConfigureAwait(false);
                return new(state, wasMigrated, true, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsStateReadFailure(ex))
            {
                return new(SetupManager.CreateInitialState(), false, false,
                    new InvalidDataException("Pane could not read setups.json. The existing file was left untouched.", ex));
            }
        }

        var legacyExists = File.Exists(legacyProfilesPath) || File.Exists(legacySetupNamePath);
        try
        {
            var profiles = File.Exists(legacyProfilesPath)
                ? await new ProfileStore(legacyProfilesPath).LoadAsync(token).ConfigureAwait(false)
                : [];
            var name = await ReadLegacyNameAsync(token).ConfigureAwait(false);
            var state = SetupManager.CreateInitialState(name, profiles);
            await SaveAsync(state, token).ConfigureAwait(false);
            return new(state, legacyExists, true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsStateReadFailure(ex))
        {
            return new(SetupManager.CreateInitialState(), false, false,
                new InvalidDataException("Pane could not migrate the existing setup. Legacy files were left untouched.", ex));
        }
    }

    public async Task SaveAsync(PaneSetupState state, CancellationToken token = default)
    {
        SetupStateValidator.Validate(state);
        var directory = Path.GetDirectoryName(setupStatePath)
            ?? throw new InvalidOperationException("The setup state path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = setupStatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporaryPath, setupStatePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<(PaneSetupState State, bool WasMigrated)> ReadStateAsync(CancellationToken token)
    {
        await using var stream = new FileStream(
            setupStatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync<PaneSetupState>(stream, JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Pane setup state was empty.");
        var wasMigrated = SetupStateMigration.UpgradeToCurrent(state);
        SetupStateValidator.Validate(state);
        return (state, wasMigrated);
    }

    private async Task<string?> ReadLegacyNameAsync(CancellationToken token)
    {
        if (!File.Exists(legacySetupNamePath)) return null;
        var name = (await File.ReadAllTextAsync(legacySetupNamePath, token).ConfigureAwait(false)).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static bool IsStateReadFailure(Exception exception)
        => exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException;
}

public static class SetupStateMigration
{
    public static bool UpgradeToCurrent(PaneSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion == PaneSetupState.CurrentSchemaVersion) return false;
        if (state.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported Pane setup schema version {state.SchemaVersion}.");

        state.MonitorAliases ??= [];
        state.MonitorVisualPreferences ??= [];
        state.SchemaVersion = PaneSetupState.CurrentSchemaVersion;
        return true;
    }
}
