namespace Omnichannel.Application.Common.Abstractions;

/// <summary>
/// The actor performing the current operation. In the API this is the authenticated user;
/// in workers it is the worker identity. Used for CreatedBy/UpdatedBy auditing.
/// </summary>
public interface ICurrentUser
{
    /// <summary>User id, or "system:{name}" for background processing. Null when anonymous.</summary>
    string? UserId { get; }
}
