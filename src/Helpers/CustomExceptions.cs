using System.Diagnostics.CodeAnalysis;

namespace NodeGuard.Helpers;

public class NoUTXOsAvailableException: Exception {}

public class UTXOsNoLongerValidException : Exception
{
   public UTXOsNoLongerValidException() {}

   public UTXOsNoLongerValidException(string? message) : base(message) {}
}

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

public class NotEnoughRoomInUtxosForFeesException : Exception
{
   public NotEnoughRoomInUtxosForFeesException(): base("Not enough room in the UTXOs to cover the fees") {}
}

public class NotEnoughBalanceInWalletException : Exception
{
   public NotEnoughBalanceInWalletException(string? message = null): base(message) {}
}

/// <summary>
/// Why a fee bump (RBF) was refused. Lets callers act on the refusal without matching message text: the UI shows the
/// message as-is, the gRPC API maps the reason to a status code.
/// </summary>
public enum BumpingErrorReason
{
   Unknown,
   RequestNotFound,
   InvalidState,
   AlreadyConfirmed,
   TransactionNotFound,
   ChangelessMultipleDestinations,
   InvalidFeeRate,
   FeeRateNotHigher,
   FeeExceedsInputs,
   PersistenceError
}

public class BumpingException : Exception
{
   public BumpingErrorReason Reason { get; }

   public BumpingException(string? message = null, BumpingErrorReason reason = BumpingErrorReason.Unknown): base(message)
   {
      Reason = reason;
   }
}

public class CustomArgumentNullException : ArgumentNullException
{
   public static void ThrowIfNull([NotNull] object? obj, string paramName, string message, params object[] args)
   {
      if (obj == null)
      {
         string formattedMessage = string.Format(message, args);
         throw new ArgumentNullException(paramName, formattedMessage);
      }
   }
}