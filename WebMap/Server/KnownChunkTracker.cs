using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Server;

namespace WebMap.Server;

/// <summary>
/// Tracks which chunk columns (x/z) have been loaded (from disk or freshly
/// generated) at some point since this mod started watching the world.
/// Persisted inside the savegame itself via ISaveGame.StoreData/GetData, so it
/// survives server restarts. Optionally bootstrapped once from the savegame's
/// own "mapchunk" SQLite table to pick up chunks that were already generated
/// before this mod was installed.
/// </summary>
public class KnownChunkTracker
{
    private const string DataKey = "webmap:knownchunks";

    // Matches Vintagestory.Common.Database.ChunkPos.ToChunkIndex()'s bit layout
    // for mapchunk rows, where Y and Dimension are always forced to 0.
    private const long ChunkCoordMask = 0x3FFFFF;
    private const int ChunkZShift = 27;

    private readonly ConcurrentDictionary<(int X, int Z), byte> known = new();

    public void Load(ICoreServerAPI api)
    {
        try
        {
            byte[] saved = api.WorldManager.SaveGame.GetData(DataKey);
            if (saved != null)
            {
                using var reader = new BinaryReader(new MemoryStream(saved));
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int x = reader.ReadInt32();
                    int z = reader.ReadInt32();
                    known[(x, z)] = 1;
                }
            }
            Debug.Log($"Loaded {known.Count} known chunk column(s) from savegame data");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed loading known chunk data, starting with an empty set: {e}");
        }

        if (Configuration.BootstrapFromSavegame)
        {
            BootstrapFromSavegame(api);
        }
    }

    private void BootstrapFromSavegame(ICoreServerAPI api)
    {
        try
        {
            string path = api.WorldManager.CurrentWorldFilepath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var connStringBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            int added = 0;
            using (var conn = new SqliteConnection(connStringBuilder.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT position FROM mapchunk";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    long position = rdr.GetInt64(0);
                    int x = (int)(position & ChunkCoordMask);
                    int z = (int)((position >> ChunkZShift) & ChunkCoordMask);
                    if (known.TryAdd((x, z), 1))
                    {
                        added++;
                    }
                }
            }
            Debug.Log($"Bootstrapped {added} additional chunk column(s) from savegame database");
        }
        catch (Exception e)
        {
            Debug.LogWarn($"Best-effort savegame bootstrap failed, continuing without it: {e}");
        }
    }

    public bool MarkLoaded(int cx, int cz)
    {
        return known.TryAdd((cx, cz), 1);
    }

    public bool IsKnown(int cx, int cz)
    {
        return known.ContainsKey((cx, cz));
    }

    public List<(int X, int Z)> GetAll()
    {
        return new List<(int X, int Z)>(known.Keys);
    }

    public void Save(ICoreServerAPI api)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                var snapshot = GetAll();
                writer.Write(snapshot.Count);
                foreach (var (x, z) in snapshot)
                {
                    writer.Write(x);
                    writer.Write(z);
                }
            }
            api.WorldManager.SaveGame.StoreData(DataKey, stream.ToArray());
        }
        catch (Exception e)
        {
            Debug.LogWarn($"Failed saving known chunk data: {e}");
        }
    }
}
