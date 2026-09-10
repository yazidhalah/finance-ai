using FinanceAi.TestSupport;
using Xunit;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// One database and one API host for this assembly; every test creates its own tenants inside them
/// (T-04). Collections run one at a time because the fixture publishes the test database name
/// through the process environment.
/// </summary>
[CollectionDefinition(ApiCollection.Name)]
public sealed class ApiTestCollection : ICollectionFixture<ApiTestFixture>;
