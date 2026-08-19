using System;
using System.Collections.Generic;

namespace MarketData.Common.Analytics
{
    /// <summary>
    /// Summary statistics of a price path and its order flow.
    /// </summary>
    /// <remarks>
    /// Chosen because they are the properties real markets are known to have and naive simulators
    /// are known to lack: returns with far heavier tails than a normal distribution, volatility
    /// that clusters rather than arriving independently, and a book whose depth and spread are
    /// skewed rather than symmetric. A simulator that matches on mean and variance while missing
    /// these is producing a price series that behaves nothing like a market.
    /// </remarks>
    public sealed class StylizedFacts
    {
        public long Observations { get; private set; }
        public double MeanReturn => _returns.Count == 0 ? 0 : _sum / _returns.Count;

        public double StandardDeviation
        {
            get
            {
                if (_returns.Count < 2)
                    return 0;

                var mean = MeanReturn;
                var total = 0.0;

                foreach (var value in _returns)
                    total += (value - mean) * (value - mean);

                return Math.Sqrt(total / (_returns.Count - 1));
            }
        }

        /// <summary>
        /// Excess kurtosis: zero for a normal distribution, strongly positive for real returns.
        /// </summary>
        /// <remarks>
        /// The single most robust stylized fact in finance. Real price changes produce far more
        /// extreme moves than a Gaussian of the same variance, and a random walk built from
        /// independent increments produces almost exactly zero here - which is how you tell the
        /// two apart in one number.
        /// </remarks>
        public double ExcessKurtosis
        {
            get
            {
                if (_returns.Count < 4)
                    return 0;

                var mean = MeanReturn;
                var deviation = StandardDeviation;

                if (deviation <= 0)
                    return 0;

                var fourth = 0.0;

                foreach (var value in _returns)
                {
                    var z = (value - mean) / deviation;
                    fourth += z * z * z * z;
                }

                return fourth / _returns.Count - 3.0;
            }
        }

        /// <summary>
        /// Autocorrelation of absolute returns at <paramref name="lag"/>: volatility clustering.
        /// </summary>
        /// <remarks>
        /// Returns themselves are close to uncorrelated in a real market - otherwise the direction
        /// would be trivially predictable - but their magnitudes are strongly and persistently
        /// correlated: violent moves follow violent moves. Measuring the magnitude rather than the
        /// signed return is what separates the two.
        /// </remarks>
        public double AbsoluteReturnAutocorrelation(int lag)
        {
            if (lag <= 0 || _returns.Count <= lag + 2)
                return 0;

            var regression = new OnlineRegression();

            for (var i = lag; i < _returns.Count; i++)
                regression.Add(Math.Abs(_returns[i - lag]), Math.Abs(_returns[i]));

            return regression.Correlation;
        }

        /// <summary>Autocorrelation of signed returns: near zero in an efficient market.</summary>
        public double ReturnAutocorrelation(int lag)
        {
            if (lag <= 0 || _returns.Count <= lag + 2)
                return 0;

            var regression = new OnlineRegression();

            for (var i = lag; i < _returns.Count; i++)
                regression.Add(_returns[i - lag], _returns[i]);

            return regression.Correlation;
        }

        public void AddReturn(double value)
        {
            _returns.Add(value);
            _sum += value;
            Observations++;
        }

        /// <summary>Fraction of observations beyond <paramref name="sigmas"/> standard deviations.</summary>
        /// <remarks>
        /// A normal distribution puts 0.27% beyond three sigma and 0.0063% beyond four. Real
        /// returns put far more there, and the ratio is more legible than kurtosis alone.
        /// </remarks>
        public double TailFraction(double sigmas)
        {
            if (_returns.Count == 0)
                return 0;

            var mean = MeanReturn;
            var deviation = StandardDeviation;

            if (deviation <= 0)
                return 0;

            var beyond = 0;

            foreach (var value in _returns)
            {
                if (Math.Abs(value - mean) > sigmas * deviation)
                    beyond++;
            }

            return beyond / (double)_returns.Count;
        }

        private readonly List<double> _returns = new List<double>(500_000);
        private double _sum;
    }
}
