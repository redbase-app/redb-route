namespace redb.Route.Firebase;

/// <summary>
/// Header constants for the Firebase Storage connector. Prefix: <c>redbStorage.</c>
/// </summary>
public static class FirebaseStorageHeaders
{
    /// <summary>Common prefix for all Storage headers.</summary>
    public const string Prefix = "redbStorage.";

    /// <summary>Bucket name.</summary>
    public const string BucketName = "redbStorage.BucketName";

    /// <summary>Object name (key) within the bucket.</summary>
    public const string ObjectName = "redbStorage.ObjectName";

    /// <summary>Content-Type MIME type of the object.</summary>
    public const string ContentType = "redbStorage.ContentType";

    /// <summary>Content-Length in bytes.</summary>
    public const string ContentLength = "redbStorage.ContentLength";

    /// <summary>MD5 hash of the object.</summary>
    public const string Md5Hash = "redbStorage.Md5Hash";

    /// <summary>CRC32C checksum.</summary>
    public const string Crc32c = "redbStorage.Crc32c";

    /// <summary>Object generation (version) number.</summary>
    public const string Generation = "redbStorage.Generation";

    /// <summary>Metadata generation number.</summary>
    public const string MetaGeneration = "redbStorage.MetaGeneration";

    /// <summary>Object creation timestamp.</summary>
    public const string TimeCreated = "redbStorage.TimeCreated";

    /// <summary>Last update timestamp.</summary>
    public const string Updated = "redbStorage.Updated";

    /// <summary>Direct download URL (media link).</summary>
    public const string MediaLink = "redbStorage.MediaLink";

    /// <summary>Prefix for custom metadata entries.</summary>
    public const string MetadataPrefix = "redbStorage.Meta.";

    /// <summary>Number of objects in a list result.</summary>
    public const string ObjectCount = "redbStorage.ObjectCount";
}
