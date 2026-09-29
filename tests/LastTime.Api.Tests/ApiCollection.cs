namespace LastTime.Api.Tests;

/// <summary>All API tests share one database, so they run sequentially in a single collection.</summary>
[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";
}
