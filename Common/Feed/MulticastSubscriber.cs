using MarketData.Common.Books;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MarketData.Common.Feed
{
    /// <summary>
    /// Joins a multicast group and feeds received datagrams to a <see cref="FeedDecoder"/>.
    /// </summary>
    /// <remarks>
    /// Nothing here interprets the feed; this type is only the socket. The interesting behaviour -
    /// loss detection, staleness, recovery - lives in the decoder, where it can be tested without
    /// a network.
    /// </remarks>
    public sealed class MulticastSubscriber : IDisposable
    {
        public FeedDecoder Decoder { get; }
        public FeedStatistics Statistics => Decoder.Statistics;

        public MulticastSubscriber(IPAddress group, int port, IPAddress @interface,
            Func<int, IOrderBook> bookFactory, int receiveBufferBytes = 1 << 20)
        {
            Decoder = new FeedDecoder(bookFactory);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // A generous receive buffer is the subscriber's only shock absorber: a multicast feed
            // applies no backpressure, so whatever the socket cannot hold is simply gone.
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, receiveBufferBytes);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(group, @interface ?? IPAddress.Loopback));
        }

        /// <summary>Receives until <paramref name="token"/> is cancelled.</summary>
        public void Receive(CancellationToken token)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            _socket.ReceiveTimeout = 250;

            while (!token.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = _socket.Receive(buffer);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (Exception)
                {
                    return;
                }

                Decoder.Consume(buffer.AsSpan(0, received));
            }
        }

        /// <summary>
        /// Receives asynchronously until <paramref name="token"/> is cancelled.
        /// </summary>
        /// <remarks>
        /// A blocking receive costs a dedicated thread per subscriber, which stops being viable at
        /// a few hundred subscribers and makes the harness - not the server - the thing under
        /// test. This awaits the socket instead, so thousands of subscribers share the thread pool
        /// and the measurement stays about the feed.
        /// </remarks>
        public async Task ReceiveAsync(CancellationToken token)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];

            while (!token.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = await _socket.ReceiveAsync(new Memory<byte>(buffer), SocketFlags.None, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Decoder.Consume(buffer.AsSpan(0, received));
            }
        }

        public void Dispose() => _socket.Dispose();

        private readonly Socket _socket;
    }
}
