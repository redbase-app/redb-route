namespace redb.Route.Tests.Telemetry;

/// <summary>
/// The classes that listen to the route's ActivitySource and Meter, which are process-wide. Their own classes run one
/// after another, and since the collection disables parallelization they also never run beside any other test: a span
/// or a measurement of a test running at the same time cannot reach their exporters, and their listeners cannot change
/// what another test's code records (findings register row 44).
/// </summary>
[CollectionDefinition("Telemetry", DisableParallelization = true)]
public class TelemetryCollection;
