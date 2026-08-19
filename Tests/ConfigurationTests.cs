using MarketData.Common;
using MarketData.Common.Feed;
using MarketData.Server.Configuration;
using Xunit;

namespace MarketData.Tests
{
    public sealed class ConfigurationTests
    {
        [Fact]
        public void ValidConfigurationPasses()
            => Valid().Validate();

        [Fact]
        public void DuplicateInstrumentIdsAreRejected()
        {
            var config = Valid();
            config.Instruments = new[]
            {
                Instrument(1, "ONE"),
                Instrument(1, "TWO"),
            };

            Assert.Throws<InvalidDataException>(config.Validate);
        }

        [Fact]
        public void DuplicateInstrumentSymbolsAreRejectedCaseInsensitively()
        {
            var config = Valid();
            config.Instruments = new[]
            {
                Instrument(1, "TEST"),
                Instrument(2, "test"),
            };

            Assert.Throws<InvalidDataException>(config.Validate);
        }

        [Fact]
        public void SnapshotDepthMustFitOneDatagram()
        {
            var config = Valid();
            config.Instruments = new[]
            {
                Instrument(1, "DEEP", FeedProtocol.MaxSnapshotLevels / 2 + 1),
            };

            Assert.Throws<InvalidDataException>(config.Validate);

            config.Instruments = new[] { Instrument(1, "OVERFLOW", int.MaxValue) };
            Assert.Throws<InvalidDataException>(config.Validate);
        }

        [Fact]
        public void MulticastAddressAndRecoveryIntervalAreValidated()
        {
            var config = Valid();
            config.Multicast.Enabled = true;
            config.Multicast.Group = "127.0.0.1";
            Assert.Throws<InvalidDataException>(config.Validate);

            config.Multicast.Group = "239.7.7.7";
            config.Multicast.SnapshotIntervalSeconds = 0;
            Assert.Throws<InvalidDataException>(config.Validate);
        }

        [Fact]
        public void RedundantMulticastEndpointMustBePresentAndDistinct()
        {
            var config = Valid();
            config.Multicast.Enabled = true;
            config.Multicast.RedundantPort = 31008;
            Assert.Throws<InvalidDataException>(config.Validate);

            config.Multicast.RedundantGroup = config.Multicast.Group;
            config.Multicast.RedundantPort = config.Multicast.Port;
            Assert.Throws<InvalidDataException>(config.Validate);

            config.Multicast.RedundantGroup = "239.7.7.8";
            config.Multicast.RedundantPort = 31008;
            config.Validate();
        }

        private static ServerConfiguration Valid() => new ServerConfiguration
        {
            Instruments = new[] { Instrument(1, "TEST") },
        };

        private static Instrument Instrument(int id, string symbol, int depth = 10)
            => new Instrument(id, symbol, new Specifications(depth, 1_000, 0.01));
    }
}
