using ViewOwl.Data.Models;

namespace ViewOwl.Data.Repositories.Interfaces
{
    /// <summary>
    /// Data access contract for <see cref="User"/> entities.
    /// </summary>
    public interface IUserRepository
    {
        /// <summary>Returns all users (no-tracking).</summary>
        Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

        /// <summary>Returns the user with the given <paramref name="id"/>, or <c>null</c>.</summary>
        Task<User?> GetByIdAsync(int id, CancellationToken ct = default);

        /// <summary>Returns the user with the given <paramref name="login"/>, or <c>null</c>.</summary>
        Task<User?> GetByLoginAsync(string login, CancellationToken ct = default);

        /// <summary>Persists a new user and returns the saved entity.</summary>
        Task<User> CreateAsync(User user, CancellationToken ct = default);

        /// <summary>Saves changes to an existing user.</summary>
        Task UpdateAsync(User user, CancellationToken ct = default);

        /// <summary>Deletes the user with the given <paramref name="id"/>. No-op if not found.</summary>
        Task DeleteAsync(int id, CancellationToken ct = default);

        /// <summary>Returns <c>true</c> when at least one user exists in the database.</summary>
        Task<bool> AnyAsync(CancellationToken ct = default);
    }
}
