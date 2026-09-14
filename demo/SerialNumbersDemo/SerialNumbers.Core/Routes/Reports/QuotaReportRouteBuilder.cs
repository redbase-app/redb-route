using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.Quartz;
using redb.Route.RedbCore.Extensions;
using redb.Route.Sql;
using SerialNumbers.Core.Services;

namespace SerialNumbers.Core.Routes.Reports;

/// <summary>
/// A route nothing has to arrive for: Quartz fires it on a cron schedule. Each run sums this year's
/// allocation ledger per product, sets it against the product's annual quota and writes a CSV file.
/// <para>
/// <c>Stateful()</c> keeps two runs from overlapping when one takes longer than the interval. The
/// schedule is read in <see cref="ModuleSettings.ReportTimeZone"/>, so "06:00" means the same hour on
/// every server, whatever its local clock says.
/// </para>
/// </summary>
public sealed class QuotaReportRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        var settings = ModuleSettings.FromContext(Context!);

        From(Cron.Schedule("reports/quota-usage", settings.ReportSchedule)
                .TimeZone(settings.ReportTimeZone)
                .Stateful())
            .RouteId("quota-report")
            .To(Sql.Execute("SELECT gtin, COUNT(*) AS requests, SUM(CAST(quantity AS bigint)) AS issued " +
                            "FROM dbo.serial_allocations WHERE allocation_year = YEAR(SYSUTCDATETIME()) " +
                            "GROUP BY gtin ORDER BY gtin")
                .DataSource(Constant(RegistryNames.SerialsDatabase)))
            .Choice()
                .When(e => e.In.GetHeader<int>(SqlHeaders.RowCount) == 0)
                    .Log("Quota report skipped: nothing allocated this year")
                .Otherwise()
                    .ProcessWithRedb(QuotaReport.RenderCsvAsync)
                    .SetHeader(SerialHeaders.ReportFileName, _ => $"quota_usage_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv")
                    .To(FileDsl.Write(settings.ReportDirectory).FileName("${header.serials.reportFileName}"))
                    .Log("Quota report written: ${header.serials.reportFileName}")
            .EndChoice();
    }
}
