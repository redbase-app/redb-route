using redb.Route.As4.Signals;

namespace redb.Route.Tests.As4;

/// <summary>
/// The in-memory store of sent receipts empties itself: almost no receipt is read again, so entries leave by age and
/// by count when new ones come, not when they are read.
/// </summary>
public class RecentReceiptsTests
{
    [Fact]
    public async Task BeyondTheCapacity_TheOldestLeave()
    {
        var receipts = new RecentReceipts(TimeSpan.FromHours(1), capacity: 2);

        await receipts.Store("1", new byte[] { 1 });
        await receipts.Store("2", new byte[] { 2 });
        await receipts.Store("3", new byte[] { 3 });

        (await receipts.Retrieve("1")).Should().BeNull();
        (await receipts.Retrieve("2")).Should().Equal(2);
        (await receipts.Retrieve("3")).Should().Equal(3);
    }

    [Fact]
    public async Task Expired_IsNotReturned_AndLeavesWithTheNextStore()
    {
        var receipts = new RecentReceipts(TimeSpan.FromHours(1), capacity: 10);

        await receipts.Store("old", new byte[] { 1 }, ttl: TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);

        (await receipts.Retrieve("old")).Should().BeNull();
        await receipts.Store("new", new byte[] { 2 });
        (await receipts.Retrieve("new")).Should().Equal(2);
    }

    [Fact]
    public async Task StoredAgain_UnderTheSameKey_KeepsTheNewerAcrossTheOldersRemoval()
    {
        var receipts = new RecentReceipts(TimeSpan.FromHours(1), capacity: 2);

        await receipts.Store("a", new byte[] { 1 });
        await receipts.Store("a", new byte[] { 2 });
        await receipts.Store("b", new byte[] { 3 });   // the first "a" item leaves the queue, the newer value stays

        (await receipts.Retrieve("a")).Should().Equal(2);
    }
}
