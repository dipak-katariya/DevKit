using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace DevKit.Web.Services;

/// <summary>
/// Shared mode: how many pages (Blazor circuits, open or kept for a while after their connection dropped) each person
/// has, at most <see cref="MaxPerPerson"/>. Each holds its page's data on the server, so without a limit one account
/// could take the memory everyone shares.
/// </summary>
internal sealed class PersonCircuits
{
    /// <summary>Far more tabs than anyone works in, few enough that one account cannot fill the server.</summary>
    public const int MaxPerPerson = 30;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, string> _personOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _count = new(StringComparer.Ordinal);

    /// <summary>Counts <paramref name="circuit"/> for <paramref name="person"/>; false, counting nothing, when they have the most already.</summary>
    public bool TryOpen(string person, string circuit)
    {
        lock (_sync)
        {
            var open = _count.GetValueOrDefault(person);
            if (open >= MaxPerPerson)
            {
                return false;
            }

            _count[person] = open + 1;
            _personOf[circuit] = person;
            return true;
        }
    }

    /// <summary>Stops counting <paramref name="circuit"/>; nothing happens for one that was never counted.</summary>
    public void Close(string circuit)
    {
        lock (_sync)
        {
            if (!_personOf.Remove(circuit, out var person))
            {
                return;
            }

            var left = _count[person] - 1;
            if (left > 0)
            {
                _count[person] = left;
            }
            else
            {
                _count.Remove(person);
            }
        }
    }

    /// <summary>How many pages <paramref name="person"/> has now, for the tests.</summary>
    internal int OpenFor(string person)
    {
        lock (_sync)
        {
            return _count.GetValueOrDefault(person);
        }
    }
}

/// <summary>
/// Counts each new page against its person's <see cref="PersonCircuits.MaxPerPerson"/>, and refuses the page past it: the
/// circuit ends before anything of the page is made.
/// </summary>
internal sealed class CircuitsPerPerson : CircuitHandler
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly PersonCircuits _circuits;
    private readonly ILogger<CircuitsPerPerson> _logger;

    public CircuitsPerPerson(AuthenticationStateProvider authentication, PersonCircuits circuits, ILogger<CircuitsPerPerson> logger)
    {
        _authentication = authentication;
        _circuits = circuits;
        _logger = logger;
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        // The WebSocket needs a sign-in, so every page has its person; the account's name stays out of the log.
        var person = (await _authentication.GetAuthenticationStateAsync()).User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (person is not null && !_circuits.TryOpen(person, circuit.Id))
        {
            _logger.LogWarning("Refused a new page for an account that has {Pages} DevKit pages already, the most one account may have", PersonCircuits.MaxPerPerson);
            throw new InvalidOperationException(
                $"More than {PersonCircuits.MaxPerPerson} DevKit pages are open for this account. Close some, then reload this page.");
        }
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        _circuits.Close(circuit.Id);
        return Task.CompletedTask;
    }
}
