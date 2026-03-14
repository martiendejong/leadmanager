namespace LeadManager.Api.DTOs;

public record LoginRequest(string Email, string Password);

public record LoginResponse(string Token, DateTime ExpiresAt, UserDto User);

public record UserDto(string Id, string Email, string FirstName, string LastName, string Role, bool IsActive, DateTime CreatedAt);

public record CreateUserRequest(
    string Email,
    string FirstName,
    string LastName,
    string Password,
    string Role);

public record UpdateUserRequest(
    string FirstName,
    string LastName,
    bool IsActive,
    string Role);
