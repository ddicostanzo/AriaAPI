// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using AriaAPI.API.IdentityResolvers;
using AriaAPI.Core;
using AriaAPI.Networking.Core;
using AriaAPI.Security;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;

namespace AriaAPI.Networking.Helpers
{
    /// <summary>
    /// Extension methods for <see cref="IServiceCollection"/> that register the Aria FHIR client stack.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers all services required by the Aria FHIR client stack, including
        /// <see cref="FhirClientFactory"/>, <see cref="TokenProvider"/>, <see cref="ClientConfigurator"/>,
        /// <see cref="IFhirFactory"/>, <see cref="IPatientResolver"/>, and <see cref="IPractitionerResolver"/>.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
        /// <param name="fhirConfigSection">
        /// The <see cref="IConfiguration"/> section that contains the FHIR options
        /// (e.g., <c>config.GetSection("Fhir")</c>).
        /// </param>
        /// <returns>The same <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="services"/> or <paramref name="fhirConfigSection"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Call this method at most once per <see cref="IServiceCollection"/>. Calling it multiple times
        /// registers duplicate singleton factories; only the last-registered factory wins for resolution,
        /// and earlier <see cref="ClientConfigurator"/> instances will leak until the container is disposed.
        /// </para>
        /// <para>
        /// <see cref="TokenProvider"/> is registered as a typed <c>HttpClient</c> (transient per DI conventions)
        /// and captured by the singleton <see cref="ClientConfigurator"/>. The 15-minute handler lifetime
        /// is managed by <c>IHttpClientFactory</c>; the captured instance's token cache remains valid
        /// across handler rotations because it shares the singleton <c>IMemoryCache</c>.
        /// To fully align handler rotation with the singleton pattern, consider refactoring
        /// <see cref="TokenProvider"/> to accept <c>IHttpClientFactory</c> directly.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddAriaFhirClient(
            this IServiceCollection services,
            IConfiguration fhirConfigSection)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(fhirConfigSection);

            // Idempotency guard — prevent duplicate registrations that would silently leak instances.
            if (services.Any(d => d.ServiceType == typeof(ClientConfigurator)))
                return services;

            // 1) Bind and validate FhirOptions
            services
                .AddOptions<FhirOptions>()
                .Bind(fhirConfigSection)
                .Validate(opt =>
                {
                    var name = opt.ActiveSystem ?? "Test";
                    if (!opt.Systems.TryGetValue(name, out var sys)) return false;
                    return !string.IsNullOrWhiteSpace(sys.BaseUrl)
                        && !string.IsNullOrWhiteSpace(sys.Auth.Authority)
                        && !string.IsNullOrWhiteSpace(sys.Auth.ClientId);
                }, "Invalid or incomplete FHIR configuration for the active system.")
                .ValidateOnStart();

            // 2) Register IMemoryCache for token caching
            services.AddMemoryCache();

            // 3) Register FhirClientFactory as singleton
            services.AddSingleton<FhirClientFactory>();

            // 4) Register TokenProvider via HttpClient factory with a long-lived handler lifetime
            services.AddHttpClient<TokenProvider>()
                    .SetHandlerLifetime(TimeSpan.FromMinutes(15));

            // 5) Register ClientConfigurator as singleton, built using FhirClientSettings
            //    that replicate the settings from Program_Examples.cs exactly
            services.AddSingleton(sp =>
            {
                var factory = sp.GetRequiredService<FhirClientFactory>();
                var tokenProvider = sp.GetRequiredService<TokenProvider>();
                var logger = sp.GetRequiredService<ILogger<ClientConfigurator>>();

                var system = factory.GetActiveSystem(out var name);

                var fhirSettings = new FhirClientSettings
                {
                    PreferredFormat = ResourceFormat.Json,
                    UseFhirVersionInAcceptHeader = true,
                    UseAsync = true,
                    BinaryReceivePreference = BinaryTransferBehaviour.UseData,
                    Timeout = 60_000,
                    VerifyFhirVersion = true
                };

                var options = sp.GetRequiredService<IOptionsMonitor<FhirOptions>>().CurrentValue;
                return new ClientConfigurator(system, fhirSettings, tokenProvider, logger, name, options.MaxConnectionsPerServer, options.CaptureRawBodies);
            });

            // 6) Register IFhirFactory -> FhirFactory as singleton
            services.AddSingleton<IFhirFactory, FhirFactory>();

            // 7) Register identity resolvers as singletons
            services.AddSingleton<IPatientResolver, PatientResolver>();
            services.AddSingleton<IPractitionerResolver, PractitionerResolver>();

            return services;
        }
    }
}
