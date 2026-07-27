using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace WebMap;

#pragma warning disable CA2211
public static class Configuration
{
    private static Dictionary<string, object> LoadConfigurationByDirectoryAndName(ICoreServerAPI api, string directory, string name)
    {
        string directoryPath = Path.Combine(api.DataBasePath, directory);
        string configPath = Path.Combine(api.DataBasePath, directory, $"{name}.json");
        Dictionary<string, object> defaultConfig = BuildDefaultConfig();
        Dictionary<string, object> loadedConfig;
        try
        {
            // Load server configurations
            string jsonConfig = File.ReadAllText(configPath);
            loadedConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonConfig) ?? defaultConfig;

            // Backfill keys missing from the user's file (e.g. added by a mod update) with their default value
            bool missingKeyAdded = false;
            foreach (var entry in defaultConfig)
            {
                if (loadedConfig.ContainsKey(entry.Key)) continue;

                Debug.Log($"WARNING: Configuration key '{entry.Key}' missing from {name}.json, adding it with its default value");
                loadedConfig[entry.Key] = entry.Value;
                missingKeyAdded = true;
            }

            if (missingKeyAdded)
            {
                try
                {
                    string mergedJson = JsonConvert.SerializeObject(loadedConfig, Formatting.Indented);
                    File.WriteAllText(configPath, mergedJson);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Cannot save updated configs to {configPath}, reason: {ex.Message}");
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            Debug.Log($"WARNING: Configuration directory does not exist, creating {name}.json and directory...");
            try
            {
                Directory.CreateDirectory(directoryPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Cannot create directory: {ex.Message}");
            }
            Debug.Log("Loading default configurations...");
            loadedConfig = defaultConfig;

            Debug.Log($"Configurations loaded, saving configs in: {configPath}");
            try
            {
                // Saving default configurations
                string defaultJson = JsonConvert.SerializeObject(loadedConfig, Formatting.Indented);
                File.WriteAllText(configPath, defaultJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Cannot save default files to {configPath}, reason: {ex.Message}");
            }
        }
        catch (FileNotFoundException)
        {
            Debug.Log($"WARNING: Configuration {name}.json cannot be found, recreating file from default");
            Debug.Log("Loading default configurations...");
            loadedConfig = defaultConfig;

            Debug.Log($"Configurations loaded, saving configs in: {configPath}");
            try
            {
                // Saving default configurations
                string defaultJson = JsonConvert.SerializeObject(loadedConfig, Formatting.Indented);
                File.WriteAllText(configPath, defaultJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Cannot save default files to {configPath}, reason: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Cannot read the configurations: {ex.Message}");
            Debug.Log("Loading default values...");
            loadedConfig = defaultConfig;
        }
        return loadedConfig;
    }

    /// <summary>
    /// Builds the default configuration dictionary from the field defaults below.
    /// Whole numbers are boxed as <see cref="long"/> to match what Newtonsoft.Json produces when
    /// deserializing a JSON file, since the values below are also used as a fallback config source.
    /// </summary>
    private static Dictionary<string, object> BuildDefaultConfig() => new()
    {
        ["Port"] = (long)Port,
        ["Host"] = Host,
        ["BootstrapFromSavegame"] = BootstrapFromSavegame,
        ["TileCacheMaxEntries"] = (long)TileCacheMaxEntries,
    };

    #region baseconfigs
    #region HTTP API
    /// <summary>
    /// TCP port the built-in HTTP API listens on.
    /// </summary>
    public static int Port = 42500;
    /// <summary>
    /// Host/interface the HTTP API binds to. "localhost" needs no elevated
    /// privileges; binding to all interfaces may require running as
    /// administrator or a URL ACL reservation on Windows.
    /// </summary>
    public static string Host = "localhost";
    #endregion
    #region Known chunks
    /// <summary>
    /// Whether to do a one-time best-effort scan of the savegame's own
    /// "mapchunk" SQLite table on startup, to pick up chunks that were
    /// generated before this mod was installed.
    /// </summary>
    public static bool BootstrapFromSavegame = true;
    #endregion
    #region Tile cache
    /// <summary>
    /// Maximum amount of rendered tile PNGs kept in memory before the cache
    /// is cleared.
    /// </summary>
    public static int TileCacheMaxEntries = 2000;
    #endregion

    private const string ConfigDir = "ModConfig/WebMap";
    private const string ConfigFile = "base";

    internal static void Load(ICoreServerAPI api)
    {
        Dictionary<string, object> baseConfigs = LoadConfigurationByDirectoryAndName(
            api,
            ConfigDir,
            ConfigFile
        );
        { //Port
            if (baseConfigs.TryGetValue("Port", out object? value))
                if (value is null) Debug.LogError("CONFIGURATION ERROR: Port is null");
                else if (value is not long) Debug.LogError($"CONFIGURATION ERROR: Port is not int is {value.GetType()}");
                else Port = (int)(long)value;
            else Debug.LogError("CONFIGURATION ERROR: Port not set");
        }
        { //Host
            if (baseConfigs.TryGetValue("Host", out object? value))
                if (value is null) Debug.LogError("CONFIGURATION ERROR: Host is null");
                else if (value is not string) Debug.LogError($"CONFIGURATION ERROR: Host is not string is {value.GetType()}");
                else Host = (string)value;
            else Debug.LogError("CONFIGURATION ERROR: Host not set");
        }
        { //BootstrapFromSavegame
            if (baseConfigs.TryGetValue("BootstrapFromSavegame", out object? value))
                if (value is null) Debug.LogError("CONFIGURATION ERROR: BootstrapFromSavegame is null");
                else if (value is not bool) Debug.LogError($"CONFIGURATION ERROR: BootstrapFromSavegame is not boolean is {value.GetType()}");
                else BootstrapFromSavegame = (bool)value;
            else Debug.LogError("CONFIGURATION ERROR: BootstrapFromSavegame not set");
        }
        { //TileCacheMaxEntries
            if (baseConfigs.TryGetValue("TileCacheMaxEntries", out object? value))
                if (value is null) Debug.LogError("CONFIGURATION ERROR: TileCacheMaxEntries is null");
                else if (value is not long) Debug.LogError($"CONFIGURATION ERROR: TileCacheMaxEntries is not int is {value.GetType()}");
                else TileCacheMaxEntries = (int)(long)value;
            else Debug.LogError("CONFIGURATION ERROR: TileCacheMaxEntries not set");
        }
    }
    #endregion
}
