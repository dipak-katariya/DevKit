using System.Reflection;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Blazor disposes a component only when it declares IDisposable or IAsyncDisposable. A component that
/// writes a Dispose method without declaring the interface never has it called, so its timers, listeners
/// and subscriptions outlive the page.
/// </summary>
public sealed class ComponentDisposalTests
{
    private static readonly Type[] Components = typeof(DevKit.Web.Components.App).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IComponent).IsAssignableFrom(t))
        .ToArray();

    private static bool Declares(Type component, string method) =>
        component.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } found
        && found.DeclaringType == component;

    [Fact]
    public void Every_component_with_a_Dispose_method_is_disposed_by_Blazor()
    {
        var undisposed = Components
            .Where(c => Declares(c, nameof(IDisposable.Dispose)) && !typeof(IDisposable).IsAssignableFrom(c))
            .Select(c => c.Name);

        Assert.Empty(undisposed);
    }

    [Fact]
    public void Every_component_with_a_DisposeAsync_method_is_disposed_by_Blazor()
    {
        var undisposed = Components
            .Where(c => Declares(c, nameof(IAsyncDisposable.DisposeAsync)) && !typeof(IAsyncDisposable).IsAssignableFrom(c))
            .Select(c => c.Name);

        Assert.Empty(undisposed);
    }

    [Fact]
    public void The_scan_sees_the_app_components()
    {
        Assert.Contains(Components, c => c.Name == "WorkdayCalculator");
        Assert.True(Components.Length > 20);
    }
}
