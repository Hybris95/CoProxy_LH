/*
 File: ConnectionContext.cs
 Responsibility:
   - Holds per-connection metadata that informs handlers/proxy about the target server
     type (e.g., "Login", "Game") and any versioning constraints.
   - Can be extended with user/session information as needed.
*/

/// <summary>
/// Encapsulates contextual information about a client's connection,
/// including the target server type and version.
/// </summary>
public class ConnectionContext
{
    /// <summary>
    /// Target server type for this connection. Expected values: "Login" or "Game".
    /// Used by proxy and handlers to route and process packets appropriately.
    /// </summary>
    public string? TargetServerType; // "Login", "Game"

    /// <summary>
    /// Optional protocol/game version string for version-specific handling.
    /// </summary>
    public string? Version;

    // Other info like user/session could be added here in the future.
}
