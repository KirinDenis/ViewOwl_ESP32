namespace ViewOwl.Data.Models
{
    /// <summary>
    /// A device flashed from the landing page without a user account.
    /// Stores the GUID baked into the firmware via serial provisioning so that
    /// <c>GuestDeviceGrabWorker</c> can keep a fresh <c>.bin</c> file in the
    /// exchange folder.  No login is possible with a guest device.
    /// </summary>
    public sealed class GuestDevice
    {
        /// <summary>Primary key.</summary>
        public int Id { get; set; }

        /// <summary>
        /// Unique token sent to the device during serial provisioning.
        /// Matches the GUID the device sends in every UDP HELLO packet.
        /// </summary>
        public Guid Guid { get; set; }

        /// <summary>Template file name, e.g. <c>lw-airlock.html</c>.</summary>
        public string TemplateName { get; set; } = string.Empty;

        /// <summary>
        /// DB primary key of the <see cref="Template"/> record selected at flash time,
        /// or <see langword="null"/> for legacy guest devices registered before the
        /// IsPublic / public-templates migration (those use a bare filename in <see cref="TemplateName"/>).
        /// When set, <c>GuestDeviceRefreshWorker</c> routes the grab through
        /// <c>SiteTemplateController</c> as <c>t{TemplateId}.html</c> instead of looking
        /// up a static file in <c>SitesTemplates/</c>.
        /// </summary>
        public int? TemplateId { get; set; }

        /// <summary>Display pixel width reported at flash time.</summary>
        public int DisplayWidth { get; set; }

        /// <summary>Display pixel height reported at flash time.</summary>
        public int DisplayHeight { get; set; }

        /// <summary>UTC timestamp when the guest device was registered.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// UTC timestamp of the most recent successful grab, or <c>null</c> if never grabbed.
        /// </summary>
        public DateTime? LastGrabbedAt { get; set; }
    }
}
