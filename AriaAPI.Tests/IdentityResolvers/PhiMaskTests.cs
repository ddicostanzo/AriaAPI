// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using System.Text.RegularExpressions;
using AriaAPI.API.IdentityResolvers;
using Xunit;

namespace AriaAPI.Tests.IdentityResolvers
{
    /// <summary>
    /// Tests for <see cref="PhiMask.Mask"/>.
    /// PhiMask is a public, log-safe PHI masker.
    /// </summary>
    public sealed class PhiMaskTests
    {
        /// <summary>The result is always an 8-character lowercase hex string.</summary>
        [Fact]
        public void Mask_AnyInput_ReturnsEightCharHexString()
        {
            var result = PhiMask.Mask("patient-name");

            Assert.Equal(8, result.Length);
            Assert.Matches(new Regex("^[0-9a-f]{8}$"), result);
        }

        /// <summary>Null input is treated as empty and does not throw.</summary>
        [Fact]
        public void Mask_NullInput_DoesNotThrow()
        {
            var ex = Record.Exception(() => PhiMask.Mask(null));
            Assert.Null(ex);
        }

        /// <summary>The same input always produces the same hash (deterministic).</summary>
        [Fact]
        public void Mask_SameValueTwice_ReturnsSameHash()
        {
            var first = PhiMask.Mask("John Smith");
            var second = PhiMask.Mask("John Smith");

            Assert.Equal(first, second);
        }

        /// <summary>Two different values produce different hashes.</summary>
        [Fact]
        public void Mask_DifferentValues_ReturnDifferentHashes()
        {
            var hash1 = PhiMask.Mask("Alice");
            var hash2 = PhiMask.Mask("Bob");

            Assert.NotEqual(hash1, hash2);
        }

        /// <summary>
        /// Null or empty input is treated as the empty string and yields the constant
        /// SHA-256 empty-string prefix. This is the canonical Wave 1 behavior (chosen over a
        /// "(empty)" sentinel) so AriaAPI consumers' log output is unchanged.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Mask_NullOrEmpty_ReturnsEmptyStringHashPrefix(string? value)
        {
            Assert.Equal("e3b0c442", PhiMask.Mask(value));
        }
    }
}
