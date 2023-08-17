namespace NodeGuard.Helpers;

public class ChannelState
{
    public long LocalBalance { get; set; }
    public long RemoteBalance { get; set; }
    public bool Active { get; set; }
}