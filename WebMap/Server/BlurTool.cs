namespace WebMap.Server;

/// <summary>
/// Direct port of Vintagestory.GameContent.BlurTool (VSEssentials), used by the
/// game's own ChunkMapLayer to soften the hillshade pass. Reproduced here so
/// the server-side tile renderer can match the in-game map's look.
/// </summary>
public static class BlurTool
{
    public static void Blur(byte[] data, int sizeX, int sizeZ, int range)
    {
        BoxBlurHorizontal(data, range, 0, 0, sizeX, sizeZ);
        BoxBlurVertical(data, range, 0, 0, sizeX, sizeZ);
    }

    public static unsafe void BoxBlurHorizontal(byte[] map, int range, int xStart, int yStart, int xEnd, int yEnd)
    {
        fixed (byte* ptr = map)
        {
            int width = xEnd - xStart;
            int halfRange = range / 2;
            int rowOffset = yStart * width;
            byte[] row = new byte[width];
            for (int y = yStart; y < yEnd; y++)
            {
                int count = 0;
                int sum = 0;
                for (int x = xStart - halfRange; x < xEnd; x++)
                {
                    int leaving = x - halfRange - 1;
                    if (leaving >= xStart)
                    {
                        byte b = ptr[rowOffset + leaving];
                        if (b != 0) sum -= b;
                        count--;
                    }
                    int entering = x + halfRange;
                    if (entering < xEnd)
                    {
                        byte b2 = ptr[rowOffset + entering];
                        if (b2 != 0) sum += b2;
                        count++;
                    }
                    if (x >= xStart)
                    {
                        row[x] = (byte)(sum / count);
                    }
                }
                for (int x = xStart; x < xEnd; x++)
                {
                    ptr[rowOffset + x] = row[x];
                }
                rowOffset += width;
            }
        }
    }

    public static unsafe void BoxBlurVertical(byte[] map, int range, int xStart, int yStart, int xEnd, int yEnd)
    {
        fixed (byte* ptr = map)
        {
            int width = xEnd - xStart;
            int height = yEnd - yStart;
            int halfRange = range / 2;
            byte[] col = new byte[height];
            int leavingOffset = -(halfRange + 1) * width;
            int enteringOffset = halfRange * width;
            for (int x = xStart; x < xEnd; x++)
            {
                int count = 0;
                int sum = 0;
                int baseOffset = yStart * width - halfRange * width + x;
                for (int y = yStart - halfRange; y < yEnd; y++)
                {
                    if (y - halfRange - 1 >= yStart)
                    {
                        byte b = ptr[baseOffset + leavingOffset];
                        if (b != 0) sum -= b;
                        count--;
                    }
                    if (y + halfRange < yEnd)
                    {
                        byte b2 = ptr[baseOffset + enteringOffset];
                        if (b2 != 0) sum += b2;
                        count++;
                    }
                    if (y >= yStart)
                    {
                        col[y] = (byte)(sum / count);
                    }
                    baseOffset += width;
                }
                for (int y = yStart; y < yEnd; y++)
                {
                    ptr[y * width + x] = col[y];
                }
            }
        }
    }
}
