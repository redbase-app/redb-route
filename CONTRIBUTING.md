# Contributing to redb.Route

Thank you for your interest in redb.Route!

## How to Contribute

### Reporting Issues

- Use [GitHub Issues](https://github.com/redbase-app/redb/issues) for bug reports
- Use [GitHub Discussions](https://github.com/redbase-app/redb/discussions) for questions and feature requests

### Bug Reports

Please include:
- Package name and version (e.g. `redb.Route.Kafka 1.0.0-preview.1`)
- .NET version
- Transport / broker version where applicable (e.g. Kafka 3.6, RabbitMQ 3.13)
- Minimal route definition to reproduce the issue
- Expected vs actual behavior (including exception message and stack trace if any)

### Feature Requests

Open a [Discussion](https://github.com/redbase-app/redb/discussions/categories/ideas) with:
- Description of the feature
- Use case / motivation
- Example code showing the desired DSL or API (if applicable)

## Code Contributions

### Scope

This repository contains the full source of redb.Route — the core engine, all transports, and adapter packages. Pull requests for bug fixes, documentation improvements, and new transport connectors are welcome.

### Getting Started

1. Fork the repository
2. Create a branch: `git checkout -b fix/kafka-ack-handling`
3. Make your changes (see guidelines below)
4. Run existing tests if you have access to the required brokers
5. Submit a Pull Request with a clear description

### Code Guidelines

- Follow the existing code style — C# 12, `nullable enable`, `implicit usings`
- Use English for all code identifiers, XML doc summaries, and error messages
- Add `/// <summary>` for all new public classes and methods
- Do not use `dynamic` — prefer concrete types and strong typing
- Use LINQ where appropriate; avoid unnecessary nested loops
- Every new transport must implement `IComponent` and register a URI scheme
- Every new transport must have a fluent builder with `implicit operator string`
- New processors must implement `IProcessor` or `IAsyncProcessor`
- Do not suppress `CS1591` — every public member must have XML documentation

### New Transport Checklist

- [ ] Implements `IComponent` and registers URI scheme
- [ ] Has a fluent builder class (e.g. `MyTransportBuilder`) with `implicit operator string`
- [ ] Has a `README.md` with NuGet badge, Installation, Usage, Fluent Builder API table, and "Part of" footer
- [ ] Headers use a consistent `redbXxx.` prefix
- [ ] Options class inherits from appropriate base (or standalone with XML docs)
- [ ] Registered via `AddRedbRouteXxx()` extension on `IServiceCollection`

### Commit Messages

Use the format: `type(scope): description`

Examples:
- `fix(kafka): handle null message key in consumer`
- `feat(s3): add server-side encryption options`
- `docs(http): document named URL parameter binding`

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
