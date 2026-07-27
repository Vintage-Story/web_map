using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WebMap.Server;

/// <summary>
/// Minimal self-hosted HTTP API (System.Net.HttpListener, no external web
/// framework needed) exposing world/chunk/tile data for an external (e.g.
/// React) frontend to consume.
/// </summary>
public class WebMapHttpServer : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly KnownChunkTracker tracker;
    private readonly MapTileRenderer renderer;
    private readonly ConcurrentDictionary<(int X, int Z), byte[]> tileCache = new();

    private HttpListener? listener;
    private CancellationTokenSource? cts;

    public WebMapHttpServer(ICoreServerAPI api, KnownChunkTracker tracker, MapTileRenderer renderer)
    {
        this.api = api;
        this.tracker = tracker;
        this.renderer = renderer;
    }

    public void Start()
    {
        if (!TryStart(Configuration.Host))
        {
            if (!string.Equals(Configuration.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarn($"Falling back to http://localhost:{Configuration.Port}/ - to bind {Configuration.Host} you may need to run as administrator or register a URL ACL reservation (netsh http add urlacl url=http://+:{Configuration.Port}/ user=Everyone)");
                TryStart("localhost");
            }
        }
    }

    private bool TryStart(string host)
    {
        try
        {
            listener?.Close();
            var newListener = new HttpListener();
            newListener.Prefixes.Add($"http://{host}:{Configuration.Port}/");
            newListener.Start();
            listener = newListener;
            cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(newListener, cts.Token));
            Debug.Log($"HTTP API listening on http://{host}:{Configuration.Port}/");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarn($"Could not start HTTP API on {host}:{Configuration.Port}: {e.Message}");
            return false;
        }
    }

    public void Stop()
    {
        cts?.Cancel();
        try { listener?.Stop(); } catch { /* already stopped */ }
    }

    public void Dispose()
    {
        Stop();
        listener?.Close();
        tileCache.Clear();
    }

    public void InvalidateTile(int cx, int cz)
    {
        tileCache.TryRemove((cx, cz), out _);
    }

    private async Task AcceptLoop(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => HandleRequestSafe(ctx), token);
        }
    }

    private async Task HandleRequestSafe(HttpListenerContext ctx)
    {
        try
        {
            await HandleRequest(ctx);
        }
        catch (Exception e)
        {
            Debug.LogWarn($"Request handler error: {e}");
            try
            {
                await WriteJson(ctx.Response, 500, new { error = "internal error" });
            }
            catch { /* connection likely already gone */ }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";

        if (path == "/api/info")
        {
            await HandleInfo(ctx);
            return;
        }
        if (path == "/api/chunks")
        {
            await HandleChunks(ctx);
            return;
        }
        if (path.StartsWith("/api/tile/") && path.EndsWith(".png"))
        {
            await HandleTile(ctx, path);
            return;
        }

        await WriteJson(ctx.Response, 404, new { error = "not found" });
    }

    private Task HandleInfo(HttpListenerContext ctx)
    {
        var wm = api.WorldManager;

        // DefaultSpawnPosition can throw internally on a brand new world that
        // no player has spawned into yet - don't let that take down the whole
        // endpoint, just omit it.
        int[]? defaultSpawn;
        try
        {
            defaultSpawn = wm.DefaultSpawnPosition;
        }
        catch
        {
            defaultSpawn = null;
        }

        var info = new
        {
            worldName = wm.SaveGame.WorldName,
            savegameIdentifier = wm.SaveGame.SavegameIdentifier,
            seed = wm.Seed,
            mapSizeX = wm.MapSizeX,
            mapSizeY = wm.MapSizeY,
            mapSizeZ = wm.MapSizeZ,
            chunkSize = wm.ChunkSize,
            regionSize = wm.RegionSize,
            defaultSpawn,
        };
        return WriteJson(ctx.Response, 200, info);
    }

    private Task HandleChunks(HttpListenerContext ctx)
    {
        var list = new List<object>();
        foreach (var (x, z) in tracker.GetAll())
        {
            list.Add(new { x, z });
        }
        return WriteJson(ctx.Response, 200, list);
    }

    private async Task HandleTile(HttpListenerContext ctx, string path)
    {
        string[] parts = path.Trim('/').Split('/');
        // parts: "api", "tile", "{cx}", "{cz}.png"
        if (parts.Length != 4 || !int.TryParse(parts[2], out int cx) || !int.TryParse(parts[3].Substring(0, parts[3].Length - 4), out int cz))
        {
            await WriteJson(ctx.Response, 400, new { error = "invalid tile coordinates, expected /api/tile/{cx}/{cz}.png" });
            return;
        }

        byte[]? png;
        try
        {
            png = await GetOrRenderTilePng(cx, cz);
        }
        catch (TimeoutException)
        {
            await WriteJson(ctx.Response, 504, new { error = "timed out loading chunk" });
            return;
        }

        if (png == null)
        {
            await WriteJson(ctx.Response, 404, new { error = "chunk not available" });
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "image/png";
        ctx.Response.ContentLength64 = png.Length;
        await ctx.Response.OutputStream.WriteAsync(png, 0, png.Length);
        ctx.Response.OutputStream.Close();
    }

    private async Task<byte[]?> GetOrRenderTilePng(int cx, int cz)
    {
        if (tileCache.TryGetValue((cx, cz), out byte[]? cached))
        {
            return cached;
        }

        int[]? pixels = await RunOnMainThread(() => renderer.RenderTile(api, cx, cz));

        if (pixels == null)
        {
            bool valid = await RunOnMainThread(() => api.World.BlockAccessor.IsValidPos(new BlockPos(cx * 32, 1, cz * 32, 0)));
            if (!valid) return null;

            var loadTask = RequestChunkLoad(cx, cz);
            var winner = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(10)));
            if (winner != loadTask)
            {
                throw new TimeoutException($"Timed out loading chunk column {cx}/{cz}");
            }

            pixels = await RunOnMainThread(() => renderer.RenderTile(api, cx, cz));
            if (pixels == null) return null;
        }

        byte[] png = MapTileRenderer.EncodePng(pixels, 32, 32);

        if (tileCache.Count >= Configuration.TileCacheMaxEntries)
        {
            tileCache.Clear();
        }
        tileCache[(cx, cz)] = png;
        return png;
    }

    private Task RequestChunkLoad(int cx, int cz)
    {
        var tcs = new TaskCompletionSource<bool>();
        api.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                api.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
                {
                    OnLoaded = () => tcs.TrySetResult(true)
                });
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        }, "webmap-load-chunk");
        return tcs.Task;
    }

    private Task<T> RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        api.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }, "webmap-render-tile");
        return tcs.Task;
    }

    private static async Task WriteJson(HttpListenerResponse response, int statusCode, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }
}
