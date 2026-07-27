using System;
using System.Collections.Generic;
using SkiaSharp;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace WebMap.Server;

/// <summary>
/// Server-side port of Vintagestory.GameContent.ChunkMapLayer's "fast" (non
/// color-accurate) tile generation algorithm, so we can render the same
/// looking terrain tiles the in-game World Map shows, without needing a
/// running client. Reimplemented against public API only: IMapChunk.RainHeightMap
/// for per-column terrain height and IWorldChunk.UnpackAndReadBlock for the
/// block at that height.
/// </summary>
public class MapTileRenderer
{
    private const int ChunkSize = 32;

    private static readonly Dictionary<EnumBlockMaterial, string> defaultMapColorCodes = new()
    {
        { EnumBlockMaterial.Soil, "land" },
        { EnumBlockMaterial.Sand, "desert" },
        { EnumBlockMaterial.Ore, "land" },
        { EnumBlockMaterial.Gravel, "desert" },
        { EnumBlockMaterial.Stone, "land" },
        { EnumBlockMaterial.Leaves, "forest" },
        { EnumBlockMaterial.Plant, "plant" },
        { EnumBlockMaterial.Wood, "forest" },
        { EnumBlockMaterial.Snow, "glacier" },
        { EnumBlockMaterial.Water, "lake" },
        { EnumBlockMaterial.Ice, "glacier" },
        { EnumBlockMaterial.Lava, "lava" },
    };

    private static readonly (string Code, string Hex)[] hexColorsByCode =
    {
        ("ink", "#483018"),
        ("settlement", "#856844"),
        ("wateredge", "#483018"),
        ("land", "#AC8858"),
        ("desert", "#C4A468"),
        ("forest", "#98844C"),
        ("road", "#805030"),
        ("plant", "#808650"),
        ("lake", "#CCC890"),
        ("lava", "#CCC890"),
        ("ocean", "#CCC890"),
        ("glacier", "#E0E0C0"),
        ("devastation", "#755c3c"),
    };

    private Dictionary<string, int> colorsByCode = new();
    private int[] blockColorArgb = Array.Empty<int>();
    private int wateredgeColorArgb;
    private bool built;

    /// <summary>
    /// Builds the block-id -> map color lookup table. Must be called once,
    /// after blocks are registered (ModSystem.AssetsFinalize or later).
    /// </summary>
    public void BuildBlockColorTable(ICoreServerAPI api)
    {
        // Note: unlike Vintagestory.GameContent.ChunkMapLayer, we do NOT call
        // ColorUtil.ReverseColorBytes here. That reversal exists in the game's
        // client code to feed its GPU texture upload pipeline; our pixel
        // buffer is copied directly into an SKBitmap(SKColorType.Bgra8888),
        // whose expected in-memory byte order (B,G,R,A) already matches
        // ColorUtil.Hex2Int's own bit layout (R at bits 16-23), so reversing
        // it here would swap the red/blue channels.
        colorsByCode = new Dictionary<string, int>();
        foreach (var (code, hex) in hexColorsByCode)
        {
            colorsByCode[code] = ColorUtil.Hex2Int(hex);
        }
        wateredgeColorArgb = colorsByCode["wateredge"];

        IList<Block> blocks = api.World.Blocks;
        blockColorArgb = new int[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            string? code = "land";
            if (block?.Attributes != null)
            {
                code = block.Attributes["mapColorCode"].AsString();
                if (code == null && !defaultMapColorCodes.TryGetValue(block.BlockMaterial, out code))
                {
                    code = "land";
                }
            }
            blockColorArgb[i] = code != null && colorsByCode.TryGetValue(code, out int argb) ? argb : colorsByCode["land"];
        }
        built = true;
    }

    private static bool IsLake(Block block)
    {
        if (block.BlockMaterial == EnumBlockMaterial.Water) return true;
        if (block.BlockMaterial == EnumBlockMaterial.Ice) return block.Code.Path != "glacierice";
        return false;
    }

