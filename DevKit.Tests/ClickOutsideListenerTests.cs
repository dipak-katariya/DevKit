using System.Reflection;
using DevKit.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The outside-click listener's bookkeeping: one listener while a panel is open, none while it is closed, and
/// nothing left behind after app.js drops it or the component goes away.
/// </summary>
public sealed class ClickOutsideListenerTests
{
    private readonly ClickOutsideListener _listener = new();

    [Fact]
    public void Opening_adds_the_listener_once()
    {
        Assert.Equal(ListenerChange.Add, _listener.Sync(open: true));
        Assert.Equal(ListenerChange.None, _listener.Sync(open: true));
    }

    [Fact]
    public void A_closed_panel_never_listens()
    {
        Assert.Equal(ListenerChange.None, _listener.Sync(open: false));
        Assert.False(_listener.Release());
    }

    [Fact]
    public void Closing_removes_the_listener_once()
    {
        _listener.Sync(open: true);

        Assert.Equal(ListenerChange.Remove, _listener.Sync(open: false));
        Assert.Equal(ListenerChange.None, _listener.Sync(open: false));
    }

    [Fact]
    public void A_listener_app_js_dropped_is_not_removed_again()
    {
        _listener.Sync(open: true);
        _listener.Dropped();

        Assert.Equal(ListenerChange.None, _listener.Sync(open: false));
    }

    [Fact]
    public void A_panel_its_owner_keeps_open_after_an_outside_click_listens_again()
    {
        _listener.Sync(open: true);
        _listener.Dropped();

        Assert.Equal(ListenerChange.Add, _listener.Sync(open: true));
    }

    [Fact]
    public void Releasing_while_listening_asks_for_the_listener_to_be_removed_once()
    {
        _listener.Sync(open: true);

        Assert.True(_listener.Release());
        Assert.False(_listener.Release());
    }

    [Fact]
    public void Releasing_after_app_js_dropped_the_listener_removes_nothing()
    {
        _listener.Sync(open: true);
        _listener.Dropped();

        Assert.False(_listener.Release());
    }

    [Fact]
    public void After_release_nothing_is_added_or_removed()
    {
        _listener.Sync(open: true);
        _listener.Release();

        Assert.Equal(ListenerChange.None, _listener.Sync(open: true));
        Assert.Equal(ListenerChange.None, _listener.Sync(open: false));
        Assert.Equal(ListenerChange.None, _listener.Sync(open: true));
    }

    [Fact]
    public void Every_component_answering_CloseDropdown_derives_from_ClickOutsideComponent()
    {
        var components = typeof(App).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IComponent).IsAssignableFrom(t))
            .ToList();

        var answering = components
            .Where(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.Name == nameof(ClickOutsideComponent.CloseDropdown) && m.GetCustomAttribute<JSInvokableAttribute>() is not null))
            .ToList();

        Assert.Empty(answering.Where(t => !typeof(ClickOutsideComponent).IsAssignableFrom(t)).Select(t => t.Name));
        Assert.Superset(
            new HashSet<string> { "GlassDropdown", "MultiSelectDropdown", "BranchPicker", "Popover", "CommandSearch" },
            answering.Select(t => t.Name).ToHashSet());
    }
}
