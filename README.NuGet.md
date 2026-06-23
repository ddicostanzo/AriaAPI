# AriaAPI

A .NET 10 class library providing a C# SDK for interacting with the **Varian Aria oncology information system** via its FHIR R4 API.

Supports search, creation, write operations, and OAuth2 token management for 37 FHIR resource types, with built-in PHI-safe logging (HIPAA).

---

## Install

This package is hosted on GitHub Packages.

### 1. Add the feed to your `nuget.config`

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="github" value="https://nuget.pkg.github.com/ddicostanzo/index.json" />
  </packageSources>
</configuration>
```

### 2. Authenticate

GitHub Packages requires a Personal Access Token (PAT) with **`read:packages`** scope.

**Option A — environment variable (recommended for CI):**
```bash
export NUGET_AUTH_TOKEN=<your-PAT>
```
Then in `nuget.config` add:
```xml
<packageSourceCredentials>
  <github>
    <add key="Username" value="<your-github-username>" />
    <add key="ClearTextPassword" value="%NUGET_AUTH_TOKEN%" />
  </github>
</packageSourceCredentials>
```

**Option B — CLI (local dev):**
```bash
dotnet nuget add source https://nuget.pkg.github.com/ddicostanzo/index.json \
  --name github \
  --username <your-github-username> \
  --password <your-PAT> \
  --store-password-in-clear-text
```

### 3. Add the package

```bash
dotnet add package AriaAPI
```

---

## Quick Start

### Register with DI

```csharp
using AriaAPI.Core;

builder.Services.AddAriaFhirClient(config =>
{
    config.BaseUrl = "https://your-fhir-server/fhir";
    config.Auth.Authority = "https://your-auth-server";
    config.Auth.ClientId = "your-client-id";
    config.Auth.ClientSecret = "your-client-secret";
    config.Auth.Scope = "your-scope";
});
```

### Search example

```csharp
using AriaAPI.API.SingleResourceSearch;
using AriaAPI.Networking.Core;

var appointments = await AppointmentSearch.ByPatientAsync(configurator, patientId, ct);
```

---

## Configuration

Connection settings are read from the `FhirOptions` section of `IConfiguration`.

With [User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) (local dev):

```json
{
  "FhirOptions": {
    "ActiveSystem": "Production",
    "CaptureRawBodies": false,
    "Systems": {
      "Production": {
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

### Raw body capture (diagnostics)

Set `FhirOptions.CaptureRawBodies` to `true` to capture the raw HTTP request/response bodies — including the
serialized FHIR bundle JSON — for diagnostics. When enabled, the most recent bodies are exposed on
`ClientConfigurator`:

```csharp
// One-off, after a call:
var bundleJson = configurator.LastRawResponseBody;

// Or subscribe for every request/response (fires synchronously at capture time):
configurator.OnRawResponseCaptured += (_, e) =>
{
    // e.ResponseBody is the raw FHIR bundle JSON. Route to a PHI-safe sink only.
};
```

> ⚠️ **HIPAA:** captured bodies contain **unmasked PHI**. They are never written to the library's logs. Do not
> write them to plain-text logs or any non-PHI-safe destination, and leave `CaptureRawBodies` disabled in
> production unless you have a compliant destination for the data.

---

## HIPAA / PHI

This library is designed for use in environments that handle **Protected Health Information (PHI)**:

- All logged identifiers (MRNs, patient IDs, practitioner names) are **SHA-256 masked** via `PhiMask.Mask()` — plain-text PHI never appears in logs.
- Sensitive HTTP headers (`Authorization`, `X-Client-Secret`) are filtered from request/response logs.
- **Never log raw patient identifiers** returned from FHIR responses in your consuming application.

---

## Requirements

- .NET 10
- Varian Aria OIS with FHIR R4 API access
- OAuth2 client credentials grant (client ID + secret)

---

## Full Documentation

See the [repository README](https://github.com/ddicostanzo/AriaAPI) for project structure, build instructions, and contributor docs.
