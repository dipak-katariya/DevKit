using Xunit;

namespace DevKit.Tests;

/// <summary>
/// SettingsService persists to one usersettings.json beside the test assembly. xUnit runs test
/// classes in parallel, so every class that touches that file joins this collection and they
/// take turns — otherwise one class's reset deletes the file another is reading.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsFileCollection
{
    public const string Name = "usersettings.json";
}