    /// <summary>
    /// Renders a single 32x32 tile for the given chunk column. Must be called
    /// on the game's main thread (e.g. from within EnqueueMainThreadTask).
    /// Returns null if the map chunk or any of its vertical chunk slices are
    /// not currently loaded in memory - the caller should then try to load
    /// the column and call this again.
    /// </summary>
    public int[]? RenderTile(ICoreServerAPI api, int cx, int cz)
    {
        if (!built)
        {
            throw new InvalidOperationException("BuildBlockColorTable must be called before RenderTile");
        }

        IMapChunk mc = api.World.BlockAccessor.GetMapChunk(cx, cz);
        if (mc == null) return null;

        int chunksY = api.World.BlockAccessor.MapSizeY / ChunkSize;
        var chunksTmp = new IWorldChunk[chunksY];
        for (int i = 0; i < chunksY; i++)
        {
            chunksTmp[i] = api.World.BlockAccessor.GetChunk(cx, i, cz);
            if (chunksTmp[i] == null) return null;
        }

        IList<Block> worldBlocks = api.World.Blocks;
        int[] pixels = new int[ChunkSize * ChunkSize];

        IMapChunk mcNW = api.World.BlockAccessor.GetMapChunk(cx - 1, cz - 1);
        IMapChunk mcW = api.World.BlockAccessor.GetMapChunk(cx - 1, cz);
        IMapChunk mcN = api.World.BlockAccessor.GetMapChunk(cx, cz - 1);

        byte[] shadowMap = new byte[pixels.Length];
        for (int i = 0; i < shadowMap.Length; i++) shadowMap[i] = 128;

        var vec = new Vec2i();
        for (int k = 0; k < pixels.Length; k++)
        {
            int rainY = mc.RainHeightMap[k];
            int chunkY = rainY / ChunkSize;
            if (chunkY >= chunksTmp.Length) continue;

            MapUtil.PosInt2d(k, ChunkSize, vec);
            int x = vec.X;
            int y = vec.Y;

            float shadeFactor = 1f;

            IMapChunk mcTopLeft = mc, mcTop = mc, mcLeft = mc;
            int xLeft = x - 1, xHere = x, zTop = y - 1, zHere = y;
            if (xLeft < 0 && zTop < 0)
            {
                mcTopLeft = mcNW; mcTop = mcW; mcLeft = mcN;
            }
            else
            {
                if (xLeft < 0) { mcTopLeft = mcW; mcTop = mcW; }
                if (zTop < 0) { mcTopLeft = mcN; mcLeft = mcN; }
            }
            xLeft = GameMath.Mod(xLeft, ChunkSize);
            zTop = GameMath.Mod(zTop, ChunkSize);

            int dTopLeft = mcTopLeft != null ? rainY - mcTopLeft.RainHeightMap[zTop * ChunkSize + xLeft] : 0;
            int dTop = mcTop != null ? rainY - mcTop.RainHeightMap[zHere * ChunkSize + xLeft] : 0;
            int dLeft = mcLeft != null ? rainY - mcLeft.RainHeightMap[zTop * ChunkSize + xHere] : 0;
            float slopeSign = Math.Sign(dTopLeft) + Math.Sign(dTop) + Math.Sign(dLeft);
            float slopeMag = Math.Max(Math.Max(Math.Abs(dTopLeft), Math.Abs(dTop)), Math.Abs(dLeft));

            int blockIndex = chunksTmp[chunkY].UnpackAndReadBlock(MapUtil.Index3d(x, rainY % ChunkSize, y, ChunkSize, ChunkSize), BlockLayersAccess.FluidOrSolid);
            Block block = worldBlocks[blockIndex];

            if (slopeSign > 0f) shadeFactor = 1.08f + Math.Min(0.5f, slopeMag / 10f) / 1.25f;
            if (slopeSign < 0f) shadeFactor = 0.92f - Math.Min(0.5f, slopeMag / 10f) / 1.25f;

            if (block.BlockMaterial == EnumBlockMaterial.Snow)
            {
                rainY--;
                chunkY = rainY / ChunkSize;
                blockIndex = chunksTmp[chunkY].UnpackAndReadBlock(MapUtil.Index3d(vec.X, rainY % ChunkSize, vec.Y, ChunkSize, ChunkSize), BlockLayersAccess.FluidOrSolid);
                block = worldBlocks[blockIndex];
            }

            if (IsLake(block))
            {
                // Matches ChunkMapLayer: water tiles are not hillshaded, the
                // shadow map stays at its neutral (128) default here.
                pixels[k] = ResolveLakeColor(api, worldBlocks, chunksTmp, cx, cz, chunkY, rainY, vec, block);
            }
            else
            {
                shadowMap[k] = (byte)(shadowMap[k] * shadeFactor);
                pixels[k] = GetColor(block);
            }
        }

        byte[] shadowMapUnblurred = (byte[])shadowMap.Clone();
        BlurTool.Blur(shadowMap, ChunkSize, ChunkSize, 2);

        for (int m = 0; m < shadowMap.Length; m++)
        {
            float v1 = (int)((shadowMap[m] / 128f - 1f) * 5f) / 5f;
            v1 += (shadowMapUnblurred[m] / 128f - 1f) * 5f % 1f / 5f;
            pixels[m] = ColorUtil.ColorMultiply3Clamped(pixels[m], v1 + 1f) | unchecked((int)0xFF000000);
        }

        return pixels;
    }

