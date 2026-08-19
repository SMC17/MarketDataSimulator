using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace MarketData.Tests.Framework
{
    /// <summary>
    /// A very small property-based testing harness: generate many random cases, and when one
    /// fails, shrink it to a minimal counterexample before reporting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two properties make a random test useful rather than merely noisy. The first is
    /// reproducibility: every case is generated from an explicit seed, and the seed is printed on
    /// failure, so a failure found once can be replayed exactly. Randomised tests that cannot be
    /// replayed convert bugs into folklore.
    /// </para>
    /// <para>
    /// The second is shrinking. A failing sequence of 400 random operations says almost nothing;
    /// the same failure reduced to the three operations that actually cause it usually says
    /// exactly what is wrong. <see cref="ForAll{T}"/> repeatedly tries smaller candidates derived
    /// from the failing one and keeps any that still fails.
    /// </para>
    /// </remarks>
    public static class Property
    {
        public const int DefaultCases = 500;

        /// <summary>
        /// Checks <paramref name="property"/> over generated cases, shrinking any failure.
        /// </summary>
        /// <param name="generate">Builds a case from a seeded generator.</param>
        /// <param name="shrink">Yields strictly smaller candidates derived from a failing case.</param>
        /// <param name="property">Throws (or returns false) to signal failure.</param>
        /// <param name="describe">Renders a case for the failure message.</param>
        public static void ForAll<T>(
            Func<Random, T> generate,
            Func<T, IEnumerable<T>> shrink,
            Action<T> property,
            Func<T, string> describe,
            int cases = DefaultCases,
            int seed = 0)
        {
            var rootSeed = seed != 0 ? seed : Environment.TickCount;

            for (var i = 0; i < cases; i++)
            {
                var caseSeed = HashCombine(rootSeed, i);
                var candidate = generate(new Random(caseSeed));

                if (Try(property, candidate, out var failure))
                    continue;

                var minimal = Shrink(candidate, shrink, property);

                throw new PropertyFailedException(BuildMessage(rootSeed, caseSeed, i, minimal, describe, failure));
            }
        }

        private static T Shrink<T>(T failing, Func<T, IEnumerable<T>> shrink, Action<T> property)
        {
            var current = failing;

            // Greedy descent: keep the first smaller candidate that still fails, and restart from
            // it. Bounded so a pathological shrinker cannot hang the suite.
            for (var round = 0; round < MaxShrinkRounds; round++)
            {
                var improved = false;

                foreach (var candidate in shrink(current))
                {
                    if (Try(property, candidate, out _))
                        continue;

                    current = candidate;
                    improved = true;
                    break;
                }

                if (!improved)
                    break;
            }

            return current;
        }

        private static bool Try<T>(Action<T> property, T candidate, out Exception? failure)
        {
            try
            {
                property(candidate);
                failure = null;
                return true;
            }
            catch (Exception e)
            {
                failure = e;
                return false;
            }
        }

        private static string BuildMessage<T>(int rootSeed, int caseSeed, int index, T minimal,
            Func<T, string> describe, Exception? failure)
        {
            var message = new StringBuilder();
            message.AppendLine($"Property failed on case {index} (root seed {rootSeed}, case seed {caseSeed}).");
            message.AppendLine($"Replay this case with seed: {rootSeed}");
            message.AppendLine();
            message.AppendLine("Minimal counterexample:");
            message.AppendLine(describe(minimal));
            message.AppendLine();
            message.AppendLine($"Failure: {failure?.Message}");
            return message.ToString();
        }

        private static int HashCombine(int a, int b)
        {
            unchecked
            {
                var hash = (uint)a * 2654435761u;
                hash ^= (uint)b + 0x9E3779B9u + (hash << 6) + (hash >> 2);
                return (int)(hash | 1);
            }
        }

        private const int MaxShrinkRounds = 2000;
    }

    public sealed class PropertyFailedException : Xunit.Sdk.XunitException
    {
        public PropertyFailedException(string message) : base(message) { }
    }
}
