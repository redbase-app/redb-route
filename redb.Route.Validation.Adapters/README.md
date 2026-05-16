# redb.Route.Validation.Adapters

Validation adapters for [redb.Route](../../README.md): bridges **FluentValidation** and **System.ComponentModel.DataAnnotations** to the `IMessageValidator` contract used in route pipelines.

[![NuGet](https://img.shields.io/nuget/v/redb.Route.Validation.Adapters?label=NuGet&color=blue)](https://www.nuget.org/packages/redb.Route.Validation.Adapters)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

## Installation

```bash
dotnet add package redb.Route.Validation.Adapters
```

## Usage

### FluentValidation

```csharp
using FluentValidation;
using redb.Route.Validation.Adapters;

public class OrderValidator : AbstractValidator<Order>
{
    public OrderValidator()
    {
        RuleFor(o => o.Id).NotEmpty();
        RuleFor(o => o.Amount).GreaterThan(0);
    }
}

// Via DSL extension
From("kafka://orders?brokers=localhost:9092")
    .ValidateFluent(new OrderValidator())
    .To("direct://process");

// Or directly
From("kafka://orders?brokers=localhost:9092")
    .Validate(new FluentValidationMessageValidator<Order>(new OrderValidator()))
    .To("direct://process");
```

### DataAnnotations

```csharp
using System.ComponentModel.DataAnnotations;
using redb.Route.Validation.Adapters;

public class Order
{
    [Required]
    public string Id { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue)]
    public decimal Amount { get; set; }
}

// Via DSL extension
From("direct://input")
    .ValidateAnnotations()
    .To("direct://process");

// With optional DI service provider for IValidatableObject
From("direct://input")
    .ValidateAnnotations(serviceProvider: sp)
    .To("direct://process");
```

### Severity and error handling

Both adapters accept a `ValidationSeverity` and a `throwOnFailure` flag:

```csharp
// Soft warning — pipeline continues, no exception thrown
From("direct://input")
    .ValidateFluent(new OrderValidator(), ValidationSeverity.Warning)
    .To("direct://process");

// Hard error, but suppress exception (inspect exchange.ValidationErrors manually)
From("direct://input")
    .ValidateAnnotations(throwOnFailure: false)
    .To("direct://process");
```

## Key Classes

| Class / Method | Description |
|---|---|
| `FluentValidationMessageValidator<T>` | `IMessageValidator` backed by a FluentValidation `IValidator<T>` |
| `DataAnnotationsValidator` | `IMessageValidator` backed by `System.ComponentModel.DataAnnotations` |
| `ValidationDslExtensions.ValidateFluent<T>` | DSL shortcut to attach FluentValidation to a route |
| `ValidationDslExtensions.ValidateAnnotations` | DSL shortcut to attach DataAnnotations validation to a route |

## Part of

[redb.Route](../../README.md) — ESB & EIP Framework for .NET
