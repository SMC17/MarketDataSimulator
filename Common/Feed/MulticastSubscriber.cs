using MarketData.Common.Books;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MarketData.Common.Durability;

namespace MarketData.Common.Feed
{
    /// <summary>Feeds one or two multicast lines through sequencing and optional gap fill.</summary>
    public sealed class MulticastSubscriber : IDisposable
    {
        public FeedDecoder Decoder { get; }
        public FeedStatistics Statistics => Decoder.Statistics;

        public MulticastSubscriber(IPAddress group, int port, IPAddress @interface,
            Func<int, IOrderBook> bookFactory, int receiveBufferBytes = 1 << 20,
            IPAddress redundantGroup = null, int redundantPort = 0,
            RetransmissionClient retransmission = null)
        {
            Decoder = new FeedDecoder(bookFactory);
            _recovery = retransmission is null ? null :
                new FeedRecoveryCoordinator(Decoder, retransmission);

            _sockets.Add(Join(group, port, @interface, receiveBufferBytes));

            if (redundantGroup is not null)
            {
                _sockets.Add(Join(redundantGroup, redundantPort > 0 ? redundantPort : port,
                    @interface, receiveBufferBytes));
            }
        }

        private static Socket Join(IPAddress group, int port, IPAddress @interface, int receiveBufferBytes)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, receiveBufferBytes);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(group, @interface ?? IPAddress.Loopback));
            return socket;
        }

        /// <summary>Maximum reorder hold time before declaring loss.</summary>
        public TimeSpan GapTimeout
        {
            get => _gapTimeout;
            set
            {
                if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _gapTimeout = value;
            }
        }

        /// <summary>Receives asynchronously until cancellation.</summary>
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

                if (_recovery?.IsRepairing == true)
                {
                    stalledFor = TimeSpan.Zero;
                    continue;
                }

                if (Decoder.HeldPackets == 0 || expected != lastExpected)
                {
                    lastExpected = expected;
                    stalledFor = TimeSpan.Zero;
                    continue;
                }

                stalledFor += interval;

                if (stalledFor >= GapTimeout)
                {
                    if (_recovery is null)
                        Decoder.FlushGaps();
                    else
                        await _recovery.FlushGapsAsync(token).ConfigureAwait(false);

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

                if (_recovery is null)
                    Decoder.Consume(buffer.AsSpan(0, received));
                else
                    await _recovery.ConsumeAsync(buffer.AsMemory(0, received), token)
                        .ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            foreach (var socket in _sockets)
                socket.Dispose();
        }

        private readonly List<Socket> _sockets = new List<Socket>(2);
        private readonly FeedRecoveryCoordinator _recovery;
        private TimeSpan _gapTimeout = TimeSpan.FromMilliseconds(20);
    }
}
