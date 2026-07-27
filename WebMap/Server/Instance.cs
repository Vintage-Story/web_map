using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WebMap.Server;

/// <summary>
/// Server-side wiring hub: builds the tile renderer's color table, loads the
/// known-chunks tracker, starts the HTTP API, and keeps everything in sync
/// with world events (chunks loading, blocks changing, autosave).
/// </summary>
public class Instance
{
    internal static ICoreServerAPI api = null!;

    private readonly KnownChunkTracker tracker = new();
    private readonly MapTileRenderer renderer = new();
    private WebMapHttpServer? httpServer;

    internal void Init(ICoreServerAPI serverAPI)
    {
        api = serverAPI;

        Configuration.Load(api);

        renderer.BuildBlockColorTable(api);
        tracker.Load(api);

        httpServer = new WebMapHttpServer(api, tracker, renderer);

        api.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        api.Event.GameWorldSave += OnGameWorldSave;
        api.Event.DidPlaceBlock += OnDidPlaceBlock;
        api.Event.DidBreakBlock += OnDidBreakBlock;

        httpServer.Start();
    }

    private void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
    {
        if (tracker.MarkLoaded(chunkCoord.X, chunkCoord.Y))
        {
            httpServer?.InvalidateTile(chunkCoord.X, chunkCoord.Y);
        }
    }

    private void OnGameWorldSave()
    {
        tracker.Save(api);
    }

    private void OnDidPlaceBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
    {
        InvalidateTileAt(blockSel.Position);
    }

    private void OnDidBreakBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel)
    {
        InvalidateTileAt(blockSel.Position);
    }

    private void InvalidateTileAt(BlockPos pos)
    {
        httpServer?.InvalidateTile(pos.X / 32, pos.Z / 32);
    }

    internal void Dispose()
    {
        if (api != null)
        {
            api.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
            api.Event.GameWorldSave -= OnGameWorldSave;
            api.Event.DidPlaceBlock -= OnDidPlaceBlock;
            api.Event.DidBreakBlock -= OnDidBreakBlock;
            tracker.Save(api);
        }
        httpServer?.Dispose();
        api = null!;
    }
}
