using Omnichannel.Application.Auth;
using Omnichannel.Application.Users;

namespace Omnichannel.Tests.Validation;

public sealed class UserValidatorTests
{
    private const string ValidPassword = "Passw0rd-example";

    private readonly CreateUserRequestValidator _create = new();
    private readonly ChangeUserRoleRequestValidator _role = new();
    private readonly ResetUserPasswordRequestValidator _reset = new();
    private readonly UserListQueryValidator _list = new();

    [Theory]
    [InlineData("Admin", "Admin")]
    [InlineData("admin", "Admin")]
    [InlineData("MANAGER", "Manager")]
    [InlineData(" staff ", "Staff")]
    public void Role_names_are_mapped_case_insensitively_to_the_canonical_name(string input, string expected)
    {
        Assert.True(AppRoles.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Contains(normalized, AppRoles.All);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Owner")]
    [InlineData("Admin,Staff")]
    [InlineData("Admins")]
    public void Unknown_role_names_are_not_mapped(string? input)
    {
        Assert.False(AppRoles.TryNormalize(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("manager")]
    [InlineData("STAFF")]
    public void Create_accepts_a_valid_request_for_every_role(string role)
    {
        Assert.True(_create.Validate(new CreateUserRequest("new.user@example.com", ValidPassword, role)).IsValid);
    }

    [Theory]
    [InlineData("", ValidPassword, "Staff", "email")]
    [InlineData("not-an-email", ValidPassword, "Staff", "email")]
    [InlineData("user@example.com", "", "Staff", "password")]
    [InlineData("user@example.com", ValidPassword, "", "role")]
    [InlineData("user@example.com", ValidPassword, "Owner", "role")]
    public void Create_rejects_invalid_fields_with_json_keys(string email, string password, string role, string expectedKey)
    {
        var result = _create.Validate(new CreateUserRequest(email, password, role));

        Assert.Equal([expectedKey], result.Errors.Select(e => e.PropertyName).Distinct());
    }

    [Fact]
    public void Create_bounds_email_and_password_length()
    {
        var longEmail = $"{new string('a', LoginRequestValidator.MaxEmailLength)}@example.com";
        var longPassword = new string('P', LoginRequestValidator.MaxPasswordLength + 1);

        Assert.False(_create.Validate(new CreateUserRequest(longEmail, ValidPassword, "Staff")).IsValid);
        Assert.False(_create.Validate(new CreateUserRequest("user@example.com", longPassword, "Staff")).IsValid);
    }

    [Fact]
    public void Create_leaves_the_password_policy_to_identity()
    {
        // Shape only: a weak but non-empty password passes the validator; Identity rejects it with its own messages.
        Assert.True(_create.Validate(new CreateUserRequest("user@example.com", "weak", "Staff")).IsValid);
    }

    [Fact]
    public void Unknown_role_message_lists_the_roles_and_not_the_input()
    {
        var error = Assert.Single(_role.Validate(new ChangeUserRoleRequest("Owner")).Errors);

        Assert.Equal("role", error.PropertyName);
        Assert.Contains("Admin, Manager, Staff", error.ErrorMessage);
        Assert.DoesNotContain("Owner", error.ErrorMessage);
    }

    [Fact]
    public void Role_change_requires_a_known_role()
    {
        Assert.True(_role.Validate(new ChangeUserRoleRequest("manager")).IsValid);
        Assert.False(_role.Validate(new ChangeUserRoleRequest("")).IsValid);
    }

    [Fact]
    public void Reset_requires_a_bounded_password_under_the_key_newPassword()
    {
        Assert.True(_reset.Validate(new ResetUserPasswordRequest(ValidPassword)).IsValid);
        Assert.Equal("newPassword", Assert.Single(_reset.Validate(new ResetUserPasswordRequest("")).Errors).PropertyName);
        Assert.False(_reset.Validate(new ResetUserPasswordRequest(new string('P', LoginRequestValidator.MaxPasswordLength + 1))).IsValid);
    }

    [Fact]
    public void List_query_whitelists_sort_and_role()
    {
        Assert.True(_list.Validate(new UserListQuery()).IsValid);
        Assert.True(_list.Validate(new UserListQuery { SortBy = "EMAIL", SortDir = "desc", Role = "admin", Locked = true }).IsValid);
        Assert.False(_list.Validate(new UserListQuery { SortBy = "role" }).IsValid);
        Assert.False(_list.Validate(new UserListQuery { Role = "Owner" }).IsValid);
        Assert.True(_list.Validate(new UserListQuery { Role = " " }).IsValid); // blank = no filter
    }
}
