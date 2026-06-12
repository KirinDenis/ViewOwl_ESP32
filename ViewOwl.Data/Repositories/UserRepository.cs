using Microsoft.EntityFrameworkCore;
using ViewOwl.Data.Models;
using ViewOwl.Data.Repositories.Interfaces;

namespace ViewOwl.Data.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IUserRepository"/>.
    /// </summary>
    public sealed class UserRepository : IUserRepository
    {
        private readonly ViewOwlDbContext _db;

        /// <summary>
        /// Initialises the repository with the scoped database context.
        /// </summary>
        /// <param name="db">The EF Core database context.</param>
        public UserRepository(ViewOwlDbContext db)
        {
            ArgumentNullException.ThrowIfNull(db);
            _db = db;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
            => await _db.Users.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<User?> GetByIdAsync(int id, CancellationToken ct = default)
            => await _db.Users.AsNoTracking()
                              .FirstOrDefaultAsync(u => u.Id == id, ct)
                              .ConfigureAwait(false);

        /// <inheritdoc/>
        public async Task<User?> GetByLoginAsync(string login, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(login);
            // Normalize to lower-invariant so lookup is case-insensitive (logins are stored lower-cased)
            string normalized = login.ToLowerInvariant();
            return await _db.Users.AsNoTracking()
                                  .FirstOrDefaultAsync(u => u.Login == normalized, ct)
                                  .ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<User> CreateAsync(User user, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(user);
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return user;
        }

        /// <inheritdoc/>
        public async Task UpdateAsync(User user, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(user);
            _db.Users.Update(user);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            User? user = await _db.Users.FindAsync([id], ct).ConfigureAwait(false);
            if (user is not null)
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async Task<bool> AnyAsync(CancellationToken ct = default)
            => await _db.Users.AnyAsync(ct).ConfigureAwait(false);
    }
}
