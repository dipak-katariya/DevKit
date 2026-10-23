using System.Net;
using System.Security.Claims;
using DevKit.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace DevKit.Web.Services;

/// <summary>Hosted mode's sign-in routes: the page, Windows single sign-on, a typed Windows password, and sign-out.</summary>
internal static class SignInEndpoints
{
    public const string SignInPath = "/signin";
    public const string ReturnUrlParameter = "returnUrl";

    public static void MapSignInEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(SignInPath, (HttpContext context, SignInFlow flow, [FromQuery(Name = ReturnUrlParameter)] string? returnUrl) =>
                flow.ShowPage(context, returnUrl))
            .AllowAnonymous();
        app.MapGet(SignInFlow.WindowsPath, (HttpContext context, SignInFlow flow, [FromQuery(Name = ReturnUrlParameter)] string? returnUrl) =>
                flow.SignInWithWindowsAsync(context, returnUrl))
            .AllowAnonymous()
            .RequireRateLimiting(HostedAccess.WindowsSignInRateLimitPolicy);
        app.MapPost(SignInFlow.PasswordPath, (HttpContext context, SignInFlow flow, [FromForm] PasswordSignInForm form) =>
                flow.SignInWithPasswordAsync(context, form))
            .AllowAnonymous()
            .RequireRateLimiting(HostedAccess.PasswordSignInRateLimitPolicy);
        app.MapPost(SignInFlow.SignOutPath, (HttpContext context, SignInFlow flow) => flow.SignOutAsync(context));
    }
}

/// <summary>What the "sign in as someone else" form posts.</summary>
internal sealed class PasswordSignInForm
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? ReturnUrl { get; set; }
}

/// <summary>
/// Signing people in and out of shared DevKit. A sign-in becomes a session cookie that lasts
/// <see cref="DevKitHostingOptions.SessionDays"/>; every sign-in, failed attempt and sign-out goes to the audit log.
/// Passwords are handed to Windows to check and never kept, logged or shown again.
/// </summary>
internal sealed class SignInFlow
{
    public const string WindowsPath = "/signin/windows";
    public const string PasswordPath = "/signin/password";
    public const string SignOutPath = "/signout";

    private const string WindowsMethod = "windows";
    private const string PasswordMethod = "password";

    // What the audit log calls each event.
    private const string SignedInEvent = "sign-in";
    private const string FailedEvent = "failed";
    private const string SignedOutEvent = "sign-out";
    private const string RefusedSignOutEvent = "refused sign-out";
    private const string RefusedSessionEvent = "refused session";
    private const string NoSingleSignOnMessage = "This browser did not sign in with your Windows account.";
    private const string NoPasswordOverHttpMessage =
        "A password cannot be typed here: this page is not served over https, so it would cross the network unencrypted.";

    private readonly DevKitHostingOptions _options;
    private readonly IAntiforgery _antiforgery;
    private readonly SessionRegistry _sessions;
    private readonly WindowsSignIn _windows;
    private readonly SignInAudit _audit;

    public SignInFlow(
        DevKitHostingOptions options, IAntiforgery antiforgery, SessionRegistry sessions, WindowsSignIn windows, SignInAudit audit)
    {
        _options = options;
        _antiforgery = antiforgery;
        _sessions = sessions;
        _windows = windows;
        _audit = audit;
    }

    /// <summary>The sign-in page, or straight on to the page asked for when this browser is already signed in.</summary>
    public IResult ShowPage(HttpContext context, string? returnUrl) =>
        _sessions.IsActive(context.User)
            ? Results.Redirect(LocalUrl(returnUrl))
            : Page(context, returnUrl, "", "", StatusCodes.Status200OK);

    /// <summary>
    /// Single sign-on. The first request challenges the browser, which answers with the Windows account it is signed in
    /// as (automatically on the domain, or after asking), over the next two requests. A browser that does not answer
    /// sees the sign-in page again, with the reason, in the body of that challenge.
    /// </summary>
    public async Task SignInWithWindowsAsync(HttpContext context, string? returnUrl)
    {
        var step = _windows.ContinueSingleSignOn(context);
        switch (step.State)
        {
            case SingleSignOnState.Completed when step.User is { } user:
                await StartSessionAsync(context, user, WindowsMethod);
                context.Response.Redirect(LocalUrl(returnUrl));
                return;
            case SingleSignOnState.Continue:
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            case SingleSignOnState.Failed:
                await _audit.RecordAsync(context, FailedEvent, WindowsMethod, "", step.Error);
                break;
        }

        await Page(context, returnUrl, NoSingleSignOnMessage, "", StatusCodes.Status401Unauthorized).ExecuteAsync(context);
    }

