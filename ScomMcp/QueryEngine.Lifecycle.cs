using System.Collections.Immutable;

namespace ScomMcp;

public sealed partial class QueryEngine
{
    private static readonly TimeZoneInfo LifecycleZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    public static void ValidateLifecycleQuery(string? assessment, int pageSize)
    {
        ValidatePageSize(pageSize);
        Guard.Argument(assessment is null or "mainstream" or "extended" or "ended" or "unknown" or "unsupported" or "approaching",
            "assessment must be mainstream, extended, ended, unknown, unsupported, or approaching.");
    }

    public Envelope OsSupport(Dataset ds, Caller caller, string scope, string? assessment,
        int pageSize, string? cursor)
    {
        ValidateLifecycleQuery(assessment, pageSize);
        var now = clock.GetUtcNow();
        var asOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, LifecycleZone).DateTime);
        var age = asOf.DayNumber - OsLifecycleCatalog.VerifiedOn.DayNumber;
        var fresh = age >= 0 && age <= lifecyclePolicy.MaxCatalogAgeDays;
        var rows = ds.Snapshot.Devices.OrderBy(d => d.Hostname, StringComparer.Ordinal)
            .ThenBy(d => d.EntityId, StringComparer.Ordinal)
            .Select(d => OsLifecycleCatalog.Assess(d, asOf, fresh, lifecyclePolicy.ApproachingSupportDays)).ToImmutableArray();
        var matching = rows.Where(r => assessment is null ||
            (assessment == "approaching" ? r.ApproachingExtendedEnd == true : r.Assessment == assessment)).ToImmutableArray();
        var binding = Wire.Encode(new { tool = "list_os_support_assessments", assessment, pageSize,
            asOf, catalog = OsLifecycleCatalog.Version, lifecyclePolicy.ApproachingSupportDays, lifecyclePolicy.MaxCatalogAgeDays });
        var page = Page(matching, ds, caller, scope, pageSize, cursor, binding);
        var unknown = rows.Count(r => r.Assessment == "unknown");
        var unsupported = rows.Count(r => r.Assessment == "unsupported");
        var missing = Math.Max(0, ds.Snapshot.ExpectedDeviceCount - rows.Length);
        return Wrap(ds, new
        {
            asOfDate = asOf, dateTimeZone = LifecycleZone.Id, evaluatedAtUtc = now,
            catalogVersion = OsLifecycleCatalog.Version, catalogVerifiedOn = OsLifecycleCatalog.VerifiedOn,
            catalogAgeDays = age, maxCatalogAgeDays = lifecyclePolicy.MaxCatalogAgeDays,
            catalogValidThrough = OsLifecycleCatalog.VerifiedOn.AddDays(lifecyclePolicy.MaxCatalogAgeDays),
            catalogState = fresh ? "fresh" : age < 0 ? "notYetVerified" : "stale",
            approachingSupportDays = lifecyclePolicy.ApproachingSupportDays,
            scopeComputerCount = rows.Length, expectedComputerCount = ds.Snapshot.ExpectedDeviceCount,
            missingComputerCount = missing, classifiedComputerCount = rows.Length - unknown - unsupported,
            unknownComputerCount = unknown, unsupportedComputerCount = unsupported,
            assessmentComplete = fresh && unknown == 0 && unsupported == 0 && missing == 0 &&
                ds.Snapshot.Status == "ok" && ds.ValidationLimitations.IsEmpty &&
                now - ds.Snapshot.CollectedAtUtc <= TimeSpan.FromMinutes(settings.FreshMinutes),
            countsAreCompleteForRetainedComputers = true,
            countsByAssessment = rows.GroupBy(r => r.Assessment).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { assessment = g.Key, computerCount = g.Count() }).ToArray(),
            approachingExtendedEndCount = rows.Count(r => r.ApproachingExtendedEnd == true),
            matchedComputerCount = matching.Length, page.Rows, page.TotalCount, page.PageSize, page.NextCursor
        }, new[]
        {
            "PRODUCT_LIFECYCLE_NOT_PATCH_COMPLIANCE_OR_GUARANTEED_SUPPORT_ELIGIBILITY",
            "SUPPORT_END_DATES_ARE_INCLUSIVE_PACIFIC_CALENDAR_DATES",
            "OPTIONAL_ESU_IS_NOT_EXTENDED_SUPPORT_OR_PROOF_OF_ENTITLEMENT",
            "CURATED_CATALOG_NO_PER_QUESTION_SOURCE_OR_WEB_QUERIES"
        }.Concat(fresh ? [] : new[] { "LIFECYCLE_CATALOG_STALE_OR_NOT_YET_VERIFIED" })
            .Concat(unknown + unsupported + missing == 0 ? [] : new[] { "INCOMPLETE_OS_LIFECYCLE_ASSESSMENT" }),
            forcePartial: true);
    }
}
