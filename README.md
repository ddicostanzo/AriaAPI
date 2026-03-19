[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![CI](https://github.com/ddicostanzo/AriaAPI/actions/workflows/ci.yml/badge.svg)](https://github.com/ddicostanzo/AriaAPI/actions/workflows/ci.yml)

# AriaAPI

**Version:** `1.0.1`

A .NET 10 class library providing a C# SDK for interacting with the Varian Aria oncology information system via its FHIR R4 API.

## Requirements

- **Varian Aria** oncology information system with FHIR R4 API access
- **OAuth2 client credentials** (configured via [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets))
- [**.NET 10 SDK**](https://dotnet.microsoft.com/download/dotnet/10.0)

## Features

- **FHIR R4 resource search** for 37 resource types (Patient, Appointment, Procedure, Observation, Encounter, ImagingStudy, and more)
- **FHIR resource creation** (DocumentReference, Task)
- **OAuth2 client-credentials** token management with in-memory caching
- **Fluent builder pattern** for constructing FHIR search queries
- **Fan-out search** for multi-valued FHIR parameters (OR within, AND across, deduplicated by Resource.Id)
- **Multi-resource search helpers** — cross-resource queries (`PatientCareTeam`, `PatientEncounters`, `PatientObservations`, `PatientDocuments`, `PatientAppointments`)
- **FHIR Operations** — `$everything` (PatientOperations) and `$expand` (ValueSetOperations)
- **Write support** — `CareTeamWrite`, `AppointmentWrite` with update/upsert and patient-centric overloads
- **Identity resolution** for Patients and Practitioners
- **PHI-safe logging** — all logged identifiers are SHA-256 masked (HIPAA)
- **DI extension** — `IServiceCollection.AddAriaFhirClient(config)` wires the full client stack
- **Direct SQL queries** against the Aria database (excluded from default build)
- **Document generation** workflows for Special Treatment Procedures (excluded from default build)

## Quick Start

### 1. Register services (DI)

```csharp
using AriaAPI.Networking.Helpers;

// In your Startup / Program.cs
services.AddAriaFhirClient(configuration.GetSection("FhirOptions"));
```

### 2. Search for a patient

```csharp
using AriaAPI.API.SingleResourceSearch;

// Search for patients by name or identifier
var patients = await PatientSearch.PatientsAsync(
    configurator,
    new PatientSearch.PatientSearchParams { NameOrIdentifier = "Smith" });
```

### 3. Multi-resource search

```csharp
using AriaAPI.API.MultiResourceSearch;

// Get a patient and their appointments within a date window
var (patient, appointments) = await MultiResourceSearch.PatientAndAppointmentsByDateAsync(
    configurator,
    patientIdentifier: "MRN12345",
    start: DateTimeOffset.Now.AddDays(-30),
    end: DateTimeOffset.Now);
```

### 4. Write a resource

```csharp
using AriaAPI.API.Write;

// Update an existing appointment
var updated = await AppointmentWrite.UpdateAsync(configurator, appointment, logger);
```

## Tech Stack

- .NET 10 (class library)
- [Hl7.Fhir.R4](https://github.com/FirelyTeam/firely-net-sdk) v6.0.1
- Microsoft.Extensions.DependencyInjection, Http, Caching.Memory
- Microsoft.Data.SqlClient v6.1.4 *(excluded from default build)*
- DocumentFormat.OpenXml v3.3.0 *(excluded from default build)*

## Project Structure

```
AriaAPI/
├── API/
│   ├── Create/               # Resource creation (DocumentReference, Task)
│   ├── IdentityResolvers/    # Patient/Practitioner identity resolution
│   ├── MultiResourceSearch/  # Cross-resource queries
│   ├── Operations/           # FHIR operations ($everything, $expand)
│   ├── SearchHelpers/        # Search parameter type definitions
│   ├── SingleResourceSearch/ # Individual FHIR resource search classes
│   └── Write/                # CareTeamWrite, AppointmentWrite
├── Core/                     # FhirService, FanOutSearchHelper, HTTP pipeline, token handling
├── Networking/
│   ├── Core/                 # AriaFhirClient, Builder, Factory patterns
│   └── Helpers/              # Builder extensions, FHIR includes/enums
├── Security/                 # OAuth2 TokenProvider with caching
├── Config/                   # JSON-driven handler dispatch (excluded from build)
├── Resources/                # FHIR resource models (excluded from build)
├── SQL/                      # Direct SQL query infrastructure (excluded from build)
└── Workflows/                # Special Treatment Procedure workflows (excluded from build)
```

## Build

```bash
dotnet build AriaAPI.sln
```

## Tests

The `AriaAPI.Tests` project contains 277 xUnit tests covering the fluent builder, fan-out search, FHIR extensions, search types, name formatting, PHI masking, and configuration validation. No live FHIR server is required.

```bash
dotnet test AriaAPI.Tests/AriaAPI.Tests.csproj
```

## NuGet Packaging

```bash
dotnet pack AriaAPI/AriaAPI.csproj -o ./out
```

Produces `AriaAPI.1.0.0-beta.1.nupkg`. The library targets `net10.0`.

## Configuration

This project uses [User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) for credential management. Configure FHIR connection settings via the `FhirOptions` section:

```json
{
  "FhirOptions": {
    "ActiveSystem": "Test",
    "Systems": {
      "Test": {
        "BaseUrl": "https://your-fhir-server/fhir",
        "Auth": {
          "Authority": "https://your-auth-server",
          "ClientId": "your-client-id",
          "ClientSecret": "your-client-secret",
          "Scope": "your-scope"
        }
      }
    }
  }
}
```

## License

See [LICENSE.txt](LICENSE.txt).
