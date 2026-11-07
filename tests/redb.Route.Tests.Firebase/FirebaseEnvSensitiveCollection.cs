namespace redb.Route.Tests.Firebase;

/// <summary>
/// xUnit collection used to serialize tests that mutate process-wide Firebase/Firestore
/// environment variables (FIRESTORE_EMULATOR_HOST, GOOGLE_APPLICATION_CREDENTIALS) and tests
/// that read them. Prevents race conditions between option/validation tests and the live
/// emulator integration suite.
/// </summary>
[CollectionDefinition("FirebaseEnvSensitive", DisableParallelization = true)]
public sealed class FirebaseEnvSensitiveCollection { }
