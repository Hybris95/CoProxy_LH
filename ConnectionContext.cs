// Context holds the client's intended server type/version
class ConnectionContext
{
    public string TargetServerType; // "Login", "Game", "Logging"
    public string Version;
    // Other info like user/session
}
