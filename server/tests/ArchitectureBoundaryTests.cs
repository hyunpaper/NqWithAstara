using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Domain_does_not_reference_application_or_host_assemblies()
    {
        var references = typeof(StructurePolicy).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name is "Astra.Application" or "Astra.Server" ||
            reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Application_references_domain()
    {
        var references = typeof(ILocalStore).Assembly.GetReferencedAssemblies();

        Assert.Contains(references, reference => reference.Name == "Astra.Domain");
    }

    [Fact]
    public void Application_does_not_reference_server_or_host_assemblies()
    {
        var references = typeof(ILocalStore).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference =>
            reference.Name == "Astra.Server" ||
            reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true ||
            reference.Name?.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) == true);
    }
}
