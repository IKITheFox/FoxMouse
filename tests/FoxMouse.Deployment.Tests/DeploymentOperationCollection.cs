namespace FoxMouse.Deployment.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeploymentOperationCollection
{
    public const string Name = "Deployment operation serialization";
}
