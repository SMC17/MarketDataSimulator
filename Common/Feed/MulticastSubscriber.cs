using MarketData.Common.Books;
using System;
using System.Collections.Generic;
using System.Linq;
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
            Func<int, IOrderBook> bookFactory, int receiveBufferBytes = 1 << 20,
            IPAddress redundantGroup = null, int redundantPort = 0)
        {
            Decoder = new FeedDecoder(bookFactory);

            _sockets.Add(Join(group, port, @interface, receiveBufferBytes));

            if (redundantGroup is not null)
            {
                // Both lines feed the same decoder. Its duplicate suppression performs the
                // arbitration: the first copy of a packet to arrive wins, the second is discarded,
                // and a packet dropped on one line leaves no gap so long as the other delivers it.
                _sockets.Add(Join(redundantGroup, redundantPort > 0 ? redundantPort : port,
                    @interface, receiveBufferBytes));
            }
        }

        private static Socket Join(IPAddress group, int port, IPAddress @interface, int receiveBufferBytes)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // A generous receive buffer is the subscriber's only shock absorber: a multicast feed
            // applies no backpressure, so whatever the socket cannot hold is simply gone.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, receiveBufferBytes);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(group, @interface ?? IPAddress.Loopback));
            return socket;
        }

        /// <summary>Receives until <paramref name="token"/> is cancelled.</summary>
        public void Receive(CancellationToken token)
        {
            var socket = _sockets[0];
            var buffer = new byte[FeedProtocol.MaxPacketSize];
            socket.ReceiveTimeout = 250;

            while (!token.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = socket.Receive(buffer);
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
        /// <summary>
        /// Time out-of-order packets may be held before the hole in front of them is ruled lost.
        /// Must exceed the plausible delay between the A and B copies of a packet, or arbitration
        /// would be reported as loss.
        /// </summary>
        public TimeSpan GapTimeout { get; set; } = TimeSpan.FromMilliseconds(20);

        public Task ReceiveAsync(CancellationToken token)
            => Task.WhenAll(_sockets.Select(socket => ReceiveAsync(socket, token)).Append(RunGapTimerAsync(token)));

        /// <summary>
        /// Declares a gap once the stream has stopped advancing while packets are still held.
        /// </summary>
        private async Task RunGapTimerAsync(CancellationToken token)
        {
            var lastExpected = Decoder.ExpectedSequence;
            var stalledFor = TimeSpan.Zero;
            var interval = TimeSpan.FromMilliseconds(Math.Max(1, GapTimeout.TotalMilliseconds / 4));

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var expected = Decoder.ExpectedSequence;

                if (Decoder.HeldPackets == 0 || expected != lastExpected)
                {
                    lastExpected = expected;
                    stalledFor = TimeSpan.Zero;
                    continue;
                }

                stalledFor += interval;

                if (stalledFor >= GapTimeout)
                {
                    Decoder.FlushGaps();
                    stalledFor = TimeSpan.Zero;
                    lastExpected = Decoder.ExpectedSequence;
                }
            }
        }

        private async Task ReceiveAsync(Socket socket, CancellationToken token)
        {
            var buffer = new byte[FeedProtocol.MaxPacketSize];

            while (!token.IsCancellationRequested)
            {
                int received;

                try
                {
                    received = await socket.ReceiveAsync(new Memory<byte>(buffer), SocketFlags.None, token)
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

        public void Dispose()
        {
            foreach (var socket in _sockets)
                socket.Dispose();
        }

        private readonly List<Socket> _sockets = new List<Socket>(2);
    }
}
