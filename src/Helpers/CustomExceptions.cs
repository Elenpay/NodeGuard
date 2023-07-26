namespace NodeGuard.Helpers;

public class NoUTXOsAvailableException: Exception {}

public class UTXOsNoLongerValidException: Exception {}

public class NBXplorerNotFullySyncedException: Exception
{
   public NBXplorerNotFullySyncedException(): base("Error, nbxplorer not fully synched") {}
}

public class ShowToUserException : Exception
{
   public ShowToUserException(string? message): base(message) {}
}

public class PeerNotOnlineException : Exception
{
   public PeerNotOnlineException(string? message = null): base(message) {}
}

public class RemoteCanceledFundingException : Exception
{
   public RemoteCanceledFundingException(string? message = null): base(message) {}
}