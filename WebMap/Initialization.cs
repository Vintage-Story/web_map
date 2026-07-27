using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace WebMap;

/// <summary>
/// Server-side mod exposing a small HTTP API that serves world map tiles and
/// chunk metadata, meant to be consumed by an external (e.g. React) frontend.
/// See Server.Instance and Server.WebMapHttpServer for the actual wiring/routes.
/// </summary>
public class Initialization : ModSystem
{
    private Server.Instance? serverInstance;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        Debug.LoadLogger(api.Logger);
        Debug.Log($"Running on Version: {Mod.Info.Version}");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        serverInstance = new Server.Instance();
        serverInstance.Init(api);
    }

    public override void Dispose()
    {
        serverInstance?.Dispose();
        base.Dispose();
    }
}

public static class Debug
{
    private static ILogger? logger;

    public static void LoadLogger(ILogger _logger) => logger = _logger;

    public static void Log(string message)
        => logger?.Log(EnumLogType.Notification, $"[WebMap] {message}");

    public static void LogWarn(string message)
        => logger?.Log(EnumLogType.Warning, $"[WebMap] {message}");

    public static void LogError(string message)
        => logger?.Log(EnumLogType.Error, $"[WebMap] {message}");
}
