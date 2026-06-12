namespace ViewOwl.Config
{
    /// <summary>
    /// JWT Bearer token configuration bound from <c>appsettings.json → Jwt</c>.
    /// </summary>
    public sealed class JwtConfig
    {
        /// <summary>
        /// HMAC-SHA256 signing secret. Must be at least 32 characters.
        /// Override in production via environment variable or secrets manager.
        /// </summary>
        public string Secret { get; set; } = string.Empty;

        /// <summary>Token validity period in minutes. Default: 60.</summary>
        public int ExpiryMinutes { get; set; } = 60;

        /// <summary>JWT issuer claim (<c>iss</c>).</summary>
        public string Issuer { get; set; } = "ViewOwl";

        /// <summary>JWT audience claim (<c>aud</c>).</summary>
        public string Audience { get; set; } = "ViewOwl";
    }
}
