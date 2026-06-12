namespace ViewOwl.Data.Models
{
    /// <summary>
    /// Access roles available to a <see cref="User"/>.
    /// </summary>
    public enum UserRole
    {
        /// <summary>Full access including user management.</summary>
        Admin,

        /// <summary>Standard access: own devices and templates only.</summary>
        User
    }
}
