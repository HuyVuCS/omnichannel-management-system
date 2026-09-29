using System.Runtime.CompilerServices;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// FluentValidation's property-name resolver is process-wide. The API sets it at start-up, which in this test process
    /// happens whenever the first integration test runs; setting it before any test makes validator unit tests deterministic.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => ValidationConventions.UseJsonPropertyNames();
}
