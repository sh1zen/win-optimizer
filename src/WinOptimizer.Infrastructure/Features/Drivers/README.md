# Driver catalog and downloads

**Drivers** compares devices and installed drivers with enabled official
sources. A scan collects the Windows version, build, and architecture.
It also collects the PC and board make and model, and the ordered hardware
and compatible IDs for each device. **Settings → Drivers** saves enabled
sources and NVIDIA channels. See the
[driver guide](../../../../docs/DRIVERS.md) for the scan and removal procedures.

## Source coverage

| Source | Coverage |
| --- | --- |
| Windows Update | Native public WUA search, optional offers, per-offer results and exports of bundled payloads. Manual acquisition requests the exact offer revision. |
| Microsoft Update Catalog | Searches up to 12 detected IDs and examines up to six first-page listings per ID to retain up to two resolved offers. Uses the build requirements for the running Windows generation, reads the declared architecture and hardware IDs, and resolves files and digests through the official download dialog. |
| NVIDIA | Resolves the detected GPU through NVIDIA’s product matrix. Queries Windows and architecture-specific Game Ready and Studio releases, plus professional and legacy branches where the product can be identified. The scan asks for license acceptance before acquiring packages. |
| AMD | Resolves the detected GPU or APU and identified board chipset through AMD’s product directory. Reads matching Windows release cards and chipset component versions from release notes. Keeps Adrenalin branches and optional releases separate. |
| Intel | Scoped download pages for Arc and Core Ultra graphics, Intel 7th–14th generation graphics families, Wi-Fi, Bluetooth, AX200 legacy releases, Ethernet and chipset INF packages. |
| Realtek | Public PCIe Ethernet metadata and legacy Windows 10 HDA releases. The publisher’s interactive download step opens in the browser. Components without a usable public catalog can be reviewed through Microsoft or the PC maker. |
| ASUS and MSI | Product-level driver services for a detected support model. |
| Gigabyte and ASRock | Gigabyte’s product directory and download panels, or ASRock’s public board index and product driver tables. Ambiguous revisions and challenged pages are reported. |
| Dell | Client CAB index and matching model manifests, with the complete client CAB as a fallback. |
| HP | HPIA platform and SoftPaq reference CABs for a listed SMBIOS platform and exact Windows build. |
| Lenovo | Lenovo machine-type and OS System Update manifests. Package descriptors supply the original installer names and digests. |

Use **Sources** to review coverage for this scan. Providers can return partial
results, report an unsupported model, hit a publisher challenge or work from cached
metadata. The catalog describes releases found in the sources and products checked.

## Matching and verification

The scan uses exact PnP identifiers in signed INF Models sections applicable to the
running Windows version. Windows Update offers also carry native applicability.
Device-specific hardware IDs retain subsystem and revision fields. The comparison
uses the installed device's driver date and version and the matching DDInstall
section's `DriverVer`, inheriting the INF's global value when needed.
Software-component drivers are compared with their own installed bindings. Component
details show the Models section, version, and signed catalog evidence.

The scan selects a verified upgrade per device and comparable release channel,
preferring device-specific matches before newer dates and versions. OEM
recommendations come from publisher metadata.

The default filter requires verified WHQL evidence for matching INF/catalog pairs.
Turning off the filter also admits valid Authenticode signatures from publishers
in the Windows Trusted Publishers store. A publisher WHQL claim and a valid
installer or catalog signature are displayed separately.
The scan can inspect up to four Windows Update offers and four eligible HTTP packages by
default. Automatic HTTP inspection allows up to 2 GiB for NVIDIA and 256 MiB for
other sources. The scan prioritizes recent offers for distinct targets, verifies
up to two HTTP packages at once, and waits for each operation to finish or for the
user to cancel the scan. **Sources** shows coverage and the reasons for package rejection.
**Download selected** starts a cancellable operation.

## Acquisition and cache

Downloads follow publisher-provided HTTPS URLs through approved distribution hosts.
Archives need published digest evidence for publication. Executable packages require a
valid Authenticode signature. Driver inspection reads CAB, ZIP and 7z archives and
supported signed self-extractors, including one level of nested archives. Installers
that unpack only at run time retain an explicit inspection limitation. Packages that
require a CAPTCHA or publisher-managed license step offer a link to official support.

The app saves the original package after confirming its verified content hashes. It removes
workspace files on cancellation or failure. Scans and package operations
leave the Windows Driver Store unchanged.

Usable metadata is reused for 12 hours and revalidated with publisher HTTP validators
where available. During an access challenge the scan reports the block and can use
earlier usable metadata. The metadata cache keeps up to 14 days of data and is pruned
toward 256 MiB.
Verified payloads are reused for seven days and pruned toward 4 GiB. Pruning runs on
subsequent catalog or package activity. Saved downloads remain available in the Drivers
download folder.
