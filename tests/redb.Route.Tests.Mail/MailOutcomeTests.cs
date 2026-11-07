using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Mail;
using redb.Route.Processors;

namespace redb.Route.Tests.Mail;

/// <summary>
/// What the imap and pop3 consumers do with a message after its exchange, against GreenMail (SMTP 3025, IMAP 3143,
/// POP3 3110, API 8080): <c>docker compose -f docker-compose.tests.yml up greenmail -d</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailOutcomeTests
{
    private static readonly HttpClient Http = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>How long a test that expects nothing more to happen watches: about ten polls at delay=300.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    private readonly string _user = $"outcome-{Guid.NewGuid():N}";

    private static async Task Provision(string login)
    {
        using var content = new StringContent(
            $"{{\"email\":\"{login}@localhost\",\"login\":\"{login}\",\"password\":\"secret\"}}", Encoding.UTF8, "application/json");
        using var _ = await Http.PostAsync("http://localhost:8080/api/user", content);
    }

    /// <summary>Delivers one mail to the user; returns its Message-ID, or null when it was sent without one.</summary>
    private async Task<string?> Deliver(string subject, bool withMessageId = true)
    {
        await Provision("sender");
        await Provision(_user);
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("sender", "sender@localhost"));
        mime.To.Add(new MailboxAddress(_user, $"{_user}@localhost"));
        mime.Subject = subject;
        mime.Body = new TextPart("plain") { Text = "payload" };
        if (withMessageId)
            mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        else
            mime.Headers.Remove(HeaderId.MessageId);
        using var client = new SmtpClient();
        await client.ConnectAsync("localhost", 3025, MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync("sender", "secret");
        await client.SendAsync(mime);
        await client.DisconnectAsync(true);
        return withMessageId ? mime.MessageId : null;
    }

    private IConsumer Consumer(string protocol, string options, Func<IExchange, Task> route)
    {
        var uri = EndpointUriParser.Parse(
            $"{protocol}://localhost?port={(protocol == "imap" ? 3143 : 3110)}&username={_user}&password=secret" +
            $"&security=None&delay=300&{options}");
        IEndpoint endpoint = protocol == "imap"
            ? new ImapComponent().CreateEndpoint(uri)
            : new Pop3Component().CreateEndpoint(uri);
        return endpoint.CreateConsumer(new DelegateProcessor((exchange, _) => route(exchange)));
    }

    private static string Subject(IExchange exchange) => (string)exchange.In.Headers[MailHeaders.Subject]!;

    private static async Task Until(Func<bool> done, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? Wait);
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    private async Task<ImapClient> ImapSession()
    {
        var client = new ImapClient();
        await client.ConnectAsync("localhost", 3143, MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync(_user, "secret");
        return client;
    }

    /// <summary>Creates folders of the user's mailbox next to INBOX.</summary>
    private async Task CreateFolders(params string[] names)
    {
        await Provision(_user);
        using var client = await ImapSession();
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        foreach (var name in names)
            await root.CreateAsync(name, isMessageFolder: true);
        await client.DisconnectAsync(true);
    }

    /// <summary>The subjects in one folder of the user's mailbox, with whether each is marked read.</summary>
    private async Task<IReadOnlyList<(string Subject, bool Seen)>> ImapFolder(string name)
    {
        using var client = await ImapSession();
        var folder = await client.GetFolderAsync(name);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        IList<IMessageSummary> summaries = folder.Count == 0
            ? []
            : await folder.FetchAsync(0, -1, MessageSummaryItems.Envelope | MessageSummaryItems.Flags);
        var result = summaries
            .Select(s => (s.Envelope!.Subject ?? "", s.Flags.GetValueOrDefault().HasFlag(MessageFlags.Seen)))
            .ToList();
        await client.DisconnectAsync(true);
        return result;
    }

    private Task<IReadOnlyList<(string Subject, bool Seen)>> ImapInbox() => ImapFolder("INBOX");

    private async Task<IReadOnlyList<string>> Pop3Inbox()
    {
        using var client = new Pop3Client();
        await client.ConnectAsync("localhost", 3110, MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync(_user, "secret");
        var subjects = new List<string>();
        for (var i = 0; i < client.Count; i++)
            subjects.Add((await client.GetMessageHeadersAsync(i))[HeaderId.Subject] ?? "");
        await client.DisconnectAsync(true);
        return subjects;
    }

    // ── A throwing mail is one failed exchange ──────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imap_a_mail_whose_route_throws_does_not_hold_up_the_mails_after_it(bool idle)
    {
        await Deliver("bad");
        await Deliver("good");
        var routed = new ConcurrentQueue<string>();

        var consumer = Consumer("imap", $"fetchFilter=Unseen&postProcess=MarkRead&idle={(idle ? "true" : "false")}", exchange =>
        {
            routed.Enqueue(Subject(exchange));
            return Subject(exchange) == "bad"
                ? Task.FromException(new InvalidOperationException("route failed"))
                : Task.CompletedTask;
        });
        await consumer.Start();
        await Until(() => routed.Contains("good"));
        await consumer.Stop();

        routed.Should().Contain("good", "the mail after a failed one is routed in the same fetch");
        var inbox = await ImapInbox();
        inbox.Should().Contain(("good", true));
        inbox.Should().Contain(("bad", false), "a mail whose route threw is left as it is");
    }

    [Fact]
    public async Task Pop3_deletes_made_before_a_mail_whose_route_throws_are_committed()
    {
        await Deliver("good");
        await Deliver("bad");
        var routed = new ConcurrentQueue<string>();

        var consumer = Consumer("pop3", "postProcess=Delete", exchange =>
        {
            routed.Enqueue(Subject(exchange));
            return Subject(exchange) == "bad"
                ? Task.FromException(new InvalidOperationException("route failed"))
                : Task.CompletedTask;
        });
        await consumer.Start();
        await Until(() => routed.Count(s => s == "bad") >= 3);
        await consumer.Stop();

        routed.Count(s => s == "good").Should().Be(1, "the session ends with QUIT, so the delete of the good mail commits");
        (await Pop3Inbox()).Should().Equal("bad");
    }

    // ── Commit-time headers: redbMail.Delete, redbMail.MoveTo, redbMail.CopyTo ──

    /// <summary>Routes the delivered mails once each, setting the headers <paramref name="headers"/> gives per subject.</summary>
    private async Task RouteOnce(string protocol, string options, int mails,
        Func<string, IReadOnlyDictionary<string, object>> headers)
    {
        var routed = new ConcurrentQueue<string>();
        var consumer = Consumer(protocol, options, exchange =>
        {
            foreach (var (key, value) in headers(Subject(exchange)))
                exchange.In.Headers[key] = value;
            routed.Enqueue(Subject(exchange));
            return Task.CompletedTask;
        });
        await consumer.Start();
        await Until(() => routed.Count >= mails);
        await consumer.Stop();
        routed.Should().HaveCount(mails);
    }

    private static IReadOnlyDictionary<string, object> None() => new Dictionary<string, object>();

    private static IReadOnlyDictionary<string, object> Header(string key, object value) =>
        new Dictionary<string, object> { [key] = value };

    [Fact]
    public async Task Imap_delete_header_false_keeps_the_mail_read_where_postProcess_deletes()
    {
        await Deliver("keep");
        await Deliver("drop");

        await RouteOnce("imap", "postProcess=Delete", 2,
            subject => subject == "keep" ? Header(MailHeaders.Delete, false) : None());

        (await ImapInbox()).Should().Equal([("keep", true)]);
    }

    [Fact]
    public async Task Imap_delete_header_true_deletes_the_mail_where_postProcess_marks_it_read()
    {
        await Deliver("drop");
        await Deliver("keep");

        await RouteOnce("imap", "postProcess=MarkRead", 2,
            subject => subject == "drop" ? Header(MailHeaders.Delete, "true") : None());

        (await ImapInbox()).Should().Equal([("keep", true)]);
    }

    [Fact]
    public async Task Imap_moveTo_header_moves_the_mail_marked_read()
    {
        await CreateFolders("Archive");
        await Deliver("moved");
        await Deliver("stays");

        await RouteOnce("imap", "postProcess=MarkRead", 2,
            subject => subject == "moved" ? Header(MailHeaders.MoveTo, "Archive") : None());

        (await ImapInbox()).Should().Equal([("stays", true)]);
        (await ImapFolder("Archive")).Should().Equal([("moved", true)]);
    }

    [Fact]
    public async Task Imap_moveTo_header_with_delete_moves_the_mail_without_marking_it_read()
    {
        await CreateFolders("Archive");
        await Deliver("moved");

        await RouteOnce("imap", "postProcess=MarkRead", 1, _ => new Dictionary<string, object>
        {
            [MailHeaders.Delete] = true,
            [MailHeaders.MoveTo] = "Archive",
        });

        (await ImapInbox()).Should().BeEmpty();
        (await ImapFolder("Archive")).Should().Equal([("moved", false)]);
    }

    [Fact]
    public async Task Imap_copyTo_header_copies_the_mail_and_postProcess_still_applies()
    {
        await CreateFolders("Copies");
        await Deliver("copied");

        await RouteOnce("imap", "postProcess=MarkRead", 1, _ => Header(MailHeaders.CopyTo, "Copies"));

        (await ImapInbox()).Should().Equal([("copied", true)]);
        (await ImapFolder("Copies")).Should().Equal([("copied", false)], "the copy is taken before the original is marked read");
    }

    [Fact]
    public async Task Imap_the_headers_are_read_from_the_message_the_route_ended_with()
    {
        await Deliver("keep");
        var routed = new TaskCompletionSource();
        var consumer = Consumer("imap", "postProcess=Delete", exchange =>
        {
            var reply = new Message("reply");
            reply.Headers[MailHeaders.Delete] = false;
            exchange.Out = reply;
            routed.TrySetResult();
            return Task.CompletedTask;
        });
        await consumer.Start();
        await Task.WhenAny(routed.Task, Task.Delay(Wait));
        await consumer.Stop();

        (await ImapInbox()).Should().Equal([("keep", true)]);
    }

    [Fact]
    public async Task Pop3_delete_header_false_keeps_the_mail_where_postProcess_deletes()
    {
        await Deliver("keep");
        await Deliver("drop");

        await RouteOnce("pop3", "postProcess=Delete", 2,
            subject => subject == "keep" ? Header(MailHeaders.Delete, false) : None());

        (await Pop3Inbox()).Should().Equal("keep");
    }

    [Fact]
    public async Task Pop3_delete_header_true_deletes_the_mail_where_postProcess_leaves_it()
    {
        await Deliver("drop");
        await Deliver("keep");

        await RouteOnce("pop3", "postProcess=None", 2,
            subject => subject == "drop" ? Header(MailHeaders.Delete, true) : None());

        (await Pop3Inbox()).Should().Equal("keep");
    }

    // ── Outcome: a failed exchange rolls back, the message is left as it is ──

    /// <summary>How a route can end an exchange.</summary>
    public enum Ending { Unhandled, RollbackOnly, Handled }

    private static void End(IExchange exchange, Ending ending)
    {
        switch (ending)
        {
            case Ending.Unhandled:
                exchange.Exception = new InvalidOperationException("left on the exchange");
                break;
            case Ending.RollbackOnly:
                exchange.MarkRollbackOnly();
                break;
            case Ending.Handled:
                exchange.Exception = new InvalidOperationException("handled");
                exchange.ExceptionHandled = true;
                break;
        }
    }

    /// <summary>Routes one mail <paramref name="passes"/> times (or until the wait ends), ending each exchange the given way.</summary>
    private async Task<int> RouteEnding(string protocol, string options, Ending ending, int passes,
        IReadOnlyDictionary<string, object>? headers = null, TimeSpan? within = null)
    {
        var routed = 0;
        var consumer = Consumer(protocol, options, exchange =>
        {
            foreach (var (key, value) in headers ?? None())
                exchange.In.Headers[key] = value;
            End(exchange, ending);
            Interlocked.Increment(ref routed);
            return Task.CompletedTask;
        });
        await consumer.Start();
        await Until(() => Volatile.Read(ref routed) >= passes, within);
        await consumer.Stop();
        return routed;
    }

    [Theory]
    [InlineData(Ending.Unhandled)]
    [InlineData(Ending.RollbackOnly)]
    public async Task Imap_a_failed_exchange_leaves_the_mail_unseen_and_it_is_fetched_again(Ending ending)
    {
        await Deliver("failed");

        var passes = await RouteEnding("imap", "fetchFilter=Unseen&postProcess=Delete", ending, passes: 2);

        passes.Should().BeGreaterThanOrEqualTo(2, "with peek (the default) a rolled back mail stays unseen");
        (await ImapInbox()).Should().Equal([("failed", false)]);
    }

    [Fact]
    public async Task Imap_a_handled_failure_commits()
    {
        await Deliver("handled");

        await RouteEnding("imap", "fetchFilter=Unseen&postProcess=Delete", Ending.Handled, passes: 1);

        (await ImapInbox()).Should().BeEmpty("a handled failure is a success, as in Apache Camel");
    }

    [Fact]
    public async Task Imap_the_commit_headers_are_not_read_for_a_failed_exchange()
    {
        await Deliver("failed");

        await RouteEnding("imap", "fetchFilter=Unseen&postProcess=MarkRead", Ending.RollbackOnly, passes: 1,
            Header(MailHeaders.Delete, true));

        (await ImapInbox()).Should().Equal([("failed", false)]);
    }

    [Theory]
    [InlineData(Ending.Unhandled)]
    [InlineData(Ending.RollbackOnly)]
    public async Task Imap_peek_false_marks_the_mail_read_at_fetch_so_a_failed_one_is_not_fetched_again(Ending ending)
    {
        await Deliver("failed");

        var passes = await RouteEnding("imap", "fetchFilter=Unseen&postProcess=Delete&peek=false", ending, passes: 3, within: Settle);

        passes.Should().Be(1, "the mail was marked read when it was fetched");
        (await ImapInbox()).Should().Equal([("failed", true)]);
    }

    [Fact]
    public async Task Imap_peek_false_marks_a_mail_whose_route_throws_read()
    {
        await Deliver("failed");
        var routed = 0;
        var consumer = Consumer("imap", "fetchFilter=Unseen&postProcess=Delete&peek=false", _ =>
        {
            Interlocked.Increment(ref routed);
            return Task.FromException(new InvalidOperationException("route failed"));
        });
        await consumer.Start();
        await Until(() => Volatile.Read(ref routed) >= 3, Settle);
        await consumer.Stop();

        routed.Should().Be(1);
        (await ImapInbox()).Should().Equal([("failed", true)]);
    }

    [Theory]
    [InlineData(Ending.Unhandled)]
    [InlineData(Ending.RollbackOnly)]
    public async Task Pop3_a_failed_exchange_keeps_the_mail_on_the_server(Ending ending)
    {
        await Deliver("failed");

        var passes = await RouteEnding("pop3", "postProcess=Delete", ending, passes: 2,
            Header(MailHeaders.Delete, true));

        passes.Should().BeGreaterThanOrEqualTo(2, "a POP3 mail left on the server is fetched again");
        (await Pop3Inbox()).Should().Equal("failed");
    }

    [Fact]
    public async Task Pop3_a_handled_failure_commits()
    {
        await Deliver("handled");

        await RouteEnding("pop3", "postProcess=Delete", Ending.Handled, passes: 1);

        (await Pop3Inbox()).Should().BeEmpty();
    }

    // ── Apache Camel's options delete and copyTo ─────────────────────────

    [Fact]
    public async Task Imap_delete_option_deletes_the_mail()
    {
        await Deliver("gone");

        await RouteOnce("imap", "delete=true", 1, _ => None());

        (await ImapInbox()).Should().BeEmpty();
    }

    [Fact]
    public async Task Imap_delete_option_with_moveTo_moves_the_mail_without_marking_it_read()
    {
        await CreateFolders("Archive");
        await Deliver("moved");

        await RouteOnce("imap", "delete=true&moveTo=Archive", 1, _ => None());

        (await ImapInbox()).Should().BeEmpty();
        (await ImapFolder("Archive")).Should().Equal([("moved", false)]);
    }

    [Fact]
    public async Task Imap_delete_header_false_overrides_the_delete_option()
    {
        await Deliver("keep");

        await RouteOnce("imap", "delete=true", 1, _ => Header(MailHeaders.Delete, false));

        (await ImapInbox()).Should().Equal([("keep", true)]);
    }

    [Fact]
    public async Task Imap_copyTo_option_copies_the_mail_before_postProcess()
    {
        await CreateFolders("Copies");
        await Deliver("copied");

        await RouteOnce("imap", "copyTo=Copies", 1, _ => None());

        (await ImapInbox()).Should().Equal([("copied", true)]);
        (await ImapFolder("Copies")).Should().Equal([("copied", false)]);
    }

    [Fact]
    public async Task Imap_copyTo_header_overrides_the_copyTo_option()
    {
        await CreateFolders("Copies", "Other");
        await Deliver("copied");

        await RouteOnce("imap", "copyTo=Copies", 1, _ => Header(MailHeaders.CopyTo, "Other"));

        (await ImapFolder("Copies")).Should().BeEmpty();
        (await ImapFolder("Other")).Should().Equal([("copied", false)]);
    }

    [Fact]
    public async Task Pop3_delete_option_deletes_the_mail()
    {
        await Deliver("gone");

        await RouteOnce("pop3", "delete=true", 1, _ => None());

        (await Pop3Inbox()).Should().BeEmpty();
    }

    // ── A missing target folder is created, as Camel's copyOrMoveMessageIfRequired does ──

    private async Task<char> FolderSeparator()
    {
        await Provision(_user);
        using var client = await ImapSession();
        var separator = client.Inbox.DirectorySeparator;
        await client.DisconnectAsync(true);
        return separator;
    }

    [Fact]
    public async Task Imap_postProcess_Move_creates_a_missing_folder()
    {
        await Deliver("moved");

        await RouteOnce("imap", "postProcess=Move&moveTo=Created", 1, _ => None());

        (await ImapInbox()).Should().BeEmpty();
        (await ImapFolder("Created")).Should().Equal([("moved", false)]);
    }

    [Fact]
    public async Task Imap_moveTo_header_creates_a_missing_nested_folder()
    {
        var nested = $"Archive{await FolderSeparator()}2026";
        await Deliver("moved");

        await RouteOnce("imap", "postProcess=MarkRead", 1, _ => Header(MailHeaders.MoveTo, nested));

        (await ImapFolder(nested)).Should().Equal([("moved", true)]);
    }

    [Fact]
    public async Task Imap_copyTo_creates_a_missing_folder()
    {
        await Deliver("copied");

        await RouteOnce("imap", "copyTo=CreatedCopies", 1, _ => None());

        (await ImapFolder("CreatedCopies")).Should().Equal([("copied", false)]);
        (await ImapInbox()).Should().Equal([("copied", true)]);
    }

    [Fact]
    public async Task Imap_delete_with_moveTo_creates_a_missing_folder()
    {
        await Deliver("moved");

        await RouteOnce("imap", "delete=true&moveTo=CreatedArchive", 1, _ => None());

        (await ImapInbox()).Should().BeEmpty();
        (await ImapFolder("CreatedArchive")).Should().Equal([("moved", false)]);
    }

    // ── Idempotency: idempotentRepository, and the in-memory idempotent=true ──

    /// <summary>
    /// Runs a route from a mail endpoint in a context that registers <paramref name="repository"/> as "dedup", for
    /// <paramref name="watch"/> or until <paramref name="done"/>; returns the subjects routed, in order.
    /// </summary>
    private async Task<IReadOnlyList<string>> RouteInContext(string protocol, string options, Action<IExchange> route,
        IIdempotentRepository? repository, TimeSpan watch, Func<IReadOnlyList<string>, bool>? done = null)
    {
        await Provision(_user);
        var routed = new ConcurrentQueue<string>();
        var ctx = new RouteContext();
        ctx.AddComponent(protocol == "imap" ? new ImapComponent() : new Pop3Component());
        if (repository is not null)
            ctx.AddIdempotentRepository("dedup", repository);
        ctx.AddRoutes(r => r.From(
                $"{protocol}://localhost?port={(protocol == "imap" ? 3143 : 3110)}&username={_user}&password=secret" +
                $"&security=None&delay=300&{options}")
            .Process(exchange =>
            {
                routed.Enqueue(Subject(exchange));
                route(exchange);
            }));
        await using (ctx)
        {
            await ctx.Start();
            await Until(() => done?.Invoke(routed.ToArray()) == true, watch);
        }
        return routed.ToArray();
    }

    [Fact]
    public async Task Imap_idempotentRepository_skips_a_mail_the_repository_holds()
    {
        var id = await Deliver("held");
        var repository = new InMemoryIdempotentRepository();
        await repository.Add(id!);

        var routed = await RouteInContext("imap", "idempotentRepository=dedup", _ => { }, repository, Settle);

        routed.Should().BeEmpty();
        (await ImapInbox()).Should().Equal([("held", false)]);
    }

    [Fact]
    public async Task Imap_idempotentRepository_removes_the_key_of_a_committed_mail_by_default()
    {
        var id = await Deliver("committed");
        var repository = new InMemoryIdempotentRepository();

        var routed = await RouteInContext("imap", "idempotentRepository=dedup", _ => { }, repository, Wait,
            r => r.Count == 1);

        routed.Should().Equal("committed");
        (await repository.Contains(id!)).Should().BeFalse("idempotentRepositoryRemoveOnCommit is true by default");
        (await ImapInbox()).Should().Equal([("committed", true)]);
    }

    [Fact]
    public async Task Imap_idempotentRepository_without_remove_on_commit_skips_the_mail_fetched_again()
    {
        var id = await Deliver("kept");
        var repository = new InMemoryIdempotentRepository();

        var routed = await RouteInContext("imap",
            "postProcess=None&idempotentRepository=dedup&idempotentRepositoryRemoveOnCommit=false", _ => { }, repository,
            Settle);

        routed.Should().Equal(["kept"], "the mail stays unseen and is fetched on every poll, but its key is confirmed");
        (await repository.Contains(id!)).Should().BeTrue();
    }

    [Fact]
    public async Task Imap_idempotentRepository_releases_the_key_of_a_failed_mail()
    {
        var id = await Deliver("failed");
        var repository = new InMemoryIdempotentRepository();

        var routed = await RouteInContext("imap",
            "postProcess=Delete&idempotentRepository=dedup&idempotentRepositoryRemoveOnCommit=false",
            exchange => exchange.MarkRollbackOnly(), repository, Wait, r => r.Count >= 2);

        routed.Should().HaveCountGreaterThanOrEqualTo(2, "a failed mail is routed again");
        (await repository.Contains(id!)).Should().BeFalse();
    }

    [Fact]
    public async Task Imap_idempotentRepository_that_nothing_is_registered_under_fails_the_start()
    {
        await using var ctx = new RouteContext();
        var component = new ImapComponent();
        ctx.AddComponent(component);
        var consumer = component
            .CreateEndpoint(EndpointUriParser.Parse("imap://localhost?port=3143&idempotentRepository=missing"))
            .CreateConsumer(new DelegateProcessor((_, _) => Task.CompletedTask));

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
            .Contain("Nothing is registered under 'missing'");
    }

    [Fact]
    public async Task Imap_idempotent_routes_a_failed_mail_again()
    {
        await Deliver("retried");
        var attempts = 0;

        var routed = await RouteInContext("imap", "idempotent=true", _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("first attempt fails");
        }, null, Wait, r => r.Count >= 2);

        routed.Should().Equal("retried", "retried");
        (await ImapInbox()).Should().Equal([("retried", true)]);
    }

    [Fact]
    public async Task Imap_idempotent_does_not_skip_a_mail_that_minAge_held_back()
    {
        await Deliver("young");

        var routed = await RouteInContext("imap", "idempotent=true&minAge=1500", _ => { }, null, Wait,
            r => r.Count >= 1);

        routed.Should().Equal(["young"], "the age filter runs before the key is taken");
    }

    [Fact]
    public async Task Pop3_idempotent_tells_mails_without_a_Message_ID_apart_by_UIDL()
    {
        await Deliver("first", withMessageId: false);
        var routed = new ConcurrentQueue<string>();
        var missingId = true;
        var ctx = new RouteContext();
        ctx.AddComponent(new Pop3Component());
        ctx.AddRoutes(r => r.From(
                $"pop3://localhost?port=3110&username={_user}&password=secret&security=None&delay=300" +
                "&idempotent=true&postProcess=Delete")
            .Process(exchange =>
            {
                missingId &= string.IsNullOrEmpty(exchange.In.GetHeader<string>(MailHeaders.MessageId));
                routed.Enqueue(Subject(exchange));
            }));
        await using (ctx)
        {
            await ctx.Start();
            await Until(() => routed.Count >= 1);
            await Deliver("second", withMessageId: false);
            await Until(() => routed.Count >= 2);
        }

        missingId.Should().BeTrue("the test needs mails without a Message-ID");
        routed.Should().Equal("first", "second");
    }
}