    private int GetColor(Block block) => blockColorArgb[block.Id];

    private int ResolveLakeColor(ICoreServerAPI api, IList<Block> worldBlocks, IWorldChunk[] chunksTmp, int cx, int cz, int chunkY, int rainY, Vec2i vec, Block block)
    {
        int xm1 = vec.X - 1, xp1 = vec.X + 1, zm1 = vec.Y - 1, zp1 = vec.Y + 1;
        IWorldChunk cW = chunksTmp[chunkY], cE = cW, cN = cW, cS = cW;
        if (xm1 < 0) cW = api.World.BlockAccessor.GetChunk(cx - 1, chunkY, cz);
        if (xp1 >= ChunkSize) cE = api.World.BlockAccessor.GetChunk(cx + 1, chunkY, cz);
        if (zm1 < 0) cN = api.World.BlockAccessor.GetChunk(cx, chunkY, cz - 1);
        if (zp1 >= ChunkSize) cS = api.World.BlockAccessor.GetChunk(cx, chunkY, cz + 1);

        if (cW != null && cE != null && cN != null && cS != null)
        {
            int mxm1 = GameMath.Mod(xm1, ChunkSize);
            int mxp1 = GameMath.Mod(xp1, ChunkSize);
            int mzm1 = GameMath.Mod(zm1, ChunkSize);
            int mzp1 = GameMath.Mod(zp1, ChunkSize);
            Block bW = worldBlocks[cW.UnpackAndReadBlock(MapUtil.Index3d(mxm1, rainY % ChunkSize, vec.Y, ChunkSize, ChunkSize), BlockLayersAccess.FluidOrSolid)];
            Block bE = worldBlocks[cE.UnpackAndReadBlock(MapUtil.Index3d(mxp1, rainY % ChunkSize, vec.Y, ChunkSize, ChunkSize), BlockLayersAccess.FluidOrSolid)];
            Block bN = worldBlocks[cN.UnpackAndReadBlock(MapUtil.Index3d(vec.X, rainY % ChunkSize, mzm1, ChunkSize, ChunkSize), BlockLayersAccess.FluidOrSolid)];
            Block bS = worldBlocks[cS.UnpackAndReadBlock(MapUtil.Index3d(vec.X, rainY % ChunkSize, mzp1, ChunkSize, ChunkSize), BlockLayersAccess.FluidOrSolid)];

            if (IsLake(bW) && IsLake(bE) && IsLake(bN) && IsLake(bS))
            {
                return GetColor(block);
            }
            return wateredgeColorArgb;
        }

        return GetColor(block);
    }

    public static byte[] EncodePng(int[] pixels, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        unsafe
        {
            fixed (int* src = pixels)
            {
                Buffer.MemoryCopy(src, (void*)bitmap.GetPixels(), pixels.Length * 4, pixels.Length * 4);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
