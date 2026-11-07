namespace redb.Route.Mail;

/// <summary>Security mode for mail connections.</summary>
public enum MailSecurityMode
{
    /// <summary>No encryption (port 25/110/143).</summary>
    None,

    /// <summary>Implicit SSL/TLS — connect encrypted from the start (port 465/993/995).</summary>
    Ssl,

    /// <summary>Explicit STARTTLS — upgrade cleartext to TLS after connect (port 587/143/110).</summary>
    StartTls,

    /// <summary>Auto-detect based on port number.</summary>
    Auto
}

/// <summary>Authentication mechanism for mail connections.</summary>
public enum MailAuthMechanism
{
    /// <summary>Auto-select (let MailKit choose the best available).</summary>
    Auto,

    /// <summary>PLAIN (RFC 4616).</summary>
    Plain,

    /// <summary>LOGIN.</summary>
    Login,

    /// <summary>CRAM-MD5 (RFC 2195).</summary>
    CramMd5,

    /// <summary>XOAUTH2 (Google, Microsoft).</summary>
    XOAuth2,

    /// <summary>OAUTHBEARER (RFC 7628).</summary>
    OAuthBearer,

    /// <summary>NTLM (Windows Integrated).</summary>
    Ntlm
}

/// <summary>What to do with messages after processing (consumer).</summary>
public enum PostProcessAction
{
    /// <summary>Leave message untouched.</summary>
    None,

    /// <summary>Mark as read (IMAP: set \Seen flag).</summary>
    MarkRead,

    /// <summary>Delete the message.</summary>
    Delete,

    /// <summary>Move to another folder (IMAP only).</summary>
    Move,

    /// <summary>Mark as read and move to another folder (IMAP only).</summary>
    MarkReadAndMove,

    /// <summary>Flag the message (IMAP: set \Flagged).</summary>
    Flag
}

/// <summary>Sort order for fetched messages.</summary>
public enum MailSortBy
{
    /// <summary>No sorting (server order).</summary>
    None,

    /// <summary>Oldest first (by date).</summary>
    DateAsc,

    /// <summary>Newest first (by date).</summary>
    DateDesc,

    /// <summary>By subject ascending.</summary>
    SubjectAsc,

    /// <summary>By sender ascending.</summary>
    FromAsc,

    /// <summary>By size ascending.</summary>
    SizeAsc,

    /// <summary>By size descending.</summary>
    SizeDesc
}

/// <summary>IMAP search filter for message selection.</summary>
public enum MailFetchFilter
{
    /// <summary>Only unseen (unread) messages.</summary>
    Unseen,

    /// <summary>All messages.</summary>
    All,

    /// <summary>Only recent messages (IMAP \Recent flag).</summary>
    Recent,

    /// <summary>Only flagged messages.</summary>
    Flagged,

    /// <summary>Only answered messages.</summary>
    Answered,

    /// <summary>Unanswered messages.</summary>
    Unanswered
}
