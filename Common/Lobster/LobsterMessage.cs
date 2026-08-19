using System;

namespace MarketData.Common.Lobster
{
    /// <summary>
    /// Event types in a LOBSTER message file, which are the visible order events NASDAQ published
    /// on its ITCH feed for that instrument and day.
    /// </summary>
    public enum LobsterEventType : byte
    {
        NewLimitOrder = 1,

        /// <summary>Size reduced; the order remains on the book.</summary>
        PartialCancel = 2,

        /// <summary>Order removed entirely.</summary>
        Delete = 3,

        /// <summary>A resting visible order traded.</summary>
        VisibleExecution = 4,

        /// <summary>
        /// A hidden order traded. Deliberately invisible to the depth feed, so it must leave the
        /// reconstructed book untouched - a reconstruction that reacts to these will drift.
        /// </summary>
        HiddenExecution = 5,

        /// <summary>Auction or cross trade.</summary>
        CrossTrade = 6,

        TradingHalt = 7,
    }

    /// <summary>
    /// One row of a LOBSTER message file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prices are integers in units of $0.0001, exactly as NASDAQ publishes them, and are kept that
    /// way end to end. Converting to a floating point dollar value and back is how reconstructions
    /// acquire off-by-one-tick errors that take a day to find.
    /// </para>
    /// <para>
    /// <see cref="Direction"/> is the side of the <em>limit order</em> the event concerns, not of
    /// the aggressor. An execution against a resting sell order is a buyer-initiated trade but
    /// carries direction -1, because it is the sell order that changed.
    /// </para>
    /// </remarks>
    public readonly record struct LobsterMessage(
        long TimeNanoseconds,
        LobsterEventType Type,
        long OrderId,
        uint Size,
        int Price,
        sbyte Direction)
    {
        public Books.Side Side => Direction == 1 ? Books.Side.Bid : Books.Side.Ask;

        /// <summary>Events that change the visible book.</summary>
        public bool AffectsVisibleBook => Type is LobsterEventType.NewLimitOrder
            or LobsterEventType.PartialCancel
            or LobsterEventType.Delete
            or LobsterEventType.VisibleExecution;

        /// <summary>Signed change this event makes to the resting size at its price level.</summary>
        public long SizeDelta => Type switch
        {
            LobsterEventType.NewLimitOrder => Size,
            LobsterEventType.PartialCancel => -(long)Size,
            LobsterEventType.Delete => -(long)Size,
            LobsterEventType.VisibleExecution => -(long)Size,
            _ => 0,
        };

        public override string ToString()
            => $"{TimeNanoseconds / 1_000_000_000.0:F9} {Type} #{OrderId} {Size}@{Price} dir {Direction}";
    }
}
