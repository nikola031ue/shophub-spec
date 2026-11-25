using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using ShopHub.Api.Auth;

namespace ShopHub.Api.Accounts;

/// <summary>Accounts API (contracts/panel-api.md "Accounts", FR-001 to FR-004).</summary>
public static class AccountEndpoints
{
    public const string WrongCredentials = "Wrong email or password.";

    public sealed record Credentials(string? Email, string? Password);

    public sealed record PasswordChange(string? CurrentPassword, string? NewPassword);

    public sealed record SessionDto(bool SignedIn, string? Email);

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/session", SessionAsync);
        api.MapPost("/register", RegisterAsync).RequireXsrf().RequireRateLimiting(RateLimits.Registration);
        api.MapPost("/signin", SignInAsync).RequireXsrf();

        var owner = api.MapGroup("").RequireAuthorization(Sessions.Policy);
        owner.MapPost("/signout", SignOutAsync).RequireXsrf();
        owner.MapPost("/password", ChangePasswordAsync).RequireXsrf();
        return app;
    }

    /// <summary>Who is signed in; also (re)issues the XSRF cookie, so the panel can make changes.</summary>
    private static async Task<IResult> SessionAsync(
        HttpContext context, IAntiforgery antiforgery, AccountService accounts, CancellationToken ct)
    {
        Sessions.IssueXsrfCookie(context, antiforgery);
        return Sessions.OwnerId(context.User) is { } owner && await accounts.EmailAsync(owner, ct) is { } email
            ? Results.Ok(new SessionDto(true, email))
            : Results.Ok(new SessionDto(false, null));
    }

    /// <summary>202 with no body and no cookie, whether or not the email already has an account (FR-001).</summary>
    private static async Task<IResult> RegisterAsync(Credentials request, AccountService accounts, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (AccountRules.EmailProblem(request.Email) is { } emailProblem)
        {
            errors["email"] = [emailProblem];
        }

        if (AccountRules.PasswordProblem(request.Password) is { } passwordProblem)
        {
            errors["password"] = [passwordProblem];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await accounts.RegisterAsync(request.Email!, request.Password!, ct);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> SignInAsync(
        Credentials request, AccountService accounts, HttpContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["credentials"] = ["Enter your email address and password."],
            });
        }

        var result = await accounts.SignInAsync(request.Email, request.Password, ClientAddress.Of(context), ct);
        switch (result.Outcome)
        {
            case SignInOutcome.Locked:
                return Locked(context, result.RetryAfter);
            case SignInOutcome.Failed:
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: WrongCredentials);
        }

        await Sessions.SignInAsync(context, result.OwnerId, await accounts.StartSessionAsync(result.OwnerId, ct));
        return Results.NoContent();
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, CancellationToken ct)
    {
        await Sessions.SignOutAsync(context, ct);
        context.Response.Cookies.Delete(Sessions.XsrfCookie);
        return Results.NoContent();
    }

    /// <summary>Ends every other session of the owner; this one stays (FR-002).</summary>
    private static async Task<IResult> ChangePasswordAsync(
        PasswordChange request, AccountService accounts, HttpContext context, CancellationToken ct)
    {
        if (AccountRules.PasswordProblem(request.NewPassword) is { } problem)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [problem] });
        }

        var owner = Sessions.OwnerId(context.User)!.Value;
        var (outcome, retry) = await accounts.ChangePasswordAsync(
            owner, request.CurrentPassword ?? "", request.NewPassword!, ClientAddress.Of(context), ct);
        switch (outcome)
        {
            case PasswordChangeOutcome.Locked:
                return Locked(context, retry);
            case PasswordChangeOutcome.WrongCurrentPassword:
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The current password is wrong.");
        }

        await Sessions.EndOtherSessionsAsync(context, ct);
        return Results.NoContent();
    }

    private static IResult Locked(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.Headers.RetryAfter =
            Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many failed sign-ins. Try again later.");
    }
}
