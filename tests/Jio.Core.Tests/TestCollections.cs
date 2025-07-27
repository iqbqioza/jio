using Xunit;

namespace Jio.Core.Tests;

[CollectionDefinition("Command Tests", DisableParallelization = true)]
public class CommandTestCollection
{
    // This class has no code, and is never created.
    // Its purpose is to be the place to apply [CollectionDefinition]
    // and all the ICollectionFixture<> interfaces.
}