// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
﻿using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;

namespace AriaAPI.Core
{

    /// <summary>
    /// Provides access to the active FHIR system configuration, including resolved auth scopes.
    /// </summary>
    public class FhirService
    {
        private readonly FhirClientFactory _factory;

        /// <summary>
        /// Initializes a new instance of <see cref="FhirService"/> with the given factory.
        /// </summary>
        /// <param name="factory">The <see cref="FhirClientFactory"/> used to resolve the active FHIR system.</param>
        public FhirService(FhirClientFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// Returns the active FHIR system options, its configuration key name, and the parsed OAuth scopes.
        /// </summary>
        /// <returns>
        /// A tuple of the active <see cref="FhirSystemOptions"/>, its key name, and the split scope strings.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the active system's scope is null or empty.
        /// </exception>
        public (FhirSystemOptions system, string name, string[] scopes) GetActive()
        {
            var system = _factory.GetActiveSystem(out var name);

            var scope = system.Auth.Scope;
            if (string.IsNullOrEmpty(scope))
                throw new InvalidOperationException($"Missing scope for FHIR system '{name}'.");

            var scopes = scope.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return (system, name, scopes);
        }
    }


    /// <summary>
    /// Top-level configuration object for FHIR system settings, bound from <c>appsettings.json</c> or user secrets.
    /// </summary>
    public class FhirOptions
    {
        /// <summary>Gets or sets the key name of the currently active FHIR system in <see cref="Systems"/>.</summary>
        public string ActiveSystem { get; set; } = "Test";

        /// <summary>Gets or sets the map of named FHIR system configurations keyed by system name.</summary>
        public Dictionary<string, FhirSystemOptions> Systems { get; set; } = new();

        /// <summary>Gets or sets the maximum number of concurrent HTTP connections per FHIR server. Defaults to 50.</summary>
        public int MaxConnectionsPerServer { get; set; } = 50;

        /// <summary>
        /// Gets or sets a value indicating whether the raw HTTP request and response bodies
        /// (e.g., the serialized FHIR bundle JSON) are captured for diagnostics. Defaults to <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When enabled, a <c>RawCaptureHandler</c> is inserted into the HTTP pipeline and the most recent
        /// request/response bodies are exposed via <see cref="ClientConfigurator.LastRawRequestBody"/>,
        /// <see cref="ClientConfigurator.LastRawResponseBody"/>, and the
        /// <see cref="ClientConfigurator.OnRawRequestCaptured"/> / <see cref="ClientConfigurator.OnRawResponseCaptured"/> events.
        /// </para>
        /// <para>
        /// <b>HIPAA warning:</b> FHIR bundles contain Protected Health Information (PHI). Captured bodies are
        /// <i>not</i> masked. Never write them to plain-text logs or any non-PHI-safe sink. Leave this
        /// disabled in production unless you have a compliant destination for the captured data.
        /// </para>
        /// </remarks>
        public bool CaptureRawBodies { get; set; } = false;
    }

    /// <summary>
    /// Configuration options for a single named FHIR system endpoint and its authentication settings.
    /// </summary>
    public class FhirSystemOptions
    {
        /// <summary>Gets or sets the base URL of the FHIR server (e.g., <c>https://fhir.example.com/fhir</c>).</summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>Gets or sets the OAuth 2.0 authentication options for this FHIR system.</summary>
        public AuthOptions Auth { get; set; } = new();
    }

    /// <summary>
    /// OAuth 2.0 client credentials configuration used to obtain tokens for FHIR API access.
    /// </summary>
    public class AuthOptions
    {
        /// <summary>Gets or sets the token authority (issuer) URL.</summary>
        public string Authority { get; set; } = "";

        /// <summary>Gets or sets the OAuth client identifier.</summary>
        public string ClientId { get; set; } = "";

        /// <summary>Gets or sets the OAuth client secret.</summary>
        public string ClientSecret { get; set; } = "";

        /// <summary>Gets or sets the space- or comma-delimited OAuth scopes to request.</summary>
        public string Scope { get; set; } = "";
    }

    /// <summary>
    /// Resolves and validates the active FHIR system from <see cref="FhirOptions"/> configuration.
    /// </summary>
    public class FhirClientFactory
    {
        private readonly IOptionsMonitor<FhirOptions> _options;

        /// <summary>
        /// Initializes a new instance of <see cref="FhirClientFactory"/>.
        /// </summary>
        /// <param name="options">Live configuration monitor for <see cref="FhirOptions"/>.</param>
        public FhirClientFactory(IOptionsMonitor<FhirOptions> options)
        {
            _options = options;
        }

        /// <summary>
        /// Returns the <see cref="FhirSystemOptions"/> for the active system and outputs its key name.
        /// </summary>
        /// <param name="name">Receives the active system's key name from <see cref="FhirOptions.ActiveSystem"/>.</param>
        /// <returns>The resolved and validated <see cref="FhirSystemOptions"/>.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="FhirOptions.ActiveSystem"/> is empty, the system key is missing,
        /// or any required auth field (BaseUrl, Authority, ClientId, ClientSecret) is empty.
        /// </exception>
        public FhirSystemOptions GetActiveSystem(out string name)
        {
            var cfg = _options.CurrentValue;

            if (string.IsNullOrEmpty(cfg.ActiveSystem))
                throw new InvalidOperationException("FhirOptions.ActiveSystem must not be null or empty.");

            name = cfg.ActiveSystem;

            if (!cfg.Systems.TryGetValue(name, out var system))
                throw new InvalidOperationException($"FhirOptions.Systems does not contain the active system key '{name}'.");

            if (string.IsNullOrEmpty(system.BaseUrl))
                throw new InvalidOperationException($"FhirSystemOptions.BaseUrl must not be null or empty for system '{name}'.");

            if (string.IsNullOrEmpty(system.Auth.Authority))
                throw new InvalidOperationException($"AuthOptions.Authority must not be null or empty for system '{name}'.");

            if (string.IsNullOrEmpty(system.Auth.ClientId))
                throw new InvalidOperationException($"AuthOptions.ClientId must not be null or empty for system '{name}'.");

            if (string.IsNullOrEmpty(system.Auth.ClientSecret))
                throw new InvalidOperationException($"AuthOptions.ClientSecret must not be null or empty for system '{name}'.");

            return system;
        }
    }

}