    /// <summary>
    /// "Sign in as someone else": Windows checks the typed username and password. Only over https or on this machine,
    /// where the password never crosses the network readable (<see cref="AcceptsPassword"/>).
    /// </summary>
    public async Task<IResult> SignInWithPasswordAsync(HttpContext context, PasswordSignInForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var typed = form.Username ?? "";
        if (!AcceptsPassword(context))
        {
            await _audit.RecordAsync(context, FailedEvent, PasswordMethod, typed, "password sent over plain http from another machine");
            return Page(context, form.ReturnUrl, NoPasswordOverHttpMessage, "", StatusCodes.Status403Forbidden);
        }

        var check = _windows.CheckPassword(typed, form.Password ?? "");
        if (check.User is not { } user)
        {
            await _audit.RecordAsync(context, FailedEvent, PasswordMethod, typed, check.Detail);
            return Page(context, form.ReturnUrl, check.Error, typed, StatusCodes.Status401Unauthorized);
        }

        await StartSessionAsync(context, user, PasswordMethod);
        return Results.Redirect(LocalUrl(form.ReturnUrl));
    }

    /// <summary>Ends this session in every tab, and goes back to the sign-in page for the next person.</summary>
    public async Task<IResult> SignOutAsync(HttpContext context)
    {
        if (!await _antiforgery.IsRequestValidAsync(context))
        {
            await _audit.RecordAsync(context, RefusedSignOutEvent, "", context.User.Identity?.Name ?? "", "the request did not come from a DevKit page");
            return Results.BadRequest();
        }

        _sessions.SignOut(context.User);
        await _audit.RecordAsync(context, SignedOutEvent, context.User.FindFirstValue(SessionClaims.Method) ?? "", context.User.Identity?.Name ?? "");
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect(SignInEndpoints.SignInPath);
    }

    /// <summary>
    /// Every request's session check: the cookie must be a session that has neither ended nor been signed out, used
    /// from the address it signed in from. A refused cookie is deleted; one used from another address is a copy or a
    /// moved machine, and goes to the audit log.
    /// </summary>
    public async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal is not { } principal || !_sessions.IsActive(principal))
        {
            await RefuseSessionAsync(context);
            return;
        }

        if (SessionClaims.CameFrom(principal, LocalAccessGate.ClientAddress(context.HttpContext)))
        {
            return;
        }

        await _audit.RecordAsync(context.HttpContext, RefusedSessionEvent, principal.FindFirstValue(SessionClaims.Method) ?? "",
            principal.Identity?.Name ?? "", $"signed in from {principal.FindFirstValue(SessionClaims.Address)}, used from another address");
        await RefuseSessionAsync(context);
    }

    /// <summary>
    /// Whether a typed password may be sent here: over https, or from this machine itself. Over plain http from
    /// another machine anyone on the way could read it, so the page offers no password form and a post is refused.
    /// </summary>
    internal static bool AcceptsPassword(HttpContext context) =>
        context.Request.IsHttps || LocalAccessGate.IsFromThisMachine(context);

    /// <summary>
    /// Only a path on this site: anything else (another site, "//host", "/\host") would send the person on to a page
    /// DevKit does not control once they have signed in.
    /// </summary>
    internal static string LocalUrl(string? url) =>
        url is { Length: > 0 }
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl)
            ? url
            : "/";

    private static Task RefuseSessionAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private async Task StartSessionAsync(HttpContext context, SignedInUser user, string method)
    {
        var (principal, expires) = _sessions.Start(user, method, _options.SessionLength, LocalAccessGate.ClientAddress(context));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expires, AllowRefresh = false });
        await _audit.RecordAsync(context, SignedInEvent, method, user.Name);
    }

    /// <summary>The page with its form token; the antiforgery check also marks the response as never cached.</summary>
    private IResult Page(HttpContext context, string? returnUrl, string error, string typedUser, int status)
    {
        var tokens = _antiforgery.GetAndStoreTokens(context);
        return Results.Content(
            SignInPage.Render(new SignInPage.Model(
                _options.Domain,
                LocalUrl(returnUrl),
                tokens.FormFieldName,
                tokens.RequestToken ?? "",
                error,
                typedUser,
                AcceptsPassword(context))),
            "text/html; charset=utf-8",
            statusCode: status);
    }
}

