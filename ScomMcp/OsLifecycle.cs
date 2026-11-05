using System.Collections.Immutable;

namespace ScomMcp;

public sealed class LifecycleSettings
{
    public int ApproachingSupportDays { get; set; } = 365;
    public int MaxCatalogAgeDays { get; set; } = 90;

    public void Validate()
    {
        if (ApproachingSupportDays is < 1 or > 3650 || MaxCatalogAgeDays is < 1 or > 90)
            throw new InvalidOperationException("Lifecycle settings require ApproachingSupportDays 1..3650 and MaxCatalogAgeDays 1..90.");
    }
}

public sealed record LifecycleProduct(string Product, DateOnly Start, DateOnly MainstreamEnd,
    DateOnly ExtendedEnd, ImmutableArray<string> Editions, string SourceUrl, DateOnly? OptionalEsuEnd = null);

public sealed record OsLifecycleRow(string EntityId, string Hostname, string? ObservedOsCaption,
    string Assessment, string Reason, string? Product, string? Edition, string? LifecyclePolicy,
    DateOnly? MainstreamEnd, DateOnly? ExtendedEnd, int? DaysToExtendedEnd,
    bool? ApproachingExtendedEnd, DateOnly? OptionalEsuEnd, string EsuEntitlement,
    ImmutableArray<string> EvidenceUrls);

public static class OsLifecycleCatalog
{
    public const string Version = "windows-server-fixed-2026-11-02.1";
    public static readonly DateOnly VerifiedOn = new(2026, 11, 2);
    public const string EsuSource = "https://learn.microsoft.com/en-us/lifecycle/faq/extended-security-updates";
    public const string ChannelSource = "https://learn.microsoft.com/en-us/windows-server/get-started/servicing-channels-comparison";
    public static readonly ImmutableArray<LifecycleProduct> Products =
    [
        new("Windows Server 2012 R2", new(2013, 11, 25), new(2018, 10, 9), new(2023, 10, 10),
            ["Datacenter", "Essentials", "Foundation", "Standard"],
            "https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2012-r2", new(2026, 10, 13)),
        new("Windows Server 2016", new(2016, 10, 15), new(2022, 1, 11), new(2027, 1, 12),
            ["Datacenter", "Essentials", "Standard"],
            "https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2016"),
        new("Windows Server 2019", new(2018, 11, 13), new(2024, 1, 9), new(2029, 1, 9),
            ["Datacenter", "Essentials", "Standard"],
            "https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2019"),
        new("Windows Server 2022", new(2021, 8, 18), new(2026, 10, 13), new(2031, 10, 14),
            ["Datacenter", "Essentials", "Standard"],
            "https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2022"),
        new("Windows Server 2025", new(2024, 11, 1), new(2029, 11, 13), new(2034, 11, 14),
            ["Datacenter", "Essentials", "Standard"],
            "https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2025")
    ];

    public static OsLifecycleRow Assess(Device device, DateOnly asOf, bool catalogFresh, int threshold)
    {
        OsLifecycleRow Unknown(string assessment, string reason) => new(device.EntityId, device.Hostname,
            device.OsVersion, assessment, reason, null, null, null, null, null, null, null, null,
            "notAssessed", []);
        if (device.OsFamily != "windows")
            return Unknown("unsupported", "OS_FAMILY_OUTSIDE_WINDOWS_SERVER_PROFILE");
        if (string.IsNullOrWhiteSpace(device.OsVersion))
            return Unknown("unknown", "OS_CAPTION_MISSING");
        var caption = device.OsVersion.Trim();
        foreach (var product in Products)
        foreach (var edition in product.Editions)
        {
            // Only explicit product + edition aliases; never infer a channel from build numbers or fuzzy captions.
            if (!string.Equals(caption, $"{product.Product} {edition}", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(caption, $"Microsoft {product.Product} {edition}", StringComparison.OrdinalIgnoreCase))
                continue;
            var usable = catalogFresh && asOf >= product.Start;
            var phase = !usable ? "unknown" : asOf <= product.MainstreamEnd ? "mainstream" :
                asOf <= product.ExtendedEnd ? "extended" : "ended";
            var days = usable ? product.ExtendedEnd.DayNumber - asOf.DayNumber : (int?)null;
            return new(device.EntityId, device.Hostname, device.OsVersion, phase,
                !catalogFresh ? "CATALOG_STALE_OR_NOT_YET_VERIFIED" :
                asOf < product.Start ? "PRODUCT_NOT_RELEASED_AS_OF_DATE" : "EXACT_PRODUCT_EDITION_CAPTION",
                product.Product, edition, "fixed", product.MainstreamEnd, product.ExtendedEnd, days,
                days is null ? null : days >= 0 && days <= threshold, product.OptionalEsuEnd, "notAssessed",
                [product.SourceUrl, ChannelSource, EsuSource]);
        }
        return Unknown("unknown", "PRODUCT_EDITION_OR_CHANNEL_NOT_EXACTLY_MAPPED");
    }
}
