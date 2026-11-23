namespace RevitMcpBridge;

/// <summary>Version and per-user data folder of the bridge.</summary>
internal static class BridgeInfo
{
    /// <summary>Keep server/catalog.py BRIDGE_VERSION in step; the tests check it.</summary>
    public const string Version = "0.14.0";

    /// <summary>%LOCALAPPDATA%\RevitMcpBridge: bridge.json (connection details) and audit.log.</summary>
    public static string DataFolder => IOPath.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "RevitMcpBridge");
}
