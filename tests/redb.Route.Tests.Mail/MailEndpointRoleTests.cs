using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Mail;
using redb.Route.Processors;
using redb.Route.Xml;

namespace redb.Route.Tests.Mail;

/// <summary>
/// imap, pop3 and smtp share <see cref="MailEndpointOptions"/>; an option only one side reads carries
/// <c>[EndpointRole]</c>, so the endpoint of the other side refuses it by name when it is created, and the catalog
/// shows the side.
/// </summary>
public sealed class MailEndpointRoleTests
{
    /// <summary>Every option only a consumer reads, with a value that binds.</summary>
    public static TheoryData<string, string> ConsumerOptions => new()
    {
        { "folder", "Archive" }, { "additionalFolders", "Sent" }, { "delay", "1000" }, { "initialDelay", "1" },
        { "fetchFilter", "All" }, { "maxMessages", "5" }, { "postProcess", "None" }, { "moveTo", "Archive" },
        { "sortBy", "DateAsc" }, { "idle", "true" }, { "idleTimeout", "1000" }, { "fetchBody", "false" },
        { "fetchAttachments", "false" }, { "maxAttachmentSize", "10" }, { "peek", "false" }, { "idempotent", "true" },
        { "searchQuery", "x" }, { "minAge", "1" }, { "maxAge", "1" }, { "subjectFilter", "x" },
        { "fromFilter", "a@b.c" }, { "mapMimeHeaders", "true" }, { "keepRawMessage", "true" },
        { "delete", "true" }, { "copyTo", "Copies" }, { "idempotentRepository", "x" },
        { "idempotentRepositoryRemoveOnCommit", "false" },
    };

    /// <summary>Every option only a producer reads, with a value that binds.</summary>
    public static TheoryData<string, string> ProducerOptions => new()
    {
        { "from", "a@b.c" }, { "to", "a@b.c" }, { "cc", "a@b.c" }, { "bcc", "a@b.c" }, { "replyTo", "a@b.c" },
        { "subject", "x" }, { "contentType", "text/html" }, { "alternativeBody", "true" }, { "attachments", "x" },
    };

    [Theory]
    [MemberData(nameof(ConsumerOptions))]
    public void Smtp_refuses_an_option_only_a_consumer_reads(string name, string value)
    {
        var act = () => new SmtpComponent().CreateEndpoint(
            EndpointUriParser.Parse($"smtp://mail.example.com?password=secret&{name}={value}"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"smtp producer '").And.Contain($"sets {name}: only an imap or pop3 consumer reads it.")
            .And.NotContain("secret");
    }

    [Theory]
    [MemberData(nameof(ProducerOptions))]
    public void Imap_and_pop3_refuse_an_option_only_a_producer_reads(string name, string value)
    {
        foreach (IComponent component in new IComponent[] { new ImapComponent(), new Pop3Component() })
        {
            var act = () => component.CreateEndpoint(
                EndpointUriParser.Parse($"{component.Scheme}://mail.example.com?password=secret&{name}={value}"));

            act.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Contain($"{component.Scheme} consumer '")
                .And.Contain($"sets {name}: only an smtp producer reads it.").And.NotContain("secret");
        }
    }

    [Fact]
    public void The_refusal_names_every_stray_option()
    {
        var act = () => new SmtpComponent().CreateEndpoint(
            EndpointUriParser.Parse("smtp://mail.example.com?delete=true&idempotentRepository=x"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("sets delete, idempotentRepository: only an imap or pop3 consumer reads them.");
    }

    [Theory]
    [InlineData("delay=-1", "delay")]
    [InlineData("maxMessages=-1", "maxMessages")]
    [InlineData("postProcess=Move", "postProcess")]
    public void Smtp_names_a_stray_consumer_option_instead_of_validating_it(string query, string named)
    {
        var act = () => new SmtpComponent().CreateEndpoint(EndpointUriParser.Parse($"smtp://mail.example.com?{query}"));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain($"sets {named}: only an imap or pop3 consumer reads it.");
    }

    [Fact]
    public void A_consumer_still_validates_its_own_options()
    {
        var act = () => new ImapComponent().CreateEndpoint(EndpointUriParser.Parse("imap://mail.example.com?delay=-1"));

        act.Should().Throw<ArgumentOutOfRangeException>().Which.Message.Should().Contain("Delay must be non-negative");
    }

    [Theory]
    [InlineData("imap")]
    [InlineData("pop3")]
    public void Imap_and_pop3_consumers_take_their_own_options(string scheme)
    {
        var uri = EndpointUriParser.Parse(
            $"{scheme}://mail.example.com?delete=true&copyTo=Copies&idempotentRepository=x" +
            "&idempotentRepositoryRemoveOnCommit=false&folder=INBOX&delay=1000&postProcess=Delete");
        IComponent component = scheme == "imap" ? new ImapComponent() : new Pop3Component();

        var act = () => component.CreateEndpoint(uri).CreateConsumer(new DelegateProcessor((_, _) => Task.CompletedTask));

        act.Should().NotThrow();
    }

    [Fact]
    public void Smtp_takes_its_own_options()
    {
        var endpoint = new SmtpComponent().CreateEndpoint(EndpointUriParser.Parse(
            "smtp://mail.example.com?to=a@b.c&from=b@c.d&subject=x&contentType=text/html&disconnect=true"));

        endpoint.CreateProducer().Should().NotBeNull();
    }

    [Fact]
    public void Catalog_marks_each_option_with_the_side_that_reads_it()
    {
        var catalog = ComponentCatalog.Build([new ImapComponent(), new Pop3Component(), new SmtpComponent()]);
        var consumer = ConsumerOptions.Select(row => (string)row[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var producer = ProducerOptions.Select(row => (string)row[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var component in catalog)
        {
            foreach (var option in component.Options)
            {
                var expected = consumer.Contains(option.Name) ? EndpointRole.Consumer
                    : producer.Contains(option.Name) ? EndpointRole.Producer
                    : (EndpointRole?)null;
                option.Role.Should().Be(expected, $"{component.Scheme} {option.Name}");
            }
        }
    }
}
