using System;

namespace MarketData.Common.Analytics
{
    /// <summary>
    /// Single-variable ordinary least squares, accumulated in one pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses Welford-style updating rather than accumulating raw sums of squares. The textbook
    /// formula subtracts two large nearly-equal numbers to get a variance, which loses most of its
    /// significant digits when the mean is far from zero - and order flow imbalance summed over a
    /// session is exactly that shape. This form updates around the running mean instead, so
    /// precision does not depend on where the data happens to sit.
    /// </para>
    /// <para>
    /// One pass, constant memory, no history retained: the same constraint as everything else on
    /// this path.
    /// </para>
    /// </remarks>
    public sealed class OnlineRegression
    {
        public long Count { get; private set; }
        public double MeanX { get; private set; }
        public double MeanY { get; private set; }

        private double _m2X;
        private double _m2Y;
        private double _comoment;

        public void Add(double x, double y)
        {
            Count++;

            var deltaX = x - MeanX;
            var deltaY = y - MeanY;

            MeanX += deltaX / Count;
            MeanY += deltaY / Count;

            // Uses one pre-update and one post-update delta, which is what makes the co-moment
            // exact rather than merely close.
            _m2X += deltaX * (x - MeanX);
            _m2Y += deltaY * (y - MeanY);
            _comoment += deltaX * (y - MeanY);
        }

        public double VarianceX => Count < 2 ? 0 : _m2X / (Count - 1);
        public double VarianceY => Count < 2 ? 0 : _m2Y / (Count - 1);
        public double Covariance => Count < 2 ? 0 : _comoment / (Count - 1);

        /// <summary>Slope of y on x.</summary>
        public double Slope => _m2X <= 0 ? 0 : _comoment / _m2X;

        public double Intercept => MeanY - Slope * MeanX;

        /// <summary>Pearson correlation in [-1, 1].</summary>
        public double Correlation
        {
            get
            {
                var denominator = Math.Sqrt(_m2X * _m2Y);
                return denominator <= 0 ? 0 : _comoment / denominator;
            }
        }

        /// <summary>Fraction of variance in y explained by x.</summary>
        public double RSquared => Correlation * Correlation;

        /// <summary>
        /// t-statistic for the slope against a null of zero.
        /// </summary>
        /// <remarks>
        /// Reported because a correlation on a large sample can be tiny and still be real, and can
        /// look impressive and not be. With hundreds of thousands of observations the distinction
        /// is not obvious from R-squared alone.
        /// </remarks>
        public double SlopeTStatistic
        {
            get
            {
                if (Count < 3 || _m2X <= 0)
                    return 0;

                var residual = _m2Y - Slope * _comoment;

                if (residual <= 0)
                    return double.PositiveInfinity;

                var standardError = Math.Sqrt(residual / (Count - 2) / _m2X);
                return standardError <= 0 ? 0 : Slope / standardError;
            }
        }
    }
}