/// <summary>The sign-in page's HTML. Everything taken from the request or the configuration is HTML-encoded.</summary>
internal static class SignInPage
{
    /// <param name="AcceptsPassword">Whether a typed password may be sent (<see cref="SignInFlow.AcceptsPassword"/>); without it the form is not shown.</param>
    public sealed record Model(
        string Domain, string ReturnUrl, string TokenField, string Token, string Error, string TypedUser, bool AcceptsPassword);

    private const string NoPasswordNotice = """
          <p class="signin-hint signin-warn">Typing a password is turned off here: this page is not served over https, so
          the password would cross the network unencrypted. "Sign in with your Windows account" sends no password.</p>
        """;

    public static string Render(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var domain = WebUtility.HtmlEncode(model.Domain);
        var windowsHref = WebUtility.HtmlEncode($"{SignInFlow.WindowsPath}?{SignInEndpoints.ReturnUrlParameter}={Uri.EscapeDataString(model.ReturnUrl)}");
        var error = model.Error.Length == 0 ? "" : $"""<p class="signin-error" role="alert">{WebUtility.HtmlEncode(model.Error)}</p>""";
        var password = model.AcceptsPassword ? PasswordForm(model, domain) : NoPasswordNotice;

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Sign in · DevKit</title>
            <link rel="icon" type="image/svg+xml" href="/favicon.svg?v={{App.AssetVersion}}" />
            <link rel="stylesheet" href="/app.css?v={{App.AssetVersion}}" />
            <link rel="stylesheet" href="/signin.css?v={{App.AssetVersion}}" />
            </head>
            <body class="signin-page">
            <main class="signin-card">
              <div class="signin-brand"><span class="profile-avatar">DK</span><span>DevKit</span></div>
              <h1>Sign in</h1>
              <p class="signin-lead">DevKit works in TFS as you, so it needs to know who you are.</p>
              {{error}}
              <a class="signin-button signin-primary" href="{{windowsHref}}"><span class="signin-windows-logo" aria-hidden="true"><i></i><i></i><i></i><i></i></span>Sign in with your Windows account</a>
              <p class="signin-hint">Single sign-on: nothing to type if this browser is on the {{domain}} domain.</p>
              <div class="signin-divider"><span>or sign in as someone else</span></div>
            {{password}}
              <footer class="signin-footer">
                After you sign in, your TFS token and settings are kept encrypted in this browser on your PC, for your Windows account only, and never on the DevKit server.
                Sign-ins, sign-outs and failed attempts are recorded as user@machine in the server's audit log.
              </footer>
            </main>
            </body>
            </html>
            """;
    }

    private static string PasswordForm(Model model, string domain) =>
        $$"""
              <form method="post" action="{{SignInFlow.PasswordPath}}">
                <input type="hidden" name="{{WebUtility.HtmlEncode(model.TokenField)}}" value="{{WebUtility.HtmlEncode(model.Token)}}" />
                <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(model.ReturnUrl)}}" />
                <label class="signin-label" for="username">Windows username</label>
                <input class="signin-input" id="username" name="username" autocomplete="username" maxlength="{{CredentialCheck.MaxLength}}" required
                       placeholder="{{domain}}\username" value="{{WebUtility.HtmlEncode(model.TypedUser)}}" />
                <label class="signin-label" for="password">Password</label>
                <input class="signin-input" id="password" name="password" type="password" autocomplete="current-password"
                       maxlength="{{CredentialCheck.MaxLength}}" required placeholder="your domain password" />
                <button class="signin-button signin-primary" type="submit">Sign in</button>
              </form>
              <p class="signin-hint">Checked against the {{domain}} domain by Windows itself. DevKit has no password of its own and never stores yours.</p>
            """;
}
