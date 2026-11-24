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
| Microsoft Update Catalog | Searches up to 12 detected IDs and examines up to six first-page listings per ID to retain up to two relevant candidates. Reads published architecture and hardware IDs and resolves files and digests through the official download dialog. |
| NVIDIA | Resolves the detected GPU through NVIDIA’s product matrix. Queries Windows and architecture-specific Game Ready and Studio releases, plus professional and legacy branches where the product can be identified. |
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

Windows Update offers carry native applicability. Other listings start as candidates
until their INF Models sections and exact PnP identifiers match the PC. Device-specific
hardware IDs retain subsystem and revision fields. Component details show the selected
Models section, installed version, INF DriverVer and applicable signed catalog evidence.
Package release versions are shown separately. Extension and software-component versions
remain visible without comparison to a base device’s driver.

The newest compatible label applies within comparable verified model, component and branch
groups. The details also show a more specific identifier when known. OEM recommendations
come from publisher metadata. Generic vendor releases remain available as alternatives.

The default filter requires verified WHQL evidence for matching INF/catalog pairs. A
publisher WHQL claim and a valid installer or catalog signature are displayed separately.
The scan can inspect up to four Windows Update offers and four eligible HTTP packages by
default, with a 256 MiB limit per automatically inspected package. Larger packages,
publisher-gated packages and unresolved evidence can be reviewed in **Filtered candidates**.
**Verify package** or **Download selected** starts a cancellable operation.

## Acquisition and cache

Downloads follow publisher-provided HTTPS URLs through approved distribution hosts.
Archives need published digest evidence for publication. Executable packages require a
valid Authenticode signature. Driver inspection reads CAB, ZIP and 7z archives and
supported signed self-extractors, including one level of nested archives. Installers
that unpack only at run time retain an explicit inspection limitation. Packages that
require a CAPTCHA or publisher-managed license step offer a link to official support.

The app saves the original package after a complete download. It removes
workspace files on cancellation or failure. Scans and package operations
leave the Windows Driver Store unchanged.

Metadata is reused for 12 hours and revalidated with publisher HTTP validators where
available. The metadata cache keeps up to 14 days of data and is pruned toward 256 MiB.
Verified payloads are reused for seven days and pruned toward 4 GiB. Pruning runs on
subsequent catalog or package activity. Saved downloads remain available in the Drivers
download folder.
