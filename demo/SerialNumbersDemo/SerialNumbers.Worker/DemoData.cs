using redb.Core;
using redb.Core.Models.Entities;
using SerialNumbers.Domain.Entities;
using SerialNumbers.Domain.Services;

namespace SerialNumbers.Worker;

/// <summary>
/// The demo's reference data: two partners and three products. Idempotent: an object whose unique
/// key already exists is left as it is.
/// </summary>
public static class DemoData
{
    public static async Task SeedAsync(IRedbService redb)
    {
        await redb.SyncSchemeAsync<Partner>();
        await redb.SyncSchemeAsync<Product>();

        await EnsurePartnerAsync(redb, new Partner
        {
            Code = "acme",
            Name = "ACME Manufacturing",
            Transport = Transports.Sftp,
            SftpInboundFolder = "/upload/acme/to-hub",
            SftpOutboundFolder = "/upload/acme/from-hub",
        });

        await EnsurePartnerAsync(redb, new Partner
        {
            Code = "globex",
            Name = "Globex Pharma",
            Transport = Transports.As2,
            As2Id = "GLOBEX",
            As2Url = "http://localhost:4081/as2/globex-inbox",
        });

        await EnsureProductAsync(redb, new Product
        {
            Gtin = "04607001234567",
            Name = "Paracetamol 500 mg, 20 tablets",
            Status = ProductStatuses.Active,
            MaxPerRequest = 50_000,
            AnnualQuota = 10_000_000,
        });

        // A small yearly quota, so the second GLOBEX sample runs into it.
        await EnsureProductAsync(redb, new Product
        {
            Gtin = "04607009990001",
            Name = "Vitamin D3 drops, 10 ml",
            Status = ProductStatuses.Active,
            MaxPerRequest = 30_000,
            AnnualQuota = 40_000,
        });

        // Not activated yet: requests for it wait on hold until someone changes its status.
        await EnsureProductAsync(redb, new Product
        {
            Gtin = "04607005550001",
            Name = "Ibuprofen 200 mg, 10 capsules",
            Status = ProductStatuses.Draft,
            MaxPerRequest = 20_000,
            AnnualQuota = 1_000_000,
        });
    }

    private static async Task EnsurePartnerAsync(IRedbService redb, Partner partner)
    {
        var existing = await redb.Query<Partner>().WhereRedb(o => o.ValueUnique == partner.Code).FirstOrDefaultAsync();
        if (existing is null)
            await redb.SaveAsync(new RedbObject<Partner> { name = partner.Name, ValueUnique = partner.Code, Props = partner });
    }

    private static async Task EnsureProductAsync(IRedbService redb, Product product)
    {
        var existing = await redb.Query<Product>().WhereRedb(o => o.ValueUnique == product.Gtin).FirstOrDefaultAsync();
        if (existing is null)
            await redb.SaveAsync(new RedbObject<Product> { name = product.Name, ValueUnique = product.Gtin, Props = product });
    }
}
