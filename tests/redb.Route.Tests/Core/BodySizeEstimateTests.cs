using FluentAssertions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// The byte count the endpoint statistics keep is measured, not produced: text, bytes and a seekable stream have a
/// size; any other object counts as 0, as Camel does not count bytes at all. It used to be serialized to JSON on every
/// incoming exchange, only to count the bytes of the result.
/// </summary>
public class BodySizeEstimateTests
{
    private sealed class Order
    {
        public int Reads;
        public string Customer
        {
            get
            {
                Interlocked.Increment(ref Reads);
                return "acme";
            }
        }
    }

    [Fact]
    public void An_object_body_is_not_serialized_to_be_counted()
    {
        var order = new Order();

        var size = EndpointBase<EndpointOptions>.EstimateBodySize(order);

        order.Reads.Should().Be(0, "counting bytes must not serialize the body");
        size.Should().Be(0);
    }

    [Fact]
    public void Text_bytes_and_a_seekable_stream_are_measured()
    {
        EndpointBase<EndpointOptions>.EstimateBodySize("héllo").Should().Be(6);
        EndpointBase<EndpointOptions>.EstimateBodySize(new byte[7]).Should().Be(7);
        EndpointBase<EndpointOptions>.EstimateBodySize(new MemoryStream(new byte[9])).Should().Be(9);
    }
}
