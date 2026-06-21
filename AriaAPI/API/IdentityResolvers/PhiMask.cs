// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using System;
using System.Security.Cryptography;
using System.Text;

namespace AriaAPI.API.IdentityResolvers
{
    /// <summary>
    /// Provides log-safe masking of PHI values for operational correlation without exposing raw
    /// protected health information.
    /// </summary>
    public static class PhiMask
    {
        /// <summary>
        /// Returns an 8-character SHA-256 hex prefix of <paramref name="value"/> — sufficient for
        /// log-correlation while ensuring the raw PHI value is never written to any log sink.
        /// </summary>
        public static string Mask(string? value)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
        }
    }
}
