namespace WinOptimizer.Core.Features.Drivers.Models;

[Flags]
public enum DriverSources
{
    WindowsUpdate = 1, MicrosoftCatalog = 2, Nvidia = 4, Amd = 8, Intel = 16, Realtek = 32,
    Asus = 64, Msi = 128, Gigabyte = 256, AsRock = 512, Dell = 1024, Hp = 2048, Lenovo = 4096,
    All = 8191
}

[Flags]
public enum NvidiaChannels { GameReady = 1, Studio = 2, Both = 3 }

public enum DriverSourceState { Success, Partial, Unavailable, Unsupported, Stale, VerificationFailed }
public enum DriverApplicability { Unknown, WindowsUpdate, PackageMatch, Incompatible }
public enum DriverVersionRelation { Unknown, Newer, Current, Older, NotComparable }
public enum DriverVerification { Metadata, Verified, Unsupported, Failed, LicenseRequired }
public enum DriverIdentifierKind { Hardware, Compatible }

public sealed record DriverSystemContext
{
    public int Major { get; init; }
    public int Minor { get; init; }
    public int Build { get; init; }
    public int ProductType { get; init; }
    public int SuiteMask { get; init; }
    public string Architecture { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Sku { get; init; } = "";
    public string BoardManufacturer { get; init; } = "";
    public string BoardProduct { get; init; } = "";
    public string BoardVersion { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public string[] ProcessorNames { get; init; } = [];
}

public sealed record DriverDiscoveryContext(DriverInventory Inventory, DriverScanOptions Options);
public sealed record DriverSourceOutcome(DriverSources Source, DriverSourceState State, int Records,
    IReadOnlyList<string> Notices, Uri? SupportPage);
public sealed record DriverSourceReference(DriverSources Source, string Id, string Revision, DateTimeOffset RetrievedAt,
    Uri? LandingPage, Uri? Descriptor, Uri[] Payloads);
public sealed record DriverHash(string Algorithm, string Value);
public sealed record DriverPayload(Uri Url, DriverHash[] Hashes);
public sealed record DriverIdentifier(string Value, DriverIdentifierKind Kind, int Order);
public sealed record DriverTarget(string Architecture = "", int? MinBuild = null, int? MaxBuild = null,
    string Oem = "", string Model = "", string Os = "");
public sealed record DriverComponent(string Inf, string ClassName, string Provider, string Version, string Date,
    DriverIdentifier[] Identifiers, string[] ModelsSections, string InfSha256, string CatalogSha256,
    string Signer, bool SignatureValid, DriverCertification Certification)
{
    public string Catalog { get; init; } = "";
    public string ExtensionId { get; init; } = "";
}

public sealed record DriverCatalogRecord(DriverSourceReference[] References, DriverPayload[] Payloads)
{
    public string PackageVersion { get; init; } = "";
    public DateOnly? ReleaseDate { get; init; }
    public string Branch { get; init; } = "";
    public string Family { get; init; } = "";
    public DriverTarget Target { get; init; } = new();
    public string[] ListedHardwareIds { get; init; } = [];
    public bool ClaimsWhql { get; init; }
    public bool OemRecommended { get; init; }
    public bool LicenseRequired { get; init; }
    public DriverVerification Verification { get; init; }
    public string VerificationDetail { get; init; } = "";
    public string[] ContentSha256 { get; init; } = [];
    public bool PayloadSignatureValid { get; init; }
    public DriverComponent[] Components { get; init; } = [];
    public string[] DeclaredComponents { get; init; } = [];
    public string[] DeclaredPrerequisites { get; init; } = [];
}

public sealed record DriverDeviceMatch(DriverDevice Device, DriverComponent Component, DriverIdentifier DeviceIdentifier,
    DriverIdentifier InfIdentifier, DriverVersionRelation VersionRelation, bool MoreSpecificIdentifier)
{
    public bool NewestFound { get; init; }
}

public sealed record DriverPackageResult(DriverUpdate Update, string? OutputPath = null);
