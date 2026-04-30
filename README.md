# Conductor

`Conductor` is a CLI tool that scans C# MassTransit + Amazon SQS/SNS configuration and generates a least-privilege IAM policy JSON.

## What it does (v1)

- Scans a repo for MassTransit Amazon SQS usage.
- Detects queues/topics from common patterns:
  - `UsingAmazonSqs(...)`
  - `ReceiveEndpoint(...)`
  - `ec.Subscribe(...)`
  - `bus.Message<T>(x => x.SetEntityName(...))`
  - `EndpointConvention.Map<T>(new Uri(...))`
- Resolves names from:
  - string literals
  - `const` values
  - simple `.Replace("{{env}}", ...)` patterns
- Outputs deterministic IAM JSON policy.

## Requirements

- .NET SDK 10+ installed
- macOS/Linux/Windows shell

Check version:

```bash
dotnet --version
```

## Install / Build

From repo root:

```bash
dotnet restore Conductor.slnx
dotnet build Conductor.slnx -c Release
```

## Run

Use the built CLI:

```bash
dotnet ./src/Conductor.Cli/bin/Release/net10.0/Conductor.dll generate \
  --repo /absolute/path/to/your/repo \
  --out /absolute/path/to/policy.json
```

## Command

```text
transitpolicy generate --repo <path> --out <path> [options]
```

### Required flags

- `--repo` Path to the repository to scan
- `--out` Output path:
  - if `--scope repo`: output file path
  - if `--scope folder`: output directory path

### Optional flags

- `--scope` `repo|folder`. Default: `repo`
- `--format` Output format. Default: `iam-json`
- `--strict` `true|false`. Default: `true`
- `--region` ARN region token/value. Default: `${region}`
- `--account-id` ARN account token/value. Default: `${account_id}`
- `--env` reserved for future expansion. Default: `${env}`

## Examples

### 1) Default tokenized output

```bash
dotnet ./src/TransitPolicy.Cli/bin/Release/net10.0/transitpolicy.dll generate \
  --repo /Users/me/source/my-service \
  --out ./policy.json
```

### 2) Concrete account + region in ARNs

```bash
dotnet ./src/TransitPolicy.Cli/bin/Release/net10.0/transitpolicy.dll generate \
  --repo /Users/me/source/my-service \
  --out ./policy.prod.json \
  --region us-east-2 \
  --account-id 123456789012
```

### 3) Non-strict mode (warn and continue)

```bash
dotnet ./src/TransitPolicy.Cli/bin/Release/net10.0/transitpolicy.dll generate \
  --repo /Users/me/source/my-service \
  --out ./policy.json \
  --strict false
```

### 4) Monorepo mode (one policy per top-level folder)

```bash
dotnet ./src/TransitPolicy.Cli/bin/Release/net10.0/transitpolicy.dll generate \
  --repo /Users/me/source/fp-api \
  --scope folder \
  --out ./policies \
  --strict false
```

This creates files like:

- `./policies/Company.Api.policy.json`
- `./policies/Company.BlogAgent.policy.json`
- etc. (for folders where MassTransit SQS/SNS usage is detected)

## Strict mode behavior

When `--strict true`:

- unresolved queue/topic names are treated as errors
- command exits non-zero
- diagnostics are printed to stderr with source file paths

## Test

```bash
dotnet test TransitPolicy.slnx -c Release
```

## Output

- `scope=repo`: one IAM policy document (`Version: 2012-10-17`)
- `scope=folder`: one IAM policy document per top-level folder in repo

## Current limitations

- Focused on common MassTransit SQS/SNS patterns (not every dynamic/metaprogrammed style).
- If endpoint names are fully dynamic/runtime-only, strict mode will fail by design.

## Roadmap (planned)

- Terraform output emitter using same intermediate topology model.
- Expanded analyzer coverage for additional MassTransit patterns.

## Contributing

1. Create feature branch
2. Add/adjust tests
3. Run build + tests
4. Open PR
# Conductor
