using System;
using MarketData.Common.Analytics;
using Xunit;

namespace MarketData.Tests
{
    public class StylizedFactsTests
    {
        /// <summary>Normal returns must score near zero excess kurtosis - the reference point.</summary>
        [Fact]
        public void GaussianReturnsHaveNoExcessKurtosis()
        {
            var facts = new StylizedFacts();
            var random = new Random(7);

            for (var i = 0; i < 200_000; i++)
                facts.AddReturn(NextGaussian(random));

            Assert.InRange(facts.ExcessKurtosis, -0.15, 0.15);
            Assert.InRange(facts.TailFraction(3), 0.001, 0.005);   // normal: 0.27%
            Assert.InRange(facts.StandardDeviation, 0.95, 1.05);
        }

        [Fact]
        public void HeavyTailedReturnsAreDetected()
        {
            var facts = new StylizedFacts();
            var random = new Random(11);

            // A normal body with occasional large shocks, which is the shape real returns take.
            for (var i = 0; i < 200_000; i++)
                facts.AddReturn(random.NextDouble() < 0.01 ? NextGaussian(random) * 10 : NextGaussian(random));

            Assert.True(facts.ExcessKurtosis > 5,
                $"expected heavy tails to show up as excess kurtosis, got {facts.ExcessKurtosis}");
            Assert.True(facts.TailFraction(3) > 0.005, "expected more than normal mass beyond three sigma");
        }

        /// <summary>
        /// Independent returns must show no volatility clustering, so a positive reading elsewhere
        /// means something real rather than an artefact of the estimator.
        /// </summary>
        [Fact]
        public void IndependentReturnsShowNoVolatilityClustering()
        {
            var facts = new StylizedFacts();
            var random = new Random(13);

            for (var i = 0; i < 100_000; i++)
                facts.AddReturn(NextGaussian(random));

            Assert.InRange(facts.AbsoluteReturnAutocorrelation(1), -0.02, 0.02);
            Assert.InRange(facts.ReturnAutocorrelation(1), -0.02, 0.02);
        }

        [Fact]
        public void ClusteredVolatilityIsDetected()
        {
            var facts = new StylizedFacts();
            var random = new Random(17);
            var logVolatility = 0.0;

            for (var i = 0; i < 100_000; i++)
            {
                // Persistent volatility, independent direction - the textbook shape. Modelled on
                // log volatility so the level stays positive and shocks are multiplicative, which
                // is what makes the persistence visible in |r| rather than swamped by noise.
                logVolatility = 0.99 * logVolatility + 0.25 * NextGaussian(random);
                facts.AddReturn(NextGaussian(random) * Math.Exp(logVolatility));
            }

            Assert.True(facts.AbsoluteReturnAutocorrelation(1) > 0.1,
                $"expected clustering in |r|, got {facts.AbsoluteReturnAutocorrelation(1)}");

            // Direction must stay unpredictable even when magnitude is not.
            Assert.InRange(facts.ReturnAutocorrelation(1), -0.05, 0.05);
        }

        private static double NextGaussian(Random random)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
