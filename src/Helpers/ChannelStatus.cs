namespace NodeGuard.Helpers;

public class ChannelStatus
{
    public int ManagedNodeId { get; set; }
    public long LocalBalance { get; set; }
    public long RemoteBalance { get; set; }
    public bool Active { get; set; }
}