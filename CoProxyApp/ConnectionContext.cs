// Context holds the client's intended server type/version
public class ConnectionContext
{
    public string? TargetServerType; // "Login", "Game"
    public string? Version;
    // Other info like user/session
}
